using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>What became of a registration.</summary>
public enum RegistrationStatus
{
    Created,
    Invalid,
    Reserved,
    Taken,
    TooMany,

    /// <summary>The name is blocked by an administrator: it cannot be registered (again).</summary>
    Blocked,
}

/// <summary>A registration's outcome; <see cref="Key"/> is set when it was created (and is never stored).</summary>
public sealed record RegistrationResult(RegistrationStatus Status, string Name, string? Key = null);

/// <summary>
/// Registers and gives back names: the one way for the API (MatOS) and the public DynDNS page, so both obey the same rules and limits.
/// </summary>
public sealed class Registrar(
    ServicesOptions options, InstanceDirectory directory, RegistrationLock registrationLock, InstanceActivity activity, TunnelRegistry registry,
    CustomDomains domains, Usage.UsageRecorder usage,
    TimeProvider time, ILogger<Registrar> logger)
{
    /// <summary>Registrations count an IPv6 address as its /48 (see <see cref="ServicesOptions.RegistrationsPerIpPerDay"/>).</summary>
    public const int RegistrationPrefix = 48;

    /// <summary>
    /// Registers <paramref name="requested"/> for the client at <paramref name="clientIp"/>. A DynDNS-only name starts in direct mode
    /// without an address (the DNS answers nothing for it until the first update) and never uses the tunnel.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(
        ServicesDbContext db, string? requested, string? version, System.Net.IPAddress clientIp, bool dynDnsOnly, CancellationToken ct)
    {
        var name = NameRules.Normalize(requested);
        var problem = NameRules.Check(name);
        if (problem is not null)
        {
            return new RegistrationResult(problem == NameProblem.Reserved ? RegistrationStatus.Reserved : RegistrationStatus.Invalid, name);
        }

        var ip = Networks.Unmap(clientIp).ToString();
        var limitKey = Networks.LimitKey(clientIp, RegistrationPrefix);
        string key;
        using (await registrationLock.EnterAsync(ct))
        {
            var now = time.GetUtcNow().UtcDateTime;
            var lastDay = now.AddDays(-1);
            var lastHour = now.AddHours(-1);
            var fromHere = await db.Registrations.CountAsync(r => r.Ip == limitKey && r.CreateDate > lastDay, ct);
            var everybody = await db.Registrations.CountAsync(r => r.CreateDate > lastHour, ct);
            if (fromHere >= options.RegistrationsPerIpPerDay || everybody >= options.RegistrationsPerHour)
            {
                return new RegistrationResult(RegistrationStatus.TooMany, name);
            }

            if (await db.Instances.Where(i => i.Name == name).Select(i => (bool?)i.Blocked).FirstOrDefaultAsync(ct) is { } blocked)
            {
                return new RegistrationResult(blocked ? RegistrationStatus.Blocked : RegistrationStatus.Taken, name);
            }

            key = InstanceKeys.Create();
            db.Instances.Add(new Instance
            {
                Name = name,
                KeyHash = InstanceKeys.Hash(key),
                Mode = dynDnsOnly ? InstanceMode.Direct : InstanceMode.Tunnel,
                DynDnsOnly = dynDnsOnly,
                Version = Truncate(version, 64),
                CreateDate = now,
                LastSeen = now,
                CreatedFromIp = ip,
            });
            db.Registrations.Add(new Registration { Ip = limitKey, CreateDate = now });

            // The log only needs the last day; older entries go with each registration rather than with a job of their own.
            var old = now.AddDays(-2);
            await db.Registrations.Where(r => r.CreateDate < old).ExecuteDeleteAsync(ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The unique index decides if a name came in some other way meanwhile; the loser hears "taken".
                return new RegistrationResult(RegistrationStatus.Taken, name);
            }
        }

        await directory.RefreshAsync(name, ct);
        usage.Registered();
        logger.LogInformation("Registered {Name} ({Kind}) from {Ip}", name, dynDnsOnly ? "DynDNS" : "MatOS", ip);
        return new RegistrationResult(RegistrationStatus.Created, name, key);
    }

    /// <summary>Gives a name back: the row, the records and a tunnel go.</summary>
    public async Task ReleaseAsync(ServicesDbContext db, Instance instance, CancellationToken ct)
    {
        db.Instances.Remove(instance);
        await db.SaveChangesAsync(ct);
        await directory.RefreshAsync(instance.Name, CancellationToken.None);
        activity.Forget(instance.Name);
        await registry.CloseAsync(instance.Name);
        await domains.ReloadAsync(CancellationToken.None);
        usage.GaveBack();
        logger.LogInformation("{Name} was given back", instance.Name);
    }

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= length ? value.Trim() : value.Trim()[..length];
}
