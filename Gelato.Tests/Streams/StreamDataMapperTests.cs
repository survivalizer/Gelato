using System.Text.Json;
using Gelato.Streams;

namespace Gelato.Tests.Streams;

/// <summary>
/// streamData is AIOStreams' own format and changes between releases. The mapper reads each
/// field only when it has the expected JSON type, so one malformed field costs that field -
/// never the stream, and never the rest of the response.
/// </summary>
public class StreamDataMapperTests
{
    private const string Full = """
        {
          "type": "debrid",
          "size": 15033561088,
          "duration": 5160000,
          "filename": "The.Runner.2026.2160p.mkv",
          "addon": "Comet",
          "message": "ok",
          "service": { "id": "torbox", "cached": true },
          "futureField": { "anything": [1, 2, 3] },
          "parsedFile": {
            "resolution": "2160p",
            "encode": "HEVC",
            "visualTags": ["DV", "HDR10"],
            "audioTags": ["DD+", "Atmos"],
            "audioChannels": ["5.1"],
            "languages": ["English"],
            "subtitles": ["English", "French"],
            "mediaInfoQuality": "probe",
            "container": "mkv",
            "extension": "mkv",
            "releaseGroup": "FLUX",
            "audioTracks": [
              { "lang": "English", "codec": "eac3", "channels": "5.1", "default": true,
                "commentary": false, "tag": "DD+" }
            ],
            "subtitleTracks": [
              { "lang": "English", "codec": "subrip", "forced": true, "hearingImpaired": false }
            ]
          }
        }
        """;

    [Fact]
    public void Maps_EveryFieldGelatoUses()
    {
        var d = StreamDataMapper.TryMap(Json.Parse(Full))!;

        Assert.Equal("debrid", d.Type);
        Assert.Equal(15033561088L, d.Size);
        Assert.Equal(5160000L, d.DurationMs);
        Assert.Equal("The.Runner.2026.2160p.mkv", d.Filename);
        Assert.Equal("Comet", d.Addon);
        Assert.Equal("ok", d.Message);
        Assert.Equal(new StreamService("torbox", true), d.Service);

        var pf = d.ParsedFile!;
        Assert.Equal("2160p", pf.Resolution);
        Assert.Equal("HEVC", pf.Encode);
        Assert.Equal(new[] { "DV", "HDR10" }, pf.VisualTags);
        Assert.Equal(new[] { "DD+", "Atmos" }, pf.AudioTags);
        Assert.Equal(new[] { "5.1" }, pf.AudioChannels);
        Assert.Equal(new[] { "English" }, pf.Languages);
        Assert.Equal(new[] { "English", "French" }, pf.Subtitles);
        Assert.Equal("probe", pf.MediaInfoQuality);
        Assert.Equal("mkv", pf.Container);
        Assert.Equal("mkv", pf.Extension);
        Assert.Equal("FLUX", pf.ReleaseGroup);

        var audio = Assert.Single(pf.AudioTracks);
        Assert.Equal("English", audio.Lang);
        Assert.Equal("eac3", audio.Codec);
        Assert.Equal("5.1", audio.Channels);
        Assert.Equal("DD+", audio.Tag);
        Assert.True(audio.Default);
        Assert.False(audio.Commentary);

        var sub = Assert.Single(pf.SubtitleTracks);
        Assert.Equal("subrip", sub.Codec);
        Assert.True(sub.Forced);
        Assert.False(sub.HearingImpaired);
    }

    [Fact]
    public void WrongTypedField_IsDropped_AndTheRestStillMaps()
    {
        var raw = Json.Parse(
            """{ "type": "http", "size": "big", "parsedFile": "oops", "duration": 5 }"""
        );

        var d = StreamDataMapper.TryMap(raw)!;

        Assert.Equal("http", d.Type);
        Assert.Null(d.Size);
        Assert.Null(d.ParsedFile);
        Assert.Equal(5L, d.DurationMs);
    }

    [Fact]
    public void NonStringListItems_AreSkipped()
    {
        var raw = Json.Parse(
            """{ "parsedFile": { "visualTags": ["DV", 3, null, "HDR10"], "languages": 7 } }"""
        );

        var pf = StreamDataMapper.TryMap(raw)!.ParsedFile!;

        Assert.Equal(new[] { "DV", "HDR10" }, pf.VisualTags);
        Assert.Empty(pf.Languages);
    }

    [Fact]
    public void FractionalNumbers_AreTruncated()
    {
        var d = StreamDataMapper.TryMap(Json.Parse("""{ "duration": 5160000.7 }"""))!;

        Assert.Equal(5160000L, d.DurationMs);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public void NonObject_YieldsNull(string json)
    {
        Assert.Null(StreamDataMapper.TryMap(Json.Parse(json)));
    }

    [Fact]
    public void Absent_YieldsNull()
    {
        Assert.Null(StreamDataMapper.TryMap(null));
    }

    private const string Response = """
        {
          "streams": [
            {
              "name": "TB 4K",
              "url": "https://cdn.example.com/dl/runner.mkv",
              "streamData": { "type": "debrid", "size": 100 }
            },
            { "name": "Plain addon stream", "url": "https://other.example.com/a.mp4" },
            { "name": "Bad data", "url": "https://x.example.com/b.mkv", "streamData": 42 },
            { "name": "[!] Error", "externalUrl": "https://docs.example.com/why" }
          ]
        }
        """;

    [Fact]
    public void StremioStream_CarriesStreamDataAndExternalUrl_PerStream()
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var streams = JsonSerializer.Deserialize<StremioStreamsResponse>(Response, opts)!.Streams;

        var mapped = streams.Select(s => StreamDataMapper.TryMap(s.StreamData)).ToList();

        Assert.Equal(100L, mapped[0]!.Size);
        Assert.Null(mapped[1]);
        Assert.Null(mapped[2]);
        Assert.Null(mapped[3]);
        Assert.Equal("https://docs.example.com/why", streams[3].ExternalUrl);
    }
}
