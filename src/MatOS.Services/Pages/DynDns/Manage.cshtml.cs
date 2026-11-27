using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.DynDns;

/// <summary>
/// Manage a DynDNS name (<c>/dyndns/manage</c>): sign in with the name and its key in a POST form, nothing kept after the page (each further
/// form carries them again as hidden fields; the page is <c>no-store</c>). Its address, update now from this browser, the port check from the
/// name's own address, give it back. A name of a MatOS installation is managed in MatOS.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class ManageModel(ServicesOptions options, ServicesDbContext db, PortCheck check, PortCheckLimits limits) : PublicPageModel
{
    public ServicesOptions Options => options;

    /// <summary>The signed-in name (null: the sign-in form).</summary>
    public Instance? Instance { get; private set; }

    /// <summary>The key of the signed-in name, carried by every form of the page.</summary>
    public string Key { get; private set; } = "";

    /// <summary>The name typed into the sign-in, shown again after a refusal.</summary>
    public string? Name { get; private set; }

    public string? Message { get; private set; }

    public bool MessageIsError { get; private set; }

    public PortsView? Ports { get; private set; }

    /// <summary>The address of a name just given back.</summary>
    public string? Released { get; private set; }

    public string ClientIp => HttpContext.ClientIp().ToString();

    /// <summary>Whether this browser comes from the address the name points to (only then may it check the ports).</summary>
    public bool IsOwnAddress => Instance is { } i && (ClientIp == i.PublicIpv4 || ClientIp == i.PublicIpv6);

    public string Address => $"{Instance?.Name}.{options.Zone}";

    public PortsCard PortsCard => new($"{PublicSite.DynDnsBase}/manage/ports#ports", ClientIp, Instance?.Name, Key, Ports);

    public DynDnsSettings Settings => new(options, Instance!.Name, Key);

    public IActionResult OnGet() => NamesAForm ? NoSuchForm() : Page();

    /// <summary>The sign-in.</summary>
    public async Task<IActionResult> OnPostAsync() => await SignedInAsync() ?? Page();

    public async Task<IActionResult> OnPostUpdateAsync([FromServices] InstanceDirectory directory, [FromServices] UsageRecorder usage,
        [FromServices] TimeProvider time)
    {
        if (await SignedInAsync() is { } failure)
        {
            return failure;
        }

        var instance = Instance!;
        var ip = HttpContext.ClientIp();
        var outcome = Instances.DynDns.Evaluate(instance.Name, options.Zone, instance.PublicIpv4, instance.PublicIpv6,
            $"{instance.Name}.{options.Zone}", null, null, ip);
        if (outcome.Reply.StartsWith("dnserr", StringComparison.Ordinal))
        {
            return Say($"This browser came from {ip}, which is not a public address, so it cannot be used. Open the page from your home connection without a VPN.", true);
        }

        var now = time.GetUtcNow().UtcDateTime;
        instance.LastSeen = now;
        if (outcome.Changed)
        {
            instance.PublicIpv4 = outcome.Ipv4;
            instance.PublicIpv6 = outcome.Ipv6;
            instance.UpdateDate = now;
        }

        await db.SaveChangesAsync(HttpContext.RequestAborted);
        await directory.RefreshAsync(instance.Name);
        usage.DynDnsUpdated(instance.Name, outcome.Changed);
        return Say(outcome.Changed ? $"Done: the name points to {ip} now." : $"Nothing to change: the name points to {ip} already.", false);
    }

    public async Task<IActionResult> OnPostPortsAsync()
    {
        if (await SignedInAsync() is { } failure)
        {
            return failure;
        }

        if (!IsOwnAddress)
        {
            return Say("The ports can only be checked from the connection the name points to.", true);
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var (view, status) = await DynDnsForms.RunPortsAsync(HttpContext, form["port"], db, check, limits);
        Ports = view;
        return PageWith(status);
    }

    public async Task<IActionResult> OnPostReleaseAsync([FromServices] Registrar registrar, [FromServices] UsageRecorder usage)
    {
        if (await SignedInAsync() is { } failure)
        {
            return failure;
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        if (form["confirm"] != "yes")
        {
            return Say("Tick the box to confirm that you want to give the name back.", true);
        }

        var address = Address;
        await registrar.ReleaseAsync(db, Instance!, HttpContext.RequestAborted);
        usage.GaveBack();
        Released = address;
        Instance = null;
        return Page();
    }

    /// <summary>
    /// The name and the key of the form; null when they fit (<see cref="Instance"/> is set), else the answer: "Not allowed" for another
    /// origin, the sign-in again with its reason.
    /// </summary>
    private async Task<IActionResult?> SignedInAsync()
    {
        if (!IsSameOrigin(out var refusal))
        {
            return refusal;
        }

        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var name = NameRules.Normalize(form["name"]);
        var suffix = "." + options.Zone;
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }

        Name = name;
        var key = form["key"].ToString().Trim();
        var instance = NameRules.Check(name) is null
            ? await db.Instances.FirstOrDefaultAsync(i => i.Name == name, HttpContext.RequestAborted)
            : null;
        if (instance is null || !InstanceKeys.Matches(key, instance.KeyHash))
        {
            return SignInAgain("The name and the key do not fit together.", StatusCodes.Status401Unauthorized);
        }

        if (!instance.DynDnsOnly)
        {
            return SignInAgain("This name belongs to a MatOS installation. Manage it in MatOS, under Settings > Address.", StatusCodes.Status409Conflict);
        }

        Instance = instance;
        Key = key;
        return null;
    }

    private IActionResult SignInAgain(string message, int status)
    {
        Message = message;
        MessageIsError = true;
        return PageWith(status);
    }

    /// <summary>The page of the signed-in name with a message.</summary>
    private IActionResult Say(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
        return Page();
    }
}
