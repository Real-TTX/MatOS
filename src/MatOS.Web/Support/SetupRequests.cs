using MatOS.Core.Setup;

namespace MatOS.Web.Support;

/// <summary>The request side of <see cref="SetupFlow"/>.</summary>
public static class SetupRequests
{
    /// <summary>This request's page at <c>127.0.0.1</c> when the browser came as <c>localhost</c>, else null (<see cref="SetupFlow.LoopbackOf"/>).</summary>
    public static string? LoopbackOf(this HttpRequest request, string path)
        => SetupFlow.LoopbackOf(request.Scheme, request.Host.Host, request.Host.Port, path);
}
