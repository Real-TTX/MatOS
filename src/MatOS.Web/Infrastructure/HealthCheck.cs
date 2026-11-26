using System.Text.RegularExpressions;

namespace MatOS.Web.Infrastructure;

/// <summary>The container's own health check: <c>dotnet MatOS.Web.dll --healthcheck</c> asks <c>/healthz</c> on localhost.</summary>
public static partial class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var match = PortRegex().Match(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? string.Empty);
        var port = match.Success ? match.Groups[1].Value : "8030";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }

    [GeneratedRegex(@":(\d+)")]
    private static partial Regex PortRegex();
}
