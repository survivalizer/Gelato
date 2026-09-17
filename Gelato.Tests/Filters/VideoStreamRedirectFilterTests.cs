using Gelato.Config;
using Gelato.Filters;
using Microsoft.AspNetCore.Http;

namespace Gelato.Tests.Filters;

/// <summary>
/// Putting the stream URL in the PlaybackInfo response only suggests direct play - a client
/// is free to ignore it and pull /Videos/{id}/stream from the server instead, which drags
/// every byte back through the Jellyfin host. Redirecting that endpoint is not a suggestion:
/// following a redirect is mandatory for an HTTP client, so the bytes go straight to the
/// debrid host whatever the client would have preferred.
/// </summary>
public class VideoStreamRedirectFilterTests
{
    private const string Remote = "https://cdn.torbox.app/dl/abc/movie.mkv";

    [Theory]
    [InlineData("GetVideoStream")]
    [InlineData("GetVideoStreamByContainer")]
    public void IsVideoStreamAction_MatchesTheStreamingEndpoints(string action)
    {
        Assert.True(VideoStreamRedirectFilter.IsVideoStreamAction(action));
    }

    [Theory]
    [InlineData("GetPlaybackInfo")]
    [InlineData("GetPostedPlaybackInfo")]
    [InlineData("GetDownload")]
    [InlineData("GetLatestMedia")]
    [InlineData("GetMasterHlsVideoPlaylist")]
    [InlineData("")]
    [InlineData(null)]
    public void IsVideoStreamAction_IgnoresEverythingElse(string? action)
    {
        Assert.False(VideoStreamRedirectFilter.IsVideoStreamAction(action));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public void IsStaticRequest_TrueWhenClientAsksForTheUntouchedFile(string value)
    {
        var q = new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["static"] = value,
            }
        );

        Assert.True(VideoStreamRedirectFilter.IsStaticRequest(q));
    }

    /// <summary>
    /// Without static=true the client is asking the server to transcode or remux. Redirecting
    /// it to the original file would hand back something it just said it cannot play, so
    /// those requests must fall through to Jellyfin untouched.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("")]
    public void IsStaticRequest_FalseWhenATranscodeWasAskedFor(string value)
    {
        var q = new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["static"] = value,
            }
        );

        Assert.False(VideoStreamRedirectFilter.IsStaticRequest(q));
    }

    [Fact]
    public void IsStaticRequest_FalseWhenAbsent()
    {
        Assert.False(VideoStreamRedirectFilter.IsStaticRequest(new QueryCollection()));
    }

    [Fact]
    public void ShouldRedirect_True_ForAStaticStreamOfADirectPlayableUrl()
    {
        var cfg = new PluginConfiguration { DirectPlay = true };

        Assert.True(
            VideoStreamRedirectFilter.ShouldRedirect(
                "GetVideoStream",
                isStatic: true,
                cfg,
                Guid.NewGuid(),
                Remote
            )
        );
    }

    [Fact]
    public void ShouldRedirect_False_WhenDirectPlayIsOff()
    {
        var cfg = new PluginConfiguration { DirectPlay = false };

        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect(
                "GetVideoStream",
                isStatic: true,
                cfg,
                Guid.NewGuid(),
                Remote
            )
        );
    }

    /// <summary>
    /// The per-user override has to reach here too, or a user opted out of direct play would
    /// still be redirected to the debrid host by the streaming endpoint.
    /// </summary>
    [Fact]
    public void ShouldRedirect_False_WhenTheUserOptedOut()
    {
        var user = Guid.NewGuid();
        var cfg = new PluginConfiguration
        {
            DirectPlay = true,
            UserConfigs = [new UserConfig { UserId = user, DirectPlay = false }],
        };

        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect("GetVideoStream", true, cfg, user, Remote)
        );
        Assert.True(
            VideoStreamRedirectFilter.ShouldRedirect(
                "GetVideoStream",
                true,
                cfg,
                Guid.NewGuid(),
                Remote
            )
        );
    }

    /// <summary>
    /// A client cannot reach a container hostname, so redirecting it there turns a working
    /// proxied stream into a stall - the same defect the reachability check exists to stop.
    /// </summary>
    [Theory]
    [InlineData("https://mycelium:8088/stream/abc.mkv")]
    [InlineData("http://192.168.1.50/stream.mkv")]
    [InlineData("http://127.0.0.1:8096/gelato/stream?ih=x")]
    [InlineData("/stub")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldRedirect_False_ForAnUnreachablePath(string? path)
    {
        var cfg = new PluginConfiguration { DirectPlay = true };

        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect("GetVideoStream", true, cfg, Guid.Empty, path)
        );
    }

    [Fact]
    public void ShouldRedirect_False_ForANonStreamingAction()
    {
        var cfg = new PluginConfiguration { DirectPlay = true };

        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect("GetDownload", true, cfg, Guid.Empty, Remote)
        );
    }

    [Fact]
    public void ShouldRedirect_False_WhenTheClientAskedForATranscode()
    {
        var cfg = new PluginConfiguration { DirectPlay = true };

        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect(
                "GetVideoStream",
                isStatic: false,
                cfg,
                Guid.Empty,
                Remote
            )
        );
    }

    [Fact]
    public void ShouldRedirect_False_WhenNoConfiguration()
    {
        Assert.False(
            VideoStreamRedirectFilter.ShouldRedirect(
                "GetVideoStream",
                true,
                null,
                Guid.Empty,
                Remote
            )
        );
    }
}
