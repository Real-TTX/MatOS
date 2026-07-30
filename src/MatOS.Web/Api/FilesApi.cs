using MatOS.Web.Docker;

namespace MatOS.Web.Api;

/// <summary>Volume file browser API (Admin-only). Powers the File Explorer app.</summary>
public static class FilesApi
{
    public record PathBody(string Path);
    public record WriteBody(string Path, string Content);
    public record RenameBody(string Path, string NewName);
    public record TransferBody(string SrcVolume, string SrcPath, string DstVolume, string DstDir);

    public static void MapFilesApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/files").RequireAuthorization("Admin");

        g.MapGet("/{volume}/list", (string volume, string? path, VolumeFilesService svc) =>
            Run(() => Results.Ok(new { path = path ?? "", entries = svc.List(volume, path) })));

        g.MapGet("/{volume}/read", (string volume, string path, VolumeFilesService svc) =>
            Run(() => { var r = svc.ReadText(volume, path); return Results.Ok(new { r.Text, r.Binary, r.Truncated, r.Size }); }));

        g.MapGet("/{volume}/download", (string volume, string path, VolumeFilesService svc) =>
            Run(() => { var (p, name) = svc.FilePath(volume, path); return Results.File(p, "application/octet-stream", name); }));

        g.MapPost("/{volume}/write", (string volume, WriteBody b, VolumeFilesService svc) =>
            Run(() => { svc.WriteText(volume, b.Path, b.Content); return Results.Ok(new { ok = true }); }));

        g.MapPost("/{volume}/mkdir", (string volume, PathBody b, VolumeFilesService svc) =>
            Run(() => { svc.Mkdir(volume, b.Path); return Results.Ok(new { ok = true }); }));

        g.MapPost("/{volume}/delete", (string volume, PathBody b, VolumeFilesService svc) =>
            Run(() => { svc.Delete(volume, b.Path); return Results.Ok(new { ok = true }); }));

        g.MapPost("/{volume}/rename", (string volume, RenameBody b, VolumeFilesService svc) =>
            Run(() => { svc.Rename(volume, b.Path, b.NewName); return Results.Ok(new { ok = true }); }));

        g.MapPost("/copy", (TransferBody b, VolumeFilesService svc) =>
            Run(() => { svc.Copy(b.SrcVolume, b.SrcPath, b.DstVolume, b.DstDir); return Results.Ok(new { ok = true }); }));

        g.MapPost("/move", (TransferBody b, VolumeFilesService svc) =>
            Run(() => { svc.Move(b.SrcVolume, b.SrcPath, b.DstVolume, b.DstDir); return Results.Ok(new { ok = true }); }));

        g.MapPost("/{volume}/upload", async (string volume, HttpContext http, VolumeFilesService svc) =>
        {
            try
            {
                var form = await http.Request.ReadFormAsync();
                var path = form["path"].ToString();
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "No file." });
                await using var s = file.OpenReadStream();
                await svc.SaveUpload(volume, path, file.FileName, s, http.RequestAborted);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex) { return Map(ex); }
        }).DisableAntiforgery();

        g.MapGet("/{volume}/export", (string volume, VolumeFilesService svc, HttpContext http) =>
        {
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{volume}.tar.gz\"";
            return Results.Stream(stream => svc.ExportAsync(volume, stream, http.RequestAborted), "application/gzip");
        });

        g.MapPost("/{volume}/import", async (string volume, HttpContext http, VolumeFilesService svc) =>
        {
            try
            {
                var form = await http.Request.ReadFormAsync();
                var file = form.Files.GetFile("file");
                if (file is null) return Results.BadRequest(new { error = "No archive." });
                await using var s = file.OpenReadStream();
                await svc.ImportAsync(volume, s, http.RequestAborted);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex) { return Map(ex); }
        }).DisableAntiforgery();
    }

    private static IResult Run(Func<IResult> op)
    {
        try { return op(); }
        catch (Exception ex) { return Map(ex); }
    }

    private static IResult Map(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Results.Json(new { error = ex.Message }, statusCode: 403),
        FileNotFoundException or DirectoryNotFoundException => Results.NotFound(new { error = ex.Message }),
        ArgumentException or InvalidOperationException => Results.BadRequest(new { error = ex.Message }),
        _ => Results.Problem(ex.Message)
    };
}
