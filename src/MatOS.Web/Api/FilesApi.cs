using MatOS.Web.Docker;

namespace MatOS.Web.Api;

/// <summary>Volume file browser API (Admin-only). Powers the File Explorer app.</summary>
public static class FilesApi
{
    public record PathBody(string Path);
    public record WriteBody(string Path, string Content);
    public record RenameBody(string Path, string NewName);
    public record TransferBody(string SrcVolume, string SrcPath, string DstVolume, string DstDir);
    public record CreateVolBody(string Name, string Kind, string? Server, string? Share, string? Username, string? Password, string? Options);
    public record VolNameBody(string Name);

    public static void MapFilesApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/files").RequireAuthorization("Admin");

        g.MapGet("/{volume}/list", (string volume, string? path, VolumeFilesService svc) =>
            Run(() => Results.Ok(new { path = path ?? "", entries = svc.List(volume, path) })));

        g.MapGet("/{volume}/read", (string volume, string path, VolumeFilesService svc) =>
            Run(() => { var r = svc.ReadText(volume, path); return Results.Ok(new { r.Text, r.Binary, r.Truncated, r.Size }); }));

        g.MapGet("/{volume}/download", (string volume, string path, VolumeFilesService svc) =>
            Run(() => { var (p, name) = svc.FilePath(volume, path); return Results.File(p, "application/octet-stream", name); }));

        g.MapGet("/{volume}/view", (string volume, string path, VolumeFilesService svc) =>
            Run(() => { var (p, ct) = svc.ViewFile(volume, path); return Results.File(p, contentType: ct, enableRangeProcessing: true); }));

        // ---- Volume management (create local / SMB, remove) ----
        g.MapPost("/volumes/create", async (CreateVolBody b, DockerService docker) =>
        {
            var name = (b.Name ?? "").Trim();
            if (name.Length == 0 || !name.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                return Results.BadRequest(new { error = "Invalid volume name (letters, digits, _ - . only)." });
            try
            {
                Dictionary<string, string>? opts = null;
                if (string.Equals(b.Kind, "smb", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(b.Server) || string.IsNullOrWhiteSpace(b.Share))
                        return Results.BadRequest(new { error = "SMB volumes need a server and share." });
                    var device = "//" + b.Server.Trim().TrimStart('/') + "/" + b.Share.Trim().Trim('/');
                    var o = new List<string>();
                    if (!string.IsNullOrWhiteSpace(b.Username)) { o.Add("username=" + b.Username.Trim()); o.Add("password=" + (b.Password ?? "")); }
                    else o.Add("guest");
                    o.Add("vers=3.0"); o.Add("file_mode=0777"); o.Add("dir_mode=0777");
                    if (!string.IsNullOrWhiteSpace(b.Options)) o.Add(b.Options.Trim());
                    opts = new Dictionary<string, string> { ["type"] = "cifs", ["device"] = device, ["o"] = string.Join(",", o) };
                }
                await docker.CreateVolumeAsync(name, "local", opts);
                return Results.Ok(new { ok = true, name });
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        g.MapPost("/volumes/remove", async (VolNameBody b, DockerService docker) =>
        {
            try { await docker.RemoveVolumeAsync(b.Name); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

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
