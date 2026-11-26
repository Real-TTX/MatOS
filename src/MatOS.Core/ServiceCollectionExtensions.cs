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

        services.AddScoped<JobService>();

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

        // The bindings (MatDesign docs/PLATFORM.md "Bindings"): one broker for every capability, no code per app.
        services.AddScoped<AppAddresses>();
        services.AddScoped<AppManifests>();
        services.AddScoped<BindingBroker>();
        services.AddSingleton<ManifestCache>();
        services.AddScoped<IAppProvisioner, MatSmtpProvisioner>();

        return services;
    }

    /// <summary>The background workers: the job runner and the backup scheduler.</summary>
    public static IServiceCollection AddMatOSWorkers(this IServiceCollection services)
    {
        services.AddHostedService<JobRunnerService>();
        services.AddHostedService<BackupSchedulerService>();
        return services;
    }
}
