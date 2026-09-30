using Gelato.Streams;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using static Gelato.Tests.Streams.StreamDataFactory;

namespace Gelato.Tests.Streams;

/// <summary>
/// A port of AIOStreams' buildMediaStreams (packages/core/src/jellyfin/media.ts @ d029954).
/// Jellyfin computes VideoRangeType from colour and Dolby Vision fields rather than taking
/// it as input, so the range tests assert Jellyfin's own computed result - they prove the
/// fields set make Jellyfin reach the intended range.
/// </summary>
public class StreamMediaInfoTests
{
    private static StreamMediaInfoResult Build(
        ParsedFile? pf,
        long? size = null,
        long? durationMs = null,
        string? filename = null,
        string? url = null,
        string? type = "debrid"
    ) => StreamMediaInfo.Build(Data(pf, size, durationMs, filename, type), url, Iso);

    private static MediaStream Video(StreamMediaInfoResult r) =>
        Assert.Single(r.Streams, s => s.Type == MediaStreamType.Video);

    [Theory]
    [InlineData("2160p", 3840, 2160)]
    [InlineData("1440p", 2560, 1440)]
    [InlineData("1080p", 1920, 1080)]
    [InlineData("720p", 1280, 720)]
    [InlineData("576p", 720, 576)]
    [InlineData("480p", 640, 480)]
    [InlineData("360p", 480, 360)]
    [InlineData("240p", 320, 240)]
    [InlineData("144p", 256, 144)]
    public void Resolution_SetsDimensions(string resolution, int width, int height)
    {
        var v = Video(Build(Parsed(resolution: resolution)));

        Assert.Equal(width, v.Width);
        Assert.Equal(height, v.Height);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("4320p")]
    [InlineData(null)]
    public void UnknownResolution_LeavesDimensionsEmpty(string? resolution)
    {
        var v = Video(Build(Parsed(resolution: resolution)));

        Assert.Null(v.Width);
        Assert.Null(v.Height);
    }

    [Theory]
    [InlineData("HEVC", "hevc")]
    [InlineData("AVC", "h264")]
    [InlineData("AV1", "av1")]
    [InlineData("VC-1", "vc1")]
    [InlineData("XviD", "mpeg4")]
    [InlineData("DivX", "mpeg4")]
    [InlineData("MPEG-4", "mpeg4")]
    [InlineData("Unknown", null)]
    public void Encode_SetsCodec(string encode, string? codec)
    {
        Assert.Equal(codec, Video(Build(Parsed(encode: encode))).Codec);
    }

    [Theory]
    [InlineData(new string[0], VideoRangeType.SDR)]
    [InlineData(new[] { "SDR" }, VideoRangeType.SDR)]
    [InlineData(new[] { "10bit" }, VideoRangeType.SDR)]
    [InlineData(new[] { "IMAX", "3D" }, VideoRangeType.SDR)]
    [InlineData(new[] { "HDR10" }, VideoRangeType.HDR10)]
    [InlineData(new[] { "HDR" }, VideoRangeType.HDR10)]
    [InlineData(new[] { "HDR10+" }, VideoRangeType.HDR10Plus)]
    [InlineData(new[] { "HLG" }, VideoRangeType.HLG)]
    [InlineData(new[] { "DV" }, VideoRangeType.DOVI)]
    [InlineData(new[] { "DV", "HDR10" }, VideoRangeType.DOVIWithHDR10)]
    [InlineData(new[] { "HDR", "DV" }, VideoRangeType.DOVIWithHDR10)]
    [InlineData(new[] { "DV", "HDR10+" }, VideoRangeType.DOVIWithHDR10Plus)]
    [InlineData(new[] { "DV", "HLG" }, VideoRangeType.DOVIWithHLG)]
    public void VisualTags_MakeJellyfinComputeTheIntendedRange(
        string[] tags,
        VideoRangeType expected
    )
    {
        Assert.Equal(expected, Video(Build(Parsed(visualTags: tags))).VideoRangeType);
    }

    [Theory]
    [InlineData(new string[0], 8)]
    [InlineData(new[] { "10bit" }, 10)]
    [InlineData(new[] { "HDR10" }, 10)]
    [InlineData(new[] { "DV" }, 10)]
    public void BitDepth_IsTenForHdrOrTenBit(string[] tags, int bitDepth)
    {
        Assert.Equal(bitDepth, Video(Build(Parsed(visualTags: tags))).BitDepth);
    }

    [Fact]
    public void AudioTracks_BecomeAudioStreams()
    {
        var pf = Parsed(
            audioTracks:
            [
                Track(lang: "English", codec: "eac3", channels: "5.1", isDefault: true),
                Track(lang: "French", codec: null, tag: "DTS", channels: "7.1"),
                Track(lang: "Latino", codec: "aac", channels: "2.0"),
            ]
        );

        var audio = Build(pf).Streams.Where(s => s.Type == MediaStreamType.Audio).ToList();

        Assert.Equal(3, audio.Count);
        Assert.Equal(("eac3", "eng", 6, "5.1", true), Shape(audio[0]));
        Assert.Equal(("dts", "fre", 8, "7.1", false), Shape(audio[1]));
        Assert.Equal(("aac", null, 2, "stereo", false), Shape(audio[2]));

        static (string?, string?, int?, string?, bool) Shape(MediaStream s) =>
            (s.Codec, s.Language, s.Channels, s.ChannelLayout, s.IsDefault);
    }

    [Fact]
    public void FirstAudioTrack_IsDefault_WhenNoneSaysSo()
    {
        var pf = Parsed(audioTracks: [Track(), Track(lang: "French")]);

        var audio = Build(pf).Streams.Where(s => s.Type == MediaStreamType.Audio).ToList();

        Assert.True(audio[0].IsDefault);
        Assert.False(audio[1].IsDefault);
    }

    /// <summary>
    /// Jellyfin has native flags for forced, hearing-impaired and original, and adds their
    /// labels itself. Commentary, dubs and audio description have no field, so the title
    /// carries them - and only those, or the label would appear twice.
    /// </summary>
    [Fact]
    public void TrackTitles_CarryOnlyTheFlagsJellyfinLacks()
    {
        var pf = Parsed(
            audioTracks:
            [
                Track(title: "Director", commentary: true, forced: true, original: true),
                Track(title: "Dub commentary", dub: true, commentary: true),
                Track(title: null, visualImpaired: true, hearingImpaired: true),
            ]
        );

        var audio = Build(pf).Streams.Where(s => s.Type == MediaStreamType.Audio).ToList();

        Assert.Equal("Director Commentary", audio[0].Title);
        Assert.True(audio[0].IsOriginal);
        Assert.Equal("Dub commentary", audio[1].Title);
        Assert.Equal("Audio Description", audio[2].Title);
        Assert.True(audio[2].IsHearingImpaired);
    }

    [Fact]
    public void NoAudioTracks_ManyLanguages_GivesUnnamedPlaceholders()
    {
        var pf = Parsed(languages: ["English", "French", "Multi"], quality: "indexer");

        var audio = Build(pf).Streams.Where(s => s.Type == MediaStreamType.Audio).ToList();

        Assert.Equal(new[] { "Audio 1", "Audio 2" }, audio.Select(a => a.Title));
        Assert.All(audio, a => Assert.Null(a.Codec));
    }

    [Fact]
    public void NoAudioTracks_OneLanguage_FallsBackToTags()
    {
        var pf = Parsed(
            languages: ["English"],
            audioTags: ["Unknown", "DD+", "Atmos"],
            audioChannels: ["5.1"],
            quality: "addon"
        );

        var a = Assert.Single(Build(pf).Streams, s => s.Type == MediaStreamType.Audio);

        Assert.Equal("eac3", a.Codec);
        Assert.Equal("eng", a.Language);
        Assert.Equal(6, a.Channels);
        Assert.True(a.IsDefault);
    }

    [Fact]
    public void SubtitleTracks_AreUsedAtProbeQuality()
    {
        var pf = Parsed(
            quality: "probe",
            subtitles: ["English"],
            subtitleTracks:
            [
                Track(lang: "English", codec: "subrip", channels: null, forced: true),
                Track(lang: "French", codec: "hdmv_pgs_subtitle", channels: null),
            ]
        );

        var subs = Build(pf).Streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

        Assert.Equal(2, subs.Count);
        Assert.Equal(("subrip", "eng", true), (subs[0].Codec, subs[0].Language, subs[0].IsForced));
        Assert.True(subs[0].IsTextSubtitleStream);
        Assert.False(subs[1].IsTextSubtitleStream);
    }

    /// <summary>
    /// At probe quality an empty track list means AIOStreams probed the file and found no
    /// subtitles. Placeholders from the filename's language list would advertise subtitles the
    /// file doesn't have, on a row that is never re-probed.
    /// </summary>
    [Fact]
    public void ProbeQuality_WithNoSubtitleTracks_HasNoSubtitles()
    {
        var pf = Parsed(quality: "probe", subtitles: ["English", "French"]);

        Assert.DoesNotContain(Build(pf).Streams, s => s.Type == MediaStreamType.Subtitle);
    }

    [Fact]
    public void GuessedQuality_GivesSubtitlePlaceholders()
    {
        var one = Build(Parsed(quality: "indexer", subtitles: ["French"]));
        var many = Build(Parsed(quality: "indexer", subtitles: ["English", "French"]));

        var only = Assert.Single(one.Streams, s => s.Type == MediaStreamType.Subtitle);
        Assert.Equal(("fre", "French"), (only.Language, only.Title));
        Assert.Equal(
            new[] { "Subtitle 1", "Subtitle 2" },
            many.Streams.Where(s => s.Type == MediaStreamType.Subtitle).Select(s => s.Title)
        );
    }

    [Fact]
    public void NoQuality_GivesNoPlaceholders()
    {
        var r = Build(
            Parsed(
                quality: null,
                languages: ["English", "French"],
                subtitles: ["English", "French"]
            )
        );

        Assert.DoesNotContain(r.Streams, s => s.Type == MediaStreamType.Subtitle);
        Assert.Single(r.Streams, s => s.Type == MediaStreamType.Audio);
    }

    [Fact]
    public void Indexes_RunVideoThenAudioThenSubtitles()
    {
        var r = Build(Probed());

        Assert.Equal(Enumerable.Range(0, r.Streams.Count), r.Streams.Select(s => s.Index));
        Assert.Equal(MediaStreamType.Video, r.Streams[0].Type);
        Assert.Equal(MediaStreamType.Audio, r.Streams[1].Type);
        Assert.Equal(MediaStreamType.Subtitle, r.Streams[^1].Type);
    }

    [Theory]
    [InlineData("mkv", null, null, null, "debrid", "mkv")]
    [InlineData(null, "MP4", null, null, "debrid", "mp4")]
    [InlineData(null, null, "The.Film.avi", null, "debrid", "avi")]
    [InlineData(null, null, null, "https://h.example.com/f.webm?token=x", "debrid", "webm")]
    [InlineData(null, null, "noextension", "https://comet.feels.legal/playback/a", "debrid", "mkv")]
    [InlineData(null, null, null, null, "live", "ts")]
    [InlineData("iso", null, null, null, "debrid", "mkv")]
    public void Container_FollowsTheFallbackChain(
        string? container,
        string? extension,
        string? filename,
        string? url,
        string type,
        string expected
    )
    {
        var r = Build(
            Parsed(container: container, extension: extension),
            filename: filename,
            url: url,
            type: type
        );

        Assert.Equal(expected, r.Container);
    }

    [Fact]
    public void Runtime_ComesFromDurationMilliseconds()
    {
        var r = Build(Parsed(), durationMs: 5_160_000);

        Assert.Equal(TimeSpan.FromMinutes(86).Ticks, r.RunTimeTicks);
    }

    /// <summary>A zero runtime would force a probe on every play (runtime under 2 min).</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(null)]
    public void NonPositiveDuration_LeavesRuntimeEmpty(long? durationMs)
    {
        Assert.Null(Build(Parsed(), durationMs: durationMs).RunTimeTicks);
    }

    [Fact]
    public void Bitrate_IsTheAverageOfSizeOverDuration()
    {
        var r = Build(Parsed(), size: 15_000_000_000, durationMs: 5_000_000);

        Assert.Equal(24_000_000, Video(r).BitRate);
        Assert.Equal(15_000_000_000, r.Size);
    }

    [Fact]
    public void Bitrate_IsEmpty_WithoutSizeAndDuration()
    {
        Assert.Null(Video(Build(Parsed(), size: 1000)).BitRate);
    }

    [Fact]
    public void MissingParsedFile_GivesUntrustedVideoAndAudio()
    {
        var r = Build(null);

        Assert.Equal(
            new[] { MediaStreamType.Video, MediaStreamType.Audio },
            r.Streams.Select(s => s.Type)
        );
        Assert.All(r.Streams, s => Assert.Null(s.Codec));
        Assert.False(r.IsTrusted);
    }

    [Fact]
    public void Trusted_WhenProbedAndComplete()
    {
        Assert.True(Build(Probed()).IsTrusted);
    }

    public static TheoryData<ParsedFile> Incomplete() =>
        new()
        {
            Parsed(quality: "indexer", audioTracks: [Track()]),
            Parsed(quality: "addon", audioTracks: [Track()]),
            Parsed(quality: "probe", encode: "Unknown", audioTracks: [Track()]),
            Parsed(quality: "probe", resolution: "Unknown", audioTracks: [Track()]),
            Parsed(quality: "probe", audioTags: ["DD+"], audioChannels: ["5.1"]),
            Parsed(quality: "probe", audioTracks: [Track(codec: null, tag: "Unknown")]),
        };

    [Theory]
    [MemberData(nameof(Incomplete))]
    public void Untrusted_WhenAnythingJellyfinDecidesOnIsMissing(ParsedFile pf)
    {
        Assert.False(Build(pf).IsTrusted);
    }
}
