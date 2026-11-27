using MatOS.Core.Volumes;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Volumes;

/// <summary>
/// The file explorer of a volume (docs/ARCHITECTURE.md "Volumes and the file explorer"): its folders and files, a preview of pictures and texts, upload, download,
/// a new folder, rename, move and delete. Everything goes through MatDock, which keeps every path inside the volume (no <c>..</c>, no link out of it); MatOS checks
/// the paths first too. Only administrators (the folder is AdminOnly).
/// </summary>
[RequestSizeLimit(MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
public class FilesModel : PageModel
{
    /// <summary>The most one upload may carry (all its files together).</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The largest picture the preview shows (a bigger one is downloaded).</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    private const int PageSize = 100;
    private static readonly string[] Sorts = ["name", "size"];

    private readonly VolumeService _volumes;

    public FilesModel(VolumeService volumes)
    {
        _volumes = volumes;
    }

    /// <summary>The volume.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Volume { get; set; }

    /// <summary>The folder shown (relative to the root of the volume; empty is the root).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Path { get; set; }

    /// <summary>A file of the folder to preview, or an entry to rename or move (by its name).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Open { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Rename { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Move { get; set; }

    public VolumeView? Info { get; private set; }

    /// <summary>The safe form of <see cref="Path"/>.</summary>
    public string Folder { get; private set; } = string.Empty;

    public ListView<VolumeEntry>? List { get; private set; }

    /// <summary>The file shown in the preview.</summary>
    public VolumeEntry? Preview { get; private set; }

    public string? PreviewText { get; private set; }

    public bool PreviewTruncated { get; private set; }

    /// <summary>The entry being renamed or moved.</summary>
    public VolumeEntry? Target { get; private set; }

    /// <summary>The folders a moved entry can go to (the root and the folders of this one, without itself).</summary>
    public List<string> MoveTargets { get; private set; } = [];

    /// <summary>Why the folder could not be read.</summary>
    public string? Problem { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!await _volumes.IsAvailableAsync(ct))
        {
            StatusMessage = "Volumes cannot be used right now: MatDock does not answer or is not up to date.";
            IsError = true;
            return RedirectToPage("/Volumes/Index");
        }

        if (!VolumePaths.IsVolumeName(Volume) || VolumePaths.Normalize(Path) is not { } folder)
        {
            return NotFound();
        }

        Folder = folder;
        try
        {
            Info = await _volumes.GetAsync(Volume!, size: false, ct);
        }
        catch (InvalidOperationException ex)
        {
            Problem = VolumeService.Reason(ex);
        }

        if (Info is null && Problem is null)
        {
            return NotFound();
        }

        List<VolumeEntry> entries = [];
        if (Problem is null)
        {
            try
            {
                var listed = await _volumes.ListFilesAsync(Volume!, Folder, ct);
                if (listed is null)
                {
                    StatusMessage = "This folder does not exist (any more).";
                    IsError = true;
                    return RedirectToPage(new { volume = Volume, path = VolumePaths.Parent(Folder) });
                }

                entries = listed;
            }
            catch (InvalidOperationException ex)
            {
                Problem = VolumeService.Reason(ex);
            }
        }

        await LoadSelectionAsync(entries, ct);
        BuildList(entries);
        return Page();
    }

    /// <summary>A picture of the volume for the preview (an <c>img</c>): only a picture, only up to <see cref="MaxImageBytes"/>, never run as a page.</summary>
    public async Task<IActionResult> OnGetRawAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!VolumePaths.IsVolumeName(Volume) || VolumePaths.Normalize(Path) is not { Length: > 0 } file || VolumePaths.ImageType(file) is not { } type)
        {
            return NotFound();
        }

        var entry = await StatOrNullAsync(file, ct);
        if (entry is not { IsFolder: false } || entry.Size > MaxImageBytes)
        {
            return NotFound();
        }

        var download = await _volumes.DownloadAsync(Volume!, file, ct);
        HttpContext.Response.RegisterForDispose(download);
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(download.Body, type);
    }

    /// <summary>A file as it is (an attachment, whatever it holds).</summary>
    public async Task<IActionResult> OnGetDownloadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!VolumePaths.IsVolumeName(Volume) || VolumePaths.Normalize(Path) is not { Length: > 0 } file)
        {
            return NotFound();
        }

        var entry = await StatOrNullAsync(file, ct);
        if (entry is not { IsFolder: false })
        {
            StatusMessage = entry is null ? "The file does not exist (any more)." : "A folder cannot be downloaded; open it and download its files.";
            IsError = true;
            return RedirectToPage(new { volume = Volume, path = VolumePaths.Parent(file) });
        }

        var download = await _volumes.DownloadAsync(Volume!, file, ct);
        HttpContext.Response.RegisterForDispose(download);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.ContentLength = download.Length ?? entry.Size;
        return File(download.Body, "application/octet-stream", entry.Name);
    }

    /// <summary>Uploads files into the folder; a file of the same name is replaced only when asked.</summary>
    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files, bool replace)
    {
        var ct = HttpContext.RequestAborted;
        if (!Check(out var folder))
        {
            return NotFound();
        }

        if (files.Count == 0)
        {
            return Done(folder, "Choose the files to upload first.", error: true);
        }

        var done = 0;
        var problems = new List<string>();
        foreach (var file in files)
        {
            var name = VolumePaths.Name(System.IO.Path.GetFileName(file.FileName));
            if (name is null)
            {
                problems.Add($"\"{file.FileName}\" is not a name a file can have");
                continue;
            }

            await using var stream = file.OpenReadStream();
            var error = await _volumes.UploadAsync(Volume!, VolumePaths.Combine(folder, name), stream, file.Length, replace, ct);
            if (error is null)
            {
                done++;
            }
            else
            {
                problems.Add($"{name}: {error}");
            }
        }

        var message = done == 0 ? "Nothing was uploaded." : done == 1 ? "One file was uploaded." : $"{done} files were uploaded.";
        return Done(folder, problems.Count == 0 ? message : $"{message} {string.Join("; ", problems)}.", error: problems.Count > 0);
    }

    public async Task<IActionResult> OnPostFolderAsync(string? name)
    {
        if (!Check(out var folder))
        {
            return NotFound();
        }

        if (VolumePaths.Name(name) is not { } clean)
        {
            return Done(folder, "A name of a folder is one word or more, without / \\ or :.", error: true);
        }

        var error = await _volumes.MakeFolderAsync(Volume!, VolumePaths.Combine(folder, clean), HttpContext.RequestAborted);
        return Done(folder, error is null ? $"The folder {clean} was made." : $"The folder was not made: {error}", error is not null);
    }

    public async Task<IActionResult> OnPostRenameAsync(string? item, string? name)
    {
        if (!Check(out var folder))
        {
            return NotFound();
        }

        if (VolumePaths.Name(item) is not { } from || VolumePaths.Name(name) is not { } to)
        {
            return Done(folder, "A name is one word or more, without / \\ or :.", error: true);
        }

        if (from == to)
        {
            return Done(folder, "The name is the same.", error: false);
        }

        var error = await _volumes.MoveAsync(Volume!, VolumePaths.Combine(folder, from), VolumePaths.Combine(folder, to), HttpContext.RequestAborted);
        return Done(folder, error is null ? $"{from} is called {to} now." : $"{from} was not renamed: {error}", error is not null);
    }

    public async Task<IActionResult> OnPostMoveAsync(string? item, string? to)
    {
        if (!Check(out var folder))
        {
            return NotFound();
        }

        if (VolumePaths.Name(item) is not { } name || VolumePaths.Normalize(to) is not { } target)
        {
            return Done(folder, "Choose what to move and where.", error: true);
        }

        var source = VolumePaths.Combine(folder, name);
        if (VolumePaths.IsInside(target, source))
        {
            return Done(folder, "A folder cannot move into itself.", error: true);
        }

        if (target == folder)
        {
            return Done(folder, $"{name} is in this folder already.", error: false);
        }

        var error = await _volumes.MoveAsync(Volume!, source, VolumePaths.Combine(target, name), HttpContext.RequestAborted);
        return Done(folder, error is null ? $"{name} was moved to {(target.Length == 0 ? "the top" : target)}." : $"{name} was not moved: {error}", error is not null);
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? item)
    {
        if (!Check(out var folder))
        {
            return NotFound();
        }

        if (VolumePaths.Name(item) is not { } name)
        {
            return Done(folder, "Choose what to delete.", error: true);
        }

        var error = await _volumes.DeleteFileAsync(Volume!, VolumePaths.Combine(folder, name), HttpContext.RequestAborted);
        return Done(folder, error is null ? $"{name} was deleted." : $"{name} was not deleted: {error}", error is not null);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The address of a folder of this volume (with the tools of the list kept out).</summary>
    public string FolderUrl(string folder) => Url.Page("/Volumes/Files", new { volume = Volume, path = folder.Length == 0 ? null : folder }) ?? "/Volumes";

    public string EntryUrl(string action, VolumeEntry entry)
        => Url.Page("/Volumes/Files", new Dictionary<string, object?> { ["volume"] = Volume, ["path"] = Folder.Length == 0 ? null : Folder, [action] = entry.Name }) ?? "/Volumes";

    public string FileUrl(string handler, VolumeEntry entry) => Url.Page("/Volumes/Files", handler, new { volume = Volume, path = entry.Path }) ?? "/Volumes";

    private bool Check(out string folder)
    {
        folder = VolumePaths.Normalize(Path) ?? string.Empty;
        return VolumePaths.IsVolumeName(Volume) && VolumePaths.Normalize(Path) is not null;
    }

    private RedirectToPageResult Done(string folder, string message, bool error)
    {
        StatusMessage = message;
        IsError = error;
        return RedirectToPage(new { volume = Volume, path = folder.Length == 0 ? null : folder });
    }

    private async Task<VolumeEntry?> StatOrNullAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _volumes.StatAsync(Volume!, path, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The file of the preview and the entry of a rename or move, from the entries of the folder (a name that is not there is ignored).</summary>
    private async Task LoadSelectionAsync(List<VolumeEntry> entries, CancellationToken ct)
    {
        if (Open is not null && entries.FirstOrDefault(e => e.Name == Open && !e.IsFolder) is { } file)
        {
            Preview = file;
            if (file.Preview == FilePreview.Text)
            {
                try
                {
                    var (text, truncated, binary) = await _volumes.ReadTextAsync(Volume!, file.Path, ct);
                    // A file that is not text after all (a NUL in it) is only offered for download.
                    PreviewText = binary ? null : text;
                    PreviewTruncated = !binary && truncated;
                }
                catch (InvalidOperationException ex)
                {
                    Problem = VolumeService.Reason(ex);
                }
            }
        }

        var chosen = Rename ?? Move;
        if (chosen is not null && entries.FirstOrDefault(e => e.Name == chosen) is { } target)
        {
            Target = target;
            if (Move is not null)
            {
                MoveTargets = [string.Empty];
                if (Folder.Length > 0)
                {
                    MoveTargets.Add(VolumePaths.Parent(Folder));
                }

                MoveTargets.AddRange(entries.Where(e => e.IsFolder && e.Name != target.Name).Select(e => e.Path));
                MoveTargets = MoveTargets.Distinct().Where(t => t != Folder).ToList();
            }
        }
    }

    private void BuildList(List<VolumeEntry> all)
    {
        var q = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is { } s && Sorts.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);
        IEnumerable<VolumeEntry> query = all;
        if (q is not null)
        {
            query = query.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = sort == "size"
            ? (descending ? query.OrderByDescending(e => e.IsFolder ? -1 : e.Size) : query.OrderBy(e => e.IsFolder ? -1 : e.Size))
            : (descending ? query.OrderByDescending(e => e.IsFolder).ThenByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        var list = ordered.ToList();

        var toolbar = new ToolbarModel
        {
            Id = "files",
            Action = "/Volumes/Files",
            Label = "Files",
            Search = q,
            SearchPlaceholder = "Search this folder",
            SearchField = true,
            SortOptions = [new("name", "Name"), new("size", "Size")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "item" : "items",
            Keep = new Dictionary<string, string?> { ["volume"] = Volume, ["path"] = Folder.Length == 0 ? null : Folder },
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<VolumeEntry> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}
