namespace Gelato.Tests.Common;

/// <summary>
/// Which addon streams SyncStreams accepts. A stream is reachable either through a URL or,
/// with no URL, through a torrent info hash that the in-process P2P proxy serves. Torrent-only
/// streams were rejected outright once the URL check stopped consulting the info hash.
/// </summary>
public class StremioStreamValidityTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("https://cdn.example.com/dl/abc/movie.mkv")]
    [InlineData("http://host.example.com:8080/stream?id=1")]
    public void UrlWithAPath_IsValid(string url)
    {
        Assert.True(new StremioStream { Url = url }.IsValid());
    }

    /// <summary>
    /// A bare host is what some addons return in place of a real link; it plays nothing.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrPathlessUrl_WithoutAHash_IsInvalid(string url)
    {
        Assert.False(new StremioStream { Url = url }.IsValid());
    }

    [Theory]
    [InlineData(Hash)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    public void TorrentOnlyStream_IsValid(string infoHash)
    {
        Assert.True(new StremioStream { InfoHash = infoHash, FileIdx = 2 }.IsValid());
    }

    /// <summary>
    /// The hash is spliced into the P2P proxy path, so only a real BitTorrent v1 info hash
    /// (40 hex characters) stands in for a URL.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc123")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("zz23456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef01234567&idx=9")]
    public void MalformedHash_WithoutAUrl_IsInvalid(string infoHash)
    {
        Assert.False(new StremioStream { InfoHash = infoHash }.IsValid());
    }

    /// <summary>
    /// A stream that carries a URL plays from that URL (SyncStreams checks IsFile first), so
    /// its hash must not rescue a URL that plays nothing.
    /// </summary>
    [Fact]
    public void PathlessUrl_IsInvalid_EvenWithAHash()
    {
        Assert.False(new StremioStream { Url = "https://example.com/", InfoHash = Hash }.IsValid());
    }
}
