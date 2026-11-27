namespace MatOS.Core.Setup;

/// <summary>What the card of the test on <c>/Setup/Choose</c> sends (<c>POST /api/setup/test</c>): the answers of the tree, as they are now.</summary>
public sealed record SetupTestInput(string? Reach, string? Name, string? Domain, string? Provider, string? IpMode, string? FixedIp, string? Cname);

/// <summary>
/// One row of the test: its key (<see cref="SetupTest"/>), its label, its state (<see cref="SetupTest.Done"/>, <see cref="SetupTest.Failed"/>,
/// <see cref="SetupTest.Warn"/> or <see cref="SetupTest.Skip"/>), whether it only advises (<paramref name="Advisory"/>: it never keeps the person here) and
/// what it found, in plain words.
/// </summary>
public sealed record SetupTestRow(string Key, string Label, string State, bool Advisory, string Text);

/// <summary>The rows of the test, whether the person can go on (no required row failed) and the public addresses MatOS Services saw (for the records).</summary>
public sealed record SetupTestResult(IReadOnlyList<SetupTestRow> Rows, bool CanContinue, string? Ipv4, string? Ipv6);

/// <summary>The rows of the test of the first start (<c>SetupService.TestAsync</c>): their keys, labels and states, and whether the person can go on.</summary>
public static class SetupTest
{
    /// <summary>Ports 80 and 443 are free on this computer (Matcad needs them, also at home). Required.</summary>
    public const string Ports80 = "ports80";

    /// <summary>MatOS Services answers (a free address). Required.</summary>
    public const string Services = "services";

    /// <summary>The free name is reserved (here, not only checked). Required.</summary>
    public const string Name = "name";

    /// <summary>The own domain exists (its name servers; only "does not exist" fails). Required.</summary>
    public const string Domain = "domain";

    /// <summary>The records of an own domain made by hand (or for a fixed IP) point to this computer. Advisory.</summary>
    public const string Records = "records";

    /// <summary>The ports 80 and 443 are reached from outside (one cached check of MatOS Services). Advisory.</summary>
    public const string Ports = "ports";

    public const string Done = "done";
    public const string Failed = "failed";
    public const string Warn = "warn";
    public const string Skip = "skip";

    /// <summary>Every row, in the order of the card.</summary>
    public static readonly IReadOnlyList<string> All = [Ports80, Services, Name, Domain, Records, Ports];

    /// <summary>The label of a row.</summary>
    public static string Label(string key) => key switch
    {
        Ports80 => "Ports 80 and 443 are free on this computer",
        Services => "MatOS Services answers",
        Name => "Your free name is reserved",
        Domain => "Your domain exists",
        Records => "Your records point to this computer",
        Ports => "Reached from outside on 80 and 443",
        _ => key,
    };

    /// <summary>The rows that only advise: they never keep the person on the screen.</summary>
    public static bool IsAdvisory(string key) => key is Records or Ports;

    /// <summary>A row with its label and whether it advises.</summary>
    public static SetupTestRow Row(string key, string state, string text) => new(key, Label(key), state, IsAdvisory(key), text);

    /// <summary>The person can go on: no required row failed (a warning or a skipped row does not keep them).</summary>
    public static bool CanContinue(IEnumerable<SetupTestRow> rows) => !rows.Any(r => !r.Advisory && r.State == Failed);
}
