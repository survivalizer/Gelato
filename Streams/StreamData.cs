using System.Text.Json;

namespace Gelato.Streams;

/// <summary>
/// The subset of AIOStreams' per-stream <c>streamData</c> that Gelato uses. AIOStreams sends
/// it when the request's User-Agent starts with "AIO" (see <see cref="AddonUserAgent"/>).
/// Every field is optional: other addons never send it, and AIOStreams can add or change
/// fields in any release.
/// </summary>
public sealed record StreamData(
    string? Type,
    long? Size,
    long? DurationMs,
    string? Filename,
    string? Addon,
    string? Message,
    StreamService? Service,
    ParsedFile? ParsedFile
);

public sealed record StreamService(string? Id, bool? Cached);

public sealed record ParsedFile(
    string? Resolution,
    string? Encode,
    IReadOnlyList<string> VisualTags,
    IReadOnlyList<string> AudioTags,
    IReadOnlyList<string> AudioChannels,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Subtitles,
    IReadOnlyList<MediaTrack> AudioTracks,
    IReadOnlyList<MediaTrack> SubtitleTracks,
    string? MediaInfoQuality,
    string? Container,
    string? Extension,
    string? ReleaseGroup
);

public sealed record MediaTrack(
    string? Lang,
    string? Codec,
    string? Title,
    string? Tag,
    string? Channels,
    bool? Default,
    bool? Forced,
    bool? Commentary,
    bool? Dub,
    bool? Original,
    bool? HearingImpaired,
    bool? VisualImpaired
);

/// <summary>
/// Maps raw <c>streamData</c> JSON to <see cref="StreamData"/>. Each field is read only when
/// it has the expected JSON type, so a malformed field is dropped instead of discarding the
/// stream - or, with a strict deserializer, the whole response.
/// </summary>
public static class StreamDataMapper
{
    public static StreamData? TryMap(JsonElement? raw)
    {
        if (raw is not { } o || o.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return new StreamData(
                Str(o, "type"),
                Long(o, "size"),
                Long(o, "duration"),
                Str(o, "filename"),
                Str(o, "addon"),
                Str(o, "message"),
                Obj(o, "service") is { } svc
                    ? new StreamService(Str(svc, "id"), Bool(svc, "cached"))
                    : null,
                Obj(o, "parsedFile") is { } pf ? MapParsedFile(pf) : null
            );
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ParsedFile MapParsedFile(JsonElement pf) =>
        new(
            Str(pf, "resolution"),
            Str(pf, "encode"),
            Strings(pf, "visualTags"),
            Strings(pf, "audioTags"),
            Strings(pf, "audioChannels"),
            Strings(pf, "languages"),
            Strings(pf, "subtitles"),
            Tracks(pf, "audioTracks"),
            Tracks(pf, "subtitleTracks"),
            Str(pf, "mediaInfoQuality"),
            Str(pf, "container"),
            Str(pf, "extension"),
            Str(pf, "releaseGroup")
        );

    private static IReadOnlyList<MediaTrack> Tracks(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];

        return v.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object)
            .Select(t => new MediaTrack(
                Str(t, "lang"),
                Str(t, "codec"),
                Str(t, "title"),
                Str(t, "tag"),
                Str(t, "channels"),
                Bool(t, "default"),
                Bool(t, "forced"),
                Bool(t, "commentary"),
                Bool(t, "dub"),
                Bool(t, "original"),
                Bool(t, "hearingImpaired"),
                Bool(t, "visualImpaired")
            ))
            .ToList();
    }

    private static IReadOnlyList<string> Strings(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];

        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static JsonElement? Obj(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static long? Long(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return null;

        if (v.TryGetInt64(out var whole))
            return whole;

        return v.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < long.MaxValue
            ? (long)d
            : null;
    }
}
