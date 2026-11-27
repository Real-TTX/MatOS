using MatOS.Core.Apps;
using MatOS.Core.Auth;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using MatOS.Core.Security;
using MatOS.Core.Users;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Core;

/// <summary>
/// Registers the MatOS domain/services. The hosting layer is still responsible for the
/// infrastructure that needs configuration: the <c>MatOSDbContext</c> (connection string),
/// Data Protection, options binding, <c>AppPaths</c> and <c>ICurrentUserAccessor</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMatOSCore(this IServiceCollection services)
    {
        // Stateless helpers and singletons.
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<JsonConfigStore>();
        services.AddSingleton<SystemConfigService>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<DockerCli>();
        services.AddSingleton<DockerService>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<JobLogStore>();
        services.AddHttpClient();

        // Per-request services (depend on the scoped DbContext).
        services.AddScoped<AuthService>();
        services.AddScoped<SessionService>();
        services.AddScoped<UserService>();

        // Apps and the engines that run them.
        services.AddScoped<ComposeRenderer>();
        services.AddScoped<ComposeFiles>();
        services.AddScoped<ComposeRunner>();
        services.AddScoped<ImagePreflight>();
        services.AddScoped<PortPlanner>();
        services.AddScoped<EngineLocator>();
        services.AddScoped<MatDockClient>();
        services.AddScoped<RuntimeReader>();
        services.AddSingleton<MatOS.Core.Tasks.TaskMonitor>();
        services.AddScoped<MatOS.Core.Tasks.TaskManagerService>();
        services.AddScoped<LocalComposeEngine>();
        services.AddScoped<MatDockEngine>();
        services.AddScoped<StackEngines>();
        services.AddScoped<AppService>();
        services.AddScoped<AppReconciler>();

        // Backups: MatDock keeps those of its stacks, MatOS's own runner those of the rest.
        services.AddScoped<BackupService>();
        services.AddScoped<IBackupProvider, LocalBackupProvider>();
        services.AddScoped<IBackupProvider, MatDockBackupProvider>();
        services.AddScoped<BackupHub>();
        services.AddScoped<BackupTargetService>();

        services.AddScoped<JobService>();

        // Updates: whether a newer image is there (the registry), MatOS's own update.
        services.AddScoped<Updates.RegistryClient>();
        services.AddScoped<Updates.SelfUpdateService>();

        // Matcad (routes, certificates) and Matddns (DNS records).
        services.AddScoped<MatcadClient>();
        services.AddScoped<MatddnsClient>();
        services.AddScoped<ProxyPublisher>();
        services.AddScoped<DnsPublisher>();
        services.AddScoped<IAppProvisioner, MatDockProvisioner>();
        services.AddScoped<IAppProvisioner, MatcadProvisioner>();
        services.AddScoped<IAppProvisioner, MatddnsProvisioner>();

        // MatIdentity, the single sign-on: the apps are registered there by MatOS.
        services.AddScoped<MatIdentityClient>();
        services.AddScoped<IdentityLink>();
        services.AddScoped<PlatformClient>();
        // The People area: the people, groups and grants of MatIdentity (SCIM) and the apps' roles, permissions and quotas.
        services.AddHttpClient(MatIdentityPeople.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<MatIdentityPeople>();
        // What each person may use, for the home screen (kept a minute per person).
        services.AddScoped<PersonAccess>();
        services.AddScoped<IAppProvisioner, MatIdentityProvisioner>();

        // MatSMTP, the mail relay: every app that sends mail gets a relay user there from MatOS.
        services.AddScoped<MatSmtpClient>();
        services.AddScoped<MailLink>();
        services.AddScoped<FrameLink>();
        // Every app in the window: the frame headers on Matcad's route of an app that does not take MAT_FRAME_ANCESTORS. Its client follows no redirect by
        // itself (one to the public address of an app cannot be reached from inside Docker).
        services.AddHttpClient(RouteFrameHeaders.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<RouteFrameHeaders>();

        // The bindings (MatDesign docs/PLATFORM.md "Bindings"): one broker for every capability, no code per app.
        services.AddScoped<AppAddresses>();
        services.AddScoped<AppManifests>();
        services.AddScoped<BindingBroker>();
        services.AddSingleton<ManifestCache>();
        services.AddScoped<IAppProvisioner, MatSmtpProvisioner>();

        // The free address of the address service (docs/SERVICES.md): its client, the switch of its mode and the target of the tunnel.
        services.AddHttpClient(ServicesClient.HttpClientName);
        services.AddScoped<ServicesClient>();
        services.AddScoped<HostedAddressService>();
        // Reached from anywhere: the check of the web ports and the route (direct or through the tunnel), the same addresses either way.
        services.AddScoped<RouteService>();
        services.AddScoped<MatOS.Core.Integrations.OwnerHandover>();
        services.AddScoped<MatOS.Core.Setup.SetupService>();
        // The screen "Notifications / mail": MatSMTP's state and the check of its account.
        services.AddScoped<MatOS.Core.Setup.MailSetup>();
        // The list "Getting started" after the first start (/Start, the widget Setup): what is done, a minute kept for the engines.
        services.AddScoped<MatOS.Core.Setup.GettingStartedService>();
        // The test of the first start: whether an own domain exists and where its hosts point (three seconds per question).
        services.AddSingleton<DnsCheck>();
        // The self check of the first start: MatOS answers where the person opens it (certificates checked as a browser would).
        services.AddHttpClient(AppJobs.SelfCheckClient, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<TunnelTarget>();
        services.AddSingleton<TunnelClient>();

        return services;
    }

    /// <summary>The background workers: the job runner, the backup scheduler and the tunnel of the free address.</summary>
    public static IServiceCollection AddMatOSWorkers(this IServiceCollection services)
    {
        services.AddHostedService<JobRunnerService>();
        services.AddHostedService<BackupSchedulerService>();
        // The tunnel of the free address: up while it is used in tunnel mode.
        services.AddHostedService(sp => sp.GetRequiredService<TunnelClient>());
        // The check of the reachability every day (and an hour after one that could not be made).
        services.AddHostedService<ReachWatcher>();
        // The automatic updates in their window, the look for a newer MatOS, the report of a self-update after the restart.
        services.AddHostedService<Updates.AutoUpdateService>();
        return services;
    }
}
