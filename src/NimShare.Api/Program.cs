using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using NimShare.Api;
using NimShare.Api.Middleware;
using NimShare.Api.Services;
using NimShare.Core.Data;
using NimShare.Core.Entities;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────────────────
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<IpHashOptions>(builder.Configuration.GetSection(IpHashOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));

// ── Database ───────────────────────────────────────────────────────────────
// The persistent DB config file (written by /settings/database) takes
// precedence over env-var / appsettings.json. That's what lets an admin flip
// the app from Sqlite to Azure SQL without a redeploy: they save the config,
// we restart, and Program.cs picks the new provider up here.
var dbConfigStore = new DbConfigStore(builder.Configuration);
builder.Services.AddSingleton(dbConfigStore);
var persistedDbConfig = dbConfigStore.Load();
var dbProvider = persistedDbConfig?.Provider
                 ?? builder.Configuration["Database:Provider"]
                 ?? "Sqlite";
var connString = persistedDbConfig?.ConnectionString
                 ?? builder.Configuration.GetConnectionString("Default")
                 ?? "Data Source=nimshare.db";

// When Sqlite points at a mounted volume like /data/nimshare.db, make sure
// the directory exists — Azure Files creates the share, but not sub-paths.
if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var dataSourceStart = connString.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase);
    if (dataSourceStart >= 0)
    {
        var raw = connString[(dataSourceStart + "Data Source=".Length)..];
        var end = raw.IndexOf(';');
        var path = end >= 0 ? raw[..end] : raw;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* Azure Files may EPERM briefly */ }
        }
    }
}
builder.Services.AddDbContext<NimShareDbContext>(o =>
{
    if (string.Equals(dbProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        // SqlServer migrations live in a dedicated assembly (kept separate so
        // provider-specific SQL — nvarchar / IX names / etc. — doesn't collide
        // with the Sqlite migration set that ships in NimShare.Api itself).
        o.UseSqlServer(connString, b =>
        {
            b.MigrationsAssembly("NimShare.Migrations.SqlServer");
            b.CommandTimeout(45);
            // Built-in transient-error retry — the SqlServer provider knows
            // its own error codes (deadlocks, throttling, connection resets).
            b.EnableRetryOnFailure(maxRetryCount: 4, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
        });
    }
    else
    {
        // Sqlite on Azure Files (SMB mount) sees transient "unable to open
        // database file" errors when the mount is remounted or throttled. Two
        // guards: (1) enable WAL + a 15 s busy_timeout so brief locks don't
        // fault, (2) SqliteRecoveryMiddleware clears the pool + retries GETs.
        //
        // MigrationsAssembly is NimShare.Core because the migration .cs
        // files physically live under src/NimShare.Core/Migrations/ and get
        // compiled into NimShare.Core.dll (the SDK-style project auto-
        // includes any .cs under the project folder). The old
        // MigrationsAssembly("NimShare.Api") was WRONG — EF loaded Api.dll,
        // found zero Migration subclasses, MigrateAsync did nothing, and
        // every schema-change migration since V177 silently never ran. That
        // pattern was the source of the recurring "no such column" 500s;
        // the RepairSqliteMissingColumnsAsync helper below is a belt-and-
        // braces catch-up for deployed DBs that missed columns during that
        // window.
        o.UseSqlite(connString, b =>
        {
            b.MigrationsAssembly("NimShare.Core");
            b.CommandTimeout(45);
        });
    }
});

// ── Auth ───────────────────────────────────────────────────────────────────
// Cookie sign-in is ALWAYS the default (local email+password accounts + the
// first-run admin setup wizard depend on it). Entra ID is layered on top as
// an OPTIONAL second sign-in method when AzureAd:ClientId is configured.
var entraClientId = builder.Configuration["AzureAd:ClientId"];
var entraConfigured = !string.IsNullOrWhiteSpace(entraClientId);

var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "nimshare.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

if (entraConfigured)
{
    // Add Entra as a SECONDARY scheme; Cookies stays the default so the local
    // login page and setup wizard work without an Entra tenant.
    authBuilder
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"),
            openIdConnectScheme: OpenIdConnectDefaults.AuthenticationScheme,
            cookieScheme: null,
            subscribeToOpenIdConnectMiddlewareDiagnosticsEvents: false)
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"), jwtBearerScheme: "Bearer");
}

// Local JWT scheme for mobile clients that authenticate with email + password.
// Coexists with the Entra Bearer scheme (when Entra is configured) — the ApiUser
// policy accepts either.
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
{
    // A separate helper to build the scheme without pulling the whole service
    // provider up here — the JwtTokenService is a singleton so it's safe to
    // resolve once.
    using var sp = builder.Services.BuildServiceProvider();
    var jwt = sp.GetRequiredService<IJwtTokenService>();
    builder.Services.AddAuthentication()
        .AddJwtBearer(JwtTokenService.SchemeName, o =>
        {
            o.TokenValidationParameters = jwt.ValidationParameters;
            o.SaveToken = true;
        })
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthHandler>(
            ApiTokenAuthHandler.SchemeName, _ => { });
}

builder.Services.AddAuthorization(options =>
{
    // ApiUser accepts BOTH schemes so the same /api/v1/* endpoints work for
    //   • mobile / server-to-server clients using a JWT bearer token, AND
    //   • same-origin browser calls from the Razor UI (cookie session).
    // Cookie-authenticated state-changing calls need antiforgery — see the
    // [AutoValidateAntiforgeryToken] filter registered on controllers below.
    var schemes = entraConfigured
        ? new[] { "Bearer", JwtTokenService.SchemeName, ApiTokenAuthHandler.SchemeName, CookieAuthenticationDefaults.AuthenticationScheme }
        : new[] { JwtTokenService.SchemeName, ApiTokenAuthHandler.SchemeName, CookieAuthenticationDefaults.AuthenticationScheme };
    options.AddPolicy("ApiUser", p =>
        p.RequireAuthenticatedUser().AddAuthenticationSchemes(schemes));
    options.AddPolicy("WebUser", p =>
        p.RequireAuthenticatedUser()
         .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme));
});

// Guard state-changing web POSTs. API clients using Bearer tokens are exempt
// by adding [IgnoreAntiforgeryToken] on those specific endpoints or by simply
// omitting the token header when using JWT (the middleware only enforces
// when a cookie session is present).
builder.Services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN");

// DataProtection keys must persist across container restarts — otherwise
// every existing Webhook.SecretEncrypted, AiGateway.ApiKeyEncrypted, and
// SignatureParticipant token stash becomes undecryptable when Azure App
// Service recycles the instance. On App Service Linux the app runs from
// /home/site/wwwroot and /home is backed by Azure Files (persistent);
// anywhere ELSE on the container filesystem is ephemeral.
// v1.10.21 hardening: try harder to land on a persistent path AND log
// clearly where we landed, so operators can catch mis-configuration.
{
    var keysPath = builder.Configuration["DataProtection:KeysPath"];
    var configured = !string.IsNullOrWhiteSpace(keysPath);
    if (!configured)
    {
        // v1.10.26: Zurück zum PRE-v1.10.21-Verhalten (ContentRoot-relativ).
        // Der /home-Pfad in v1.10.21 hat auf Marcus' App Service offenbar
        // Sekundäreffekte ausgelöst (existierende Keys blieben nicht lesbar).
        // Wer /home/data/dp-keys will, setzt DataProtection__KeysPath explizit
        // in den App-Settings.
        keysPath = Path.Combine(
            builder.Environment.ContentRootPath,
            "..", "..", "data", "dp-keys");
    }
    // Normalize
    keysPath = Path.GetFullPath(keysPath);
    bool persistent = false;
    try
    {
        Directory.CreateDirectory(keysPath);
        // Sanity write — if this throws we're on a read-only FS and the
        // whole scheme falls back to in-memory keys (which regenerate on
        // every restart). Log that loudly.
        var testFile = Path.Combine(keysPath, ".nimshare-dp-write-test");
        File.WriteAllText(testFile, DateTimeOffset.UtcNow.ToString("o"));
        File.Delete(testFile);
        persistent = true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[STARTUP] DataProtection keys directory '{keysPath}' NOT writable: {ex.Message}. Encrypted values (API keys, tokens) will be LOST on every restart. Fix: set App-Setting DataProtection__KeysPath to a persistent path (e.g. /home/data/dp-keys on Azure App Service).");
    }
    try
    {
        builder.Services.AddDataProtection()
            .SetApplicationName("NimShare")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        int keyCount = 0;
        try { if (Directory.Exists(keysPath)) keyCount = Directory.GetFiles(keysPath, "*.xml").Length; } catch { }
        Console.WriteLine($"[STARTUP] DataProtection KeysPath={keysPath} (configured={configured}, persistent={persistent}). Existing key files: {keyCount}.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[STARTUP] DataProtection PersistKeysToFileSystem failed for '{keysPath}': {ex.Message}. Falling back to ephemeral (in-memory) keys — encrypted values will not survive a restart.");
        builder.Services.AddDataProtection().SetApplicationName("NimShare");
    }
}

// SameSite=Strict on the auth cookie combined with SameOrigin JSON APIs
// gives us CSRF protection for API endpoints: a cross-site attacker cannot
// send the cookie AND cross-origin form-encoded POSTs won't hit our
// [FromBody]-only actions. MVC form endpoints still use per-action
// [ValidateAntiForgeryToken] plus @Html.AntiForgeryToken() in the view.

// ── Localization (EFIGS + Dutch) ────────────────────────────────────────────
var supportedCultures = new[] { "en", "de", "fr", "it", "es", "nl" }
    .Select(c => new CultureInfo(c))
    .ToArray();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    // Falls back to English if the browser advertises a culture we don't ship.
    o.DefaultRequestCulture = new RequestCulture("en");
    o.SupportedCultures = supportedCultures;
    o.SupportedUICultures = supportedCultures;
    // Priority: 1) ?ui-culture=xx query, 2) cookie the user picked, 3) Accept-Language
    // header from the browser. QueryString + Cookie are already inserted at 0 and 1
    // by AddRequestLocalization; the built-in AcceptLanguageHeader provider handles
    // step 3 and stays the last resort before DefaultRequestCulture.
    o.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider());
    o.RequestCultureProviders.Insert(1, new CookieRequestCultureProvider());
    // v1.10.34 — public share/sign/upload-request landings ignore the cookie
    // and follow the visitor's browser language instead. See
    // PublicPathBrowserLanguageProvider for the rationale. Inserted at
    // position 0 so it wins over query+cookie IF the query didn't set one
    // — but it also honours ?ui-culture= itself, so a language-pill click
    // on the landing keeps working.
    o.RequestCultureProviders.Insert(0, new NimShare.Api.Middleware.PublicPathBrowserLanguageProvider());
});

// ── MVC / Razor Pages / API ────────────────────────────────────────────────
builder.Services
    .AddControllersWithViews(mvc =>
    {
        // Global scope guard for personal API tokens: unsafe HTTP verbs on
        // any endpoint require the token to have at least one write/manage/*
        // scope. Cookie / JWT / Entra sessions carry no scope claims and
        // pass through. This is the ONLY correct way to register a filter
        // globally — via MvcOptions.Filters, not raw DI.
        mvc.Filters.Add<NimShare.Api.Services.ApiTokenMethodGuard>();
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(o =>
    {
        o.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResources));
    })
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorPages()
    .AddViewLocalization()
    .AddMicrosoftIdentityUI();

// Rate limiting for public share/upload endpoints — brute-force defence.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // 30 hits per minute per IP for public share/upload landings.
    o.AddPolicy("public-share", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Only include real API endpoints; the Razor/MVC HTML pages have no
    // explicit HTTP verb attributes and would trip the Swagger generator.
    c.DocInclusionPredicate((_, api) =>
        (api.RelativePath ?? "").StartsWith("api/", StringComparison.OrdinalIgnoreCase));
    c.SwaggerDoc("v1", new() { Title = "NimShare API", Version = "v1" });
});

// ── App services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IDbMigrationService, DbMigrationService>();
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddSingleton<ITimeService, TimeService>();
builder.Services.AddScoped<ISlugService, SlugService>();
builder.Services.AddSingleton<IIpHashService, IpHashService>();
// v1.10.42 — GeoIp-Auflösung für Signatur-Audit + Link-Report. Default
// Null (kein externer Call, keine DSGVO-Frage). Wenn Marcus
// "NimShare:GeoIp:Provider" = "IpApiCo" in appsettings setzt, wird
// stattdessen ipapi.co (HTTPS, kein Key, 1000 req/day) verwendet.
{
    var geoProvider = builder.Configuration["NimShare:GeoIp:Provider"];
    if (string.Equals(geoProvider, "IpApiCo", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddSingleton<IGeoIpService, IpApiCoGeoIpService>();
    else
        builder.Services.AddSingleton<IGeoIpService, NullGeoIpService>();
}
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddScoped<ILinkAccessService, LinkAccessService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ILocalAuthService, LocalAuthService>();
builder.Services.AddScoped<IFileAccessService, FileAccessService>();
builder.Services.AddScoped<IFolderService, FolderService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IActivityLogger, ActivityLogger>();
builder.Services.AddScoped<IUserNotifier, UserNotifier>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<ITotpChallengeStore, TotpChallengeStore>();
builder.Services.AddScoped<ISignaturePdfService, SignaturePdfService>();
builder.Services.AddSingleton<IPdfSignatureService, PdfSignatureService>();
builder.Services.AddScoped<ISignatureFinalizerService, SignatureFinalizerService>();
builder.Services.AddHostedService<SignatureReminderService>();
builder.Services.AddSingleton<IWebhookDispatcher, WebhookDispatcher>();

// ApiTokenMethodGuard is applied globally via MvcOptions.Filters above (line ~164).
// It needs to be resolvable from DI as an ActionFilter.
builder.Services.AddScoped<NimShare.Api.Services.ApiTokenMethodGuard>();
builder.Services.AddHostedService<RecurringUploadReopenerService>();

// Session cookie backs the 2FA setup + login-challenge stashes.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(10);
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddHttpClient();
builder.Services.AddScoped<IEmailGatewayService, EmailGatewayService>();
builder.Services.AddScoped<IAiGatewayService, AiGatewayService>();
builder.Services.AddSingleton<IAiPostProcessor, AiPostProcessor>();
// v1.10.70: Office-Preview (DOCX/XLSX/PPTX → PDF via LibreOffice-headless).
// Scoped weil er den scoped IBlobStorageService braucht. Concurrency ist
// intern per statischer SemaphoreSlim(2) geregelt.
builder.Services.AddScoped<IOfficePreviewService, OfficePreviewService>();
// v1.10.82: App-Store-Blocker — Account-Löschung + UGC-Moderation
builder.Services.AddScoped<IUserDeletionService, UserDeletionService>();
builder.Services.AddScoped<IModerationService, ModerationService>();
// The old SmtpNotificationService is replaced by the gateway-backed adapter so
// existing callers (link download/upload notifications, "send by email" button)
// route through the persisted, per-tenant email configuration.
builder.Services.AddScoped<INotificationService, GatewayBackedNotificationService>();

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// Wire the extension-method holder so Razor views can call `.ToDisplay()`
// on any DateTimeOffset without threading ITimeService through every model.
// Catch anything — a broken TimeService must NEVER take the whole app
// down at startup; extension falls back to UTC-formatted output.
try
{
    NimShare.Api.Services.TimeDisplay.Register(
        app.Services.GetRequiredService<NimShare.Api.Services.ITimeService>());
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[STARTUP] TimeService registration failed: {ex.Message}. Timestamps will fall back to raw UTC.");
}

// Refuse to boot in Production with the default IP-hash salt — otherwise
// visitor IPs would be pseudonymised with a public constant, and anyone who
// dumps the DB could rainbow-table them back.
if (!app.Environment.IsDevelopment())
{
    var salt = builder.Configuration["IpHash:Salt"];
    if (string.IsNullOrEmpty(salt) || salt == "override-with-env-var-in-production" || salt == "change-me-in-production")
    {
        throw new InvalidOperationException(
            "IpHash:Salt is still the default. Set it via IpHash__Salt env var or Key Vault before running outside Development.");
    }
}

// ── Migrations + container bootstrap ───────────────────────────────────────
// Wrapped in try/catch so a failed migration doesn't kill Kestrel entirely
// (which produces Azure's opaque "site can't process the request now" page
// and leaves the operator blind). Instead we log to stderr — Azure Log Stream
// / App Service log will show the actual EF exception.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
    var isSqlServer = string.Equals(dbProvider, "SqlServer", StringComparison.OrdinalIgnoreCase);
    // Migrate with retry — on Azure Files, the SMB mount can be momentarily
    // unavailable at cold start, which used to abort the whole boot with
    // "unable to open database file". Six attempts on 2 s cadence ≈ 12 s of
    // grace, which is enough for the mount to appear.
    for (int attempt = 1; attempt <= 6; attempt++)
    {
        try
        {
            // Both providers now have a proper migration set — SqlServer's
            // lives in NimShare.Migrations.SqlServer, Sqlite's in
            // NimShare.Core/Migrations. MigrateAsync is idempotent and
            // handles schema evolution correctly on both sides.
            if (isSqlServer)
            {
                // Rescue path for admins who flipped to Azure SQL on
                // v1.8.0-v1.8.4 (which used EnsureCreatedAsync, no history
                // table). The tables exist but __EFMigrationsHistory is
                // empty — MigrateAsync would re-play InitialSqlServer and
                // die on "object already exists". If we detect that state,
                // baseline-stamp the history row so MigrateAsync sees "up
                // to date".
                await BaselineSqlServerIfNeededAsync(db, scope.ServiceProvider);
            }
            else
            {
                // Rescue path 1: catch-up any columns that never made it in
                // during the "MigrationsAssembly was wrong" era. Idempotent.
                await RepairSqliteMissingColumnsAsync(db, scope.ServiceProvider);
                // Rescue path 2: baseline-stamp __EFMigrationsHistory when
                // it's empty on an already-populated schema. Without this,
                // MigrateAsync now (with the corrected MigrationsAssembly)
                // sees every V17x/V18x as "pending" and crashes on the first
                // CREATE TABLE / ADD COLUMN whose target already exists.
                await BaselineSqliteIfNeededAsync(db, scope.ServiceProvider);
            }
            // v1.10.108: MUSS vor MigrateAsync laufen. DBs, die v1.10.106
            // sahen, haben Folders.IsPrivate bereits per Rescue-ALTER —
            // aber KEINEN V184-History-Eintrag (die Migration war damals
            // wegen fehlender Attribute unsichtbar). Ohne den Stamp würde
            // MigrateAsync V184 erneut anwenden → "duplicate column name"
            // → Migration-Loop bricht ab und JEDE künftige Migration
            // (V185+) bliebe für immer liegen.
            await EnsureFolderIsPrivateColumnAsync(db, isSqlServer);
            await db.Database.MigrateAsync();
            break;
        }
        catch (Microsoft.Data.Sqlite.SqliteException sx) when (attempt < 6)
        {
            Console.Error.WriteLine($"[STARTUP] Migration attempt {attempt}/6 failed with SQLite error: {sx.Message}. Retrying in 2 s…");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[STARTUP] Database migration failed: " + ex);
            var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Startup");
            logger?.LogCritical(ex, "Database migration failed — app will run against the current DB schema and may 500 on any query that touches unmigrated tables.");
            NimShare.Api.Controllers.StartupState.Errors.Add("Migration failure: " + ex.Message);
            break;
        }
    }

    // v1.10.45 — Column-Backfill AUßERHALB des Retry-Loops, damit er auch
    // dann läuft wenn MigrateAsync selber gebrochen ist (retry gescheitert).
    // In v1.10.44 stand er im try, wurde also bei MigrateAsync-Fehler
    // umgangen. Der Backfill ist idempotent, kann jederzeit laufen und
    // fixt die "no such column: s.City"-Fehler die trotz Deploy blieben.
    try
    {
        Console.Error.WriteLine("[STARTUP] Running EnsureForensicColumnsAsync…");
        await EnsureForensicColumnsAsync(db, isSqlServer);
        Console.Error.WriteLine("[STARTUP] EnsureForensicColumnsAsync done.");
        // v1.10.106: Rescue für Folders.IsPrivate. In v1.10.104 landete die
        // V184-Migration ohne [DbContext]/[Migration]-Attribute im Repo —
        // MigrateAsync hat sie beim Assembly-Scan uebersprungen, Column
        // fehlt in prod-DBs. Idempotent, laeuft ab jetzt bei jedem Start.
        Console.Error.WriteLine("[STARTUP] Running EnsureFolderIsPrivateColumnAsync…");
        await EnsureFolderIsPrivateColumnAsync(db, isSqlServer);
        Console.Error.WriteLine("[STARTUP] EnsureFolderIsPrivateColumnAsync done.");
        // v1.10.121: Rescue für die LinkEntries-Tabelle (Linksammlung, V185).
        // Auf DBs, bei denen der MigrateAsync-Loop wegen eines V184-Replays
        // („duplicate column IsPrivate") abbrach, wurde V185 nie angewandt →
        // „no such table: LinkEntries"-500 in Web + iOS. Idempotentes
        // CREATE TABLE IF NOT EXISTS + History-Stamp, läuft ab jetzt bei
        // jedem Start und ausserhalb des Retry-Loops.
        Console.Error.WriteLine("[STARTUP] Running EnsureLinkEntriesTableAsync…");
        await EnsureLinkEntriesTableAsync(db, isSqlServer);
        Console.Error.WriteLine("[STARTUP] EnsureLinkEntriesTableAsync done.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] EnsureForensicColumnsAsync threw: " + ex);
    }

    // v1.10.48: einmalige Aufräum-Pass für alte Duplikate von "Scope-Root"-
    // Ordnern (ParentFolderId==null) pro (Scope, OwnerUserId, OwnerGroupId).
    // v1.10.37/38 haben Anzeige+Navigation entkoppelt, aber die DB behält die
    // Dubletten. Diese Funktion re-parented alle nicht-offiziellen Roots
    // unter den offiziellen (ältesten) Root. Idempotent — bei sauberen DBs
    // findet sie keine Kandidaten.
    try
    {
        Console.Error.WriteLine("[STARTUP] Running RepairDuplicateFolderRootsAsync…");
        await RepairDuplicateFolderRootsAsync(db);
        Console.Error.WriteLine("[STARTUP] RepairDuplicateFolderRootsAsync done.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] RepairDuplicateFolderRootsAsync threw: " + ex);
    }

    // WAL journal mode lets readers proceed while a writer holds the lock,
    // which stops "upload complete" from freezing the /browse pages. 15 s
    // busy_timeout absorbs brief lock contention (each connection retries
    // automatically before surfacing SQLITE_BUSY). Best-effort — never fatal.
    if (!isSqlServer)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=15000; PRAGMA synchronous=NORMAL;");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[STARTUP] Could not set WAL / busy_timeout PRAGMAs: " + ex.Message);
        }
    }
}

// Ensuring the blob container exists can take 30+ s of retry when the storage
// endpoint isn't reachable (dev without Azurite, prod during a brief outage).
// Fire-and-forget so Kestrel starts serving immediately.
// v1.10.46 — Rescue-Pass für gestrandete Signatur-Vorgänge.
// Marcus's Fall: er hat einen Vorgang unterschrieben, aber der Finalizer
// lief nicht durch (Exception, Timeout, oder Row-Refresh-Miss). Der
// SignatureRequest steht auf Status=Sent obwohl alle Participants schon
// Signed sind → er landet in "In Bearbeitung" statt "Abgeschlossen",
// keine Complete-Mail geht raus. Beim Startup scannen wir alle Sent-
// Vorgänge und retry-triggern den Finalizer für die, wo tatsächlich
// alles fertig ist. Fire-and-forget, damit Kestrel nicht wartet.
_ = Task.Run(async () =>
{
    // Kleiner Delay, damit die App zuerst richtig hochkommt bevor wir
    // den Background-Finalizer belasten (der Blob-SAS + PDF-Merge macht).
    await Task.Delay(TimeSpan.FromSeconds(10));
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
    var finalizer = scope.ServiceProvider.GetRequiredService<ISignatureFinalizerService>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var stuck = await db.SignatureRequests
            .Where(r => r.Status == SignatureRequestStatus.Sent)
            .Include(r => r.Participants)
            .ToListAsync();
        var candidates = stuck.Where(r => r.Participants.All(p =>
            (p.Role == SignatureParticipantRole.Signer && p.Status == SignatureParticipantStatus.Signed)
            || (p.Role == SignatureParticipantRole.Viewer
                && (p.Status == SignatureParticipantStatus.Viewed || p.Status == SignatureParticipantStatus.Signed))
        )).ToList();
        if (candidates.Count > 0)
        {
            log.LogInformation("[STARTUP] Retrying finalize for {Count} stuck signature request(s).", candidates.Count);
            foreach (var r in candidates)
            {
                try { await finalizer.TryFinalizeAsync(r.Id); }
                catch (Exception ex) { log.LogWarning(ex, "Retry-finalize threw for {Id}", r.Id); }
            }
        }
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "[STARTUP] Retry-finalize pass failed.");
    }
});

_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try { await blobs.EnsureContainerAsync(); }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not ensure blob container — is the storage connection configured?");
    }
});

// ── Pipeline ───────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/error");
    // Sits just inside the exception handler so transient Sqlite failures get
    // one automatic retry before the 503 page ever appears. Sqlite-only —
    // SqlServer has its own EnableRetryOnFailure execution strategy.
    if (!string.Equals(dbProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        app.UseMiddleware<SqliteRecoveryMiddleware>();
    app.UseHsts();
}

// Behind Azure App Service the app is on plain HTTP inside the container; the
// front-door proxy terminates TLS. Trust the forwarded headers so Request.Scheme
// reflects the outside "https" and generated redirect URIs match reality.
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

if (!app.Environment.IsDevelopment())
{
    // Only redirect to HTTPS in prod; in dev we run plain HTTP on localhost.
    app.UseHttpsRedirection();
}
app.UseStaticFiles();

app.UseRequestLocalization();

app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<CustomDomainMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

// Make "is Entra configured?" available to any Razor view.
app.Use((ctx, next) =>
{
    ctx.Items["EntraConfigured"] = entraConfigured;
    return next(ctx);
});

app.MapControllers();
app.MapRazorPages();

app.Run();

/// <summary>
/// Rescue for v1.8.0-v1.8.4 upgraders: those versions provisioned Azure SQL
/// with EnsureCreatedAsync, so every table exists but __EFMigrationsHistory
/// is empty. v1.8.5 introduced real migrations, and MigrateAsync on that
/// state re-plays InitialSqlServer → dies on "object already exists" and
/// bricks the upgrade. Detect the mismatch (Users table exists AND history
/// row is missing) and INSERT the InitialSqlServer row so MigrateAsync sees
/// "up to date". Fresh installs skip the branch because Users doesn't exist
/// yet. Safe to leave in indefinitely — the fingerprint check is cheap.
/// </summary>
// v1.10.44 — idempotenter Column-Backfill für die v1.10.42 forensischen
// Felder. Der eigentliche Weg wäre die V182_ForensicFields Migration —
// die aber wegen der partial-class-ohne-Designer-Auslassung von EF Core
// nicht discovered wurde. Diese Funktion prüft schema-getrieben ob die
// Spalten existieren und fügt sie ansonsten via ALTER TABLE ADD COLUMN
// nach. Reihenfolge:
//   SignatureAudits: Country, City, DeviceType, Timezone
//   ShareLinkAccesses: City, DeviceType, Timezone (CountryCode war schon da)
// Bei einer bereits sauber migrierten DB macht die Funktion nichts.
static async Task EnsureForensicColumnsAsync(NimShareDbContext db, bool isSqlServer)
{
    // Spezifikation: (Tabelle, Spalte, SQLite-Typ, SqlServer-Typ)
    var wanted = new (string Table, string Column, string SqliteType, string SqlServerType)[]
    {
        ("SignatureAudits", "Country", "TEXT", "nvarchar(2)"),
        ("SignatureAudits", "City", "TEXT", "nvarchar(80)"),
        ("SignatureAudits", "DeviceType", "TEXT", "nvarchar(20)"),
        ("SignatureAudits", "Timezone", "TEXT", "nvarchar(60)"),
        ("ShareLinkAccesses", "City", "TEXT", "nvarchar(80)"),
        ("ShareLinkAccesses", "DeviceType", "TEXT", "nvarchar(20)"),
        ("ShareLinkAccesses", "Timezone", "TEXT", "nvarchar(60)"),
        // v1.10.50: Per-User Timezone-Preference
        ("Users", "PreferredTimezone", "TEXT", "nvarchar(60)"),
        // v1.10.77: optionale Klartext-IP für Signatur-Forensik (DSGVO
        // Art. 6(1)(f), Admin-toggle Signatures:StoreFullIp=true).
        ("SignatureParticipants", "IpAddress", "TEXT", "nvarchar(45)"),
        ("SignatureAudits", "IpAddress", "TEXT", "nvarchar(45)"),
        // v1.10.117: optionale Status-Seiten-URL für die KI-Begrüssung.
        ("AiGateways", "StatusPageUrl", "TEXT", "nvarchar(1000)"),
        // v1.10.118: Produkt-Filter für die Status-Begrüssung.
        ("AiGateways", "StatusPageProducts", "TEXT", "nvarchar(1000)"),
    };
    foreach (var w in wanted)
    {
        try
        {
            bool exists;
            if (isSqlServer)
            {
                var connStr = db.Database.GetConnectionString();
                using var cn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
                await cn.OpenAsync();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"SELECT COL_LENGTH('{w.Table}', '{w.Column}')";
                var res = await cmd.ExecuteScalarAsync();
                exists = res is not null && res != DBNull.Value;
                if (!exists)
                {
                    using var alter = cn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE [{w.Table}] ADD [{w.Column}] {w.SqlServerType} NULL";
                    await alter.ExecuteNonQueryAsync();
                    Console.Error.WriteLine($"[STARTUP] Added missing column {w.Table}.{w.Column} (SqlServer).");
                }
            }
            else
            {
                var connStr = db.Database.GetConnectionString();
                using var cn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
                await cn.OpenAsync();
                using var cmd = cn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{w.Table}\")";
                exists = false;
                using (var rdr = await cmd.ExecuteReaderAsync())
                {
                    while (await rdr.ReadAsync())
                    {
                        var name = rdr.GetString(1);
                        if (string.Equals(name, w.Column, StringComparison.OrdinalIgnoreCase))
                        { exists = true; break; }
                    }
                }
                if (!exists)
                {
                    using var alter = cn.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"{w.Table}\" ADD COLUMN \"{w.Column}\" {w.SqliteType} NULL";
                    await alter.ExecuteNonQueryAsync();
                    Console.Error.WriteLine($"[STARTUP] Added missing column {w.Table}.{w.Column} (SQLite).");
                }
            }
        }
        catch (Exception ex)
        {
            // Nicht fatal — wenn die Spalte trotzdem nicht angelegt werden kann
            // (Tabelle existiert nicht in der DB, unerwartete DB-Version) soll
            // der App-Start weiterlaufen. Marcus sieht die Fehler im Log.
            Console.Error.WriteLine($"[STARTUP] EnsureForensicColumns for {w.Table}.{w.Column} failed: {ex.Message}");
        }
    }
}

// v1.10.106: Standalone-Rescue fuer Folders.IsPrivate. Die V184-Migration
// hatte in v1.10.104 die [DbContext]/[Migration]-Attribute vergessen, EF
// hat sie beim Scan uebergangen — Column existiert weder in Sqlite noch
// in SqlServer, jede Query auf Folder crasht mit "no such column
// f.IsPrivate". Idempotent: legt die Spalte an, wenn sie fehlt, sonst
// no-op. Anders als EnsureForensicColumnsAsync ist IsPrivate NOT NULL
// DEFAULT 0 (bool), deshalb eigene Routine mit eigenem DDL-Suffix.
static async Task EnsureFolderIsPrivateColumnAsync(NimShareDbContext db, bool isSqlServer)
{
    // v1.10.108: Läuft VOR MigrateAsync. Zwei Aufgaben:
    //  (1) Column nachlegen, falls sie fehlt (DBs, die v1.10.104/105 ohne
    //      funktionierende V184 sahen).
    //  (2) V184 in __EFMigrationsHistory stempeln, sobald die Column da
    //      ist — sonst wendet MigrateAsync die (seit v1.10.106
    //      attributierte) V184 erneut an und stirbt an "duplicate column",
    //      was ALLE künftigen Migrationen blockieren würde.
    // Auf frischen DBs existiert die Folders-Tabelle noch nicht: beide
    // Schritte schlagen fehl bzw. greifen nicht, MigrateAsync legt danach
    // alles regulär inkl. V184 an.
    const string V184 = "20260721145510_V184_FolderIsPrivate";
    try
    {
        if (isSqlServer)
        {
            var connStr = db.Database.GetConnectionString();
            using var cn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            await cn.OpenAsync();
            using var check = cn.CreateCommand();
            check.CommandText = "SELECT COL_LENGTH('Folders', 'IsPrivate')";
            var res = await check.ExecuteScalarAsync();
            var exists = res is not null && res != DBNull.Value;
            if (!exists)
            {
                using var alter = cn.CreateCommand();
                alter.CommandText = "ALTER TABLE [Folders] ADD [IsPrivate] bit NOT NULL DEFAULT (0)";
                await alter.ExecuteNonQueryAsync();
                exists = true;
                Console.Error.WriteLine("[STARTUP] Added Folders.IsPrivate (SqlServer).");
            }
            if (exists)
            {
                using var stamp = cn.CreateCommand();
                stamp.CommandText =
                    "IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__EFMigrationsHistory') " +
                    "AND NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = @mid) " +
                    "INSERT INTO [__EFMigrationsHistory] ([MigrationId],[ProductVersion]) VALUES (@mid, '8.0.10')";
                stamp.Parameters.AddWithValue("@mid", V184);
                var stamped = await stamp.ExecuteNonQueryAsync();
                if (stamped > 0) Console.Error.WriteLine("[STARTUP] Baseline-stamped V184 (SqlServer).");
            }
        }
        else
        {
            var connStr = db.Database.GetConnectionString();
            using var cn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            await cn.OpenAsync();
            using var check = cn.CreateCommand();
            check.CommandText = "PRAGMA table_info(\"Folders\")";
            var exists = false;
            using (var rdr = await check.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                {
                    if (string.Equals(rdr.GetString(1), "IsPrivate", StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
                }
            }
            if (!exists)
            {
                // Auf frischen DBs gibt es die Folders-Tabelle noch nicht —
                // dann wirft ALTER, der catch unten schluckt, MigrateAsync
                // übernimmt. Auf Bestands-DBs legt das die Column nach.
                using var alter = cn.CreateCommand();
                alter.CommandText = "ALTER TABLE \"Folders\" ADD COLUMN \"IsPrivate\" INTEGER NOT NULL DEFAULT 0";
                await alter.ExecuteNonQueryAsync();
                exists = true;
                Console.Error.WriteLine("[STARTUP] Added Folders.IsPrivate (SQLite).");
            }
            if (exists)
            {
                using var stamp = cn.CreateCommand();
                stamp.CommandText =
                    "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") " +
                    "SELECT $mid, '8.0.10' " +
                    "WHERE EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory') " +
                    "AND NOT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $mid)";
                stamp.Parameters.AddWithValue("$mid", V184);
                var stamped = await stamp.ExecuteNonQueryAsync();
                if (stamped > 0) Console.Error.WriteLine("[STARTUP] Baseline-stamped V184 (SQLite).");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] EnsureFolderIsPrivateColumnAsync failed: " + ex.Message);
    }
}

// v1.10.121: Legt die LinkEntries-Tabelle (Linksammlung, Migration V185) an,
// falls sie fehlt, und stempelt V185 in __EFMigrationsHistory. Nötig, weil auf
// Bestands-DBs der MigrateAsync-Loop an einem V184-Replay hängen bleiben und
// V185 dadurch nie ausführen konnte → „no such table: LinkEntries". CREATE
// TABLE IF NOT EXISTS ist idempotent; auf frischen DBs, die V185 regulär via
// MigrateAsync bekommen, ist die Tabelle bereits da und der Aufruf ein No-op.
static async Task EnsureLinkEntriesTableAsync(NimShareDbContext db, bool isSqlServer)
{
    const string V185 = "20260722120000_V185_LinkEntries";
    try
    {
        var connStr = db.Database.GetConnectionString();
        if (isSqlServer)
        {
            using var cn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            await cn.OpenAsync();
            using (var create = cn.CreateCommand())
            {
                create.CommandText =
                    "IF OBJECT_ID(N'[LinkEntries]', N'U') IS NULL " +
                    "CREATE TABLE [LinkEntries] (" +
                    "[Id] uniqueidentifier NOT NULL CONSTRAINT [PK_LinkEntries] PRIMARY KEY, " +
                    "[Title] nvarchar(200) NOT NULL, " +
                    "[Url] nvarchar(2000) NOT NULL, " +
                    "[Description] nvarchar(500) NULL, " +
                    "[Emoji] nvarchar(8) NULL, " +
                    "[SortOrder] int NOT NULL, " +
                    "[CreatedByUserId] uniqueidentifier NOT NULL, " +
                    "[CreatedAt] bigint NOT NULL, " +
                    "[UpdatedAt] bigint NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }
            using (var idx = cn.CreateCommand())
            {
                idx.CommandText =
                    "IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LinkEntries') " +
                    "AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_LinkEntries_SortOrder' " +
                    "AND object_id = OBJECT_ID('LinkEntries')) " +
                    "CREATE INDEX [IX_LinkEntries_SortOrder] ON [LinkEntries] ([SortOrder])";
                await idx.ExecuteNonQueryAsync();
            }
            using (var stamp = cn.CreateCommand())
            {
                stamp.CommandText =
                    "IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '__EFMigrationsHistory') " +
                    "AND NOT EXISTS (SELECT 1 FROM [__EFMigrationsHistory] WHERE [MigrationId] = @mid) " +
                    "INSERT INTO [__EFMigrationsHistory] ([MigrationId],[ProductVersion]) VALUES (@mid, '8.0.10')";
                stamp.Parameters.AddWithValue("@mid", V185);
                var stamped = await stamp.ExecuteNonQueryAsync();
                if (stamped > 0) Console.Error.WriteLine("[STARTUP] Baseline-stamped V185 (SqlServer).");
            }
        }
        else
        {
            using var cn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            await cn.OpenAsync();
            using (var create = cn.CreateCommand())
            {
                create.CommandText =
                    "CREATE TABLE IF NOT EXISTS \"LinkEntries\" (" +
                    "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_LinkEntries\" PRIMARY KEY, " +
                    "\"Title\" TEXT NOT NULL, " +
                    "\"Url\" TEXT NOT NULL, " +
                    "\"Description\" TEXT NULL, " +
                    "\"Emoji\" TEXT NULL, " +
                    "\"SortOrder\" INTEGER NOT NULL, " +
                    "\"CreatedByUserId\" TEXT NOT NULL, " +
                    "\"CreatedAt\" INTEGER NOT NULL, " +
                    "\"UpdatedAt\" INTEGER NOT NULL)";
                await create.ExecuteNonQueryAsync();
            }
            using (var idx = cn.CreateCommand())
            {
                idx.CommandText = "CREATE INDEX IF NOT EXISTS \"IX_LinkEntries_SortOrder\" ON \"LinkEntries\" (\"SortOrder\")";
                await idx.ExecuteNonQueryAsync();
            }
            using (var stamp = cn.CreateCommand())
            {
                stamp.CommandText =
                    "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") " +
                    "SELECT $mid, '8.0.10' " +
                    "WHERE EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory') " +
                    "AND NOT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $mid)";
                stamp.Parameters.AddWithValue("$mid", V185);
                var stamped = await stamp.ExecuteNonQueryAsync();
                if (stamped > 0) Console.Error.WriteLine("[STARTUP] Baseline-stamped V185 (SQLite).");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] EnsureLinkEntriesTableAsync failed: " + ex.Message);
    }
}

// v1.10.48: Sortiert Legacy-Duplikate von Scope-Root-Ordnern in einen
// einzigen offiziellen Root pro (Scope, OwnerUserId, OwnerGroupId). Der
// älteste Ordner (per CreatedAt / dann Id) gewinnt. Alle jüngeren Roots
// werden zu Kindern des offiziellen Roots. Speichert nur wenn was
// verschoben wurde.
static async Task RepairDuplicateFolderRootsAsync(NimShareDbContext db)
{
    var allRoots = await db.Folders
        .Where(f => f.ParentFolderId == null)
        .ToListAsync();
    // Gruppierung nach dem "logischen Owner": Scope + OwnerUserId + OwnerGroupId.
    // Personal/Group haben Owner, Public hat beide null.
    var groups = allRoots.GroupBy(f => (f.Scope, f.OwnerUserId, f.OwnerGroupId)).ToList();
    int moved = 0;
    foreach (var g in groups)
    {
        if (g.Count() <= 1) continue;
        var ordered = g.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList();
        var official = ordered[0];
        for (int i = 1; i < ordered.Count; i++)
        {
            var dup = ordered[i];
            dup.ParentFolderId = official.Id;
            moved++;
        }
    }
    if (moved > 0)
    {
        await db.SaveChangesAsync();
        Console.Error.WriteLine($"[STARTUP] Re-parented {moved} legacy duplicate root folder(s) under their official scope-root.");
    }
}

static async Task BaselineSqlServerIfNeededAsync(NimShareDbContext db, IServiceProvider services)
{
    try
    {
        // Does the schema look already populated?
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        bool usersExists = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM sys.tables WHERE name = 'Users'";
            usersExists = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
        }
        if (!usersExists) return; // fresh DB — regular MigrateAsync will apply everything
        // Is __EFMigrationsHistory present?
        bool historyExists;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM sys.tables WHERE name = '__EFMigrationsHistory'";
            historyExists = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
        }
        if (!historyExists)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE [__EFMigrationsHistory] (
                [MigrationId] nvarchar(150) NOT NULL,
                [ProductVersion] nvarchar(32) NOT NULL,
                CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
            )";
            await cmd.ExecuteNonQueryAsync();
        }
        // Already stamped? Nothing to do.
        long stamped;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM [__EFMigrationsHistory]";
            stamped = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        }
        if (stamped > 0) return;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (@id, @v)";
            // v1.10.108: Die ID muss EXAKT der echten Migration entsprechen
            // (20260720042650, nicht 20260719180245 — Tippfehler aus v1.8.5).
            // Mit der falschen ID sah MigrateAsync Initial weiter als pending
            // und crashte auf "object already exists".
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = "20260720042650_InitialSqlServer"; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@v"; p2.Value = "8.0.10"; cmd.Parameters.Add(p2);
            await cmd.ExecuteNonQueryAsync();
        }
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Startup");
        logger?.LogWarning("SqlServer __EFMigrationsHistory was empty on an already-populated schema. Baseline-stamped InitialSqlServer so MigrateAsync can proceed. This is the v1.8.0-1.8.4 → v1.8.5 upgrade path.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] Baseline-stamp for SqlServer failed: " + ex.Message);
        // Fall through — MigrateAsync will either succeed on a fresh DB or
        // fail loudly, which is the pre-fix behaviour.
    }
}

/// <summary>
/// Idempotently re-adds columns that v1.9.1's V179 dropped on the Sqlite
/// side. Runs BEFORE MigrateAsync so the migrator can proceed against a
/// well-formed schema. Each expected column is probed via
/// <c>PRAGMA table_info</c>; only missing ones get an ALTER TABLE ADD
/// COLUMN. Runs on every startup — cheap, safe, and self-heals any Sqlite
/// DB corrupted by the v1.9.1 deploy.
/// </summary>
static async Task RepairSqliteMissingColumnsAsync(NimShareDbContext db, IServiceProvider services)
{
    // First: ensure any post-baseline tables exist. BaselineSqliteIfNeededAsync
    // stamps every known migration as applied, so MigrateAsync would skip
    // CREATE TABLE ops for anything the baseline covered — this catches
    // instances that jumped past the release that introduced a new table.
    // Add here every table introduced by a migration that might have been
    // missed on old-DB installs (pre-v1.10.1 wrong-MigrationsAssembly window).
    var expectedTables = new (string Name, string CreateSql)[]
    {
        // V178 — SigningCertificates (signature workflow, cert management).
        // On instances that installed before v1.7.x AND never ran the
        // migration due to the MigrationsAssembly bug, this table simply
        // didn't exist — cert-generation crashed with "no such table".
        ("SigningCertificates", @"CREATE TABLE IF NOT EXISTS SigningCertificates (
            Id TEXT NOT NULL PRIMARY KEY,
            OwnerUserId TEXT NOT NULL,
            Name TEXT NOT NULL,
            SubjectCommonName TEXT NOT NULL,
            Issuer TEXT NOT NULL,
            NotBefore INTEGER NOT NULL,
            NotAfter INTEGER NOT NULL,
            Thumbprint TEXT NOT NULL,
            IsSelfIssued INTEGER NOT NULL,
            PfxDataEncrypted BLOB NOT NULL,
            IsDefault INTEGER NOT NULL,
            CreatedAt INTEGER NOT NULL,
            LastUsedAt INTEGER NULL,
            UseCount INTEGER NOT NULL,
            FOREIGN KEY (OwnerUserId) REFERENCES Users(Id) ON DELETE CASCADE
        )"),
        // V181 — FilePins. Idempotent CREATE TABLE IF NOT EXISTS matches the
        // migration column-for-column (Sqlite types). Indexes below.
        ("FilePins", @"CREATE TABLE IF NOT EXISTS FilePins (
            Id TEXT NOT NULL PRIMARY KEY,
            UserId TEXT NOT NULL,
            FileId TEXT NOT NULL,
            Note TEXT NULL,
            PinnedAt INTEGER NOT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (FileId) REFERENCES Files(Id) ON DELETE CASCADE
        )"),
        // v1.10.82: UGC-Moderation — analog zum V181-Muster: idempotente
        // CREATE-Statements matchen V183 exakt, damit baseline-stamped
        // Instanzen ohne Explicit-Migrate ebenfalls die Tabellen bekommen.
        ("BlockedUsers", @"CREATE TABLE IF NOT EXISTS BlockedUsers (
            Id TEXT NOT NULL PRIMARY KEY,
            UserId TEXT NOT NULL,
            BlockedUserId TEXT NOT NULL,
            CreatedAt INTEGER NOT NULL,
            Reason TEXT NULL,
            FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (BlockedUserId) REFERENCES Users(Id) ON DELETE CASCADE
        )"),
        ("ContentReports", @"CREATE TABLE IF NOT EXISTS ContentReports (
            Id TEXT NOT NULL PRIMARY KEY,
            ReporterUserId TEXT NOT NULL,
            SubjectKind INTEGER NOT NULL,
            SubjectId TEXT NOT NULL,
            SubjectLabel TEXT NULL,
            SubjectOwnerUserId TEXT NULL,
            Reason INTEGER NOT NULL,
            Note TEXT NULL,
            CreatedAt INTEGER NOT NULL,
            Status INTEGER NOT NULL,
            ResolvedAt INTEGER NULL,
            ResolvedByUserId TEXT NULL,
            Resolution INTEGER NULL,
            ResolutionNote TEXT NULL,
            FOREIGN KEY (ReporterUserId) REFERENCES Users(Id) ON DELETE CASCADE,
            FOREIGN KEY (ResolvedByUserId) REFERENCES Users(Id) ON DELETE SET NULL
        )"),
    };
    try
    {
        var conn0 = db.Database.GetDbConnection();
        await conn0.OpenAsync();
        foreach (var (name, sql) in expectedTables)
        {
            await using var cmd = conn0.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        // Indexes are separate statements so a missing table above doesn't
        // dropkick the whole batch.
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_FilePins_UserId_FileId ON FilePins(UserId, FileId)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_FilePins_UserId_PinnedAt ON FilePins(UserId, PinnedAt)";
            await cmd.ExecuteNonQueryAsync();
        }
        // Cascade-delete perf: matches migration V181 which creates this
        // non-unique FK index. Missing it caused a full FilePins scan for
        // every StorageFile delete.
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_FilePins_FileId ON FilePins(FileId)";
            await cmd.ExecuteNonQueryAsync();
        }
        // SigningCertificates indexes (v1.10.14 fix): match V178 exactly.
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_SigningCertificates_OwnerUserId_NotAfter ON SigningCertificates(OwnerUserId, NotAfter)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_SigningCertificates_OwnerUserId_Thumbprint ON SigningCertificates(OwnerUserId, Thumbprint)";
            await cmd.ExecuteNonQueryAsync();
        }
        // v1.10.82: UGC-Moderation indexes (matches V183)
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_BlockedUsers_UserId_BlockedUserId ON BlockedUsers(UserId, BlockedUserId)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_BlockedUsers_BlockedUserId ON BlockedUsers(BlockedUserId)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ContentReports_Status_CreatedAt ON ContentReports(Status, CreatedAt)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ContentReports_SubjectKind_SubjectId ON ContentReports(SubjectKind, SubjectId)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ContentReports_ReporterUserId ON ContentReports(ReporterUserId)";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn0.CreateCommand())
        {
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ContentReports_ResolvedByUserId ON ContentReports(ResolvedByUserId)";
            await cmd.ExecuteNonQueryAsync();
        }
        // If Repair just brought FilePins / SigningCertificates into existence
        // on an instance that was baseline-stamped BEFORE these migrations
        // (history has rows but no V178/V181 row), MigrateAsync would try to
        // re-run their CREATE TABLE and crash on "table already exists".
        // Stamp them as applied so history and schema agree.
        var stampNames = new[]
        {
            "20260719145800_V178_SigningCertificates",
            "20260720055248_V181_FilePins",
        };
        foreach (var mid in stampNames)
        {
            await using var cmd = conn0.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                                SELECT $mid, '8.0.0'
                                WHERE EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory')
                                  AND EXISTS (SELECT 1 FROM __EFMigrationsHistory);";
            var p = cmd.CreateParameter(); p.ParameterName = "$mid"; p.Value = mid; cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync();
        }
        // Diagnostic: list any table from the EF model that's STILL missing
        // after repair. If Marcus sees something else 500 with "no such
        // table", this log tells us exactly what to add here next.
        try
        {
            var expectedFromModel = db.Model.GetEntityTypes()
                .Select(e => e.GetTableName())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn0.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) existing.Add(rd.GetString(0));
            }
            var missing = expectedFromModel.Where(n => n is not null && !existing.Contains(n!)).ToList();
            if (missing.Count > 0)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger("Startup")
                    ?.LogWarning("Sqlite schema drift: tables in EF model but missing from DB: {Missing}. Add to RepairSqliteMissingColumnsAsync.expectedTables.",
                        string.Join(", ", missing));
            }
        }
        catch { /* diagnostic only */ }
    }
    catch (Exception ex) { Console.Error.WriteLine("[STARTUP] Table repair failed: " + ex.Message); }

    // (table, column, type-and-constraints) tuples for anything V179 might
    // have dropped. Keep this list narrow — repairs are a last-resort
    // safety net, not a substitute for real migrations.
    var expected = new (string Table, string Column, string DdlType)[]
    {
        ("Folders", "Color", "TEXT"),
        ("Folders", "Emoji", "TEXT"),
        // V180 — never applied on instances that ran with the wrong
        // MigrationsAssembly. NOT NULL DEFAULT 0 matches the migration.
        ("Users", "ShowAvatarOnLandings", "INTEGER NOT NULL DEFAULT 0"),
        // V181 — belt-and-braces; MigrateAsync now handles new migrations
        // correctly, but this catches instances that upgrade past a broken
        // window. Note: adding a column, NOT the whole FilePins table (that
        // MigrateAsync WILL create fresh since it's un-stamped in baseline).
    };
    try
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        foreach (var (table, column, ddlType) in expected)
        {
            bool tableExists;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$t";
                var p = cmd.CreateParameter(); p.ParameterName = "$t"; p.Value = table; cmd.Parameters.Add(p);
                tableExists = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
            }
            if (!tableExists) continue;
            bool columnExists;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(1) FROM pragma_table_info('{table}') WHERE name=$c";
                var p = cmd.CreateParameter(); p.ParameterName = "$c"; p.Value = column; cmd.Parameters.Add(p);
                columnExists = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
            }
            if (columnExists) continue;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {ddlType}";
                await cmd.ExecuteNonQueryAsync();
            }
            services.GetService<ILoggerFactory>()?.CreateLogger("Startup")
                ?.LogWarning("Restored missing Sqlite column {Table}.{Column} (dropped by v1.9.1 V179 bug).",
                    table, column);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] Sqlite column repair failed: " + ex.Message);
        // Non-fatal — MigrateAsync will surface any real damage.
    }
}

/// <summary>
/// Sqlite counterpart to the SqlServer baseline stamper. On any instance
/// where the Users table exists but <c>__EFMigrationsHistory</c> is empty
/// (the case for every deployed DB from v1.7 through v1.10.0, because
/// MigrationsAssembly pointed at NimShare.Api which contained ZERO Migration
/// subclasses), stamp every migration currently known to the model as
/// applied. Then MigrateAsync becomes a no-op for the current release and
/// only future migrations added after this point actually run.
///
/// Combined with RepairSqliteMissingColumnsAsync (which ensures the schema
/// really matches), this is safe: the DB and history agree.
/// </summary>
static async Task BaselineSqliteIfNeededAsync(NimShareDbContext db, IServiceProvider services)
{
    try
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        bool usersExists;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='Users'";
            usersExists = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
        }
        if (!usersExists) return; // fresh DB — MigrateAsync will create everything cleanly
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                MigrationId TEXT NOT NULL PRIMARY KEY,
                ProductVersion TEXT NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
        }
        long historyRows;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM __EFMigrationsHistory";
            historyRows = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0);
        }
        if (historyRows > 0) return; // already stamped or partly migrated — leave alone
        var toStamp = db.Database.GetMigrations().ToList();
        foreach (var m in toStamp)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, $v)";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "$id"; p1.Value = m; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "$v"; p2.Value = "8.0.10"; cmd.Parameters.Add(p2);
            await cmd.ExecuteNonQueryAsync();
        }
        services.GetService<ILoggerFactory>()?.CreateLogger("Startup")
            ?.LogWarning("Baseline-stamped {Count} Sqlite migrations. The MigrationsAssembly config used to point at the wrong assembly, so nothing was ever recorded; RepairSqliteMissingColumnsAsync already brought the schema in line with the model.",
                toStamp.Count);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("[STARTUP] Sqlite baseline-stamp failed: " + ex.Message);
    }
}

public partial class Program { }
