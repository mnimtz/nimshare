using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connString = builder.Configuration.GetConnectionString("Default")
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
        o.UseSqlServer(connString, b => b.MigrationsAssembly("NimShare.Api"));
    else
        o.UseSqlite(connString, b => b.MigrationsAssembly("NimShare.Api"));
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

builder.Services.AddAuthorization(options =>
{
    // ApiUser accepts BOTH schemes so the same /api/v1/* endpoints work for
    //   • mobile / server-to-server clients using a JWT bearer token, AND
    //   • same-origin browser calls from the Razor UI (cookie session).
    // Cookie-authenticated state-changing calls need antiforgery — see the
    // [AutoValidateAntiforgeryToken] filter registered on controllers below.
    var schemes = entraConfigured
        ? new[] { "Bearer", CookieAuthenticationDefaults.AuthenticationScheme }
        : new[] { CookieAuthenticationDefaults.AuthenticationScheme };
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
builder.Services.AddAntiforgery();

// ── Localization (EFIGS) ────────────────────────────────────────────────────
var supportedCultures = new[] { "en", "de", "fr", "it", "es" }
    .Select(c => new CultureInfo(c))
    .ToArray();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("en");
    o.SupportedCultures = supportedCultures;
    o.SupportedUICultures = supportedCultures;
    o.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider());
    o.RequestCultureProviders.Insert(1, new CookieRequestCultureProvider());
});

// ── MVC / Razor Pages / API ────────────────────────────────────────────────
builder.Services
    .AddControllersWithViews()
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
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddScoped<ISlugService, SlugService>();
builder.Services.AddSingleton<IIpHashService, IpHashService>();
builder.Services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
builder.Services.AddSingleton<IQrCodeService, QrCodeService>();
builder.Services.AddScoped<ILinkAccessService, LinkAccessService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<ILocalAuthService, LocalAuthService>();
builder.Services.AddScoped<IFileAccessService, FileAccessService>();
builder.Services.AddScoped<INotificationService, SmtpNotificationService>();

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
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NimShareDbContext>();
    await db.Database.MigrateAsync();
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

// Make "is Entra configured?" available to any Razor view.
app.Use((ctx, next) =>
{
    ctx.Items["EntraConfigured"] = entraConfigured;
    return next(ctx);
});

app.MapControllers();
app.MapRazorPages();

app.Run();

public partial class Program { }
