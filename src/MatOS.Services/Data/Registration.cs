namespace MatOS.Services.Data;

/// <summary>
/// One registration from an address: the log behind the limit of registrations per address and day. It outlives a name that is given
/// back, so registering and deleting in a loop does not get around the limit.
/// </summary>
public sealed class Registration
{
    public long Id { get; set; }

    /// <summary>The address, or the /64 of an IPv6 address (<see cref="Configuration.Networks.LimitKey"/>).</summary>
    public string Ip { get; set; } = "";

    public DateTime CreateDate { get; set; }
}
