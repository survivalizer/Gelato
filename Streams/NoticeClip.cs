using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

/// <summary>
/// The clip a notice version plays: 4 s of black, 640x360 h264. The version's name already
/// carries the message, and it is served from loopback so it always proxies through Jellyfin.
/// </summary>
public static class NoticeClip
{
    public const string ResourceName = "Gelato.Assets.notice.mp4";
    public const string ContentType = "video/mp4";
    public const string Container = "mp4";

    public static Stream Open() =>
        typeof(NoticeClip).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");

    /// <summary>
    /// The id makes each notice's path unique: a new row's item id is derived from its path.
    /// The endpoint ignores it.
    /// </summary>
    public static string PathFor(int httpPort, Guid noticeGuid) =>
        $"http://127.0.0.1:{httpPort}/gelato/notice?id={noticeGuid:N}";

    public static IReadOnlyList<MediaStream> MediaStreams() =>
        [
            new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                Width = 640,
                Height = 360,
                BitDepth = 8,
                IsDefault = true,
            },
        ];
}
