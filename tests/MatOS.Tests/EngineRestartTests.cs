using System.Net;
using System.Text;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;

namespace MatOS.Tests;

/// <summary>
/// An engine that is being redeployed refuses connections for a few seconds (a new MatSMTP redeploys the apps that send mail, MatDock among them): a job then
/// waits for it instead of failing. Only "status" answers at once, because it is how MatOS asks whether an engine is there.
/// </summary>
[Collection(nameof(EngineRestartTests))]
public class EngineRestartTests
{
    private sealed class RefusingHandler(int refusals) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= refusals)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (engine.test:4455)");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Locator : EngineLocator
    {
        public Locator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://engine.test", "key"));
    }

    private static async Task<T> WithShortWaits<T>(TimeSpan grace, Func<Task<T>> action)
    {
        var (oldGrace, oldDelay) = (EngineClient.StartGrace, EngineClient.RetryDelay);
        EngineClient.StartGrace = grace;
        EngineClient.InJob.Value = true;
        EngineClient.RetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            return await action();
        }
        finally
        {
            (EngineClient.StartGrace, EngineClient.RetryDelay) = (oldGrace, oldDelay);
        }
    }

    [Fact]
    public async Task A_request_waits_for_an_engine_that_is_starting()
    {
        var handler = new RefusingHandler(refusals: 3);
        var client = new MatSmtpClient(new Locator(), new Factory(handler));

        var users = await WithShortWaits(TimeSpan.FromSeconds(5), () => client.ListRelayUsersAsync(CancellationToken.None));

        Assert.Empty(users);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task It_gives_up_after_the_grace_and_says_the_engine_is_not_reachable()
    {
        var handler = new RefusingHandler(refusals: int.MaxValue);
        var client = new MatSmtpClient(new Locator(), new Factory(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithShortWaits(TimeSpan.FromMilliseconds(100), () => client.ListRelayUsersAsync(CancellationToken.None)));

        Assert.Contains("not reachable", ex.Message);
        Assert.True(handler.Calls > 1);
    }

    [Fact]
    public async Task A_page_does_not_wait_only_a_job_does()
    {
        // Outside a job (a page) a refused connection fails at once instead of hanging the page while the engine restarts.
        var handler = new RefusingHandler(refusals: 3);
        var client = new MatSmtpClient(new Locator(), new Factory(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListRelayUsersAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task The_status_answers_at_once()
    {
        var handler = new RefusingHandler(refusals: int.MaxValue);
        var client = new MatSmtpClient(new Locator(), new Factory(handler));

        var status = await WithShortWaits(TimeSpan.FromSeconds(5), () => client.GetStatusAsync(CancellationToken.None));

        Assert.Null(status);
        Assert.Equal(1, handler.Calls);
    }
}
