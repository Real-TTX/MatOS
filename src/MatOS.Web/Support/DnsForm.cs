using MatOS.Core.Configuration;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MatOS.Web.Support;

/// <summary>
/// The DNS account part of a form (wizard and settings): which provider hosts the domain, the zone and the credential fields, and whether the internet connection
/// has a fixed IP address (<see cref="DnsIp"/>). Secrets are never sent back to the browser; a field that has a stored value says so and keeps it when left empty.
/// </summary>
public sealed class DnsFormModel
{
    /// <summary>The names of the inputs of the question about the IP address (the page models bind them).</summary>
    public const string IpAnswerField = "DnsIp";
    public const string FixedIpField = "DnsFixedIp";

    /// <summary>The provider id, or empty for none (the value of the select).</summary>
    public string Provider { get; init; } = string.Empty;

    public string? Zone { get; init; }

    /// <summary>Ids (<see cref="DnsForm.FieldName"/>) of the credentials that are stored already.</summary>
    public IReadOnlySet<string> Stored { get; init; } = new HashSet<string>();

    /// <summary>Values to prefill: only the fields that are not secret.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>The answer preselected: <see cref="DnsIp.Fixed"/> for a fixed address, else <see cref="DnsIp.Unsure"/> (the default).</summary>
    public string IpAnswer { get; init; } = DnsIp.Unsure;

    /// <summary>The fixed address to prefill: the stored one, else the public IP MatOS detected, else empty.</summary>
    public string? FixedIp { get; init; }

    public static DnsFormModel From(SystemConfig config, IReadOnlyDictionary<string, string> credentials, string? detectedIp = null)
    {
        var provider = DnsProviders.Find(config.Dns.Provider);
        var stored = new HashSet<string>();
        var values = new Dictionary<string, string>();
        if (provider is not null)
        {
            foreach (var field in provider.Fields.Where(f => credentials.TryGetValue(f.Key, out var v) && !string.IsNullOrEmpty(v)))
            {
                stored.Add(DnsForm.FieldName(provider, field));
                if (!field.Secret)
                {
                    values[DnsForm.FieldName(provider, field)] = credentials[field.Key];
                }
            }
        }

        var ip = config.Dns.Ip;
        return new DnsFormModel
        {
            Provider = provider?.Id ?? string.Empty,
            Zone = config.Dns.Zone,
            Stored = stored,
            Values = values,
            IpAnswer = ip.Answer,
            FixedIp = ip.IsFixed ? ip.FixedIp : detectedIp,
        };
    }

    /// <summary>The same form again after a problem: what the person entered stays.</summary>
    public DnsFormModel WithIp(string? answer, string? fixedIp)
        => new()
        {
            Provider = Provider,
            Zone = Zone,
            Stored = Stored,
            Values = Values,
            IpAnswer = DnsIp.Answers.Any(a => a.Value == answer) ? answer! : DnsIp.Unsure,
            FixedIp = string.IsNullOrWhiteSpace(fixedIp) ? FixedIp : fixedIp.Trim(),
        };

    /// <summary>
    /// The answer of the question for a provider that is set (none: the default, nothing is asked); a problem with the fixed address goes into
    /// <paramref name="modelState"/>.
    /// </summary>
    public static DnsIp ReadIp(string? providerId, string? answer, string? fixedIp, ModelStateDictionary modelState)
    {
        if (DnsProviders.Find(providerId) is null)
        {
            return DnsIp.Default;
        }

        var (ip, problem) = DnsIp.FromForm(answer, fixedIp);
        if (problem is not null)
        {
            modelState.AddModelError(FixedIpField, problem);
        }

        return ip ?? DnsIp.Default;
    }
}
