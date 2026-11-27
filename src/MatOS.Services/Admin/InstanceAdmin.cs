using MatOS.Services.Data;
using MatOS.Services.Instances;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Admin;

/// <summary>
/// What an administrator can do to an installation: end its tunnel, put it back into tunnel mode, block and unblock its name, give the name
/// back. Each writes an <see cref="AdminAction"/> (who, what, which name, when).
/// </summary>
public sealed class InstanceAdmin(
    ServicesDbContext db, InstanceDirectory directory, TunnelRegistry registry, Registrar registrar, TimeProvider time, ILogger<InstanceAdmin> logger)
{
    public const int MaxReasonLength = 300;

    /// <summary>Ends the tunnel; MatOS connects again by itself (it is not blocked). False when none was connected.</summary>
    public async Task<bool> DisconnectAsync(Instance instance, string by, CancellationToken ct)
    {
        var closed = await registry.CloseAsync(instance.Name, "disconnected by an administrator");
        await AuditAsync(by, AdminActions.Disconnect, instance.Name, null, ct);
        return closed;
    }

    /// <summary>The DNS points at the edge again; the installation reaches the visitors through its tunnel.</summary>
    public async Task TunnelModeAsync(Instance instance, string by, CancellationToken ct)
    {
        instance.Mode = InstanceMode.Tunnel;
        instance.UpdateDate = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await directory.RefreshAsync(instance.Name, ct);
        await AuditAsync(by, AdminActions.TunnelMode, instance.Name, null, ct);
    }

    /// <summary>Blocks the name: its tunnel ends, and the API, the tunnel, the edge, the DNS and <c>/nic/update</c> refuse it from now on.</summary>
    public async Task BlockAsync(Instance instance, string reason, string by, CancellationToken ct)
    {
        reason = reason.Trim();
        instance.Blocked = true;
        instance.BlockedReason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
        instance.BlockedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await directory.RefreshAsync(instance.Name, ct);
        await registry.CloseAsync(instance.Name, "the name was blocked");
        await AuditAsync(by, AdminActions.Block, instance.Name, instance.BlockedReason, ct);
        logger.LogWarning("{Name} was blocked by {By}: {Reason}", instance.Name, by, instance.BlockedReason);
    }

    public async Task UnblockAsync(Instance instance, string by, CancellationToken ct)
    {
        instance.Blocked = false;
        instance.BlockedReason = null;
        instance.BlockedAt = null;
        await db.SaveChangesAsync(ct);
        await directory.RefreshAsync(instance.Name, ct);
        await AuditAsync(by, AdminActions.Unblock, instance.Name, null, ct);
        logger.LogInformation("{Name} was unblocked by {By}", instance.Name, by);
    }

    /// <summary>Gives the name back as the API's give-back does: the row, its domains and its tunnel go, and the name is free again.</summary>
    public async Task GiveBackAsync(Instance instance, string by, CancellationToken ct)
    {
        var name = instance.Name;
        await registrar.ReleaseAsync(db, instance, ct);
        await AuditAsync(by, AdminActions.GiveBack, name, null, ct);
    }

    /// <summary>The audit of a name, newest first.</summary>
    public Task<List<AdminAction>> HistoryAsync(string name, CancellationToken ct) =>
        db.AdminActions.AsNoTracking().Where(a => a.InstanceName == name).OrderByDescending(a => a.CreateDate).ThenByDescending(a => a.Id)
            .Take(50).ToListAsync(ct);

    private async Task AuditAsync(string by, string action, string name, string? detail, CancellationToken ct)
    {
        db.AdminActions.Add(new AdminAction { UserEmail = by, Action = action, InstanceName = name, Detail = detail });
        await db.SaveChangesAsync(ct);
    }
}
