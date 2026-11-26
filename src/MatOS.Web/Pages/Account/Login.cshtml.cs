using System.ComponentModel.DataAnnotations;
using MatOS.Core.Auth;
using MatOS.Core.Configuration;
using MatOS.Core.Integrations;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AuthService _authService;
    private readonly SessionService _sessionService;
    private readonly SystemConfigService _config;
    private readonly IdentityLink _identity;

    public LoginModel(AuthService authService, SessionService sessionService, SystemConfigService config, IdentityLink identity)
    {
        _config = config;
        _identity = identity;
        _authService = authService;
        _sessionService = sessionService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>The local form even when MatIdentity signs people in (the way in for the administrator when MatIdentity is down).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Local { get; set; }

    /// <summary>MatOS registered itself again in MatIdentity on the way here (its address had changed): no second try.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Reconnected { get; set; }

    /// <summary><c>?local=1</c> (or <c>true</c>), as the platform contract writes it.</summary>
    public bool WantsLocal => Local is "1" or "true" or "True";

    /// <summary>MatIdentity is connected: the page offers it (and, without <see cref="Local"/>, goes there right away).</summary>
    public bool IdentityAvailable { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your username.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Stay signed in")]
        public bool RememberMe { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(SafeReturnUrl());
        }

        var identity = (await _config.GetAsync(HttpContext.RequestAborted)).Identity;
        IdentityAvailable = identity is not null;
        if (identity is null || WantsLocal)
        {
            return Page();
        }

        // Auto login: straight to MatIdentity. An address of MatOS that MatIdentity does not know yet is registered first.
        var origin = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        if (!identity.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _identity.ConnectSelfAsync(origin, _ => { }, HttpContext.RequestAborted);
            }
            catch (InvalidOperationException)
            {
                ErrorMessage = "MatIdentity does not answer. Sign in with a local account.";
                return Page();
            }
        }

        MatIdentitySignIn.Refresh(HttpContext.RequestServices);
        if (!await MatIdentitySignIn.ReadyAsync(HttpContext))
        {
            // MatIdentity may have another address now (an update, another address of this machine): MatOS registers itself again while MatIdentity
            // answers, and the page comes once more (the sign-in of this request was set up with the old settings). A MatIdentity that does not answer leaves
            // the settings alone, and the local sign-in is the way in.
            var again = false;
            if (Reconnected is null)
            {
                try
                {
                    again = await _identity.ReconnectSelfAsync(origin, HttpContext.RequestAborted);
                }
                catch (InvalidOperationException)
                {
                    // nothing more to try
                }
            }

            if (again && (await _config.GetAsync(HttpContext.RequestAborted)).Identity is not null)
            {
                MatIdentitySignIn.Refresh(HttpContext.RequestServices);
                return LocalRedirect("/Account/Login?reconnected=1&returnUrl=" + Uri.EscapeDataString(SafeReturnUrl()));
            }

            ErrorMessage = "MatIdentity does not answer. Sign in with a local account.";
            IdentityAvailable = (await _config.GetAsync(HttpContext.RequestAborted)).Identity is not null;
            return Page();
        }

        return Challenge(new AuthenticationProperties { RedirectUri = "/Account/External?returnUrl=" + Uri.EscapeDataString(SafeReturnUrl()) }, MatIdentitySignIn.Scheme);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        IdentityAvailable = (await _config.GetAsync(HttpContext.RequestAborted)).Identity is not null;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _authService.AuthenticateAsync(Input.Username, Input.Password, HttpContext.RequestAborted);
        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = result.Error ?? "Sign-in failed.";
            return Page();
        }

        var session = await _sessionService.CreateAsync(
            result.User.Id,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted);

        var principal = PrincipalFactory.Build(result.User, session.Token);
        await HttpContext.SignInAsync(
            AuthConstants.CookieScheme,
            principal,
            new AuthenticationProperties { IsPersistent = Input.RememberMe });

        // Forced password change lands on the profile page via the guard middleware.
        return LocalRedirect(SafeReturnUrl());
    }

    private string SafeReturnUrl()
        => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
