using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>
/// The <c>Authorization: Bearer &lt;key&gt;</c> of an installation. A small helper rather than an authentication scheme: there is one kind of
/// caller and one question (which installation is this), and the endpoints stay readable.
/// </summary>
public static class InstanceAuth
{
    public static async Task<Instance?> AuthenticateAsync(HttpContext context, ServicesDbContext db, bool tracking = true)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = header[scheme.Length..].Trim();
        if (!InstanceKeys.LooksLikeKey(key))
        {
            return null;
        }

        var hash = InstanceKeys.Hash(key);
        var query = tracking ? db.Instances : db.Instances.AsNoTracking();
        return await query.FirstOrDefaultAsync(i => i.KeyHash == hash, context.RequestAborted);
    }
}
