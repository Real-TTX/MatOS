using System.Text;

namespace MatOS.Web.Api;

/// <summary>
/// Thin server-side proxy to the sibling Matcad reverse-proxy manager's REST API.
/// matOS never exposes Matcad to the browser directly — the Proxy app calls these
/// admin-only matOS endpoints, which forward to Matcad over the internal Docker
/// network with the shared X-Api-Key. Base URL + key come from configuration
/// (MatOS:Matcad:ApiUrl / MatOS:Matcad:ApiKey, env MatOS__Matcad__ApiUrl / __ApiKey).
/// </summary>
public static class MatcadApi
{
    public static void MapMatcadApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/matcad").RequireAuthorization("Admin");

        // Reads (no body)
        foreach (var path in new[] { "status", "provider-types", "providers", "authentications",
                                     "routes", "routes/manual", "certificates", "settings" })
        {
            var p = path;
            g.MapGet("/" + p, (IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
                Forward(f, c, HttpMethod.Get, p, null, ct));
        }

        // Writes (forward the raw JSON body)
        g.MapPost("/providers", (HttpRequest req, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            ForwardBody(req, f, c, HttpMethod.Post, "providers", ct));
        g.MapPost("/providers/test", (HttpRequest req, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            ForwardBody(req, f, c, HttpMethod.Post, "providers/test", ct));
        g.MapDelete("/providers/{id:long}", (long id, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            Forward(f, c, HttpMethod.Delete, $"providers/{id}", null, ct));

        g.MapPost("/authentications", (HttpRequest req, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            ForwardBody(req, f, c, HttpMethod.Post, "authentications", ct));
        g.MapDelete("/authentications/{id:long}", (long id, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            Forward(f, c, HttpMethod.Delete, $"authentications/{id}", null, ct));

        g.MapPost("/routes", (HttpRequest req, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            ForwardBody(req, f, c, HttpMethod.Post, "routes", ct));
        g.MapDelete("/routes/{id:long}", (long id, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            Forward(f, c, HttpMethod.Delete, $"routes/{id}", null, ct));

        g.MapPut("/settings", (HttpRequest req, IHttpClientFactory f, IConfiguration c, CancellationToken ct) =>
            ForwardBody(req, f, c, HttpMethod.Put, "settings", ct));
    }

    private static async Task<IResult> ForwardBody(HttpRequest req, IHttpClientFactory f, IConfiguration c,
        HttpMethod method, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync(ct);
        return await Forward(f, c, method, path, body, ct);
    }

    private static async Task<IResult> Forward(IHttpClientFactory f, IConfiguration cfg,
        HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var baseUrl = (cfg["MatOS:Matcad:ApiUrl"] ?? "http://matcad:4433").TrimEnd('/');
        var key = cfg["MatOS:Matcad:ApiKey"] ?? "";
        if (string.IsNullOrWhiteSpace(key))
            return Results.Problem("Matcad integration is not configured (no API key set).", statusCode: 503);

        var http = f.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var msg = new HttpRequestMessage(method, $"{baseUrl}/api/v1/{path}");
        msg.Headers.Add("X-Api-Key", key);
        if (body != null) msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await http.SendAsync(msg, ct);
            var txt = await resp.Content.ReadAsStringAsync(ct);
            return Results.Content(txt, "application/json", Encoding.UTF8, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Cannot reach Matcad at {baseUrl}: {ex.Message}", statusCode: 502);
        }
    }
}
