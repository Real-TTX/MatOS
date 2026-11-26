using MatOS.Core.Configuration;

namespace MatOS.Core.Integrations;

/// <summary>
/// The setup of MatSMTP, the mail relay (MatDesign docs/PLATFORM.md, "Mail through MatSMTP"): its first-run setup and the domain of the senders (the one of the wizard).
/// The apps are not here: they consume <c>mail.smtp</c> and the broker binds them like any capability (<see cref="BindingBroker"/>, MatSMTP is the provider).
/// </summary>
public sealed class MailLink
{
    private readonly MatSmtpClient _smtp;
    private readonly SystemConfigService _config;

    public MailLink(MatSmtpClient smtp, SystemConfigService config)
    {
        _smtp = smtp;
        _config = config;
    }

    /// <summary>
    /// The headless setup of MatSMTP (after its install, and when the domain changes): the first-run setup is done and the senders get the domain of the settings
    /// (the one of the wizard). Without a domain MatSMTP keeps its own. Returns false when MatSMTP does not answer.
    /// </summary>
    public async Task<bool> ConfigureRelayAsync(Action<string> log, CancellationToken ct)
    {
        if (await _smtp.GetSmtpStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            return false;
        }

        var current = await _smtp.GetSettingsAsync(ct);
        var domain = (await _config.GetAsync(ct)).BaseDomain;
        var wanted = new MatSmtpRelaySettings(true, string.IsNullOrWhiteSpace(domain) ? current.DefaultSenderDomain : domain.Trim().ToLowerInvariant());
        if (wanted != current)
        {
            await _smtp.UpdateSettingsAsync(wanted, ct);
        }

        log(wanted.DefaultSenderDomain is { } senders
            ? $"MatSMTP is set up; the apps send as <app>@{senders}."
            : "MatSMTP is set up. Set a domain for the senders in the wizard or in MatSMTP.");
        return true;
    }
}
