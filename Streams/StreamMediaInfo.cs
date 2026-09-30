using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

public sealed record StreamMediaInfoResult(
    IReadOnlyList<MediaStream> Streams,
    string Container,
    long? RunTimeTicks,
    long? Size,
    bool IsTrusted
);

/// <summary>
/// Builds Jellyfin media streams from AIOStreams' parsed stream data. A port of
/// buildMediaStreams in AIOStreams packages/core/src/jellyfin/media.ts @ d029954, adapted to
/// Jellyfin's model: Jellyfin computes VideoRange, VideoRangeType, DisplayTitle and
/// IsTextSubtitleStream itself, so this sets the fields they are computed from.
/// </summary>
public static class StreamMediaInfo
{
    private static readonly Dictionary<string, string> EncodeCodec = new(StringComparer.Ordinal)
    {
        ["AV1"] = "av1",
        ["HEVC"] = "hevc",
        ["AVC"] = "h264",
        ["VC-1"] = "vc1",
        ["XviD"] = "mpeg4",
        ["DivX"] = "mpeg4",
        ["MPEG-4"] = "mpeg4",
    };

    private static readonly Dictionary<string, string> AudioCodec = new(StringComparer.Ordinal)
    {
        ["Atmos"] = "truehd",
        ["TrueHD"] = "truehd",
        ["DTS:X"] = "dts",
        ["DTS-HD MA"] = "dts",
        ["DTS-HD"] = "dts",
        ["DTS-ES"] = "dts",
        ["DTS"] = "dts",
        ["DD+"] = "eac3",
        ["DD"] = "ac3",
        ["PCM"] = "pcm_s16le",
        ["OPUS"] = "opus",
        ["FLAC"] = "flac",
        ["AAC"] = "aac",
    };

    private static readonly Dictionary<string, (int Count, string Layout)> ChannelMap = new(
        StringComparer.Ordinal
    )
    {
        ["2.0"] = (2, "stereo"),
        ["5.1"] = (6, "5.1"),
        ["6.1"] = (7, "6.1"),
        ["7.1"] = (8, "7.1"),
    };

    private static readonly Dictionary<string, (int Width, int Height)> ResolutionSize = new(
        StringComparer.Ordinal
    )
    {
        ["2160p"] = (3840, 2160),
        ["1440p"] = (2560, 1440),
        ["1080p"] = (1920, 1080),
        ["720p"] = (1280, 720),
        ["576p"] = (720, 576),
        ["480p"] = (640, 480),
        ["360p"] = (480, 360),
        ["240p"] = (320, 240),
        ["144p"] = (256, 144),
    };

    private static readonly HashSet<string> NotATrack = new(StringComparer.Ordinal)
    {
        "Unknown",
        "Dual Audio",
        "Dubbed",
        "Multi",
        "Original",
    };

    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "mkv",
        "mp4",
        "avi",
        "mov",
        "m4v",
        "ts",
        "webm",
        "wmv",
        "flv",
        "m2ts",
        "mpg",
        "mpeg",
    };

    private enum VideoRangeKind
    {
        Sdr,
        Hdr10,
        Hdr10Plus,
        Hlg,
        Dovi,
        DoviHdr10,
        DoviHdr10Plus,
        DoviHlg,
    }

    public static StreamMediaInfoResult Build(
        StreamData data,
        string? url,
        Func<string, string?> toIso6392
    )
    {
        var pf = data.ParsedFile;
        var streams = new List<MediaStream>();

        var video = VideoStream(pf, Bitrate(data));
        streams.Add(video);

        var audio = AudioStreams(pf, streams.Count, toIso6392);
        streams.AddRange(audio);
        streams.AddRange(SubtitleStreams(pf, streams.Count, toIso6392));

        // Trusted only when AIOStreams probed the file AND everything Jellyfin decides direct
        // play on came out of that probe. Anything less is re-probed before playback.
        var trusted =
            pf is not null
            && pf.MediaInfoQuality == "probe"
            && video.Codec is not null
            && video.Width is not null
            && pf.AudioTracks.Count > 0
            && audio.All(a => !string.IsNullOrEmpty(a.Codec));

        return new StreamMediaInfoResult(
            streams,
            ContainerOf(pf, data.Filename, url, data.Type),
            RunTimeTicksOf(data.DurationMs),
            data.Size is > 0 ? data.Size : null,
            trusted
        );
    }

    /// <summary>Null when the duration would overflow a tick count, not just wrap around.</summary>
    private static long? RunTimeTicksOf(long? durationMs)
    {
        const long maxMs = long.MaxValue / TimeSpan.TicksPerMillisecond;
        return durationMs is > 0 and <= maxMs
            ? durationMs.Value * TimeSpan.TicksPerMillisecond
            : null;
    }

    private static int? Bitrate(StreamData d)
    {
        if (d.Size is not > 0 || d.DurationMs is not > 0)
            return null;

        var bitsPerSecond = d.Size.Value * 8.0 / (d.DurationMs.Value / 1000.0);
        return bitsPerSecond >= int.MaxValue ? int.MaxValue : (int)bitsPerSecond;
    }

    private static MediaStream VideoStream(ParsedFile? pf, int? bitrate)
    {
        (int Width, int Height)? size =
            pf?.Resolution is { } r && ResolutionSize.TryGetValue(r, out var wh) ? wh : null;
        var codec = pf?.Encode is { } e && EncodeCodec.TryGetValue(e, out var c) ? c : null;
        var tags = pf?.VisualTags ?? Array.Empty<string>();
        var range = RangeOf(tags);

        var stream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = codec,
            Width = size?.Width,
            Height = size?.Height,
            AspectRatio = size is { } s
                ? (s.Width / (double)s.Height >= 1.7 ? "16:9" : "4:3")
                : null,
            IsDefault = true,
            BitDepth = range != VideoRangeKind.Sdr || tags.Contains("10bit") ? 10 : 8,
            BitRate = bitrate,
        };

        ApplyRange(stream, range);
        return stream;
    }

    /// <summary>
    /// First match wins. The parser emits a DV file with an HDR10 base as ["DV", "HDR10"];
    /// AIOStreams collapses that to DOVI, but Jellyfin's direct-play decision differs between
    /// DOVI (profile 5, no fallback) and DOVI with HDR10 (profile 8.1), so keep them apart.
    /// </summary>
    private static VideoRangeKind RangeOf(IReadOnlyList<string> tags)
    {
        bool Has(string t) => tags.Contains(t, StringComparer.Ordinal);

        var dv = Has("DV");
        var hdr10Plus = Has("HDR10+");
        var hdr10 = Has("HDR10") || Has("HDR");
        var hlg = Has("HLG");

        if (dv && hdr10Plus)
            return VideoRangeKind.DoviHdr10Plus;
        if (dv && hdr10)
            return VideoRangeKind.DoviHdr10;
        if (dv && hlg)
            return VideoRangeKind.DoviHlg;
        if (dv)
            return VideoRangeKind.Dovi;
        if (hdr10Plus)
            return VideoRangeKind.Hdr10Plus;
        if (hdr10)
            return VideoRangeKind.Hdr10;
        return hlg ? VideoRangeKind.Hlg : VideoRangeKind.Sdr;
    }

    /// <summary>
    /// The inputs of MediaStream.GetVideoColorRange (Jellyfin v12.1): PQ or HLG transfer with
    /// BT.2020, and for Dolby Vision a profile plus the RPU/BL flags and compatibility id.
    /// </summary>
    private static void ApplyRange(MediaStream s, VideoRangeKind range)
    {
        switch (range)
        {
            case VideoRangeKind.Hdr10:
                Pq(s);
                break;
            case VideoRangeKind.Hdr10Plus:
                Pq(s);
                s.Hdr10PlusPresentFlag = true;
                break;
            case VideoRangeKind.Hlg:
                Hlg(s);
                break;
            case VideoRangeKind.Dovi:
                DolbyVision(s, profile: 5, compatibility: 0);
                break;
            case VideoRangeKind.DoviHdr10:
                DolbyVision(s, profile: 8, compatibility: 1);
                Pq(s);
                break;
            case VideoRangeKind.DoviHdr10Plus:
                DolbyVision(s, profile: 8, compatibility: 1);
                Pq(s);
                s.Hdr10PlusPresentFlag = true;
                break;
            case VideoRangeKind.DoviHlg:
                DolbyVision(s, profile: 8, compatibility: 4);
                Hlg(s);
                break;
        }
    }

    private static void Pq(MediaStream s)
    {
        s.ColorTransfer = "smpte2084";
        s.ColorPrimaries = "bt2020";
        s.ColorSpace = "bt2020nc";
    }

    private static void Hlg(MediaStream s)
    {
        s.ColorTransfer = "arib-std-b67";
        s.ColorPrimaries = "bt2020";
        s.ColorSpace = "bt2020nc";
    }

    private static void DolbyVision(MediaStream s, int profile, int compatibility)
    {
        s.DvProfile = profile;
        s.RpuPresentFlag = 1;
        s.BlPresentFlag = 1;
        s.DvBlSignalCompatibilityId = compatibility;
    }

    private static List<MediaStream> AudioStreams(
        ParsedFile? pf,
        int start,
        Func<string, string?> toIso6392
    )
    {
        if (pf is { AudioTracks.Count: > 0 })
        {
            return pf
                .AudioTracks.Select(
                    (t, i) =>
                    {
                        var channels = Channels(t.Channels);
                        return new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = start + i,
                            Codec = t.Codec ?? AudioCodecOf(t.Tag),
                            Language = t.Lang is { } lang ? toIso6392(lang) : null,
                            Title = TitleWithFlags(t),
                            Channels = channels?.Count,
                            ChannelLayout = channels?.Layout,
                            IsDefault = t.Default ?? i == 0,
                            IsHearingImpaired = t.HearingImpaired ?? false,
                            IsOriginal = t.Original ?? false,
                        };
                    }
                )
                .ToList();
        }

        var placeholders = PlaceholderLanguages(pf, pf?.Languages);
        if (placeholders.Count > 1)
        {
            return placeholders
                .Select(
                    (_, i) =>
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = start + i,
                            Title = $"Audio {i + 1}",
                            IsDefault = i == 0,
                        }
                )
                .ToList();
        }

        var languages = (pf?.Languages ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrEmpty(l) && l != "Unknown")
            .ToList();
        var audioTag = (pf?.AudioTags ?? Array.Empty<string>()).FirstOrDefault(t => t != "Unknown");
        var channelTag = (pf?.AudioChannels ?? Array.Empty<string>()).FirstOrDefault(c =>
            c != "Unknown"
        );
        var fallbackChannels = Channels(channelTag);

        return
        [
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = start,
                Codec = AudioCodecOf(audioTag),
                Language = languages.Count == 1 ? toIso6392(languages[0]) : null,
                Channels = fallbackChannels?.Count,
                ChannelLayout = fallbackChannels?.Layout,
                IsDefault = true,
            },
        ];
    }

    private static IEnumerable<MediaStream> SubtitleStreams(
        ParsedFile? pf,
        int start,
        Func<string, string?> toIso6392
    )
    {
        if (pf is not null && pf.MediaInfoQuality == "probe")
        {
            return pf.SubtitleTracks.Select(
                (t, i) =>
                    new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = start + i,
                        Codec = t.Codec,
                        Language = t.Lang is { } lang ? toIso6392(lang) : null,
                        Title = TitleWithFlags(t),
                        IsDefault = t.Default ?? false,
                        IsForced = t.Forced ?? false,
                        IsHearingImpaired = t.HearingImpaired ?? false,
                    }
            );
        }

        var languages = PlaceholderLanguages(pf, pf?.Subtitles);
        var only = languages.Count == 1 ? languages[0] : null;

        return languages.Select(
            (_, i) =>
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = start + i,
                    Language = only is not null ? toIso6392(only) : null,
                    Title = only ?? $"Subtitle {i + 1}",
                }
        );
    }

    /// <summary>One unnamed track per confirmed language; nothing without a quality tier.</summary>
    private static List<string> PlaceholderLanguages(ParsedFile? pf, IReadOnlyList<string>? list)
    {
        if (pf?.MediaInfoQuality is null)
            return new List<string>();

        return (list ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrEmpty(l) && !NotATrack.Contains(l))
            .ToList();
    }

    private static string? TitleWithFlags(MediaTrack t)
    {
        var lower = t.Title?.ToLowerInvariant() ?? "";
        var flags = new (bool? On, string Label)[]
        {
            (t.Dub, "Dub"),
            (t.Commentary, "Commentary"),
            (t.VisualImpaired, "Audio Description"),
        }
            .Where(f => f.On == true && !lower.Contains(f.Label.ToLowerInvariant()))
            .Select(f => f.Label);

        var parts = new[] { t.Title }
            .Concat(flags)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? AudioCodecOf(string? tag) =>
        tag is not null && AudioCodec.TryGetValue(tag, out var codec) ? codec : null;

    private static (int Count, string Layout)? Channels(string? tag) =>
        tag is not null && ChannelMap.TryGetValue(tag, out var c) ? c : null;

    private static string ContainerOf(ParsedFile? pf, string? filename, string? url, string? type)
    {
        var ext = new[]
        {
            pf?.Container,
            pf?.Extension,
            LastDotSegment(filename),
            LastDotSegment(url?.Split('?')[0]),
        }.FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var c = (ext ?? "").ToLowerInvariant().TrimStart('.');
        if (Containers.Contains(c))
            return c;

        return type == "live" ? "ts" : "mkv";
    }

    private static string? LastDotSegment(string? s) =>
        string.IsNullOrEmpty(s) ? null : s.Split('.')[^1];
}
