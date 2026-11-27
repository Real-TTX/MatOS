using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.AspNetCore.Mvc;

namespace MatOS.Services.Pages.DynDns;

/// <summary>
/// DynDNS for everybody (docs/SERVICES.md), the start page: at the zone itself (<c>https://my.mattiverse.de</c>) and at <c>/dyndns</c> on every
/// host. Pick a name (<c>POST /dyndns/register</c>: same origin, the signed one-time token with its proof of work, the honeypot, then the same
/// registration as the API's), and check the ports of one's own connection (<c>POST /dyndns/ports</c>).
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class IndexModel(ServicesOptions options, FormGuard guard, ServicesDbContext db, PortCheck check, PortCheckLimits limits) : PublicPageModel
{
    public ServicesOptions Options => options;

    /// <summary>The token of the registration form (issued for every page view, used once).</summary>
    public string Token { get; private set; } = "";

    /// <summary>The name typed in, shown again after a refusal.</summary>
    public string? Name { get; private set; }

    /// <summary>Why the last form did not work.</summary>
    public string? Message { get; private set; }

    public PortsView? Ports { get; private set; }

    /// <summary>The name just registered, with its settings: the key is in there and shown this once.</summary>
    public DynDnsSettings? Registered { get; private set; }

    public string? RegisteredKey { get; private set; }

    public PortsCard PortsCard => new($"{PublicSite.DynDnsBase}/ports#ports", HttpContext.ClientIp().ToString(), null, null, Ports);

    public IActionResult OnGet()
    {
        if (NamesAForm)
        {
            return NoSuchForm();
        }

        Token = guard.Issue();
        return Page();
    }

    public IActionResult OnPost() => NoSuchForm();

    public async Task<IActionResult> OnPostRegisterAsync([FromServices] Registrar registrar)
    {
        if (!IsSameOrigin(out var refusal))
        {
            return refusal;
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        Name = form["name"].ToString();
        if (!string.IsNullOrEmpty(form["website"]))
        {
            // The honeypot: a field people never see and bots fill in.
            return Home("That did not work. Please try again.", StatusCodes.Status400BadRequest);
        }

        var problem = guard.Redeem(form["token"], form["nonce"], options.ProofOfWorkBits);
        if (problem != FormProblem.None)
        {
            return Home(problem == FormProblem.Expired
                ? "The page was open too long. Please try again."
                : "That did not work (the check against bots failed). Please try again.", StatusCodes.Status400BadRequest);
        }

        var result = await registrar.RegisterAsync(db, Name, "dyndns", HttpContext.ClientIp(), dynDnsOnly: true, HttpContext.RequestAborted);
        var address = $"{result.Name}.{options.Zone}";
        var error = result.Status switch
        {
            RegistrationStatus.Created => null,
            RegistrationStatus.Invalid => "A name has 3 to 32 characters: letters, digits and hyphens, not at the start or the end.",
            RegistrationStatus.Reserved => $"{address} is reserved. Please pick another name.",
            RegistrationStatus.Taken => $"{address} is taken already. Please pick another name.",
            RegistrationStatus.Blocked => $"{address} is blocked and cannot be registered. Please pick another name.",
            _ => "Too many new names from your connection today. Please try again tomorrow.",
        };
        if (error is not null)
        {
            return Home(error, result.Status == RegistrationStatus.TooMany ? StatusCodes.Status429TooManyRequests : StatusCodes.Status409Conflict);
        }

        Registered = new DynDnsSettings(options, result.Name, result.Key!);
        RegisteredKey = result.Key;
        return PageWith(StatusCodes.Status201Created);
    }

    public async Task<IActionResult> OnPostPortsAsync()
    {
        if (!IsSameOrigin(out var refusal))
        {
            return refusal;
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var (view, status) = await DynDnsForms.RunPortsAsync(HttpContext, form["port"], db, check, limits);
        Ports = view;
        Token = guard.Issue();
        return PageWith(status);
    }

    /// <summary>The start page again, with a message and the status of the refusal.</summary>
    private IActionResult Home(string message, int status)
    {
        Message = message;
        Token = guard.Issue();
        return PageWith(status);
    }
}
