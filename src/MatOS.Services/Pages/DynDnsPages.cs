using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages;

/// <summary>
/// DynDNS for everybody (docs/SERVICES.md): the public page at the zone itself (<c>https://my.mattiverse.de</c>, and <c>/dyndns</c> on every
/// host): pick a free name, get its key once, the settings for a FritzBox, Matddns, ddclient and curl, manage the name with its key,
/// check the ports of one's own connection, and the rules. Plain HTML from code with one small script of the service's own; the forms
/// are POSTs that pass the same-origin check, and the registration carries a signed one-time token with a proof of work and a honeypot.
/// </summary>
public static class DynDnsPages
{
    public const string Base = "/dyndns";

    private const string Csp =
        "default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    private const string Style = """
        :root{--bg:#f6f5fb;--card:#fff;--ink:#1d1d1f;--soft:#5f5f6b;--line:#e3e1ee;--accent:#5b4bd8;--accent-ink:#fff;--ok:#137333;--bad:#a50e0e;--code:#f1f0f7}
        @media (prefers-color-scheme:dark){:root{--bg:#121218;--card:#1b1b23;--ink:#ececf1;--soft:#a5a5b3;--line:#2c2c38;--accent:#a99cff;--accent-ink:#121218;--ok:#7bd88f;--bad:#ff8a80;--code:#24242e}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:44rem;margin:0 auto;padding:2.5rem 1rem 3rem}h1{font-size:1.75rem;line-height:1.2;margin:0 0 .5rem}h2{font-size:1.15rem;margin:0 0 .75rem}
        h3{font-size:1rem;margin:1.25rem 0 .4rem}p{margin:.5rem 0}.sub{color:var(--soft);margin-bottom:1.75rem}a{color:var(--accent)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:1.25rem 1.25rem 1rem;margin:0 0 1rem}
        label{display:block;font-weight:600;margin:.25rem 0 .35rem}.row{display:flex;gap:.5rem;align-items:center;flex-wrap:wrap}
        input[type=text],input[type=password],input[type=number]{font:inherit;color:inherit;background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:.55rem .7rem;min-width:0;flex:1 1 12rem}
        .suffix{color:var(--soft)}button{font:inherit;font-weight:600;border:0;border-radius:10px;padding:.6rem 1.1rem;background:var(--accent);color:var(--accent-ink);cursor:pointer}
        button.quiet{background:var(--code);color:var(--ink);font-weight:500;padding:.35rem .75rem}button:disabled{opacity:.6;cursor:wait}
        .status{min-height:1.5em;color:var(--soft)}.yes{color:var(--ok)}.no{color:var(--bad)}.alert{border-radius:10px;padding:.6rem .8rem;background:var(--code);margin:.5rem 0 1rem}
        .alert.bad{color:var(--bad)}.alert.good{color:var(--ok)}code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:.9rem;background:var(--code);border-radius:6px}
        code{padding:0 .3rem}pre{padding:.75rem;overflow-x:auto;white-space:pre-wrap;word-break:break-all;margin:.25rem 0}.key{font-size:1rem;font-weight:600}
        table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:.4rem .25rem;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--soft);font-weight:600;width:11rem}
        ol,ul{padding-left:1.25rem}.hp{position:absolute;left:-10000px;width:1px;height:1px;overflow:hidden}footer{color:var(--soft);font-size:.9rem;text-align:center;margin-top:2rem}
        details summary{cursor:pointer;font-weight:600}.check{display:flex;gap:.5rem;align-items:flex-start;font-weight:400}
        """;

    public static void MapDynDnsPages(this WebApplication app)
    {
        app.MapGet(Base, (HttpContext c, ServicesOptions o, FormGuard g) => Home(c, o, g)).RequireRateLimiting(InstanceApi.NamesPolicy);
        app.MapGet(Base + "/rules", Rules).RequireRateLimiting(InstanceApi.NamesPolicy);
        app.MapGet(Base + "/manage", (ServicesOptions o) => SignIn(o, null, null)).RequireRateLimiting(InstanceApi.NamesPolicy);
        app.MapGet(Base + "/app.js", Script);
        app.MapPost(Base + "/register", RegisterAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost(Base + "/ports", PortsAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost(Base + "/manage", ManageAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost(Base + "/manage/update", UpdateAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost(Base + "/manage/ports", ManagePortsAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost(Base + "/manage/release", ReleaseAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
    }

    /// <summary>Whether a request is for the zone itself (<c>my.mattiverse.de</c>), where this page is the start page.</summary>
    public static bool IsZoneHost(HttpContext context, ServicesOptions options) =>
        string.Equals(context.Request.Host.Host.TrimEnd('.'), options.Zone, StringComparison.OrdinalIgnoreCase);

    /// <summary>The start page: what this is, the name field, the port check.</summary>
    public static IResult Home(HttpContext context, ServicesOptions options, FormGuard guard, string? name = null, string? message = null,
        string? portsHtml = null)
    {
        var zone = E(options.Zone);
        var ip = context.ClientIp();
        var bits = options.ProofOfWorkBits;
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <h1>Your own name on the internet, free</h1>
            <p class="sub">Pick a name and get <code>yourname.{zone}</code>. It follows the IP address of your home connection, so your
            devices stay reachable when your provider changes the address. It works with a FritzBox, ddclient, Matddns or a small script;
            you do not need MatOS for it.</p>
            {Alert(message, bad: true)}
            <section class="card">
            <h2>Pick a name</h2>
            <form id="register" method="post" action="{Base}/register" data-bits="{bits}" data-zone="{zone}" autocomplete="off">
            <label for="name">Name</label>
            <div class="row"><input type="text" id="name" name="name" value="{E(name)}" required minlength="3" maxlength="32"
            pattern="[A-Za-z0-9\-]+"spellcheck="false" autocapitalize="none" aria-describedby="name-status"><span class="suffix">.{zone}</span></div>
            <p id="name-status" class="status" aria-live="polite">3 to 32 characters: letters, digits and hyphens.</p>
            <div class="hp" aria-hidden="true"><label for="website">Leave this empty</label><input type="text" id="website" name="website" tabindex="-1" autocomplete="off"></div>
            <input type="hidden" name="token" value="{guard.Issue()}"><input type="hidden" name="nonce" value="">
            <button type="submit">Get my name</button>
            {(bits > 0 ? "<noscript><p class=\"no\">Getting a name needs JavaScript for a short check against bots (about a second).</p></noscript>" : "")}
            </form>
            </section>
            <section class="card">
            <h2>How it works</h2>
            <ol><li>Pick a free name above. You get a <strong>key</strong>: it is shown once, so keep it like a password.</li>
            <li>Put the name and the key into your router or your update program (the settings are shown right after).</li>
            <li>Your router tells the service whenever your address changes; <code>yourname.{zone}</code> and every name below it
            (<code>nas.yourname.{zone}</code>) then point to your home.</li></ol>
            <p>For visitors to reach a device at home, your router must forward the port to it. The check below tells you whether it does.</p>
            </section>
            """);
        html.Append(PortsForm($"{Base}/ports", ip, null, portsHtml));
        html.Append(CultureInfo.InvariantCulture, $"""
            <section class="card">
            <h2>You already have a name?</h2>
            <p><a href="{Base}/manage">Manage your name</a>: see its address, update it from this browser, or give it back.</p>
            </section>
            """);
        return Page("Free DynDNS names below " + options.Zone, html.ToString());
    }

    private static IResult Rules(ServicesOptions options)
    {
        var zone = E(options.Zone);
        var days = options.ExpireUnusedAfterDays;
        var body = $"""
            <h1>The rules</h1>
            <p class="sub">A free service, run in spare time. Short rules, so it stays free and useful.</p>
            <section class="card">
            <ul>
            <li>A name is <code>name.{zone}</code>; every name below it (<code>nas.name.{zone}</code>) points to the same address.</li>
            <li>Names are 3 to 32 characters: letters, digits and hyphens (not at the start or the end). Some names are reserved.</li>
            <li>At most {options.RegistrationsPerIpPerDay} new names a day from one connection.</li>
            {(days > 0 ? $"<li>A name that has not received a single address within {days} days of its registration is given back automatically, so nobody can hoard names.</li>" : "")}
            <li>Only public addresses are stored: a name never points into a private network.</li>
            <li>No phishing, malware, spam or anything illegal behind a name. A name that is abused is removed without notice.</li>
            <li>The key is the only proof that a name is yours. It cannot be recovered; lose it and the name stays until it is given back or expires.</li>
            <li>No guarantee: the service may be down now and then, and the rules may change.</li>
            </ul>
            <h3>What the service stores</h3>
            <p>The name, a hash of the key (not the key itself), the addresses your router reports, the address the name was registered from,
            and the times of registration and of the last update. Nothing else, no cookies, no tracking.</p>
            <h3>Questions, abuse</h3>
            <p>Open an issue at <a href="{StatusPages.RepositoryUrl}/issues">{StatusPages.RepositoryUrl}</a>.</p>
            </section>
            <p><a href="{Base}">Back</a></p>
            """;
        return Page("Rules", body);
    }

    private static IResult SignIn(ServicesOptions options, string? name, string? message)
    {
        var body = $"""
            <h1>Manage your name</h1>
            <p class="sub">Sign in with the name and the key you got when you registered it. Nothing is remembered after you leave this page.</p>
            {Alert(message, bad: true)}
            <section class="card">
            <form method="post" action="{Base}/manage" autocomplete="off">
            <label for="name">Name</label>
            <div class="row"><input type="text" id="name" name="name" value="{E(name)}" required spellcheck="false" autocapitalize="none"><span class="suffix">.{E(options.Zone)}</span></div>
            <label for="key">Key</label>
            <div class="row"><input type="password" id="key" name="key" required placeholder="mos_..."></div>
            <p><button type="submit">Sign in</button></p>
            </form>
            </section>
            <p><a href="{Base}">Back</a></p>
            """;
        return Page("Manage your name", body);
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context, ServicesOptions options, ServicesDbContext db, Registrar registrar, FormGuard guard)
    {
        if (!FormGuard.IsSameOrigin(context.Request))
        {
            return Forbidden();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = form["name"].ToString();
        if (!string.IsNullOrEmpty(form["website"]))
        {
            // The honeypot: a field people never see and bots fill in.
            return WithStatus(Home(context, options, guard, name, "That did not work. Please try again."), StatusCodes.Status400BadRequest);
        }

        var problem = guard.Redeem(form["token"], form["nonce"], options.ProofOfWorkBits);
        if (problem != FormProblem.None)
        {
            var text = problem == FormProblem.Expired
                ? "The page was open too long. Please try again."
                : "That did not work (the check against bots failed). Please try again.";
            return WithStatus(Home(context, options, guard, name, text), StatusCodes.Status400BadRequest);
        }

        var result = await registrar.RegisterAsync(db, name, "dyndns", context.ClientIp(), dynDnsOnly: true, context.RequestAborted);
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
            var status = result.Status == RegistrationStatus.TooMany ? StatusCodes.Status429TooManyRequests : StatusCodes.Status409Conflict;
            return WithStatus(Home(context, options, guard, name, error), status);
        }

        var days = options.ExpireUnusedAfterDays;
        var body = $"""
            <h1>{E(address)} is yours</h1>
            <p class="sub">Two more steps: keep the key, then put the settings into your router.</p>
            <section class="card">
            <h2>Your key</h2>
            <p><strong>It is shown only this once.</strong> Copy it into your password manager now; without it nobody, not even the
            service, can change or give back the name.</p>
            <pre id="key" class="key">{E(result.Key)}</pre>
            <p><button type="button" class="quiet" data-copy="key" hidden>Copy</button></p>
            </section>
            {Setup(options, result.Name, result.Key!)}
            <section class="card">
            <h2>What now</h2>
            <p>As soon as your router (or your program) sends its first update, <code>{E(address)}</code> points to your home.
            {(days > 0 ? $"A name that receives no address within {days} days is given back automatically." : "")}</p>
            <p><a href="{Base}/manage">Manage your name</a> &middot; <a href="{Base}/rules">The rules</a></p>
            </section>
            """;
        return WithStatus(Page(address + " is yours", body), StatusCodes.Status201Created);
    }

    private static async Task<IResult> PortsAsync(
        HttpContext context, ServicesOptions options, ServicesDbContext db, FormGuard guard, PortCheck check, PortCheckLimits limits)
    {
        if (!FormGuard.IsSameOrigin(context.Request))
        {
            return Forbidden();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var (html, status) = await RunPortsAsync(context, form["port"], db, check, limits);
        return WithStatus(Home(context, options, guard, portsHtml: html), status);
    }

    private static async Task<IResult> ManageAsync(HttpContext context, ServicesOptions options, ServicesDbContext db)
    {
        var (instance, failure) = await SignedInAsync(context, options, db);
        return failure ?? Manage(context, options, instance!, Key(context), null, false);
    }

    private static async Task<IResult> UpdateAsync(HttpContext context, ServicesOptions options, ServicesDbContext db,
        InstanceDirectory directory, Usage.UsageRecorder usage, TimeProvider time)
    {
        var (instance, failure) = await SignedInAsync(context, options, db);
        if (failure is not null)
        {
            return failure;
        }

        var ip = context.ClientIp();
        var outcome = DynDns.Evaluate(instance!.Name, options.Zone, instance.PublicIpv4, instance.PublicIpv6,
            $"{instance.Name}.{options.Zone}", null, null, ip);
        if (outcome.Reply.StartsWith("dnserr", StringComparison.Ordinal))
        {
            return Manage(context, options, instance, Key(context),
                $"This browser came from {ip}, which is not a public address, so it cannot be used. Open the page from your home connection without a VPN.", true);
        }

        var now = time.GetUtcNow().UtcDateTime;
        instance.LastSeen = now;
        if (outcome.Changed)
        {
            instance.PublicIpv4 = outcome.Ipv4;
            instance.PublicIpv6 = outcome.Ipv6;
            instance.UpdateDate = now;
        }

        await db.SaveChangesAsync(context.RequestAborted);
        await directory.RefreshAsync(instance.Name);
        usage.DynDnsUpdated(instance.Name, outcome.Changed);
        var text = outcome.Changed ? $"Done: the name points to {ip} now." : $"Nothing to change: the name points to {ip} already.";
        return Manage(context, options, instance, Key(context), text, false);
    }

    private static async Task<IResult> ManagePortsAsync(
        HttpContext context, ServicesOptions options, ServicesDbContext db, PortCheck check, PortCheckLimits limits)
    {
        var (instance, failure) = await SignedInAsync(context, options, db);
        if (failure is not null)
        {
            return failure;
        }

        if (!IsOwnAddress(instance!, context))
        {
            return Manage(context, options, instance!, Key(context), "The ports can only be checked from the connection the name points to.", true);
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var (html, status) = await RunPortsAsync(context, form["port"], db, check, limits);
        return WithStatus(Manage(context, options, instance!, Key(context), null, false, html), status);
    }

    private static async Task<IResult> ReleaseAsync(HttpContext context, ServicesOptions options, ServicesDbContext db, Registrar registrar)
    {
        var (instance, failure) = await SignedInAsync(context, options, db);
        if (failure is not null)
        {
            return failure;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["confirm"] != "yes")
        {
            return Manage(context, options, instance!, Key(context), "Tick the box to confirm that you want to give the name back.", true);
        }

        await registrar.ReleaseAsync(db, instance!, context.RequestAborted);
        var body = $"""
            <h1>{E(instance!.Name)}.{E(options.Zone)} is free again</h1>
            <p class="sub">The name no longer points anywhere, and anybody may register it now. Remove the settings from your router.</p>
            <p><a href="{Base}">Back to the start</a></p>
            """;
        return Page("Given back", body);
    }

    /// <summary>The page of a signed-in name: its addresses, update now, the port check, give it back, the settings again.</summary>
    private static IResult Manage(HttpContext context, ServicesOptions options, Instance instance, string key, string? message, bool bad,
        string? portsHtml = null)
    {
        var address = $"{instance.Name}.{options.Zone}";
        var ip = context.ClientIp();
        var hidden = $"<input type=\"hidden\" name=\"name\" value=\"{E(instance.Name)}\"><input type=\"hidden\" name=\"key\" value=\"{E(key)}\">";
        var ports = IsOwnAddress(instance, context)
            ? PortsForm($"{Base}/manage/ports", ip, hidden, portsHtml)
            : $"""
              <section class="card"><h2>Check my ports</h2>
              <p>The ports of {E(instance.PublicIpv4 ?? instance.PublicIpv6 ?? "the name's address")} can be checked from that connection only
              (this browser comes from {E(ip.ToString())}). Open this page at home.</p></section>
              """;
        var body = $"""
            <h1>{E(address)}</h1>
            <p class="sub">Signed in. Nothing is remembered after you leave this page.</p>
            {Alert(message, bad)}
            <section class="card">
            <table>
            <tr><th>IPv4 address</th><td>{E(instance.PublicIpv4 ?? "none yet")}</td></tr>
            <tr><th>IPv6 address</th><td>{E(instance.PublicIpv6 ?? "none yet")}</td></tr>
            <tr><th>Last update</th><td>{Date(instance.LastSeen)}</td></tr>
            <tr><th>Address changed</th><td>{Date(instance.UpdateDate)}</td></tr>
            <tr><th>Registered</th><td>{Date(instance.CreateDate)}</td></tr>
            </table>
            <form method="post" action="{Base}/manage/update">{hidden}
            <p>This browser comes from <code>{E(ip.ToString())}</code>.</p>
            <p><button type="submit">Update now from this browser</button></p>
            </form>
            </section>
            {ports}
            <details class="card"><summary>Settings for your router or program</summary>{Setup(options, instance.Name, key, card: false)}</details>
            <section class="card">
            <h2>Give the name back</h2>
            <p>The name stops pointing anywhere at once, and anybody may register it.</p>
            <form method="post" action="{Base}/manage/release">{hidden}
            <label class="check"><input type="checkbox" name="confirm" value="yes" required> Yes, give {E(address)} back</label>
            <p><button type="submit">Give it back</button></p>
            </form>
            </section>
            <p><a href="{Base}">Back to the start</a></p>
            """;
        return Page(address, body);
    }

    private static string PortsForm(string action, System.Net.IPAddress ip, string? hidden, string? result) => $"""
        <section class="card" id="ports">
        <h2>Check my ports</h2>
        <p>Is your device at home reachable from the internet? The service tries to connect to your address <code>{E(ip.ToString())}</code>
        on port 80 and 443, and on one more port if you like. Only your own address is ever checked.</p>
        {result}
        <form method="post" action="{action}#ports">{hidden}
        <label for="port">One more port (optional)</label>
        <div class="row"><input type="number" id="port" name="port" min="1" max="65535" placeholder="for example 8443"><button type="submit">Check my ports</button></div>
        </form>
        <details><summary>How to forward a port on a FritzBox</summary>
        <ol><li>Open <code>http://fritz.box</code> and go to <em>Internet &gt; Permit Access &gt; Port Sharing</em>.</li>
        <li><em>Add Device for Sharing</em>, pick the device that should be reachable (it needs a fixed address in your network).</li>
        <li><em>New Sharing &gt; Port Sharing</em>: choose <em>HTTP server</em> (80), <em>HTTPS server</em> (443), or <em>Other application</em>
        with your port and TCP, then save and switch it on.</li>
        <li>Check again here. If it still says closed, your connection may have no public IPv4 address of its own (DS-Lite, CGNAT): ask
        your provider, or use MatOS's tunnel instead.</li></ol>
        </details>
        </section>
        """;

    /// <summary>Runs the port check for the caller and renders its result (and the HTTP status for a refusal).</summary>
    private static async Task<(string Html, int Status)> RunPortsAsync(
        HttpContext context, string? portText, ServicesDbContext db, PortCheck check, PortCheckLimits limits)
    {
        int? port = null;
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return (Alert("A port is a number from 1 to 65535.", bad: true), StatusCodes.Status400BadRequest);
            }

            port = p;
        }

        var (result, refusal, retryAfter) = await PortsApi.RunAsync(context, port, null, db, check, limits);
        if (refusal == PortsApi.Refusal.NotPublic)
        {
            return (Alert($"This browser came from {context.ClientIp()}, which is not a public address, so there is nothing to check from outside.", bad: true),
                StatusCodes.Status422UnprocessableEntity);
        }

        if (refusal == PortsApi.Refusal.TooMany)
        {
            var minutes = Math.Max(1, (retryAfter + 59) / 60);
            return (Alert($"That was a lot of checks. Please try again in {minutes} minute{(minutes == 1 ? "" : "s")}.", bad: true),
                StatusCodes.Status429TooManyRequests);
        }

        var html = new StringBuilder("<table>");
        foreach (var p in result!.Ports)
        {
            var state = p.Ipv4 ?? p.Ipv6;
            var css = state == "open" ? "yes" : "no";
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><th>Port {p.Port}</th><td class=\"{css}\">{E(PortCheck.Words(state, p.Web))}</td></tr>");
        }

        html.Append("</table>");
        return (html.ToString(), StatusCodes.Status200OK);
    }

    /// <summary>The settings for a FritzBox, Matddns, ddclient and curl.</summary>
    private static string Setup(ServicesOptions options, string name, string key, bool card = true)
    {
        var address = $"{name}.{options.Zone}";
        var url = options.PublicUrl.TrimEnd('/');
        var host = new Uri(url).Host;
        var fritz = $"{url}/nic/update?hostname=<domain>&myip=<ipaddr>&myipv6=<ip6addr>";
        var matddns = $"{url}/nic/update?hostname={{hostname}}&myip={{ipv4}}&myipv6={{ipv6}}";
        var ddclient = $"""
            protocol=dyndns2
            use=web, web={url}/ip
            server={host}
            ssl=yes
            login={name}
            password='{key}'
            {address}
            """;
        var curl = $"curl -u '{name}:{key}' '{url}/nic/update?hostname={address}'";
        var inner = $"""
            <h2>Settings</h2>
            <h3>FritzBox</h3>
            <p><em>Internet &gt; Permit Access &gt; DynDNS</em>: switch DynDNS on, provider <em>User-defined</em>, then:</p>
            <table>
            <tr><th>Update URL</th><td><pre id="s-fritz-url">{E(fritz)}</pre><button type="button" class="quiet" data-copy="s-fritz-url" hidden>Copy</button></td></tr>
            <tr><th>Domain name</th><td><code>{E(address)}</code></td></tr>
            <tr><th>User name</th><td><code>{E(name)}</code></td></tr>
            <tr><th>Password</th><td>your key</td></tr>
            </table>
            <h3>Matddns</h3>
            <p>A domain group of the kind <em>DynDns</em> with the record <code>{E(address)}</code>, user name <code>{E(name)}</code>, password your key, and the update URL:</p>
            <pre id="s-matddns">{E(matddns)}</pre><button type="button" class="quiet" data-copy="s-matddns" hidden>Copy</button>
            <h3>ddclient</h3>
            <p>In <code>/etc/ddclient.conf</code>:</p>
            <pre id="s-ddclient">{E(ddclient)}</pre><button type="button" class="quiet" data-copy="s-ddclient" hidden>Copy</button>
            <h3>curl, or any script</h3>
            <p>Without <code>myip</code> the address the request comes from counts; add <code>&amp;myip=…</code> to name one.</p>
            <pre id="s-curl">{E(curl)}</pre><button type="button" class="quiet" data-copy="s-curl" hidden>Copy</button>
            """;
        return card ? $"<section class=\"card\">{inner}</section>" : inner;
    }

    /// <summary>The name and key of a manage form; null and the answer (the sign-in page with a message) when they do not fit.</summary>
    private static async Task<(Instance? Instance, IResult? Failure)> SignedInAsync(HttpContext context, ServicesOptions options, ServicesDbContext db)
    {
        if (!FormGuard.IsSameOrigin(context.Request))
        {
            return (null, Forbidden());
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = NameRules.Normalize(form["name"]);
        var suffix = "." + options.Zone;
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            name = name[..^suffix.Length];
        }

        var key = form["key"].ToString().Trim();
        var instance = NameRules.Check(name) is null
            ? await db.Instances.FirstOrDefaultAsync(i => i.Name == name, context.RequestAborted)
            : null;
        if (instance is null || !InstanceKeys.Matches(key, instance.KeyHash))
        {
            return (null, WithStatus(SignIn(options, name, "The name and the key do not fit together."), StatusCodes.Status401Unauthorized));
        }

        if (!instance.DynDnsOnly)
        {
            return (null, WithStatus(
                SignIn(options, name, "This name belongs to a MatOS installation. Manage it in MatOS, under Settings > Address."),
                StatusCodes.Status409Conflict));
        }

        context.Items[KeyItem] = key;
        return (instance, null);
    }

    private const string KeyItem = "dyndns-key";

    private static string Key(HttpContext context) => (string)context.Items[KeyItem]!;

    private static bool IsOwnAddress(Instance instance, HttpContext context)
    {
        var ip = context.ClientIp().ToString();
        return ip == instance.PublicIpv4 || ip == instance.PublicIpv6;
    }

    private static IResult Script() => new Asset(DynDnsScript.Source, "text/javascript; charset=utf-8");

    private static IResult Forbidden() => WithStatus(
        Page("Not allowed", "<h1>Not allowed</h1><p>This form can only be sent from this site. Please open the page again.</p>"),
        StatusCodes.Status403Forbidden);

    private static string Alert(string? message, bool bad) =>
        message is null ? "" : $"<p class=\"alert {(bad ? "bad" : "good")}\" role=\"status\">{E(message)}</p>";

    private static string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");

    private static string Date(DateTime? value) =>
        value is null ? "never" : value.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static Html Page(string title, string body) => new($"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{E(title)}</title><style>{Style}</style><script src="{Base}/app.js" defer></script></head>
        <body><main>{body}
        <footer><a href="{Base}">DynDNS</a> &middot; <a href="{Base}/manage">Manage your name</a> &middot; <a href="{Base}/rules">Rules</a>
        &middot; part of <a href="{StatusPages.RepositoryUrl}">MatOS</a></footer></main></body></html>
        """);

    private static Html WithStatus(IResult page, int status) => ((Html)page) with { Status = status };

    /// <summary>An HTML answer with the page's strict policy: its own script only, no frames, no cache.</summary>
    private sealed record Html(string Text, int Status = StatusCodes.Status200OK) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var headers = httpContext.Response.Headers;
            httpContext.Response.StatusCode = Status;
            headers.ContentSecurityPolicy = Csp;
            headers.XContentTypeOptions = "nosniff";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "same-origin";
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            return httpContext.Response.WriteAsync(Text);
        }
    }

    private sealed class Asset(string text, string type) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = type;
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            httpContext.Response.Headers.CacheControl = "public, max-age=3600";
            return httpContext.Response.WriteAsync(text);
        }
    }
}
