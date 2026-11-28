using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatOS.Core;
using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using MatOS.Core.Mcp;
using MatOS.Core.Tasks;
using MatOS.Core.Updates;
using MatOS.Core.Volumes;
using MatOS.Web.Support;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatOS.Web.Mcp;

/// <summary>
/// The tools of MatOS's MCP endpoint (docs/ARCHITECTURE.md "MCP"). Each one is a thin call of the services the pages use: reading and changing go through
/// the engines as everywhere, long work is a job (<see cref="AppJobs"/>) whose id the tool returns. The rules (role, confirm) are <see cref="McpRules"/>.
/// The services share the request's DbContext, so a tool calls them one after the other, never in parallel.
/// </summary>
[McpServerToolType]
public sealed class MatOSTools
{
    /// <summary>How the answers are written: camelCase, enums as words, no nulls.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const int LogChunk = 20_000;

    private readonly IHttpContextAccessor _http;
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly JobService _jobs;
    private readonly MatOSDbContext _db;
    private readonly IServiceProvider _services;

    public MatOSTools(IHttpContextAccessor http, AppService apps, CatalogService catalog, JobService jobs, MatOSDbContext db, IServiceProvider services)
    {
        _http = http;
        _apps = apps;
        _catalog = catalog;
        _jobs = jobs;
        _db = db;
        _services = services;
    }

    private HttpContext Http => _http.HttpContext ?? throw new McpException("No request.");

    private ClaimsPrincipal Caller => Http.User;

    private CancellationToken Ct => Http.RequestAborted;

    private T Service<T>() where T : notnull => _services.GetRequiredService<T>();

    // ------------------------------------------------------------------ read

    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The person this token acts as, their role (Admin or User) and the version of MatOS. Admin may do everything; User may only look at the apps they may use.")]
    public string WhoAmI()
    {
        Refuse(McpRules.PersonProblem(Caller));
        return Write(new
        {
            username = Caller.Identity?.Name,
            displayName = Caller.GetDisplayName(),
            role = McpRules.RoleOf(Caller).ToString(),
            matos = AppVersion.Current,
        });
    }

    [McpServerTool(Name = "list_apps", Title = "List the installed apps", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The installed apps (the engines included) with their state (running, stopped, installing, failed ...) and the address they open at. A user sees only the apps they may use.")]
    public async Task<string> ListApps()
    {
        var apps = await VisibleAppsAsync();
        var runtimes = await _apps.GetRuntimesAsync(Ct);
        var urls = Service<AppUrls>();
        var rows = new List<object>(apps.Count);
        foreach (var app in apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var status = AppStatus.Of(app, runtimes.GetValueOrDefault(app.Slug));
            rows.Add(new
            {
                app = app.Slug,
                name = app.Name,
                catalogId = app.CatalogId,
                engine = app.IsCore,
                lifecycle = app.Lifecycle,
                status = status.Text,
                address = await urls.OpenUrlAsync(app, Http.Request, Ct, path: string.Empty),
                node = app.NodeAddress,
            });
        }

        return Write(rows);
    }

    [McpServerTool(Name = "get_app", Title = "Details of an app", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The details of one installed app: its state, addresses, containers, the job that runs for it, its last backup and update.")]
    public async Task<string> GetApp([Description("The app: its name (slug) from list_apps, or its numeric id.")] string app)
    {
        var found = await FindAsync(app, forUser: true);
        var runtime = await _apps.GetRuntimeAsync(found.Slug, Ct);
        var addresses = await Service<AppAddresses>().AddressesAsync(found, Ct);
        var containers = McpRules.RoleOf(Caller) == UserRole.Admin
            ? (await Service<RuntimeReader>().ListAppContainersAsync(found.Slug, found.NodeId, Ct)).Select(c => new { name = c.Name, service = c.Service, image = c.Image, state = c.State, status = c.Status }).ToList<object>()
            : null;
        var active = await _jobs.GetActiveAsync(found.Id, Ct);
        var catalog = _catalog.Get(found.CatalogId);
        return Write(new
        {
            app = found.Slug,
            id = found.Id,
            name = found.Name,
            catalogId = found.CatalogId,
            category = catalog?.Category,
            engine = found.IsCore,
            lifecycle = found.Lifecycle,
            message = found.StatusMessage,
            status = AppStatus.Of(found, runtime).Text,
            running = runtime.Running,
            total = runtime.Total,
            address = await Service<AppUrls>().OpenUrlAsync(found, Http.Request, Ct, path: string.Empty),
            addresses,
            node = found.NodeAddress,
            installed = found.CreateDate,
            lastUpdatedAt = found.LastUpdatedAt,
            lastBackupAt = found.LastBackupAt,
            activeJob = active is null ? null : new { id = active.Id, title = active.Title, status = active.Status },
            containers,
        });
    }

    [McpServerTool(Name = "search_store", Title = "Search the store", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The apps of the store (the catalog) that MatOS can install, with what each one asks for when it is installed. Without a query it lists all of them.")]
    public string SearchStore(
        [Description("Words to look for in the name, the tagline, the description or the category.")] string? query = null,
        [Description("Only this category (as the results name it).")] string? category = null,
        [Description("How many results (1 to 100, 25 when left out).")] int? limit = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        IEnumerable<CatalogApp> apps = _catalog.GetAll();
        if (!string.IsNullOrWhiteSpace(category))
        {
            apps = apps.Where(a => a.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            apps = apps.Where(a => words.All(w => a.Id.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || a.Tagline.Contains(w, StringComparison.OrdinalIgnoreCase) || a.Description.Contains(w, StringComparison.OrdinalIgnoreCase)
                || a.Category.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        var list = apps.OrderBy(a => AppStates.Rank(a.State)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return Write(new
        {
            matching = list.Count,
            categories = _catalog.GetCategories(),
            apps = list.Take(McpRules.Limit(limit, 25, 100)).Select(a => new
            {
                id = a.Id,
                name = a.Name,
                tagline = a.Tagline,
                category = a.Category,
                state = a.State,
                canInstall = AppStates.CanInstall(a.State) && !a.Core,
                engine = a.Core,
                homepage = a.Homepage,
                asks = a.Variables.Where(v => !v.Hidden).Select(v => new
                {
                    key = v.Key,
                    label = v.Label,
                    required = v.Required && !v.Generate && string.IsNullOrEmpty(v.Default),
                    @default = v.IsSecret ? null : v.Default,
                    secret = v.IsSecret,
                    help = v.Help,
                }),
            }),
        });
    }

    [McpServerTool(Name = "list_jobs", Title = "List the jobs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The latest jobs (installs, updates, backups ...), the newest first, with their status.")]
    public async Task<string> ListJobs(
        [Description("Only jobs with this status: queued, running, succeeded or failed.")] string? status = null,
        [Description("Only the jobs of this app (its name from list_apps).")] string? app = null,
        [Description("How many (1 to 100, 20 when left out).")] int? limit = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var query = _db.Jobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JobStatus>(status, ignoreCase: true, out var s))
            {
                throw new McpException("The status is one of queued, running, succeeded, failed.");
            }

            query = query.Where(j => j.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(app))
        {
            var slug = app.Trim();
            query = query.Where(j => j.AppSlug == slug);
        }

        var jobs = await query.OrderByDescending(j => j.Id).Take(McpRules.Limit(limit, 20, 100))
            .Select(j => new { j.Id, j.Title, j.Kind, j.Status, j.AppSlug, j.CreateDate, j.StartedAt, j.FinishedAt, j.Error })
            .ToListAsync(Ct);
        return Write(jobs.Select(j => new { id = j.Id, title = j.Title, kind = j.Kind, status = j.Status, app = j.AppSlug, queuedAt = j.CreateDate, startedAt = j.StartedAt, finishedAt = j.FinishedAt, error = j.Error }));
    }

    [McpServerTool(Name = "get_job", Title = "A job and its log", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One job: its status and its log from an offset on. Call it again with the returned nextOffset until finished is true.")]
    public async Task<string> GetJob(
        [Description("The id of the job (the tools that start work return it).")] long id,
        [Description("Where the log starts (0, or nextOffset of the last answer).")] int offset = 0)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var job = await _jobs.GetAsync(id, Ct) ?? throw new McpException($"There is no job {id}.");
        var chunk = await _jobs.ReadLogAsync(id, Math.Max(0, offset), Ct);
        var text = chunk?.Text ?? string.Empty;
        var more = text.Length > LogChunk;
        if (more)
        {
            text = text[..LogChunk];
        }

        return Write(new
        {
            id = job.Id,
            title = job.Title,
            kind = job.Kind,
            status = chunk?.Status ?? job.Status,
            app = job.AppSlug,
            finished = (chunk?.Finished ?? false) && !more,
            error = chunk?.Error ?? job.Error,
            log = text,
            nextOffset = Math.Max(0, offset) + text.Length,
        });
    }

    [McpServerTool(Name = "list_backups", Title = "List the backups", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The backups of the apps (MatDock's and MatOS's own), the newest first. The id of one is what restore_backup takes.")]
    public async Task<string> ListBackups(
        [Description("Only the backups of this app (its name from list_apps).")] string? app = null,
        [Description("How many (1 to 200, 50 when left out).")] int? limit = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var backups = await Service<BackupHub>().ListAsync(Ct);
        var list = backups.Where(b => string.IsNullOrWhiteSpace(app) || b.AppSlug.Equals(app.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.CreatedUtc).Take(McpRules.Limit(limit, 50, 200))
            .Select(b => new { id = b.Id, app = b.AppSlug, name = b.AppName, kind = b.Kind, status = b.Status, created = b.CreatedUtc, sizeBytes = b.SizeBytes, consistent = b.Consistent, target = b.TargetName, note = b.Note, error = b.Error });
        return Write(list);
    }

    [McpServerTool(Name = "list_backup_schedules", Title = "List the backup schedules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The recurring backups: which app, how often, at what time, how many are kept, and how the last run went.")]
    public async Task<string> ListBackupSchedules()
    {
        Refuse(McpRules.AdminProblem(Caller));
        var schedules = await Service<BackupHub>().ListSchedulesAsync(Ct);
        return Write(schedules.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            app = s.AppSlug,
            frequency = s.Frequency,
            time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(s.MinuteOfDay)).ToString("HH:mm"),
            weekday = s.Frequency == BackupFrequency.Weekly ? s.Weekday.ToString() : null,
            keep = s.Retention,
            stopContainers = s.StopContainers,
            enabled = s.Enabled,
            lastStatus = s.LastStatus,
        }));
    }

    [McpServerTool(Name = "get_tasks", Title = "The Task Manager", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What runs on the computer now, as the Task Manager shows it: the host (CPU, memory, disk) and every container grouped by app, with CPU and memory.")]
    public async Task<string> GetTasks([Description("The id of a node (Settings, Nodes); this machine when left out.")] long? node = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var snapshot = await Service<TaskManagerService>().ReadAsync(Ct, node);
        return Write(new
        {
            at = snapshot.AtUtc,
            measured = snapshot.Measured,
            error = snapshot.Error,
            running = snapshot.Running,
            total = snapshot.Total,
            host = snapshot.Host,
            groups = snapshot.Groups.Select(g => new
            {
                name = g.Name,
                section = g.Section,
                app = g.App?.Slug,
                running = g.Running,
                total = g.Total,
                cpuPercent = g.Cpu,
                memoryBytes = g.MemoryBytes,
                containers = g.Rows.Select(r => new { name = r.Container.Name, image = r.Container.Image, state = r.Container.State, status = r.Container.Status, cpuPercent = r.Task.CpuPercent, memoryBytes = r.Task.MemoryBytes }),
            }),
        });
    }

    [McpServerTool(Name = "list_volumes", Title = "List the volumes", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("The Docker volumes MatDock knows: the app each belongs to, the containers that use it, its kind (local, SMB, NFS) and, when asked, its size.")]
    public async Task<string> ListVolumes([Description("Also measure the sizes (slower).")] bool sizes = false)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var volumes = Service<VolumeService>();
        if (!await volumes.IsAvailableAsync(Ct))
        {
            throw new McpException("MatDock does not answer, or it is too old for volumes: update it.");
        }

        var list = await volumes.ListAsync(sizes, Ct);
        return Write(list.Select(v => new
        {
            name = v.Name,
            kind = v.KindLabel,
            description = v.Description,
            app = v.Owner?.Slug,
            users = v.Users,
            containers = v.Containers,
            sizeBytes = v.SizeBytes,
            address = v.IsShare ? v.Address : null,
            readOnly = v.ReadOnly,
            ours = v.Ours,
        }));
    }

    [McpServerTool(Name = "list_engines", Title = "The engines", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("The five engines MatOS stands on (MatDock, Matcad, Matddns, MatIdentity, MatSMTP): installed or not, their version and API version as they answer, what each one owns.")]
    public async Task<string> ListEngines()
    {
        Refuse(McpRules.AdminProblem(Caller));
        var rows = await Service<EngineOverview>().ListAsync(Http.Request, askApi: true, Ct);
        return Write(rows.Select(r => new
        {
            id = r.Catalog.Id,
            name = r.Catalog.Name,
            installed = r.Installed,
            lifecycle = r.App?.Lifecycle,
            answers = r.Status is not null,
            version = r.Status?.Version,
            apiVersion = r.Status?.ApiVersion,
            address = r.OpenUrl,
            owns = r.Owns,
        }));
    }

    [McpServerTool(Name = "get_update_state", Title = "The update state", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The version of MatOS, the last check for a newer one, the automatic updates (mode and night window) and the updates of this computer, as MatOS last saw them.")]
    public async Task<string> GetUpdateState()
    {
        Refuse(McpRules.AdminProblem(Caller));
        var updates = (await Service<SystemConfigService>().GetAsync(Ct)).Updates;
        return Write(new
        {
            version = AppVersion.Current,
            check = updates.Check,
            restarting = updates.Pending is not null,
            last = updates.Last,
            automatic = new { mode = updates.EffectiveMode, label = UpdateMode.Label(updates.Mode), window = updates.WindowStart.ToString("HH:mm"), backupFirst = updates.BackupFirst, lastRunAt = updates.LastRunAt, lastStatus = updates.LastStatus, thisComputer = updates.HostUpdates, restartComputer = updates.HostRestart },
            thisComputer = new { reach = updates.Host.Reach, supported = updates.Host.Supported, os = updates.Host.Os, checkedAt = updates.Host.CheckedAt, pending = updates.Host.Pending, readAt = updates.Host.ReadAt },
        });
    }

    // ------------------------------------------------------------------ write

    [McpServerTool(Name = "install_app", Title = "Install an app", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Installs an app of the store (its id from search_store). Answers at once with the job id; follow it with get_job. The required questions of the app (asks in search_store) go into values; the rest has defaults.")]
    public async Task<string> InstallApp(
        [Description("The id of the app in the store (search_store).")] string catalogId,
        [Description("The name it gets in MatOS; the name of the store when left out.")] string? name = null,
        [Description("The answers to the app's questions, by their key (search_store, asks).")] Dictionary<string, string?>? values = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var result = await _apps.PrepareInstallAsync(new InstallRequest(catalogId?.Trim() ?? string.Empty, null, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), values ?? []), Ct);
        if (!result.Succeeded)
        {
            throw new McpException("The app cannot be installed: " + string.Join(" ", result.Errors));
        }

        var app = result.App!;
        var jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), Ct);
        return Started(jobId, app, $"Installing {app.Name}.");
    }

    [McpServerTool(Name = "start_app", Title = "Start an app", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts the containers of an app. Answers with the job id; follow it with get_job.")]
    public Task<string> StartApp([Description("The app: its name (slug) from list_apps.")] string app) => LifecycleAsync(app, "start");

    [McpServerTool(Name = "stop_app", Title = "Stop an app", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops the containers of an app (its data stays). Answers with the job id; follow it with get_job.")]
    public Task<string> StopApp([Description("The app: its name (slug) from list_apps.")] string app) => LifecycleAsync(app, "stop");

    [McpServerTool(Name = "restart_app", Title = "Restart an app", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Restarts the containers of an app. Answers with the job id; follow it with get_job.")]
    public Task<string> RestartApp([Description("The app: its name (slug) from list_apps.")] string app) => LifecycleAsync(app, "restart");

    [McpServerTool(Name = "update_app", Title = "Update an app", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Updates an app to the newest images of its tags, with a backup first unless told not to. Answers with the job id; follow it with get_job.")]
    public async Task<string> UpdateApp(
        [Description("The app: its name (slug) from list_apps.")] string app,
        [Description("Back the app up before the update (true when left out).")] bool backupFirst = true)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var found = await FindAsync(app, forUser: false);
        if (found.Lifecycle != AppLifecycle.Installed)
        {
            throw new McpException($"{found.Name} is not installed (it is {found.Lifecycle}).");
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Update, $"Update {found.Name}", found, AppJobs.Update(found.Id, backupFirst), Ct);
        return Started(jobId, found, $"Updating {found.Name}.");
    }

    [McpServerTool(Name = "uninstall_app", Title = "Uninstall an app", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Removes an app. Its data stays unless deleteData is true (then a backup is made first unless told not to). An engine cannot be removed. Needs confirm=true: ask the person first.")]
    public async Task<string> UninstallApp(
        [Description("The app: its name (slug) from list_apps.")] string app,
        [Description("Set to true only after the person agreed to remove the app.")] bool confirm = false,
        [Description("Also delete its volumes (its data). False when left out: installing it again picks the data up.")] bool deleteData = false,
        [Description("With deleteData: back it up first (true when left out).")] bool backupFirst = true)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var found = await FindAsync(app, forUser: false);
        if (McpRules.CannotUninstall(found))
        {
            throw new McpException($"{found.Name} is an engine of MatOS: it stays.");
        }

        Refuse(McpRules.ConfirmProblem(confirm, deleteData ? $"removes {found.Name} and deletes its data" : $"removes {found.Name} (its data stays)"));
        var jobId = await _jobs.EnqueueAsync(JobKind.Uninstall, $"Uninstall {found.Name}", found, AppJobs.Uninstall(found.Id, deleteData, deleteData && backupFirst), Ct);
        return Started(jobId, found, $"Removing {found.Name}.");
    }

    [McpServerTool(Name = "backup_app", Title = "Back up an app", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Makes a backup of an app now (on this machine, where MatDock keeps them). Answers with the job id; follow it with get_job.")]
    public async Task<string> BackupApp(
        [Description("The app: its name (slug) from list_apps.")] string app,
        [Description("Stop the app while it is copied, for a consistent copy of a database (false when left out).")] bool stopContainers = false,
        [Description("A note that goes with the backup.")] string? note = null)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var found = await FindAsync(app, forUser: false);
        if (found.Lifecycle != AppLifecycle.Installed)
        {
            throw new McpException($"{found.Name} is not installed (it is {found.Lifecycle}).");
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Backup, $"Back up {found.Name}", found,
            AppJobs.Backup(found.Id, BackupKind.Manual, stopContainers, string.IsNullOrWhiteSpace(note) ? null : note.Trim()), Ct);
        return Started(jobId, found, $"Backing up {found.Name}.");
    }

    [McpServerTool(Name = "restore_backup", Title = "Restore a backup", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Puts an app back to a backup (its id from list_backups): its current data is replaced. A backup of the current state is made first unless told not to. Needs confirm=true: ask the person first.")]
    public async Task<string> RestoreBackup(
        [Description("The id of the backup (list_backups).")] string backupId,
        [Description("Set to true only after the person agreed to replace the app's data.")] bool confirm = false,
        [Description("Back up the current state first (true when left out).")] bool backupCurrentFirst = true)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var backup = await Service<BackupHub>().GetAsync(backupId ?? string.Empty, Ct);
        if (backup is null || backup.Status != BackupStatus.Succeeded)
        {
            throw new McpException("There is no finished backup with this id (list_backups).");
        }

        var app = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == backup.AppSlug, Ct);
        Refuse(McpRules.ConfirmProblem(confirm, $"replaces the data of {backup.AppName} with the backup of {backup.CreatedUtc:yyyy-MM-dd HH:mm} UTC"));
        var jobId = await _jobs.EnqueueAsync(JobKind.Restore, $"Restore {backup.AppName} from {backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}", app,
            AppJobs.Restore(backup.Id, app is not null && backupCurrentFirst), Ct);
        return Write(new { jobId, app = backup.AppSlug, message = $"Restoring {backup.AppName}. Follow the job with get_job." });
    }

    [McpServerTool(Name = "check_for_updates", Title = "Check for updates", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Asks the registry now whether a newer MatOS is there, and with thisComputer also queues the check of the updates of this computer (through MatDock; its job id is returned).")]
    public async Task<string> CheckForUpdates([Description("Also check the updates of the operating system of this computer.")] bool thisComputer = false)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var check = await Service<SelfUpdateService>().CheckAsync(Ct);
        long? hostJob = null;
        if (thisComputer)
        {
            var busy = await _db.Jobs.AnyAsync(j => (j.Title == HostUpdateService.InstallJobTitle || j.Title == HostUpdateService.CheckJobTitle)
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), Ct);
            if (!busy)
            {
                hostJob = await _jobs.EnqueueAsync(JobKind.Update, HostUpdateService.CheckJobTitle, null, HostUpdateService.CheckJob(), Ct);
            }
        }

        return Write(new
        {
            version = AppVersion.Current,
            available = check.Available,
            downloaded = check.Downloaded,
            image = check.Image,
            error = check.Error,
            message = check.Error is { } error ? $"MatOS could not check for a newer version: {error}" : check.Available ? "A newer MatOS is there (Settings, Updates installs it)." : "MatOS is up to date.",
            thisComputerJobId = hostJob,
        });
    }

    // ------------------------------------------------------------------ helpers

    private async Task<string> LifecycleAsync(string app, string command)
    {
        Refuse(McpRules.AdminProblem(Caller));
        var kind = McpRules.LifecycleKind(command) ?? throw new McpException($"Unknown action {command}: start, stop or restart.");
        var found = await FindAsync(app, forUser: false);
        var jobId = await _jobs.EnqueueAsync(kind, $"{kind} {found.Name}", found, AppJobs.Lifecycle(found.Id, command), Ct);
        return Started(jobId, found, $"{kind} {found.Name}: queued.");
    }

    /// <summary>The apps the caller may see: all for an administrator, those MatIdentity lets a user use (all when it cannot say, as the page Apps).</summary>
    private async Task<List<InstalledApp>> VisibleAppsAsync()
    {
        Refuse(McpRules.PersonProblem(Caller));
        var apps = await _apps.ListAsync(Ct);
        if (McpRules.RoleOf(Caller) == UserRole.Admin)
        {
            return apps;
        }

        var person = await Service<PersonAccess>().ForUserAsync(Caller.GetUserId(), Ct);
        return apps.Where(a => person is null || person.MayUse(a)).ToList();
    }

    /// <summary>An app by its slug or id; with <paramref name="forUser"/> also for a user who may use it.</summary>
    private async Task<InstalledApp> FindAsync(string? app, bool forUser)
    {
        var key = app?.Trim() ?? string.Empty;
        var apps = forUser ? await VisibleAppsAsync() : await _apps.ListAsync(Ct);
        return apps.FirstOrDefault(a => a.Slug.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (long.TryParse(key, out var id) ? apps.FirstOrDefault(a => a.Id == id) : null)
            ?? throw new McpException($"There is no app '{key}'. list_apps names them.");
    }

    private static string Started(long jobId, InstalledApp app, string message)
        => Write(new { jobId, app = app.Slug, message = message + " Follow the job with get_job." });

    private static string Write(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>A problem of <see cref="McpRules"/> becomes an error of the tool (its message reaches the client); null goes on.</summary>
    private static void Refuse(string? problem)
    {
        if (problem is not null)
        {
            throw new McpException(problem);
        }
    }
}
