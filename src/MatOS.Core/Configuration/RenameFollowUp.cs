using MatOS.Core.Apps;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Core.Configuration;

/// <summary>
/// The second half of the upgrade from Mattiverse (docs/ARCHITECTURE.md "Renamed from Mattiverse"): what an install of Mattiverse left in the engines
/// follows the new name, once, as a job at the first start (<see cref="InstallNaming.FinishRename"/>, set by <see cref="RenameUpgrade"/>). It waits for the
/// engines (they start beside MatOS after a reboot) and then:
/// <list type="bullet">
/// <item>MatDock: the stacks and the schedules of Mattiverse get the mark of MatOS together (<see cref="MatDockClient.RemarkLegacyAsync"/>), so a schedule for
/// all apps keeps covering every app.</item>
/// <item>The engines are connected again (<see cref="AppJobs.ConnectEngines"/>): Matcad's routes of Mattiverse get the mark of MatOS before the broker binds
/// them, MatOS's own route goes to the container <c>matos</c> under <c>matos.&lt;domain&gt;</c> (the one of <c>mattiverse.&lt;domain&gt;</c> goes), the DNS
/// records follow, and MatIdentity gets the back-channel address of the new container.</item>
/// <item>With a domain, the apps of the contract are deployed once more: they learn the new address of MatOS that may frame them (<c>MAT_FRAME_ANCESTORS</c>).</item>
/// </list>
/// When MatDock or Matcad is installed but does not answer in time, the job fails and leaves the flag: the next start tries again.
/// </summary>
public static class RenameFollowUp
{
    /// <summary>How long the job waits for the engines to answer.</summary>
    public static readonly TimeSpan EngineWait = TimeSpan.FromMinutes(10);

    /// <summary>Queues the job when the install still has to finish the rename. Returns its id, or <c>null</c> when there is nothing to do.</summary>
    public static async Task<long?> QueueIfPendingAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var config = await services.GetRequiredService<SystemConfigService>().GetAsync(ct);
        if (config.Naming?.FinishRename != true)
        {
            return null;
        }

        return await services.GetRequiredService<JobService>().EnqueueAsync(
            JobKind.Provision, "Finish the rename from Mattiverse in the engines", null, Work(), ct);
    }

    /// <summary>The work of the job.</summary>
    public static Func<JobContext, Task> Work()
        => async ctx =>
        {
            var services = ctx.Services;
            var configs = services.GetRequiredService<SystemConfigService>();
            if ((await configs.GetAsync(ctx.Cancellation)).Naming?.FinishRename != true)
            {
                ctx.Log("Nothing to do: the rename from Mattiverse is finished.");
                return;
            }

            var deadline = DateTime.UtcNow + EngineWait;
            var matdock = services.GetRequiredService<MatDockClient>();
            var matdockInstalled = await WaitAsync(matdock, "MatDock", required: true, deadline, ctx);
            await WaitAsync(services.GetRequiredService<MatcadClient>(), "Matcad", required: true, deadline, ctx);
            await WaitAsync(services.GetRequiredService<MatddnsClient>(), "Matddns", required: false, deadline, ctx);
            await WaitAsync(services.GetRequiredService<MatIdentityClient>(), "MatIdentity", required: false, deadline, ctx);
            await WaitAsync(services.GetRequiredService<MatSmtpClient>(), "MatSMTP", required: false, deadline, ctx);

            if (matdockInstalled)
            {
                var remarked = await matdock.RemarkLegacyAsync(ctx.Log, ctx.Cancellation);
                ctx.Log(remarked == 0
                    ? "MatDock has no stacks or schedules with the mark of Mattiverse."
                    : $"MatDock: {remarked} stack{(remarked == 1 ? "" : "s")} and schedule{(remarked == 1 ? "" : "s")} of Mattiverse carry the mark of MatOS now.");
            }

            await AppJobs.ConnectEngines()(ctx);

            var config = await configs.GetAsync(ctx.Cancellation);
            if (!string.IsNullOrWhiteSpace(config.BaseDomain) && await services.GetRequiredService<ProxyPublisher>().IsActiveAsync(ctx.Cancellation))
            {
                ctx.Log("The apps learn the new address of MatOS that may show them in its window ...");
                await services.GetRequiredService<AppService>().RedeployContractAppsAsync(ctx.Log, ctx.Cancellation);
            }

            await configs.UpdateAsync(c =>
            {
                if (c.Naming is not null)
                {
                    c.Naming.FinishRename = false;
                }
            }, ctx.Cancellation);
            ctx.Log("The rename from Mattiverse is finished.");
        };

    /// <summary>
    /// Waits until an installed engine answers. Returns whether it is installed and answers. MatDock and Matcad are <paramref name="required"/> (their marks
    /// are what the job is for): when they do not answer before <paramref name="deadline"/> the job fails and the next start tries again.
    /// </summary>
    private static async Task<bool> WaitAsync(EngineClient engine, string name, bool required, DateTime deadline, JobContext ctx)
    {
        if (!await engine.IsInstalledAsync(ctx.Cancellation))
        {
            return false;
        }

        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || !await engine.WaitReadyAsync(left, ctx.Log, ctx.Cancellation))
        {
            if (required)
            {
                throw new InvalidOperationException($"{name} does not answer: the next start of MatOS tries again.");
            }

            ctx.Log($"Warning: {name} does not answer; it is connected again the next time the engines are.");
            return false;
        }

        return true;
    }
}
