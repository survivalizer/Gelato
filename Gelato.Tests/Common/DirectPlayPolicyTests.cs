using Gelato.Services;

namespace Gelato.Tests.Common;

/// <summary>
/// DirectPlayPolicy decides whether a stream URL may be handed to a client for direct play.
/// Only a URL a client elsewhere on the internet could actually reach qualifies: loopback
/// URLs (the in-process P2P proxy), private/LAN addresses, container hostnames and
/// placeholder paths must all stay masked so the bytes proxy through Jellyfin instead.
/// </summary>
public class DirectPlayPolicyTests
{
    [Theory]
    [InlineData("https://cdn.torbox.app/dl/abc123/movie.mkv")]
    [InlineData("http://example.com/file.mp4")]
    [InlineData("HTTPS://EXAMPLE.COM/UPPER.MKV")]
    [InlineData("https://comet.feels.legal/playback/abc/0")]
    [InlineData("https://host.example.com:8088/stream.mkv")]
    public void PubliclyReachableHttpUrl_IsDirectPlayable(string path)
    {
        Assert.True(DirectPlayPolicy.IsDirectPlayable(path));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096/gelato/stream?ih=abc&idx=0")]
    [InlineData("http://localhost:8096/gelato/stream?ih=abc")]
    [InlineData("http://[::1]:8096/gelato/stream?ih=abc")]
    [InlineData("http://127.5.5.5/anything")]
    public void LoopbackUrl_IsNeverDirectPlayable(string path)
    {
        Assert.False(DirectPlayPolicy.IsDirectPlayable(path));
    }

    /// <summary>
    /// Addons running beside Jellyfin hand back URLs resolved from inside the server's own
    /// network. The server can reach them; a remote client never can, so direct play on one
    /// of these is a guaranteed stall rather than a fast path.
    /// </summary>
    [Theory]
    [InlineData("https://10.0.0.5/private-lan.mkv")]
    [InlineData("http://192.168.1.50:8096/stream.mkv")]
    [InlineData("http://172.17.0.2:8088/stream.mkv")]
    [InlineData("http://172.31.255.254/stream.mkv")]
    [InlineData("http://169.254.1.1/link-local.mkv")]
    [InlineData("http://0.0.0.0/unspecified.mkv")]
    [InlineData("http://100.64.0.5/cgnat.mkv")]
    [InlineData("http://100.127.255.255/cgnat-top.mkv")]
    [InlineData("http://[fc00::1]/ula.mkv")]
    [InlineData("http://[fd12:3456:789a::1]/ula.mkv")]
    [InlineData("http://[fe80::1]/link-local.mkv")]
    public void PrivateOrNonRoutableAddress_IsNotDirectPlayable(string path)
    {
        Assert.False(DirectPlayPolicy.IsDirectPlayable(path));
    }

    /// <summary>
    /// A single-label hostname is a container or LAN name - a public host always carries a
    /// TLD. These are what Docker service discovery hands back (mycelium, aiostreams).
    /// </summary>
    [Theory]
    [InlineData("https://mycelium:8088/stream/abc.mkv")]
    [InlineData("http://aiostreams/stream/abc.mkv")]
    [InlineData("http://jellyfin:8096/stream.mkv")]
    [InlineData("https://mycelium./stream.mkv")]
    public void SingleLabelHostname_IsNotDirectPlayable(string path)
    {
        Assert.False(DirectPlayPolicy.IsDirectPlayable(path));
    }

    [Theory]
    [InlineData("http://server.local/stream.mkv")]
    [InlineData("http://box.internal/stream.mkv")]
    [InlineData("http://nas.lan/stream.mkv")]
    [InlineData("http://pi.home/stream.mkv")]
    [InlineData("http://svc.docker/stream.mkv")]
    [InlineData("http://host.localdomain/stream.mkv")]
    [InlineData("http://SERVER.LOCAL/upper.mkv")]
    public void InternalOnlySuffix_IsNotDirectPlayable(string path)
    {
        Assert.False(DirectPlayPolicy.IsDirectPlayable(path));
    }

    /// <summary>
    /// The private ranges are bounded: addresses just outside them are ordinary public
    /// addresses and must keep direct playing, or the fix would quietly disable the feature
    /// for legitimate hosts.
    /// </summary>
    [Theory]
    [InlineData("http://172.15.0.1/public.mkv")]
    [InlineData("http://172.32.0.1/public.mkv")]
    [InlineData("http://100.63.255.255/public.mkv")]
    [InlineData("http://100.128.0.1/public.mkv")]
    [InlineData("http://11.0.0.1/public.mkv")]
    [InlineData("http://192.167.1.1/public.mkv")]
    public void AddressOutsidePrivateRanges_IsDirectPlayable(string path)
    {
        Assert.True(DirectPlayPolicy.IsDirectPlayable(path));
    }

    [Theory]
    [InlineData("gelato://stub/0123456789abcdef")]
    [InlineData("/stub")]
    [InlineData("/data/movies/local.mkv")]
    [InlineData("C:\\media\\local.mkv")]
    [InlineData("ftp://example.com/file.mkv")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NonRemoteOrUnparseablePath_IsNotDirectPlayable(string? path)
    {
        Assert.False(DirectPlayPolicy.IsDirectPlayable(path));
    }

    /// <summary>
    /// Fail closed: a host that cannot be classified is not confirmed reachable either, so
    /// it proxies rather than gambling on a URL the client may not be able to open.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public void UnknownHost_IsTreatedAsInternal(string? host)
    {
        Assert.True(DirectPlayPolicy.IsInternalHost(host));
    }
}
