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
    }
}
