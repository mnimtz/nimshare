using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
builder.Services.AddDbContext<NimShareDbContext>(o =>
{
    if (string.Equals(dbProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        o.UseSqlServer(connString, b => b.MigrationsAssembly("NimShare.Api"));
    else
        o.UseSqlite(connString, b => b.MigrationsAssembly("NimShare.Api"));
});

// ── Auth ───────────────────────────────────────────────────────────────────
// Web sign-in (cookies + OIDC to Entra ID) for Razor pages,
// bearer JWT for the JSON API — both against the same Entra registration.
//
// If AzureAd:ClientId is empty (fresh checkout, no user-secrets yet), we skip
// Entra wiring so the app still boots and public /s/{slug}, /u/{slug}, and
// the branded welcome page render. Any [Authorize] route will then just 401.
var entraClientId = builder.Configuration["AzureAd:ClientId"];
var entraConfigured = !string.IsNullOrWhiteSpace(entraClientId);
if (entraConfigured)
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"), jwtBearerScheme: "Bearer");
}
else
{
    // Minimal scheme so [Authorize] challenges return 401 instead of crashing.
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie();
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

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRequestLocalization();

app.UseRouting();
app.UseRateLimiter();
app.UseMiddleware<CustomDomainMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();

public partial class Program { }
