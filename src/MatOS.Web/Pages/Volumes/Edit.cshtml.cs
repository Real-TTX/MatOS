using System.ComponentModel.DataAnnotations;
using MatOS.Core.Entities;
using MatOS.Core.Volumes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Volumes;

/// <summary>
/// Makes a volume of MatOS (on this machine, an SMB share or an NFS share), changes a share, tests it and removes it. A share is tested before it is saved; its
/// password is never shown again (a blank field keeps it). A volume on this machine keeps its settings: its data lies in it.
/// </summary>
public class EditModel : PageModel
{
    private readonly VolumeService _volumes;

    public EditModel(VolumeService volumes)
    {
        _volumes = volumes;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>The name of the saved volume on an edit (from the address); null for a new one.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Name { get; set; }

    public bool IsEdit => !string.IsNullOrEmpty(Name);

    /// <summary>The saved volume on an edit: whether a password is stored, who uses it, how big it is.</summary>
    public VolumeView? Stored { get; private set; }

    /// <summary>The apps that keep a data folder on it.</summary>
    public List<InstalledApp> Choosing { get; private set; } = [];

    public string? TestMessage { get; private set; }

    public bool TestOk { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        [StringLength(63, ErrorMessage = "The name is at most 63 characters.")]
        public string? Name { get; set; }

        public VolumeKind Kind { get; set; } = VolumeKind.Local;

        [StringLength(200, ErrorMessage = "The description is at most 200 characters.")]
        public string? Description { get; set; }

        public string? Host { get; set; }

        public string? Share { get; set; }

        public string? Path { get; set; }

        public string? Username { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        public string? Domain { get; set; }

        public string? Version { get; set; }

        public bool ReadOnly { get; set; }

        public string? Options { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await _volumes.IsAvailableAsync(HttpContext.RequestAborted))
        {
            StatusMessage = "Volumes cannot be used right now: MatDock does not answer or is not up to date.";
            IsError = true;
            return RedirectToPage("/Volumes/Index");
        }

        if (IsEdit)
        {
            if (!await LoadAsync())
            {
                return NotFound();
            }

            var s = Stored!;
            Input = new InputModel
            {
                Name = s.Name,
                Kind = s.Kind,
                Description = s.Description,
                Host = s.Host,
                Share = s.Share,
                Path = s.Path,
                Username = s.Username,
                Domain = s.Domain,
                Version = s.Version,
                ReadOnly = s.ReadOnly,
                Options = s.Options,
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await CheckAsync())
        {
            return Page();
        }

        var ct = HttpContext.RequestAborted;
        if (IsEdit)
        {
            if (!Stored!.IsShare)
            {
                ModelState.AddModelError(string.Empty, "A volume on this machine keeps its settings: its data lies in it.");
                return Page();
            }

            // The share is tested as it will be, before the old one goes.
            var (works, why) = await _volumes.TestAsync(ToInput(), isEdit: true, ct);
            if (!works)
            {
                (TestOk, TestMessage) = (false, why);
                return Page();
            }

            var error = await _volumes.ChangeAsync(ToInput(), ct);
            if (error is not null)
            {
                ModelState.AddModelError(string.Empty, error);
                return Page();
            }

            StatusMessage = $"The share {Name} was changed. {why}";
            return RedirectToPage("/Volumes/Index");
        }

        if (Input.Kind != VolumeKind.Local)
        {
            var (works, why) = await _volumes.TestAsync(ToInput(), isEdit: false, ct);
            if (!works)
            {
                (TestOk, TestMessage) = (false, why);
                return Page();
            }

            StatusMessage = $"The volume {Input.Name!.Trim()} was made. {why}";
        }
        else
        {
            StatusMessage = $"The volume {Input.Name!.Trim()} was made on this machine.";
        }

        var created = await _volumes.CreateAsync(ToInput(), ct);
        if (created is not null)
        {
            StatusMessage = null;
            ModelState.AddModelError(string.Empty, created);
            return Page();
        }

        return RedirectToPage("/Volumes/Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await CheckAsync())
        {
            return Page();
        }

        (TestOk, TestMessage) = await _volumes.TestAsync(ToInput(), IsEdit, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return RedirectToPage("/Volumes/Index");
        }

        var error = await _volumes.DeleteAsync(Name!, HttpContext.RequestAborted);
        StatusMessage = error is null ? $"The volume {Name} was removed." : $"The volume {Name} was not removed: {error}";
        IsError = error is not null;
        return error is null ? RedirectToPage("/Volumes/Index") : RedirectToPage(new { name = Name });
    }

    /// <summary>The saved volume of an edit (only one of MatOS's own) and the apps that chose it.</summary>
    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            Stored = await _volumes.GetAsync(Name!, size: true, ct);
        }
        catch (InvalidOperationException)
        {
            Stored = null;
        }

        if (Stored is null || !Stored.Ours)
        {
            return false;
        }

        Choosing = await _volumes.AppsChoosingAsync(Stored.Name, ct);
        return true;
    }

    /// <summary>The attributes, then what the kind needs; an edit loads what is stored (the name and the kind stay).</summary>
    private async Task<bool> CheckAsync()
    {
        if (IsEdit)
        {
            if (!await LoadAsync())
            {
                ModelState.AddModelError(string.Empty, "The volume does not exist any more.");
                return false;
            }

            Input.Name = Stored!.Name;
            Input.Kind = Stored.Kind;
        }
        else if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", "Please enter a name.");
        }

        if (!Enum.IsDefined(Input.Kind) || Input.Kind == VolumeKind.Other)
        {
            ModelState.AddModelError("Input.Kind", "Choose this machine, an SMB share or an NFS share.");
        }

        if (ModelState.IsValid && VolumeService.Validate(ToInput(), IsEdit) is { } problem)
        {
            ModelState.AddModelError(string.Empty, problem);
        }

        return ModelState.IsValid;
    }

    private VolumeInput ToInput() => new()
    {
        Name = (IsEdit ? Name : Input.Name)?.Trim() ?? string.Empty,
        Kind = Input.Kind,
        Description = Input.Description,
        Host = Input.Host,
        Share = Input.Share,
        Path = Input.Path,
        Username = Input.Username,
        Password = Input.Password,
        Domain = Input.Domain,
        Version = Input.Version,
        ReadOnly = Input.ReadOnly,
        Options = Input.Options,
    };
}
