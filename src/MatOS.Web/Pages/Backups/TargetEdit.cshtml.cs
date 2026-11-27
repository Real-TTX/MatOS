using System.ComponentModel.DataAnnotations;
using MatOS.Core.Backups;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Backups;

/// <summary>Creates or changes a backup target, tests it and deletes it. A password or key is never shown again: blank keeps the stored one.</summary>
public class TargetEditModel : PageModel
{
    private readonly BackupTargetService _targets;

    public TargetEditModel(BackupTargetService targets)
    {
        _targets = targets;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    /// <summary>The saved target (on an edit): whether a password or key is stored, its host key, how many backups it holds.</summary>
    public BackupTarget? Stored { get; private set; }

    public string? TestMessage { get; private set; }

    public bool TestOk { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(200, ErrorMessage = "The name is longer than 200 characters.")]
        public string Name { get; set; } = string.Empty;

        public BackupTargetKind Kind { get; set; } = BackupTargetKind.Sftp;

        public bool IsDefault { get; set; }

        public string? Host { get; set; }

        [Range(1, 65535, ErrorMessage = "The port is a number from 1 to 65535.")]
        public int? Port { get; set; }

        public string? Share { get; set; }

        public string? Folder { get; set; }

        public string? Url { get; set; }

        public string? Username { get; set; }

        public string? Domain { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        public string? PrivateKey { get; set; }

        public bool ResetHostKey { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            Stored = await _targets.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Stored is null || !Stored.Ours)
            {
                return NotFound();
            }

            Input = new InputModel
            {
                Id = Stored.Id,
                Name = Stored.Name,
                Kind = Stored.Kind,
                IsDefault = Stored.IsDefault,
                Host = Stored.Host,
                Port = Stored.Port,
                Share = Stored.Share,
                Folder = Stored.Folder,
                Url = Stored.Url,
                Username = Stored.Username,
                Domain = Stored.Domain,
            };
        }
        else if (!await _targets.IsAvailableAsync(HttpContext.RequestAborted))
        {
            StatusMessage = "Targets cannot be used right now: the backup engine does not answer.";
            IsError = true;
            return RedirectToPage("/Backups/Targets");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await CheckAsync())
        {
            return Page();
        }

        var (id, error) = await _targets.SaveAsync(ToInput(), HttpContext.RequestAborted);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }

        // A new target is tested at once: the first connection to an SFTP server remembers its key.
        var (ok, message) = await _targets.TestAsync(id!.Value, HttpContext.RequestAborted);
        StatusMessage = ok ? "The target was saved and the test file was written." : $"The target was saved, but the test failed: {message}";
        IsError = !ok;
        return ok ? RedirectToPage("/Backups/Targets") : RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await CheckAsync())
        {
            return Page();
        }

        (TestOk, TestMessage) = await _targets.TestAsync(ToInput(), HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Input.Id is not > 0)
        {
            return RedirectToPage("/Backups/Targets");
        }

        var error = await _targets.DeleteAsync(Input.Id.Value, HttpContext.RequestAborted);
        StatusMessage = error is null ? "The target was deleted." : $"The target was not deleted: {error}";
        IsError = error is not null;
        return error is null ? RedirectToPage("/Backups/Targets") : RedirectToPage(new { id = Input.Id });
    }

    /// <summary>The attributes first, then what the kind needs; an edit loads what is stored (the kind stays, a blank secret is kept).</summary>
    private async Task<bool> CheckAsync()
    {
        if (IsEdit)
        {
            Stored = await _targets.GetAsync(Input.Id!.Value, HttpContext.RequestAborted);
            if (Stored is null || !Stored.Ours)
            {
                ModelState.AddModelError(string.Empty, "The target does not exist any more.");
                return false;
            }

            Input.Kind = Stored.Kind;
        }

        if (!Enum.IsDefined(Input.Kind) || Input.Kind == BackupTargetKind.Restic)
        {
            ModelState.AddModelError("Input.Kind", "Choose SFTP, WebDAV or a network share.");
        }

        if (ModelState.IsValid && BackupTargetService.Validate(ToInput(), Stored is { HasPassword: true } or { HasPrivateKey: true }, IsEdit) is { } problem)
        {
            ModelState.AddModelError(string.Empty, problem);
        }

        return ModelState.IsValid;
    }

    private BackupTargetInput ToInput() => new()
    {
        Id = IsEdit ? Input.Id : null,
        Name = Input.Name,
        Kind = Input.Kind,
        IsDefault = Input.IsDefault,
        Host = Input.Host,
        Port = Input.Port,
        Share = Input.Share,
        Folder = Input.Folder,
        Url = Input.Url,
        Username = Input.Username,
        Domain = Input.Domain,
        Password = Input.Password,
        PrivateKey = Input.PrivateKey,
        ResetHostKey = Input.ResetHostKey,
    };
}
