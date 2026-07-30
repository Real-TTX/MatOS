using MatOS.Web.Api;
using MatOS.Web.Auth;
using MatOS.Web.Data;
using MatOS.Web.Docker;
using MatOS.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// ---- Data volume / DataProtection keys (survive restart) ----
var keysDir = MatosPaths.KeysDir(builder.Environment);
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("matOS");

// ---- Services ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<JsonConfigService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<DesktopLayoutService>();
builder.Services.AddScoped<MatOS.Web.Controls.Common.ControlIdGenerator>();
builder.Services.AddHostedService<SessionCleanupService>();

// ---- AuthN / AuthZ ----
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)));

// Reverse proxy (Caddy/Matcad) -> honour X-Forwarded-* so cookie Secure and
// scheme detection work behind the proxy.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// ---- Web ----
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Setup");
    o.Conventions.AllowAnonymousToPage("/Logout");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AuthorizeFolder("/Apps/Users", "Admin");
});
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);
builder.Services.AddProblemDetails();

var app = builder.Build();

// ---- Pipeline ----
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = MatOS.Web.BuildInfo.Version,
    channel = MatOS.Web.BuildInfo.Channel,
    utc = DateTime.UtcNow
})).AllowAnonymous();

// ---- API (all require an authenticated session via the fallback policy) ----
var api = app.MapGroup("/api/v1");
api.MapDockerApi();
api.MapDesktopApi();

app.Run();
