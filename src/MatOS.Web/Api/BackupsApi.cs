using MatOS.Web.Config;
using MatOS.Web.Docker;
using MatOS.Web.Services;

namespace MatOS.Web.Api;

/// <summary>Built-in backup engine (Admin only). Each backup is one .tar.gz file of a source
/// volume's contents, written into a target volume (which may be a normal local volume or an
/// SMB-mounted one for off-host storage).</summary>
public static class BackupsApi
{
    public record CreateBody(string SourceVolume, string? TargetVolume);
    public record RestoreBody(string TargetVolume, string FileName, string RestoreInto);
    public record DeleteBody(string TargetVolume, string FileName);
    public record DownloadQuery(string Volume, string File);
    public record ScheduleBody(string Id, string Name, List<string>? SourceVolumes, string? SourceVolume, string TargetVolume,
        string Kind, string Time, int Weekday, int RetentionDays, bool Enabled);
    public record IdBody(string Id);

    public static void MapBackupsApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/backups").RequireAuthorization("Admin");

        g.MapGet("/list", async (BackupService svc, DockerService docker, CancellationToken ct) =>
        {
            await svc.EnsureDefaultTargetAsync(ct);
            var backups = await svc.ListAsync(ct);
            var vols = await docker.ListVolumesAsync(false, ct);
            return Results.Ok(new
            {
                backups,
                sourceVolumes = vols.Where(v => v.Name != "matos-backups").Select(v => new { v.Name, v.IsSmb, size = v.SizeBytes }),
                targetVolumes = vols.Select(v => new { v.Name, v.IsSmb }),
            });
        });

        g.MapPost("/create", async (CreateBody b, BackupService svc, CancellationToken ct) =>
        {
            try { var r = await svc.CreateAsync(b.SourceVolume, b.TargetVolume, ct); return Results.Ok(new { backup = r }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        g.MapPost("/restore", async (RestoreBody b, BackupService svc, CancellationToken ct) =>
        {
            try { await svc.RestoreAsync(b.TargetVolume, b.FileName, b.RestoreInto, ct); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        g.MapPost("/delete", (DeleteBody b, BackupService svc) =>
        {
            try { svc.Delete(b.TargetVolume, b.FileName); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        g.MapGet("/download", (string volume, string file, BackupService svc, HttpContext http) =>
        {
            var (p, name) = svc.BackupFilePath(volume, file);
            return Results.File(p, "application/gzip", name);
        });

        // ---- Scheduled backups ----
        g.MapGet("/schedules", (JsonConfigService config) =>
        {
            var store = config.Get<BackupScheduleStore>("backup-schedules");
            return Results.Ok(new { schedules = store.Items });
        });

        g.MapPost("/schedules/save", async (ScheduleBody b, JsonConfigService config) =>
        {
            var store = config.Get<BackupScheduleStore>("backup-schedules");
            var s = store.Items.FirstOrDefault(x => x.Id == b.Id);
            if (s == null) { s = new BackupSchedule { Id = "s" + Guid.NewGuid().ToString("N")[..8] }; store.Items.Add(s); }
            // Accept either the new SourceVolumes list, the legacy single SourceVolume, or empty
            // (empty list = ALL volumes at run time).
            s.SourceVolumes = (b.SourceVolumes ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            s.SourceVolume = ""; // stop using the legacy field for new writes
            if (s.SourceVolumes.Count == 0 && !string.IsNullOrWhiteSpace(b.SourceVolume)) s.SourceVolumes.Add(b.SourceVolume!);
            s.Name = string.IsNullOrWhiteSpace(b.Name)
                ? (s.SourceVolumes.Count == 0 ? "Backup all volumes" : $"Backup {s.SourceVolumes[0]}{(s.SourceVolumes.Count>1?" (+"+(s.SourceVolumes.Count-1)+")":"")}")
                : b.Name.Trim();
            s.TargetVolume = string.IsNullOrWhiteSpace(b.TargetVolume) ? "matos-backups" : b.TargetVolume;
            s.Kind = (b.Kind ?? "daily").ToLowerInvariant(); s.Time = b.Time ?? "03:00";
            s.Weekday = Math.Clamp(b.Weekday, 0, 6); s.RetentionDays = Math.Clamp(b.RetentionDays, 0, 3650); s.Enabled = b.Enabled;
            await config.SaveAsync("backup-schedules", store);
            return Results.Ok(new { schedule = s });
        });

        g.MapPost("/schedules/delete", async (IdBody b, JsonConfigService config) =>
        {
            var store = config.Get<BackupScheduleStore>("backup-schedules");
            store.Items.RemoveAll(x => x.Id == b.Id);
            await config.SaveAsync("backup-schedules", store);
            return Results.Ok(new { ok = true });
        });
    }
}
