using MatOS.Core;
using MatOS.Core.Backups;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

// "docker HEALTHCHECK": the image has no curl, so the app checks itself and exits with 0 (healthy) or 1.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

// The helper of a self-update (docs/ARCHITECTURE.md "How MatOS updates itself"): a short-lived container of the new image re-creates MatOS's container.
if (args.Contains(MatOS.Core.Updates.SelfUpdateHelper.Argument))
{
    return await MatOS.Core.Updates.SelfUpdateHelper.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration / options
// ---------------------------------------------------------------------------
// Resolve the data directory before the container is built (needed for keys + SQLite path).
// In Docker the Dockerfile sets MatOS__DataPath=/data; local runs fall back to App_Data. The section Mattiverse (the name before 2026-11) still counts.
var dataPath = MatOSConfiguration.Value(builder.Configuration, nameof(MatOSOptions.DataPath))
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data");

var appPaths = new AppPaths(dataPath);
appPaths.EnsureCreated();
builder.Services.AddSingleton(appPaths);

// What an install of Mattiverse left on the volume (its database, its names), before the options and the database are used.
var upgrade = await RenameUpgrade.RunAsync(appPaths, builder.Configuration);
builder.Services.AddMatOSOptions(builder.Configuration, upgrade, dataPath);

var sessionLifetimeDays = MatOSConfiguration.Read(builder.Configuration).SessionLifetimeDays;

// ---------------------------------------------------------------------------
// Infrastructure services
// ---------------------------------------------------------------------------

// Data Protection keys live on the mounted volume so cookies and encrypted secrets survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("Mattiverse")
    .PersistKeysToFileSystem(new DirectoryInfo(appPaths.KeysPath));

builder.Services.AddDbContext<MatOSDbContext>(options => options.UseSqlite(appPaths.SqliteConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<MatOS.Web.Support.Toolkit>();

// Honour X-Forwarded-* from a TLS-terminating reverse proxy so Request.IsHttps is correct and the
// auth cookie is marked Secure when the client connection is HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddMatOSCore();
builder.Services.AddMatOSWorkers();
// The look of MatOS's sign-in at MatIdentity: the accent, the planet and the default wallpaper (Core/Integrations/SelfBrand.cs).
builder.Services.AddSingleton(new MatOS.Core.Integrations.SelfBrandAssets(
    MatOS.Web.Support.Ui.App.Accent,
    Path.Combine(builder.Environment.WebRootPath, "img", "logo.svg"),
    Path.Combine(builder.Environment.WebRootPath, "img", "wallpapers", "orbit-light.jpg")));
builder.Services.AddScoped<MatOS.Web.Support.AppUrls>();
builder.Services.AddScoped<MatOS.Web.Support.HomeContributions>();
builder.Services.AddScoped<MatOS.Web.Support.EngineOverview>();
// The probe of a frame (Support/FrameProbe.cs) reads the answer of the app through Core/Integrations/RouteFrameHeaders.cs.
builder.Services.AddScoped<MatOS.Web.Support.FrameProbe>();
builder.Services.AddHostedService<DockerStartupService>();

// ---------------------------------------------------------------------------
// Authentication / authorization
// ---------------------------------------------------------------------------
var authentication = builder.Services.AddAuthentication(AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = "matos.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, sessionLifetimeDays));
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = SessionValidation.ValidateAsync;
    })
    // The sign-in through MatIdentity (when it is installed): the answer lands in a short cookie of its own, /Account/External turns it into a session.
    .AddCookie(MatIdentitySignIn.ExternalScheme, options =>
    {
        options.Cookie.Name = "matos.external";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddOpenIdConnect(MatIdentitySignIn.Scheme, _ => { });
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, MatIdentitySignIn>();
builder.Services.AddSingleton<MatOS.Core.Auth.LogoutReplayCache>();
// The MCP endpoint for AI clients (docs/ARCHITECTURE.md "MCP"): its own sign-in by a personal access token, never the cookie.
builder.Services.AddMatOSMcp(authentication);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
    // Backstop: any endpoint without an explicit policy still requires an authenticated user.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------------------
// Razor Pages (secure by default; open only what is needed before login)
// ---------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    // The first start: Welcome and the account before there is any; the rest of it (/Setup/Engines, /Setup/Choose, /Setup/Progress, /Setup/Account, /Setup/Mail) is for the administrator it made.
    options.Conventions.AllowAnonymousToPage("/Setup/Index");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/External");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/SignedOut");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Share/View");

    // Roles: Admin manages everything; User opens apps and looks at them; Anonymous only sees a shared link.
    foreach (var folder in new[] { "/Store", "/Backups", "/Volumes", "/Jobs", "/Tasks", "/Users", "/People", "/Settings", "/Wizard", "/Engines", "/Setup", "/Start" })
    {
        options.Conventions.AuthorizeFolder(folder, "AdminOnly");
    }

    foreach (var page in new[] { "/Apps/Update", "/Apps/Uninstall", "/Apps/Share", "/Apps/Manage", "/Apps/Move", "/Apps/Folders" })
    {
        options.Conventions.AuthorizePage(page, "AdminOnly");
    }
});

var app = builder.Build();

foreach (var line in upgrade.Log)
{
    app.Logger.LogInformation("Upgrade from Mattiverse: {Line}", line);
}

// ---------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

var staticContentTypes = new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });
// A 404 or 403 shows a page of the app (with the original status code) instead of an empty response.
app.UseStatusCodePagesWithReExecute("/StatusCode/{0}");
app.UseRouting();
// The options of the sign-in through MatIdentity follow the settings before the authentication middleware builds its handler for this request.
app.Use(async (context, next) =>
{
    await MatOS.Web.Infrastructure.MatIdentitySignIn.EnsureCurrentAsync(context);
    await next(context);
});
app.UseAuthentication();
// First run: redirect everything to /Setup until an administrator exists.
app.UseMiddleware<SetupGuardMiddleware>();
app.UseMiddleware<PasswordChangeGuardMiddleware>();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet("/healthz", () => Results.Text("ok " + AppVersion.Current)).AllowAnonymous();
app.MapJobsApi();
// The live check of a name for the free address (the setup assistant, Settings, Address).
app.MapAddressApi();
// The progress screen of the first start and the tour of the home screen.
app.MapOnboardingApi();
// The screen of the choices of the first start: the public addresses for the records and the test of the answers.
app.MapSetupApi();
// Whether the apps can be reached from the internet, and "Check again".
app.MapReachApi();
// MatIdentity ends the sessions of a person who signed out there (server to server).
app.MapBackchannelLogout();
// The Model Context Protocol for AI clients, signed in by an access token of the account.
app.MapMatOSMcp();

// ---------------------------------------------------------------------------
// Database migration + first-run seed
// ---------------------------------------------------------------------------
await DbBootstrapper.InitializeAsync(app.Services, app.Logger);

app.Run();
return 0;
