using MatOS.Services.Tunnel;

namespace MatOS.Services.Admin;

/// <summary>A connected tunnel as the page Tunnels shows it and its script refreshes it: everything already in words.</summary>
public sealed record LiveTunnel(
    string Name,
    string ConnectedAt,
    string ConnectedFor,
    string From,
    string Version,
    int Streams,
    string Served,
    string Bytes,
    string BytesToVisitor,
    string BytesToInstance)
{
    public static LiveTunnel Of(TunnelSession session, DateTimeOffset now) => new(
        session.Name,
        AdminFormat.Time(session.ConnectedAt),
        AdminFormat.Duration(now - session.ConnectedAt),
        AdminFormat.Text(session.RemoteAddress),
        AdminFormat.Text(session.Version),
        session.ActiveStreams,
        AdminFormat.Number(session.StreamsServed),
        AdminFormat.Bytes(session.BytesToInstance + session.BytesToVisitor),
        AdminFormat.Bytes(session.BytesToVisitor),
        AdminFormat.Bytes(session.BytesToInstance));
}

/// <summary>Tunnels that ended (the partial _Sessions): on the page Tunnels with the name of each, on an installation without.</summary>
public sealed record SessionList(string Caption, IReadOnlyList<Data.TunnelSessionEntry> Rows, bool WithName);

/// <summary>
/// The live list of the page Tunnels: <c>GET /admin/api/tunnels</c>, polled by js/services.js (same origin, signed in; 401 without a session,
/// 404 off the host of the API like the rest of the admin area). Longest connected first.
/// </summary>
public static class LiveTunnels
{
    public static IReadOnlyList<LiveTunnel> Of(TunnelRegistry registry, DateTimeOffset now) =>
        registry.Sessions.OrderBy(s => s.ConnectedAt).ThenBy(s => s.Name, StringComparer.Ordinal).Select(s => LiveTunnel.Of(s, now)).ToList();

    public static void MapLiveTunnels(this WebApplication app) =>
        app.MapGet(AdminArea.Path + "/api/tunnels", (TunnelRegistry registry, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            return Results.Json(new { at = AdminFormat.Time(now), tunnels = Of(registry, now) });
        }).RequireAuthorization(AdminAuth.Policy).RequireRateLimiting(AdminArea.PagePolicy);
}
