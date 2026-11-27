using MatOS.Core.Auth;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, steps 1 and 2 (docs/ARCHITECTURE.md "The first start"): Welcome with the check of Docker in plain words, then the account (name, e-mail as the
/// user name, password). Creating it makes the first administrator, signs them in and keeps them for MatIdentity (<see cref="OwnerHandover"/>), then the engines
/// follow (<c>/Setup/Engines</c>). Reachable only while there is no account (<see cref="SetupGuardMiddleware"/> sends every request here until then).
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly UserService _users;
    private readonly SessionService _sessions;
    private readonly SetupState _state;
    private readonly DockerService _docker;
    private readonly OwnerHandover _owner;
    private readonly SystemConfigService _config;

    public IndexModel(UserService users, SessionService sessions, SetupState state, DockerService docker, OwnerHandover owner, SystemConfigService config)
    {
        _config = config;
        _users = users;
        _sessions = sessions;
        _state = state;
        _docker = docker;
        _owner = owner;
    }

    [BindProperty]
    public AccountInput Input { get; set; } = new();

    public DockerAdvice Docker { get; private set; } = new(false, string.Empty, string.Empty);

    /// <summary>The form came back with a problem: the page opens at the account, not at Welcome.</summary>
    public bool AtAccount { get; private set; }

    public sealed class AccountInput
    {
        public string? Name { get; set; }

        public string? Email { get; set; }

        public string? Password { get; set; }

        public string? ConfirmPassword { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await AlreadyDoneAsync())
        {
            return await ContinueAsync();
        }

        // Before the account cookie exists: a browser that came as localhost goes on at 127.0.0.1, so the cookie is made there and the first start stays there
        // (Docker Desktop may stop answering localhost once MatOS joined its network).
        if (Request.LoopbackOf("/Setup") is { } loopback)
        {
            return Redirect(loopback);
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Never a second administrator through this page once there is an account.
        if (await AlreadyDoneAsync())
        {
            return await ContinueAsync();
        }

        var ct = HttpContext.RequestAborted;
        var name = Input.Name?.Trim();
        var email = AccountRules.NormalizeEmail(Input.Email);
        if (string.IsNullOrEmpty(name))
        {
            ModelState.AddModelError("Input.Name", "Please tell us your name.");
        }
        else if (name.Length > 80)
        {
            ModelState.AddModelError("Input.Name", "Use at most 80 characters.");
        }

        if (string.IsNullOrWhiteSpace(Input.Email))
        {
            ModelState.AddModelError("Input.Email", "Please enter your e-mail address.");
        }
        else if (email is null)
        {
            ModelState.AddModelError("Input.Email", "Enter an e-mail address like anna@example.com.");
        }

        if (string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Please choose a password.");
        }
        else if (AccountRules.PasswordProblem(Input.Password, email ?? Input.Email) is { } weak)
        {
            ModelState.AddModelError("Input.Password", weak);
        }
        else if (Input.ConfirmPassword != Input.Password)
        {
            ModelState.AddModelError("Input.ConfirmPassword", "The passwords do not match.");
        }

        if (!ModelState.IsValid)
        {
            AtAccount = true;
            await LoadAsync();
            return Page();
        }

        var user = await _users.CreateAsync(email!, name!, Input.Password!, UserRole.Admin, mustChangePassword: false, ct);
        _state.Completed = true;

        // MatIdentity comes with the engines: it gets this person with the same e-mail and password, so the sign-in stays the same.
        await _owner.RememberAsync(user.Id, email!, name!, Input.Password!, ct);

        var session = await _sessions.CreateAsync(user.Id, Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        await HttpContext.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, session.Token), new AuthenticationProperties { IsPersistent = true });
        return Redirect(SetupFlow.EnginesPage);
    }

    private async Task LoadAsync()
        => Docker = AdviceFor(await _docker.GetEngineInfoAsync(HttpContext.RequestAborted));

    /// <summary>The check of Docker in plain words (Welcome and the screen of the engines).</summary>
    internal static DockerAdvice AdviceFor(DockerEngineInfo info)
        => DockerAdvice.For(info, SocketMounted(), SocketMounted() == false && System.IO.Directory.Exists("/var/run/docker.sock"));

    /// <summary>The Docker socket is in this container (null: it cannot be told, MatOS runs outside a container or talks to another Docker host).</summary>
    private static bool? SocketMounted()
        => OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")) && System.IO.File.Exists("/.dockerenv")
            ? System.IO.File.Exists("/var/run/docker.sock")
            : null;

    /// <summary>The account exists: its owner goes on where the first start stands (<see cref="SetupFlow.NextPage"/>), anybody else to the home screen.</summary>
    private async Task<IActionResult> ContinueAsync()
        => User.IsInRole(nameof(UserRole.Admin)) ? Redirect(SetupFlow.NextPage(await _config.GetAsync(HttpContext.RequestAborted))) : Redirect("/");

    private async Task<bool> AlreadyDoneAsync()
        => _state.Completed || await _users.CountAsync(HttpContext.RequestAborted) > 0;
}
