using Gelato.Streams;

namespace Gelato.Tests.Streams;

internal static class StreamDataFactory
{
    public static readonly Func<string, string?> Iso = name =>
        name switch
        {
            "English" => "eng",
            "French" => "fre",
            _ => null,
        };

    public static ParsedFile Parsed(
        string? resolution = "1080p",
        string? encode = "HEVC",
        string[]? visualTags = null,
        string[]? audioTags = null,
        string[]? audioChannels = null,
        string[]? languages = null,
        string[]? subtitles = null,
        MediaTrack[]? audioTracks = null,
        MediaTrack[]? subtitleTracks = null,
        string? quality = "indexer",
        string? container = null,
        string? extension = null
    ) =>
        new(
            resolution,
            encode,
            visualTags ?? Array.Empty<string>(),
            audioTags ?? Array.Empty<string>(),
            audioChannels ?? Array.Empty<string>(),
            languages ?? Array.Empty<string>(),
            subtitles ?? Array.Empty<string>(),
            audioTracks ?? Array.Empty<MediaTrack>(),
            subtitleTracks ?? Array.Empty<MediaTrack>(),
            quality,
            container,
            extension,
            null
        );

    public static MediaTrack Track(
        string? lang = "English",
        string? codec = "eac3",
        string? channels = "5.1",
        string? tag = null,
        string? title = null,
        bool? isDefault = null,
        bool? forced = null,
        bool? commentary = null,
        bool? dub = null,
        bool? original = null,
        bool? hearingImpaired = null,
        bool? visualImpaired = null
    ) =>
        new(
            lang,
            codec,
            title,
            tag,
            channels,
            isDefault,
            forced,
            commentary,
            dub,
            original,
            hearingImpaired,
            visualImpaired
        );

    public static StreamData Data(
        ParsedFile? parsedFile,
        long? size = null,
        long? durationMs = null,
        string? filename = null,
        string? type = "debrid"
    ) => new(type, size, durationMs, filename, "Comet", null, null, parsedFile);

    /// <summary>A complete probe-quality file: what AIOStreams sends after probing.</summary>
    public static ParsedFile Probed(string[]? visualTags = null) =>
        Parsed(
            resolution: "2160p",
            encode: "HEVC",
            visualTags: visualTags ?? new[] { "HDR10" },
            audioTracks: [Track()],
            subtitleTracks: [Track(codec: "subrip", channels: null)],
            quality: "probe"
        );
}
