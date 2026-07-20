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
        o.UseSqlite(connString, b =>
        {
            b.MigrationsAssembly("NimShare.Api");
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
// every existing Webhook.SecretEncrypted and SignatureParticipant token stash
// becomes undecryptable when Azure App Service recycles the instance.
// Persist to the shared /home path (backed by Azure Files in production).
{
    var keysPath = builder.Configuration["DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(keysPath))
    {
        keysPath = Path.Combine(
            builder.Environment.ContentRootPath,
            "..", "..", "data", "dp-keys");
    }
    try { Directory.CreateDirectory(keysPath); } catch { }
    builder.Services.AddDataProtection()
        .SetApplicationName("NimShare")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
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
builder.Services.AddScoped<ISlugService, SlugService>();
builder.Services.AddSingleton<IIpHashService, IpHashService>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddScoped<ILinkAccessService, LinkAccessService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ILocalAuthService, LocalAuthService>();
builder.Services.AddScoped<IFileAccessService, FileAccessService>();
builder.Services.AddScoped<IFolderService, FolderService>();
builder.Services.AddScoped<IActivityLogger, ActivityLogger>();
builder.Services.AddScoped<IUserNotifier, UserNotifier>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<ITotpChallengeStore, TotpChallengeStore>();
builder.Services.AddScoped<ISignaturePdfService, SignaturePdfService>();
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
// The old SmtpNotificationService is replaced by the gateway-backed adapter so
// existing callers (link download/upload notifications, "send by email" button)
// route through the persisted, per-tenant email configuration.
builder.Services.AddScoped<INotificationService, GatewayBackedNotificationService>();

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

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
                // Rescue path for the v1.9.1-V179 mess: on Sqlite, V179
                // (a scaffolded-with-wrong-factory garbage migration)
                // triggered full-table rebuilds and dropped the
                // Folder.Color / Folder.Emoji columns on deployed
                // instances. Re-add them idempotently before MigrateAsync
                // so the Ablage view starts working again.
                await RepairSqliteMissingColumnsAsync(db, scope.ServiceProvider);
            }
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
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@id"; p1.Value = "20260719180245_InitialSqlServer"; cmd.Parameters.Add(p1);
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
    // (table, column, type-and-constraints) tuples for anything V179 might
    // have dropped. Keep this list narrow — repairs are a last-resort
    // safety net, not a substitute for real migrations.
    var expected = new (string Table, string Column, string DdlType)[]
    {
        ("Folders", "Color", "TEXT"),
        ("Folders", "Emoji", "TEXT"),
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

public partial class Program { }
