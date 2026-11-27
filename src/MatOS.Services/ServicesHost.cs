using System.Text.Json.Serialization;
using MatOS.Core.Security;
using MatOS.Services.Admin;
using MatOS.Web.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using MatOS.Services.Edge;
using MatOS.Services.Instances;
using MatOS.Services.Pages;
using MatOS.Services.Tunnel;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services;

/// <summary>
/// Builds the service. A method rather than top-level code so the tests start the very same application on free ports.
/// </summary>
public static class ServicesHost
{
    /// <summary>The port of the HTTP API inside the container (Dockerfile.services); the health check asks it.</summary>
    public const int ContainerHttpPort = 8080;

    /// <param name="configure">Changes to the services after the defaults (the tests put in a resolver of their own).</param>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        // The web root is next to the assembly: the toolkit there is linked from MatOS.Web (MatOS.Services.csproj), and the tests start the
        // service from their own bin folder, which has the same files.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // The pages are found in this assembly, also when the tests (another entry assembly) start the service.
            ApplicationName = typeof(ServicesHost).Assembly.GetName().Name,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var options = builder.Configuration.GetSection(ServicesOptions.Section).Get<ServicesOptions>() ?? new ServicesOptions();
        options.Normalize();
        var problems = options.Problems();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The configuration of MatOS Services is not valid: " + string.Join(" ", problems));
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);

        Directory.CreateDirectory(options.DataPath);
        var database = Path.Combine(options.DataPath, "services.db");
        builder.Services.AddDbContext<ServicesDbContext>(o => o.UseSqlite($"Data Source={database}"));

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

        ConfigureForwardedHeaders(builder.Services, options);
        ConfigureRateLimits(builder.Services);

        builder.Services.AddSingleton<InstanceDirectory>();
        builder.Services.AddSingleton<InstanceActivity>();
        builder.Services.AddSingleton<RegistrationLock>();
        builder.Services.AddSingleton<TunnelLimits>(_ => new TunnelLimits());
        builder.Services.AddSingleton<TunnelRegistry>();

        // Before the edge and the DNS: hosted services stop in reverse order, so the last flush of the statistics comes after they have stopped.
        builder.Services.AddSingleton<UsageRecorder>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageRecorder>());
        builder.Services.AddSingleton<UsageRetention>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UsageRetention>());
        AddAdminArea(builder.Services, options);
        builder.Services.AddSingleton<InstanceExpiry>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InstanceExpiry>());
        builder.Services.AddSingleton<ReachabilityCheck>();
        builder.Services.AddSingleton<EdgeService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EdgeService>());
        builder.Services.AddSingleton<Registrar>();
        builder.Services.AddSingleton<PortCheck>();
        builder.Services.AddSingleton<PortCheckLimits>();
        builder.Services.AddSingleton<FormGuard>();
        builder.Services.AddSingleton<IDomainResolver, SystemDomainResolver>();
        builder.Services.AddSingleton<CustomDomains>();
        builder.Services.AddHostedService<DomainChecker>();
        builder.Services.AddSingleton<DnsService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DnsService>());

        configure?.Invoke(builder.Services);

        var app = builder.Build();
        Migrate(app.Services);
        // The edge looks up the verified domains synchronously: they are in memory before the first connection.
        app.Services.GetRequiredService<CustomDomains>().ReloadAsync().GetAwaiter().GetResult();

        AdminAccounts.EnsureFirstAsync(app.Services, app.Logger).GetAwaiter().GetResult();

        app.UseForwardedHeaders();
        var area = app.Services.GetRequiredService<AdminArea>();
        app.Use(area.GateAsync);
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        // The pings of the control connection are MatOS's; these keep proxies in between from dropping a quiet stream.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.MapStatusPages();
        app.MapInstanceApi();
        app.MapDynDns();
        app.MapDynDnsPages();
        app.MapPortsApi();
        app.MapDomainsApi();
        app.MapTunnel();
        app.MapRazorPages();
        app.MapAdminApi();
        return app;
    }

    /// <summary>
    /// The admin area (<c>/admin</c>, Pages/Admin): Razor Pages with MatOS.Web's controls and toolkit, the accounts and their sessions, the
    /// cookie and the antiforgery token (docs/SERVICES.md "The admin area").
    /// </summary>
    private static void AddAdminArea(IServiceCollection services, ServicesOptions options)
    {
        // The keys of the cookies live in the volume, so a sign-in survives an update of the image.
        services.AddDataProtection()
            .SetApplicationName("MatOS.Services")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataPath, "keys")));
        services.AddHttpContextAccessor();
        services.AddSingleton<AdminGuard>();
        services.AddSingleton<AdminArea>();
        services.AddSingleton<Toolkit>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<AdminAccounts>();
        services.AddScoped<InstanceAdmin>();
        services.AddAdminAuth();
        services.AddRazorPages(o =>
        {
            o.Conventions.AuthorizeFolder("/Admin", AdminAuth.Policy);
            o.Conventions.AllowAnonymousToPage("/Admin/SignIn");
            // The sign-in has its own, tighter limit (its page model).
            o.Conventions.AddFolderApplicationModelConvention("/Admin", model =>
                model.EndpointMetadata.Add(new EnableRateLimitingAttribute(
                    model.ViewEnginePath == "/Admin/SignIn" ? AdminArea.SignInPolicy : AdminArea.PagePolicy)));
        });
    }

    /// <summary>The container's health check: asks <c>/healthz</c> of the running service.</summary>
    public static async Task<int> CheckHealthAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var answer = await client.GetStringAsync($"http://127.0.0.1:{ContainerHttpPort}/healthz");
            return answer.Trim() == "ok" ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Migrations rather than EnsureCreated: the database lives on in the volume across image updates, and a migration can change it in
    /// place where EnsureCreated could only create it once.
    /// </summary>
    private static void Migrate(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        db.Database.Migrate();
        // WAL lets the edge and the DNS read while the API writes.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// The service runs behind server1's proxy: its <c>X-Forwarded-For</c> is the visitor's address, but only when it comes from a trusted
    /// proxy. Anybody else could otherwise pick the address the rate limit counts and the one <c>/nic/update</c> publishes.
    /// </summary>
    private static void ConfigureForwardedHeaders(IServiceCollection services, ServicesOptions options)
    {
        var networks = Networks.ParseList(options.TrustedProxies)!;
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var network in networks)
            {
                o.KnownIPNetworks.Add(network);
                if (Networks.MappedToIpv6(network) is { } mapped)
                {
                    o.KnownIPNetworks.Add(mapped);
                }
            }

            // Walks back through the chain while the hops are trusted, and stops at the first that is not: that is the visitor.
            o.ForwardLimit = null;
        });
    }

    /// <summary>
    /// Limits per client address: name checks come while a person types, the rest is rare. Generous enough for MatOS, tight enough that a
    /// script cannot use the service as a probe. The tunnel endpoints have limits of their own (<see cref="TunnelLimits"/>): every visitor
    /// of an installation is a stream request. The administrator view counts an IPv6 address as its /48, as the registrations do.
    /// </summary>
    private static void ConfigureRateLimits(IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                // The statistics of the API; the admin area's own limits are not its usage.
                if (!context.HttpContext.Request.Path.StartsWithSegments(AdminArea.Path))
                {
                    context.HttpContext.RequestServices.GetRequiredService<UsageRecorder>().RateLimited();
                }

                return ValueTask.CompletedTask;
            };
            o.AddPolicy(InstanceApi.NamesPolicy, context => Window(context, 120));
            o.AddPolicy(InstanceApi.ApiPolicy, context => Window(context, 60));
            o.AddPolicy(AdminArea.SignInPolicy, context => Window(context, 20, Registrar.RegistrationPrefix));
            o.AddPolicy(AdminArea.PagePolicy, context => Window(context, 300, Registrar.RegistrationPrefix));
        });
    }

    private static RateLimitPartition<string> Window(HttpContext context, int perMinute, int ipv6Prefix = 64) =>
        RateLimitPartition.GetFixedWindowLimiter(Networks.LimitKey(context.ClientIp(), ipv6Prefix), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}
