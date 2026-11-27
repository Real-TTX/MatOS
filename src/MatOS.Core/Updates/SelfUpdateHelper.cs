using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatOS.Core.Updates;

/// <summary>
/// The helper of a self-update (<c>dotnet MatOS.Web.dll --self-update &lt;container&gt; [--database-copy &lt;file&gt;]</c>): a short-lived container of the NEW image,
/// started by MatOS with <c>--volumes-from</c> its own container (so it has the data volume and the Docker socket). A container cannot re-create itself;
/// this one stops MatOS, keeps it as <c>&lt;name&gt;-previous</c>, creates it again from the new image with the same settings, and waits until it is healthy.
/// When it is not, it puts the old container back (and the database as it was before), so a failed start never leaves the person without MatOS.
/// It writes what it does to <c>/data/self-update/last.log</c> and the outcome to <c>result.json</c>, which MatOS takes over at its next start.
/// </summary>
public static class SelfUpdateHelper
{
    public const string Argument = "--self-update";
    public const string DatabaseArgument = "--database-copy";
    public const string FolderName = "self-update";
    public const string ResultFile = "result.json";
    public const string LogFile = "last.log";
    public const string PreviousSuffix = "-previous";

    /// <summary>How long the new MatOS may take to become healthy (the image's health check starts after 30 seconds and runs every 30).</summary>
    public static readonly TimeSpan HealthyWithin = TimeSpan.FromMinutes(4);

    public static async Task<int> RunAsync(string[] args)
    {
        var container = ValueAfter(args, Argument);
        var databaseCopy = ValueAfter(args, DatabaseArgument);
        var dataPath = Environment.GetEnvironmentVariable("MatOS__DataPath") ?? "/data";
        var folder = Path.Combine(dataPath, FolderName);
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, LogFile);
        await File.WriteAllTextAsync(logPath, string.Empty);

        void Log(string line)
        {
            var stamped = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {line}";
            Console.WriteLine(stamped);
            File.AppendAllText(logPath, stamped + Environment.NewLine);
        }

        if (string.IsNullOrWhiteSpace(container))
        {
            Log("No container named: nothing to update.");
            return 2;
        }

        using var docker = new DockerEngineApi(Environment.GetEnvironmentVariable("DOCKER_HOST"));
        var result = await UpdateAsync(docker, container, databaseCopy, new Configuration.AppPaths(dataPath).DatabasePath, Log);
        result.At = DateTime.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(folder, ResultFile), JsonSerializer.Serialize(result));
        Log($"Done: {result.Outcome}. {result.Message}");
        return result.IsSuccess ? 0 : 1;
    }

    public static string? ValueAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static async Task<SelfUpdateResult> UpdateAsync(DockerEngineApi docker, string name, string? databaseCopy, string databasePath, Action<string> log)
    {
        // MatOS ends the job that started this before it goes: give it a moment.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var previous = name + PreviousSuffix;
        JsonObject? old;
        try
        {
            old = await docker.InspectContainerAsync(name);
        }
        catch (Exception ex)
        {
            return new SelfUpdateResult { Outcome = SelfUpdateResult.Failed, Message = $"Docker could not be asked: {ex.Message}" };
        }

        if (old is null)
        {
            return new SelfUpdateResult { Outcome = SelfUpdateResult.Failed, Message = $"There is no container {name}." };
        }

        var image = old["Config"]?["Image"]?.GetValue<string>() ?? string.Empty;
        var oldImage = old["Image"]?.GetValue<string>() is { } oldImageId ? await docker.InspectImageAsync(oldImageId) : null;
        var plan = ContainerRecreate.Build(old, oldImage, image);
        var restartPolicy = old["HostConfig"]?["RestartPolicy"]?.DeepClone();

        log($"Updating {name} to the newest {image}.");
        try
        {
            // A -previous of an earlier update that was not cleaned up is in the way of the name.
            if (await docker.InspectContainerAsync(previous) is not null)
            {
                log($"Removing the old {previous} of an earlier update.");
                await docker.RemoveAsync(previous);
            }

            log($"Stopping {name} and keeping it as {previous}.");
            await docker.StopAsync(name, 30);
            await docker.RenameAsync(name, previous);
            await docker.SetRestartPolicyAsync(previous, new JsonObject { ["Name"] = "no" });
        }
        catch (Exception ex)
        {
            log($"Could not set the old MatOS aside: {ex.Message}");
            await PutBackAsync(docker, name, previous, restartPolicy, null, null, log);
            return new SelfUpdateResult { Outcome = SelfUpdateResult.RolledBack, Message = $"MatOS could not be stopped for the update ({ex.Message}); it runs as before." };
        }

        try
        {
            log($"Creating {name} from the new image.");
            var id = await docker.CreateAsync(name, plan.Create);
            foreach (var network in plan.Networks)
            {
                await docker.ConnectAsync(network.Network, id, network.Endpoint);
            }

            await docker.StartAsync(name);
            log("Started. Waiting until it says it is healthy ...");
            var problem = await WaitHealthyAsync(docker, name, log);
            if (problem is null)
            {
                log($"The new MatOS is healthy. Removing {previous} (its image stays, tagged previous).");
                await docker.RemoveAsync(previous);
                return new SelfUpdateResult { Outcome = SelfUpdateResult.Succeeded, Message = "MatOS was updated and started." };
            }

            var logs = await docker.LogsAsync(name, 40);
            if (!string.IsNullOrWhiteSpace(logs))
            {
                log("The last lines of the new MatOS:\n" + logs.TrimEnd());
            }

            await PutBackAsync(docker, name, previous, restartPolicy, databaseCopy, databasePath, log);
            return new SelfUpdateResult { Outcome = SelfUpdateResult.RolledBack, Message = $"The new MatOS did not start ({problem}); the old one runs again with its data as before." };
        }
        catch (Exception ex)
        {
            log($"The new MatOS could not be created: {ex.Message}");
            await PutBackAsync(docker, name, previous, restartPolicy, databaseCopy, databasePath, log);
            return new SelfUpdateResult { Outcome = SelfUpdateResult.RolledBack, Message = $"The new MatOS could not be created ({ex.Message}); the old one runs again." };
        }
    }

    /// <summary>Null once the container is healthy (or running, without a health check), else what went wrong.</summary>
    private static async Task<string?> WaitHealthyAsync(DockerEngineApi docker, string name, Action<string> log)
    {
        var until = DateTime.UtcNow + HealthyWithin;
        var runningSince = (DateTime?)null;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var info = await docker.InspectContainerAsync(name);
            if (info?["State"] is not { } state)
            {
                return "the container is gone";
            }

            var health = state["Health"]?["Status"]?.GetValue<string>();
            var running = state["Running"]?.GetValue<bool>() ?? false;
            var restarts = info["RestartCount"]?.GetValue<int>() ?? 0;
            switch (health)
            {
                case "healthy":
                    return null;
                case "unhealthy":
                    return "its health check failed";
            }

            if (restarts > 2 || (!running && state["Status"]?.GetValue<string>() is "exited" or "dead"))
            {
                return $"it stopped (exit code {state["ExitCode"]?.GetValue<int>()})";
            }

            if (health is null && running)
            {
                // No health check: running for half a minute is enough.
                runningSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - runningSince > TimeSpan.FromSeconds(30))
                {
                    return null;
                }
            }
        }

        log("It did not become healthy in time.");
        return $"it was not healthy within {HealthyWithin.TotalMinutes:0} minutes";
    }

    /// <summary>The way back: the new container goes, the database is put back as it was before (it may have been migrated), the old container gets its name and starts.</summary>
    private static async Task PutBackAsync(DockerEngineApi docker, string name, string previous, JsonNode? restartPolicy, string? databaseCopy, string? databasePath, Action<string> log)
    {
        try
        {
            if (await docker.InspectContainerAsync(previous) is null)
            {
                // Not set aside yet: it only has to run.
                await docker.StartAsync(name);
                return;
            }

            log("Putting the old MatOS back.");
            await docker.RemoveAsync(name);
            if (databaseCopy is not null && databasePath is not null && File.Exists(databaseCopy))
            {
                log("Putting the database back as it was before the update.");
                File.Copy(databaseCopy, databasePath, overwrite: true);
                foreach (var extra in new[] { "-wal", "-shm" })
                {
                    File.Delete(databasePath + extra);
                }
            }

            await docker.RenameAsync(previous, name);
            await docker.SetRestartPolicyAsync(name, restartPolicy);
            await docker.StartAsync(name);
            log("The old MatOS runs again.");
        }
        catch (Exception ex)
        {
            log($"The old MatOS could not be put back by itself: {ex.Message}. Start it with: docker rename {previous} {name} && docker start {name}");
        }
    }
}
