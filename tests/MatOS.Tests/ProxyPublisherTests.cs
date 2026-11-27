using MatOS.Core.Configuration;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;

namespace MatOS.Tests;

/// <summary>The certificates of an own domain (<see cref="SystemConfig.Certificates"/>): the wildcard route only with a DNS provider and without "one per app".</summary>
public class ProxyPublisherTests
{
    [Fact]
    public void WantsWildcard_needs_a_provider_and_not_one_certificate_per_app()
    {
        Assert.True(ProxyPublisher.WantsWildcard(new SystemConfig(), 7));
        Assert.True(ProxyPublisher.WantsWildcard(new SystemConfig { Certificates = CertificateMode.Wildcard }, 7));
        Assert.False(ProxyPublisher.WantsWildcard(new SystemConfig { Certificates = CertificateMode.PerApp }, 7));
        Assert.False(ProxyPublisher.WantsWildcard(new SystemConfig(), null));
        Assert.False(ProxyPublisher.WantsWildcard(new SystemConfig { Certificates = CertificateMode.Wildcard }, null));
    }

    [Fact]
    public void A_config_without_the_answer_keeps_the_wildcard()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<SystemConfig>("""{"BaseDomain":"example.com"}""")!;
        Assert.Equal(CertificateMode.Wildcard, config.Certificates);
    }
}
