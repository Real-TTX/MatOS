using DnsClient;

namespace MatOS.Core.Integrations;

/// <summary>What a name server said: the name exists (with what it holds), it does not exist at all, or nothing clear came back.</summary>
public enum DnsAnswerKind
{
    Found,
    NxDomain,
    Unknown,
}

/// <summary>The answer of one question to the name servers (<see cref="DnsCheck"/>).</summary>
public sealed record DnsAnswer(DnsAnswerKind Kind, IReadOnlyList<string> Values)
{
    public static DnsAnswer Unknown { get; } = new(DnsAnswerKind.Unknown, []);
}

/// <summary>
/// Asks the name servers of this machine about an own domain, for the test of the first start (<c>SetupService.TestAsync</c>): whether the domain exists and
/// where a host of it points. Three seconds per question. A resolver inside a container may see another DNS than the internet (split horizon), so only "this
/// domain does not exist" counts as an answer; a timeout or an error is <see cref="DnsAnswerKind.Unknown"/>.
/// </summary>
public sealed class DnsCheck
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = true,
        ThrowDnsErrors = false,
    });

    /// <summary>
    /// Two-level suffixes under which names are registered (<c>example.co.uk</c>): a small list of the common ones, not the whole Public Suffix List. A name
    /// below one of them is registered with one label more.
    /// </summary>
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "ac.uk", "gov.uk",
        "com.au", "net.au", "org.au", "co.nz", "org.nz", "net.nz", "co.at", "or.at", "co.jp", "ne.jp", "or.jp",
        "com.br", "net.br", "com.tr", "co.za", "com.cn", "co.in", "com.mx", "com.ar", "co.il", "co.kr", "com.sg", "com.hk", "com.tw", "com.pl", "co.id",
    };

    /// <summary>
    /// The domain that is registered at a registrar for <paramref name="domain"/>: its last two labels (<c>apps.example.com</c> gives <c>example.com</c>),
    /// three below a known two-level suffix (<c>example.co.uk</c>). Its name servers say whether the domain exists.
    /// </summary>
    public static string Registered(string domain)
    {
        var labels = domain.Trim().Trim('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }

        var take = TwoLevelSuffixes.Contains($"{labels[^2]}.{labels[^1]}") ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>The name servers of the registered domain of <paramref name="domain"/> (<see cref="Registered"/>).</summary>
    public async Task<DnsAnswer> NameServersAsync(string domain, CancellationToken ct = default)
    {
        var response = await QueryAsync(Registered(domain), QueryType.NS, ct);
        return response is null ? DnsAnswer.Unknown : Read(response, r => r.Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.').ToLowerInvariant()));
    }

    /// <summary>The IPv4 and IPv6 addresses <paramref name="host"/> points to (following a CNAME).</summary>
    public async Task<DnsAnswer> AddressesAsync(string host, CancellationToken ct = default)
    {
        var v4 = await QueryAsync(host, QueryType.A, ct);
        if (v4 is null)
        {
            return DnsAnswer.Unknown;
        }

        var answer = Read(v4, r => r.Answers.ARecords().Select(a => a.Address.ToString()));
        if (answer.Kind != DnsAnswerKind.Found)
        {
            return answer;
        }

        var v6 = await QueryAsync(host, QueryType.AAAA, ct);
        var six = v6 is { HasError: false } ? v6.Answers.AaaaRecords().Select(a => a.Address.ToString()) : [];
        return answer with { Values = answer.Values.Concat(six).Distinct().ToList() };
    }

    private static DnsAnswer Read(IDnsQueryResponse response, Func<IDnsQueryResponse, IEnumerable<string>> values)
    {
        if (response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
        {
            return new DnsAnswer(DnsAnswerKind.NxDomain, []);
        }

        // NOERROR without the asked records still means that the name exists.
        return response.HasError ? DnsAnswer.Unknown : new DnsAnswer(DnsAnswerKind.Found, values(response).Distinct().ToList());
    }

    private async Task<IDnsQueryResponse?> QueryAsync(string name, QueryType type, CancellationToken ct)
    {
        try
        {
            return await _client.QueryAsync(name, type, QueryClass.IN, ct);
        }
        catch (Exception ex) when (ex is DnsResponseException or TimeoutException or System.Net.Sockets.SocketException or ArgumentException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}
