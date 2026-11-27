using System.ComponentModel.DataAnnotations;
using MatOS.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin.Users;

/// <summary>A new account, or a change of one (name, e-mail address, a new password).</summary>
public sealed class EditModel(AdminAccounts accounts) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => Input.Id is > 0;

    public bool IsSelf => IsEdit && Input.Id == User.UserId();

    public sealed class InputModel
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Please enter an e-mail address.")]
        [StringLength(256)]
        [EmailAddress(ErrorMessage = "Please enter an e-mail address.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(256)]
        public string Name { get; set; } = "";

        [MinLength(AdminAccounts.MinPasswordLength, ErrorMessage = "The password must be at least 8 characters.")]
        [StringLength(256)]
        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string? ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(long? id)
    {
        if (id is > 0)
        {
            var user = await accounts.FindAsync(id.Value, HttpContext.RequestAborted);
            if (user is null)
            {
                return NotFound();
            }

            Input = new InputModel { Id = user.Id, Email = user.Email, Name = user.Name };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!IsEdit && string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Please set a password.");
        }

        if (ModelState.IsValid && await accounts.EmailTakenAsync(Input.Email, Input.Id, ct))
        {
            ModelState.AddModelError("Input.Email", "Another account has this e-mail address.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (IsEdit)
        {
            var user = await accounts.FindAsync(Input.Id!.Value, ct);
            if (user is null)
            {
                return NotFound();
            }

            await accounts.UpdateAsync(user, Input.Email, Input.Name, Input.Password, User.SessionToken(), ct);
            if (IsSelf)
            {
                await AdminAuth.RefreshAsync(HttpContext, user);
            }

            TempData["StatusMessage"] = string.IsNullOrEmpty(Input.Password) ? "User saved." : "User saved. The new password ended the other sessions of the account.";
        }
        else
        {
            await accounts.CreateAsync(Input.Email, Input.Name, Input.Password!, ct);
            TempData["StatusMessage"] = "User created.";
        }

        return RedirectToPage("/Admin/Users/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit || IsSelf)
        {
            TempData["StatusMessage"] = "You cannot delete your own account.";
            TempData["IsError"] = true;
            return RedirectToPage("/Admin/Users/Index");
        }

        var deleted = await accounts.DeleteAsync(Input.Id!.Value, HttpContext.RequestAborted);
        TempData["StatusMessage"] = deleted ? "User deleted." : "User not found.";
        TempData["IsError"] = !deleted;
        return RedirectToPage("/Admin/Users/Index");
    }
}
