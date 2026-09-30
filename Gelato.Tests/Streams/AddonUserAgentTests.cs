using System.Net.Http.Headers;
using Gelato.Streams;

namespace Gelato.Tests.Streams;

/// <summary>
/// AIOStreams includes its parsed stream data only for User-Agents that start with "AIO"
/// (packages/server/src/routes/stremio/stream.ts). Without it Gelato falls back to probing
/// every stream, so the prefix is load-bearing, not cosmetic.
/// </summary>
public class AddonUserAgentTests
{
    [Fact]
    public void For_NamesTheProductAndVersion()
    {
        var ua = AddonUserAgent.For(new Version(0, 26, 20, 0));

        Assert.Equal("AIOStreams-Gelato/0.26.20.0", ua);
    }

    [Fact]
    public void For_WithoutAVersion_UsesZero()
    {
        Assert.Equal("AIOStreams-Gelato/0.0.0.0", AddonUserAgent.For(null));
    }

    [Fact]
    public void Value_StartsWithAio_AndNamesGelato()
    {
        var ua = AddonUserAgent.For(new Version(1, 2, 3, 4));

        Assert.StartsWith("AIO", ua, StringComparison.Ordinal);
        Assert.Contains("Gelato", ua, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_IsAValidUserAgentProduct()
    {
        var ua = AddonUserAgent.For(new Version(0, 26, 20, 0));

        Assert.True(ProductInfoHeaderValue.TryParse(ua, out _));
    }

    [Fact]
    public void Apply_ReplacesTheClientsUserAgent()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Previous/1.0");

        AddonUserAgent.Apply(client);

        var expected = AddonUserAgent.For(typeof(AddonUserAgent).Assembly.GetName().Version);
        Assert.Equal(expected, client.DefaultRequestHeaders.UserAgent.ToString());
    }
}
