using Gelato.Services;
using Gelato.Streams;
using MediaBrowser.Model.Entities;

namespace Gelato.Tests.Streams;

/// <summary>
/// Picking a notice plays a short placeholder instead of failing. It must ship inside the
/// plugin, and its path must stay on loopback so it is never handed to a client for direct
/// play or redirected.
/// </summary>
public class NoticeClipTests
{
    [Fact]
    public void EmbeddedClip_IsASmallMp4()
    {
        using var clip = NoticeClip.Open();
        using var copy = new MemoryStream();
        clip.CopyTo(copy);
        var bytes = copy.ToArray();

        Assert.InRange(bytes.Length, 100, 20 * 1024);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
    }

    [Fact]
    public void PathFor_IsUniquePerNotice_AndStaysOnLoopback()
    {
        var a = NoticeClip.PathFor(8096, Guid.NewGuid());
        var b = NoticeClip.PathFor(8096, Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.StartsWith("http://127.0.0.1:8096/gelato/notice?id=", a, StringComparison.Ordinal);
        Assert.False(DirectPlayPolicy.IsDirectPlayable(a));
    }

    [Fact]
    public void MediaStreams_IsOneH264Video()
    {
        var s = Assert.Single(NoticeClip.MediaStreams());

        Assert.Equal(MediaStreamType.Video, s.Type);
        Assert.Equal("h264", s.Codec);
        Assert.Equal((640, 360), (s.Width, s.Height));
    }
}
