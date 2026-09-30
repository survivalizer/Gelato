# AIOStreams Stream Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gelato requests AIOStreams' parsed stream data, turns it into Jellyfin media info so
every version shows resolution/HDR/audio/subtitles before playback, skips ffprobe when
AIOStreams already probed the file, and shows addon notices and errors as versions instead of
dropping them.

**Architecture:** Six small, pure units in a new `Streams/` folder (`Gelato.Streams`
namespace) hold every decision: request identity, stream-data parsing, media-info building,
provenance rules, stream classification, and per-row write planning. The existing code only
wires them in: `GelatoStremioProvider` (User-Agent, new stream fields), `GelatoManager.SyncStreams`
(classification, media-info writes, notices), `MediaSourceManagerDecorator` (probe gate), and
`GelatoApiController` (notice clip endpoint).

**Tech Stack:** C# / .NET 10, Jellyfin 12.1 plugin APIs (`Jellyfin.Controller` /
`Jellyfin.Model` 12.1.0), System.Text.Json, xUnit 2.9.3, NSubstitute 6.2.0.

**Spec:** `docs/superpowers/specs/2026-09-29-aiostreams-stream-data-design.md`

## Global Constraints

- Target Jellyfin 12.1 and .NET 10 only; no new NuGet dependencies.
- AIOStreams behaviour and tables are ported from `Viren070/AIOStreams` @
  `d029954da44802fbb152ca5583aa2b468c45cdb0`.
- Addon User-Agent is exactly `AIOStreams-Gelato/<plugin assembly version>`.
- Other addons behave exactly as today, except that URL-less streams with text become notice
  versions.
- ffprobe media info is never overwritten by a guess; upgrading re-probes nothing and loses
  nothing.
- Torrent-only streams (hash, no URL) stay rejected, as `StremioStream.IsValid()` does today.
- TDD: every test is written first and watched fail before the code that passes it.
- Format new files with `dotnet csharpier format <file>`. **Never run csharpier on
  `GelatoManager.cs`, `GelatoStremioProvider.cs` or `Decorators/MediaSourceManagerDecorator.cs`**
  — they already fail `csharpier check` on `main`; edit them by hand.
- Every added line is at most 100 columns.
- Test command: `dotnet test Gelato.Tests/Gelato.Tests.csproj` (add `--filter` per task).
- Commits are conventional commits whose message ends with
  `Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22`.
- Work on branch `feat/aiostreams-stream-data`, created from `docs/aiostreams-stream-data-spec`.
- `gh` commands always pass `-R survivalizer/Gelato`; never act on `lostb1t/Gelato`; never run
  `make publish`, `make release` or `make prerelease` locally.
- AIOStreams URLs contain the user's token: never print, log or commit one. Fixtures are
  scrubbed before they touch the repo.

## Review Focus

1. **Upgrading an existing library** — a legacy stream row that ffprobe already filled (no
   provenance, has a video stream) keeps its media info and is stamped `ffprobe`; it is never
   overwritten by a guess. Pinned by Task 6
   `LegacyRowAlreadyProbed_IsStampedNotOverwritten`.
2. **Re-browsing a title with hundreds of unchanged versions** — no media-stream writes and no
   media-stream lookups. Pinned by Task 6 `RowsWithProvenance_AreNeitherRewrittenNorQueried`.
3. **An error-only AIOStreams response (e.g. invalid credentials)** — the error shows up, and
   the sync is not cached as successful, so the next view retries. Pinned by Task 4
   `ErrorOnlyResponse_IsANoticeWithNothingPlayable` and Task 6's return value.
4. **An AIOStreams error or info stream that carries a valid URL** — shown as a notice, never
   offered as a playable version. Pinned by Task 4
   `NoticeTypes_AreNotices_EvenWithAValidUrl`.
5. **A statistics or error notice whose description changes every sync** (timings, counts) —
   keeps one row, updated in place, instead of being deleted and recreated. Pinned by Task 4
   `NoticeGuid_IgnoresTheDescription`.

---

## File structure

| File | Responsibility |
|---|---|
| `Streams/AddonUserAgent.cs` (new) | The User-Agent string and applying it to an `HttpClient` |
| `Streams/StreamData.cs` (new) | Typed `streamData` model and the lenient JSON mapper |
| `Streams/StreamMediaInfo.cs` (new) | Stream data → Jellyfin `MediaStream`s + trust verdict |
| `Streams/MediaInfoProvenance.cs` (new) | Provenance values; probe and write decisions |
| `Streams/StreamClassifier.cs` (new) | Playable vs notice vs dropped; order; notice identity |
| `Streams/NoticeClip.cs` (new) | The embedded placeholder clip, its path and media info |
| `Streams/StreamRowPlanner.cs` (new) | Per-row media-info write plan for `SyncStreams` |
| `Streams/ProbeGate.cs` (new) | Reads a row's provenance/notice flag for the probe decision |
| `Assets/notice.mp4` (new) | 4 s black 640×360 h264 clip, ~2 KB |
| `scripts/capture-aiostreams-fixture.sh` (new) | Captures and scrubs a real AIOStreams response |
| `GelatoStremioProvider.cs` | `NewClient()` applies the User-Agent; `StremioStream` gains `ExternalUrl`, `StreamData` |
| `GelatoManager.cs` | Constructor gains two services; `SyncStreams` uses the new units |
| `Decorators/MediaSourceManagerDecorator.cs` | Probe gate; `ProbeStreamAsync` returns `bool` |
| `Controllers/GelatoApiController.cs` | `GET /gelato/notice` |
| `Gelato.csproj` | Embeds `Assets/notice.mp4` |
| `Gelato.Tests/Gelato.Tests.csproj` | Copies the captured fixture to the test output |
| `Gelato.Tests/Streams/*` (new) | Tests, a JSON helper, a stream-data factory, the fixture |

---

### Task 1: Request and parse AIOStreams stream data

**Files:**
- Create: `Streams/AddonUserAgent.cs`, `Streams/StreamData.cs`
- Modify: `GelatoStremioProvider.cs` (`NewClient()` ~line 38; `class StremioStream` ~line 804)
- Test: `Gelato.Tests/Streams/Json.cs`, `Gelato.Tests/Streams/AddonUserAgentTests.cs`,
  `Gelato.Tests/Streams/StreamDataMapperTests.cs`

**Interfaces:**
- Produces:
  - `static class AddonUserAgent { const string Product; static string For(Version?); static void Apply(HttpClient) }`
  - `record StreamData(string? Type, long? Size, long? DurationMs, string? Filename, string? Addon, string? Message, StreamService? Service, ParsedFile? ParsedFile)`
  - `record StreamService(string? Id, bool? Cached)`
  - `record ParsedFile(string? Resolution, string? Encode, IReadOnlyList<string> VisualTags, IReadOnlyList<string> AudioTags, IReadOnlyList<string> AudioChannels, IReadOnlyList<string> Languages, IReadOnlyList<string> Subtitles, IReadOnlyList<MediaTrack> AudioTracks, IReadOnlyList<MediaTrack> SubtitleTracks, string? MediaInfoQuality, string? Container, string? Extension, string? ReleaseGroup)`
  - `record MediaTrack(string? Lang, string? Codec, string? Title, string? Tag, string? Channels, bool? Default, bool? Forced, bool? Commentary, bool? Dub, bool? Original, bool? HearingImpaired, bool? VisualImpaired)`
  - `static class StreamDataMapper { static StreamData? TryMap(JsonElement? raw) }`
  - `StremioStream.ExternalUrl : string?`, `StremioStream.StreamData : JsonElement?`
  - Test helper `Gelato.Tests.Streams.Json.Parse(string) : JsonElement`

- [ ] **Step 1: Create the branch**

```bash
git checkout docs/aiostreams-stream-data-spec
git checkout -b feat/aiostreams-stream-data
```

- [ ] **Step 2: Write the test JSON helper**

Create `Gelato.Tests/Streams/Json.cs`:

```csharp
using System.Text.Json;

namespace Gelato.Tests.Streams;

internal static class Json
{
    /// <summary>A detached element that outlives the document it was parsed from.</summary>
    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
```

- [ ] **Step 3: Write the failing User-Agent tests**

Create `Gelato.Tests/Streams/AddonUserAgentTests.cs`:

```csharp
using System.Net.Http.Headers;
using Gelato.Streams;

namespace Gelato.Tests.Streams;

/// <summary>
/// AIOStreams includes its parsed stream data only for User-Agents that start with "AIO"
/// (packages/server/src/routes/stremio/stream.ts). Without it Gelato falls back to probing
/// every stream, so the prefix is load-bearing, not cosmetic.
/// </summary>
public class AddonUserAgentTests
{
    [Fact]
    public void For_NamesTheProductAndVersion()
    {
        var ua = AddonUserAgent.For(new Version(0, 26, 20, 0));

        Assert.Equal("AIOStreams-Gelato/0.26.20.0", ua);
    }

    [Fact]
    public void For_WithoutAVersion_UsesZero()
    {
        Assert.Equal("AIOStreams-Gelato/0.0.0.0", AddonUserAgent.For(null));
    }

    [Fact]
    public void Value_StartsWithAio_AndNamesGelato()
    {
        var ua = AddonUserAgent.For(new Version(1, 2, 3, 4));

        Assert.StartsWith("AIO", ua, StringComparison.Ordinal);
        Assert.Contains("Gelato", ua, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_IsAValidUserAgentProduct()
    {
        var ua = AddonUserAgent.For(new Version(0, 26, 20, 0));

        Assert.True(ProductInfoHeaderValue.TryParse(ua, out _));
    }

    [Fact]
    public void Apply_ReplacesTheClientsUserAgent()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Previous/1.0");

        AddonUserAgent.Apply(client);

        var expected = AddonUserAgent.For(typeof(AddonUserAgent).Assembly.GetName().Version);
        Assert.Equal(expected, client.DefaultRequestHeaders.UserAgent.ToString());
    }
}
```

- [ ] **Step 4: Write the failing mapper tests**

Create `Gelato.Tests/Streams/StreamDataMapperTests.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~Gelato.Tests.Streams"`
Expected: build FAILS with `error CS0234: The type or namespace name 'Streams' does not exist in the namespace 'Gelato'`.

- [ ] **Step 6: Implement the User-Agent**

Create `Streams/AddonUserAgent.cs`:

```csharp
namespace Gelato.Streams;

/// <summary>
/// The User-Agent Gelato sends to Stremio addons. AIOStreams includes its parsed stream data
/// (<c>streamData</c>) for User-Agents starting with "AIO" unless its operator overrides
/// that; "Gelato" in the product name lets AIOStreams variants - configs switched on the
/// User-Agent - single out Jellyfin.
/// </summary>
public static class AddonUserAgent
{
    public const string Product = "AIOStreams-Gelato";

    public static string For(Version? version) =>
        $"{Product}/{version ?? new Version(0, 0, 0, 0)}";

    public static void Apply(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            For(typeof(AddonUserAgent).Assembly.GetName().Version)
        );
    }
}
```

- [ ] **Step 7: Implement the model and mapper**

Create `Streams/StreamData.cs`:

```csharp
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
```

- [ ] **Step 8: Wire the provider (hand-edit; do not run csharpier on this file)**

In `GelatoStremioProvider.cs`, replace `NewClient()`:

```csharp
    private HttpClient NewClient()
    {
        var c = http.CreateClient(nameof(GelatoStremioProvider));
        c.Timeout = TimeSpan.FromSeconds(30);
        // AIOStreams sends its parsed stream data only to "AIO" User-Agents.
        Gelato.Streams.AddonUserAgent.Apply(c);
        return c;
    }
```

In `public class StremioStream`, after `public StremioBehaviorHints? BehaviorHints { get; set; }`, add:

```csharp
    /// <summary>Where a notice-only stream points (AIOStreams errors, links).</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>AIOStreams' parsed stream data, kept raw and mapped per stream.</summary>
    public JsonElement? StreamData { get; set; }
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~Gelato.Tests.Streams"`
Expected: PASS, 15 tests (5 user-agent, 10 mapper including 4 theory cases).

- [ ] **Step 10: Run the full suite and format new files**

```bash
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier format Streams/AddonUserAgent.cs Streams/StreamData.cs \
  Gelato.Tests/Streams/Json.cs Gelato.Tests/Streams/AddonUserAgentTests.cs \
  Gelato.Tests/Streams/StreamDataMapperTests.cs
```

Expected: all tests pass (181 existing + 15 new).

- [ ] **Step 11: Commit**

```bash
git add Streams/AddonUserAgent.cs Streams/StreamData.cs GelatoStremioProvider.cs \
  Gelato.Tests/Streams/Json.cs Gelato.Tests/Streams/AddonUserAgentTests.cs \
  Gelato.Tests/Streams/StreamDataMapperTests.cs
git commit -F - <<'EOF'
feat: request and parse AIOStreams stream data

Identify to addons as AIOStreams-Gelato/<version>. AIOStreams includes its
parsed stream data for User-Agents starting with "AIO", so this is what
turns it on. Map that data leniently per stream: a wrong-typed field is
dropped on its own and cannot cost the stream or the response.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 2: Build Jellyfin media info from stream data

**Files:**
- Create: `Streams/StreamMediaInfo.cs`
- Test: `Gelato.Tests/Streams/StreamDataFactory.cs`, `Gelato.Tests/Streams/StreamMediaInfoTests.cs`

**Interfaces:**
- Consumes: `StreamData`, `ParsedFile`, `MediaTrack` (Task 1)
- Produces:
  - `record StreamMediaInfoResult(IReadOnlyList<MediaStream> Streams, string Container, long? RunTimeTicks, long? Size, bool IsTrusted)`
  - `static class StreamMediaInfo { static StreamMediaInfoResult Build(StreamData data, string? url, Func<string, string?> toIso6392) }`
  - Test helper `StreamDataFactory` (`Parsed(...)`, `Track(...)`, `Data(...)`, `Probed(...)`, `Iso`)

- [ ] **Step 1: Write the test factory**

Create `Gelato.Tests/Streams/StreamDataFactory.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing builder tests**

Create `Gelato.Tests/Streams/StreamMediaInfoTests.cs`:

```csharp
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
    private static StreamMediaInfoResult Build(ParsedFile? pf, long? size = null,
        long? durationMs = null, string? filename = null, string? url = null,
        string? type = "debrid") =>
        StreamMediaInfo.Build(Data(pf, size, durationMs, filename, type), url, Iso);

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
        var r = Build(Parsed(quality: null, languages: ["English", "French"],
            subtitles: ["English", "French"]));

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
        var r = Build(Parsed(container: container, extension: extension), filename: filename,
            url: url, type: type);

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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamMediaInfoTests"`
Expected: build FAILS with `error CS0103: The name 'StreamMediaInfo' does not exist in the current context`.

- [ ] **Step 4: Implement the builder**

Create `Streams/StreamMediaInfo.cs`:

```csharp
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
        "mkv", "mp4", "avi", "mov", "m4v", "ts", "webm", "wmv", "flv", "m2ts", "mpg", "mpeg",
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
            data.DurationMs is > 0 ? data.DurationMs.Value * TimeSpan.TicksPerMillisecond : null,
            data.Size is > 0 ? data.Size : null,
            trusted
        );
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
        var audioTag = (pf?.AudioTags ?? Array.Empty<string>()).FirstOrDefault(t =>
            t != "Unknown"
        );
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamMediaInfoTests"`
Expected: PASS.

- [ ] **Step 6: Run the full suite and format new files**

```bash
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier format Streams/StreamMediaInfo.cs Gelato.Tests/Streams/StreamDataFactory.cs \
  Gelato.Tests/Streams/StreamMediaInfoTests.cs
```

Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add Streams/StreamMediaInfo.cs Gelato.Tests/Streams/StreamDataFactory.cs \
  Gelato.Tests/Streams/StreamMediaInfoTests.cs
git commit -F - <<'EOF'
feat: build Jellyfin media info from AIOStreams stream data

Port AIOStreams' buildMediaStreams mapping tables. Jellyfin 12.1
computes the video range, display titles and text-subtitle flag itself,
so set the colour and Dolby Vision fields it computes them from. Keep
Dolby Vision with an HDR10 base distinct from profile 5, because
Jellyfin's direct-play decision differs between them. Trust the result
only when AIOStreams actually probed the file and every field that
decision reads is present.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 3: Provenance rules

**Files:**
- Create: `Streams/MediaInfoProvenance.cs`
- Test: `Gelato.Tests/Streams/MediaInfoProvenanceTests.cs`

**Interfaces:**
- Produces:
  - `enum MediaInfoWrite { Write, Skip, StampFfProbe }`
  - `static class MediaInfoProvenance { const string Key = "mediaInfoSource", FfProbe = "ffprobe", AioStreamsProbed = "aiostreams-probed", AioStreamsGuessed = "aiostreams-guessed"; static string FromVerdict(bool); static bool IsTrusted(string?); static bool ShouldProbe(bool isNotice, string? provenance, bool hasVideoStream, long? runTimeTicks); static string? AfterProbe(bool succeeded, string? before); static MediaInfoWrite DecideWrite(bool isNewRow, string? existingProvenance, Func<bool> hasVideoStream, bool newIsTrusted) }`

- [ ] **Step 1: Write the failing tests**

Create `Gelato.Tests/Streams/MediaInfoProvenanceTests.cs`:

```csharp
using Gelato.Streams;
using static Gelato.Streams.MediaInfoProvenance;

namespace Gelato.Tests.Streams;

/// <summary>
/// Where a row's media info came from decides whether it is trusted, whether playback probes
/// it, and whether a sync may overwrite it. The one rule every case protects: a guess never
/// replaces real data.
/// </summary>
public class MediaInfoProvenanceTests
{
    private static readonly long Long = TimeSpan.FromMinutes(90).Ticks;
    private static readonly long Short = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void FromVerdict_NamesTheSource()
    {
        Assert.Equal(AioStreamsProbed, FromVerdict(true));
        Assert.Equal(AioStreamsGuessed, FromVerdict(false));
    }

    [Theory]
    [InlineData(FfProbe, true)]
    [InlineData(AioStreamsProbed, true)]
    [InlineData(AioStreamsGuessed, false)]
    [InlineData(null, false)]
    [InlineData("something-new", false)]
    public void IsTrusted_OnlyForRealData(string? provenance, bool trusted)
    {
        Assert.Equal(trusted, IsTrusted(provenance));
    }

    // Rows of the spec's probe decision table.
    [Theory]
    [InlineData(true, null, false, null, false)] // notice: never
    [InlineData(false, null, true, true, false)] // legacy, video, long: no
    [InlineData(false, null, false, true, true)] // legacy, no video: yes
    [InlineData(false, null, true, false, true)] // legacy, short: yes
    [InlineData(false, AioStreamsGuessed, true, true, true)] // guessed: yes
    [InlineData(false, AioStreamsProbed, true, true, false)] // probed by AIOStreams: no
    [InlineData(false, AioStreamsProbed, true, false, true)] // ...unless short
    [InlineData(false, FfProbe, true, true, false)] // probed by Gelato: no
    [InlineData(false, FfProbe, true, false, true)] // ...unless short
    public void ShouldProbe_FollowsTheDecisionTable(
        bool isNotice,
        string? provenance,
        bool hasVideo,
        bool? longRuntime,
        bool expected
    )
    {
        long? runtime = longRuntime switch
        {
            true => Long,
            false => Short,
            null => null,
        };

        Assert.Equal(expected, ShouldProbe(isNotice, provenance, hasVideo, runtime));
    }

    [Fact]
    public void AfterProbe_BecomesFfProbe_OnlyOnSuccess()
    {
        Assert.Equal(FfProbe, AfterProbe(true, AioStreamsGuessed));
        Assert.Equal(FfProbe, AfterProbe(true, null));
        Assert.Equal(AioStreamsGuessed, AfterProbe(false, AioStreamsGuessed));
        Assert.Null(AfterProbe(false, null));
    }

    private static Func<bool> Video(bool has) => () => has;

    private static readonly Func<bool> MustNotQuery = () =>
        throw new InvalidOperationException("the media-stream lookup must not run");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecideWrite_NewRow_Writes_WithoutQuerying(bool trusted)
    {
        Assert.Equal(MediaInfoWrite.Write, DecideWrite(true, null, MustNotQuery, trusted));
    }

    [Fact]
    public void DecideWrite_LegacyRowAlreadyProbed_IsStamped()
    {
        Assert.Equal(MediaInfoWrite.StampFfProbe, DecideWrite(false, null, Video(true), true));
    }

    [Fact]
    public void DecideWrite_LegacyRowWithoutMediaInfo_Writes()
    {
        Assert.Equal(MediaInfoWrite.Write, DecideWrite(false, null, Video(false), false));
    }

    [Theory]
    [InlineData(FfProbe)]
    [InlineData(AioStreamsProbed)]
    public void DecideWrite_TrustedRow_IsLeftAlone_WithoutQuerying(string provenance)
    {
        Assert.Equal(MediaInfoWrite.Skip, DecideWrite(false, provenance, MustNotQuery, true));
    }

    [Fact]
    public void DecideWrite_GuessedRow_UpgradesOnlyToTrustedData()
    {
        Assert.Equal(
            MediaInfoWrite.Write,
            DecideWrite(false, AioStreamsGuessed, MustNotQuery, true)
        );
        Assert.Equal(
            MediaInfoWrite.Skip,
            DecideWrite(false, AioStreamsGuessed, MustNotQuery, false)
        );
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~MediaInfoProvenanceTests"`
Expected: build FAILS with `error CS0234: The type or namespace name 'MediaInfoProvenance' does not exist in the namespace 'Gelato.Streams'`.

- [ ] **Step 3: Implement**

Create `Streams/MediaInfoProvenance.cs`:

```csharp
namespace Gelato.Streams;

public enum MediaInfoWrite
{
    Write,
    Skip,
    StampFfProbe,
}

/// <summary>
/// Where a stream row's media info came from, stored under <see cref="Key"/> in its
/// GelatoData. Before stream data existed ffprobe was the only writer, which is why a row
/// with no provenance but a video stream is treated as probed.
/// </summary>
public static class MediaInfoProvenance
{
    public const string Key = "mediaInfoSource";
    public const string FfProbe = "ffprobe";
    public const string AioStreamsProbed = "aiostreams-probed";
    public const string AioStreamsGuessed = "aiostreams-guessed";

    private static readonly long MinimumRuntimeTicks = TimeSpan.FromMinutes(2).Ticks;

    public static string FromVerdict(bool isTrusted) =>
        isTrusted ? AioStreamsProbed : AioStreamsGuessed;

    public static bool IsTrusted(string? provenance) => provenance is FfProbe or AioStreamsProbed;

    /// <summary>
    /// Today's triggers (no video stream, runtime under two minutes) plus one: data guessed
    /// from a filename is probed so a guess never drives a playback decision. Notices point at
    /// a placeholder clip and are never probed.
    /// </summary>
    public static bool ShouldProbe(
        bool isNotice,
        string? provenance,
        bool hasVideoStream,
        long? runTimeTicks
    ) =>
        !isNotice
        && (
            !hasVideoStream
            || (runTimeTicks ?? 0) < MinimumRuntimeTicks
            || provenance == AioStreamsGuessed
        );

    /// <summary>
    /// A failed probe leaves provenance alone, so a guess is probed again next time.
    /// </summary>
    public static string? AfterProbe(bool succeeded, string? before) =>
        succeeded ? FfProbe : before;

    /// <summary>
    /// Whether a sync may write stream-data media info to a row. <paramref name="hasVideoStream"/>
    /// runs only for legacy rows, once: afterwards the row has provenance, so re-browsing a
    /// title never queries or rewrites unchanged versions.
    /// </summary>
    public static MediaInfoWrite DecideWrite(
        bool isNewRow,
        string? existingProvenance,
        Func<bool> hasVideoStream,
        bool newIsTrusted
    )
    {
        if (isNewRow)
            return MediaInfoWrite.Write;

        if (existingProvenance is null)
            return hasVideoStream() ? MediaInfoWrite.StampFfProbe : MediaInfoWrite.Write;

        if (IsTrusted(existingProvenance))
            return MediaInfoWrite.Skip;

        return newIsTrusted ? MediaInfoWrite.Write : MediaInfoWrite.Skip;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~MediaInfoProvenanceTests"`
Expected: PASS.

- [ ] **Step 5: Full suite, format, commit**

```bash
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier format Streams/MediaInfoProvenance.cs Gelato.Tests/Streams/MediaInfoProvenanceTests.cs
git add Streams/MediaInfoProvenance.cs Gelato.Tests/Streams/MediaInfoProvenanceTests.cs
git commit -F - <<'EOF'
feat: add media info provenance rules

Record where each version's media info came from and decide from that
alone whether playback probes it and whether a sync may overwrite it.
Data guessed from a filename is always probed. A failed probe leaves
the row as it was, and a guess never replaces real data.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 4: Classify notices and order them after playable versions

**Files:**
- Create: `Streams/StreamClassifier.cs`
- Test: `Gelato.Tests/Streams/StreamClassifierTests.cs`

**Interfaces:**
- Consumes: `StremioStream` (`Url`, `Name`, `Description`, `Title`, `InfoHash`, `ExternalUrl`, `StreamData`, `IsValid()`, `IsTorrent()`, `GetGuid()`), `StreamDataMapper.TryMap` (Task 1)
- Produces:
  - `record SyncEntry(StremioStream Stream, StreamData? Data, bool IsNotice, Guid Guid)`
  - `static class StreamClassifier { const string NoticeKey = "notice"; static bool IsNotice(StremioStream, StreamData?); static bool IsPlayable(StremioStream, bool p2pEnabled); static Guid NoticeGuid(string? type, string? name, int occurrence); static IReadOnlyList<SyncEntry> PlanSync(IReadOnlyList<StremioStream> streams, bool p2pEnabled); static int PlayableCount(IReadOnlyList<SyncEntry> entries) }`

- [ ] **Step 1: Write the failing tests**

Create `Gelato.Tests/Streams/StreamClassifierTests.cs`:

```csharp
using Gelato.Streams;

namespace Gelato.Tests.Streams;

/// <summary>
/// Which streams become versions, which become notices, and in what order - mirroring
/// AIOStreams' Jellyfin mode (packages/server/src/routes/jellyfin/resolve.ts). Playable
/// versions keep exactly today's acceptance rule.
/// </summary>
public class StreamClassifierTests
{
    private const string Good = "https://cdn.example.com/dl/movie.mkv";

    private static StremioStream S(
        string? url = null,
        string? name = "Some stream",
        string? description = null,
        string? infoHash = null,
        string? data = null,
        string? externalUrl = null
    ) =>
        new()
        {
            Url = url ?? "",
            Name = name,
            Description = description,
            InfoHash = infoHash,
            ExternalUrl = externalUrl,
            StreamData = data is null ? null : Json.Parse(data),
        };

    private static string Typed(string type) => $$"""{ "type": "{{type}}" }""";

    [Fact]
    public void ValidUrl_IsPlayable()
    {
        var planned = Assert.Single(StreamClassifier.PlanSync([S(url: Good)], true));

        Assert.False(planned.IsNotice);
        Assert.Equal(S(url: Good).GetGuid(), planned.Guid);
    }

    [Fact]
    public void TorrentWithUrl_FollowsTheP2PSetting()
    {
        var torrent = S(url: Good, infoHash: new string('a', 40));

        Assert.True(StreamClassifier.IsPlayable(torrent, p2pEnabled: true));
        Assert.False(StreamClassifier.IsPlayable(torrent, p2pEnabled: false));
    }

    /// <summary>Rejected by today's IsValid(); fixing that is a separate change.</summary>
    [Fact]
    public void TorrentOnlyStream_IsDropped()
    {
        Assert.Empty(StreamClassifier.PlanSync([S(infoHash: new string('a', 40))], true));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("statistic")]
    [InlineData("info")]
    [InlineData("external")]
    [InlineData("youtube")]
    public void NoticeTypes_AreNotices_EvenWithAValidUrl(string type)
    {
        var planned = Assert.Single(
            StreamClassifier.PlanSync([S(url: Good, data: Typed(type))], true)
        );

        Assert.True(planned.IsNotice);
    }

    [Fact]
    public void WithoutStreamData_UrlLessStreamWithText_IsANotice()
    {
        var stream = S(name: "[!] AIOStreams", externalUrl: "https://docs.example.com/why");

        Assert.True(StreamClassifier.IsNotice(stream, null));
    }

    [Fact]
    public void WithoutStreamData_RootPathUrl_IsANotice()
    {
        Assert.True(StreamClassifier.IsNotice(S(url: "https://host.example.com/"), null));
    }

    [Fact]
    public void StreamDataOfAPlayableType_WithABadUrl_IsDropped()
    {
        Assert.Empty(StreamClassifier.PlanSync([S(url: "", data: Typed("http"))], true));
    }

    [Fact]
    public void NoticeWithNoText_IsDropped()
    {
        var blank = S(name: null, externalUrl: "https://docs.example.com/why");

        Assert.Empty(StreamClassifier.PlanSync([blank], true));
    }

    [Fact]
    public void PlanSync_PutsEveryPlayableBeforeEveryNotice()
    {
        var planned = StreamClassifier.PlanSync(
            [
                S(name: "n1", data: Typed("error")),
                S(url: Good, name: "p1"),
                S(name: "n2", data: Typed("statistic")),
                S(url: "https://cdn.example.com/other.mkv", name: "p2"),
            ],
            true
        );

        Assert.Equal(new[] { "p1", "p2", "n1", "n2" }, planned.Select(e => e.Stream.Name));
        Assert.Equal(new[] { false, false, true, true }, planned.Select(e => e.IsNotice));
    }

    [Fact]
    public void PlanSync_DropsANoticeThatRepeatsAPlayableUrl()
    {
        var planned = StreamClassifier.PlanSync(
            [S(url: Good, name: "p"), S(name: "same link", externalUrl: Good)],
            true
        );

        Assert.Equal(new[] { "p" }, planned.Select(e => e.Stream.Name));
    }

    [Fact]
    public void NoticeGuid_IgnoresTheDescription()
    {
        var first = StreamClassifier.PlanSync(
            [S(name: "Stats", description: "Found 50 in 1.2s", data: Typed("statistic"))],
            true
        );
        var second = StreamClassifier.PlanSync(
            [S(name: "Stats", description: "Found 51 in 0.9s", data: Typed("statistic"))],
            true
        );

        Assert.Equal(first[0].Guid, second[0].Guid);
    }

    [Fact]
    public void NoticeGuid_KeepsSameNamedNoticesApart()
    {
        var planned = StreamClassifier.PlanSync(
            [S(name: "[!] AIOStreams", data: Typed("error")),
             S(name: "[!] AIOStreams", data: Typed("error"))],
            true
        );

        Assert.NotEqual(planned[0].Guid, planned[1].Guid);
    }

    [Fact]
    public void NoticeGuid_IsTheSpecKeyHashed()
    {
        var expected = new Guid(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes("notice|error|[!] AIOStreams|0")
            )
        );

        Assert.Equal(expected, StreamClassifier.NoticeGuid("error", "[!] AIOStreams", 0));
    }

    [Fact]
    public void ErrorOnlyResponse_IsANoticeWithNothingPlayable()
    {
        var planned = StreamClassifier.PlanSync(
            [S(name: "[!] AIOStreams", description: "Invalid credentials", data: Typed("error"))],
            true
        );

        Assert.True(Assert.Single(planned).IsNotice);
        Assert.Equal(0, StreamClassifier.PlayableCount(planned));
    }

    [Fact]
    public void PlanSync_MapsStreamData()
    {
        var planned = StreamClassifier.PlanSync(
            [S(url: Good, data: """{ "type": "debrid", "size": 7 }""")],
            true
        );

        Assert.Equal(7L, planned[0].Data!.Size);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamClassifierTests"`
Expected: build FAILS with `error CS0103: The name 'StreamClassifier' does not exist in the current context`.

- [ ] **Step 3: Implement**

Create `Streams/StreamClassifier.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Gelato.Streams;

/// <summary>A stream placed for sync: its row identity, and whether it is a notice.</summary>
public sealed record SyncEntry(StremioStream Stream, StreamData? Data, bool IsNotice, Guid Guid);

/// <summary>
/// Decides which addon streams become playable versions and which become notices - entries
/// carrying an addon message, error or statistic instead of something to play - and orders
/// them. Mirrors AIOStreams' Jellyfin mode: notices come after every playable version, so a
/// notice is never the default while something playable exists.
/// </summary>
public static class StreamClassifier
{
    /// <summary>GelatoData key marking a stream row as a notice.</summary>
    public const string NoticeKey = "notice";

    private static readonly HashSet<string> NoticeTypes = new(StringComparer.Ordinal)
    {
        "error",
        "statistic",
        "info",
        "external",
        "youtube",
    };

    public static bool IsNotice(StremioStream stream, StreamData? data)
    {
        var hasText =
            !string.IsNullOrWhiteSpace(stream.Name)
            || !string.IsNullOrWhiteSpace(stream.Description)
            || !string.IsNullOrWhiteSpace(stream.Title);
        if (!hasText)
            return false;

        if (data is not null)
            return data.Type is { } type && NoticeTypes.Contains(type);

        return !stream.IsValid() && !stream.IsTorrent();
    }

    /// <summary>
    /// Exactly today's acceptance rule: a valid URL, and no torrent while P2P is off.
    /// </summary>
    public static bool IsPlayable(StremioStream stream, bool p2pEnabled) =>
        stream.IsValid() && (p2pEnabled || !stream.IsTorrent());

    /// <summary>
    /// Keyed on type and name, not description: statistics and many errors carry timings or
    /// counts that change every sync, and keying on them would recreate the row each time.
    /// </summary>
    public static Guid NoticeGuid(string? type, string? name, int occurrence)
    {
        var key = $"notice|{type}|{name}|{occurrence}";
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static IReadOnlyList<SyncEntry> PlanSync(
        IReadOnlyList<StremioStream> streams,
        bool p2pEnabled
    )
    {
        var playable = new List<SyncEntry>();
        var notices = new List<(StremioStream Stream, StreamData? Data)>();

        foreach (var stream in streams)
        {
            var data = StreamDataMapper.TryMap(stream.StreamData);
            if (IsNotice(stream, data))
                notices.Add((stream, data));
            else if (IsPlayable(stream, p2pEnabled))
                playable.Add(new SyncEntry(stream, data, false, stream.GetGuid()));
        }

        var playableUrls = playable
            .Select(e => e.Stream.Url)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<(string Type, string Name), int>();
        var planned = new List<SyncEntry>(playable);

        foreach (var (stream, data) in notices)
        {
            // A link that repeats a playable version would only be that version again.
            var link = stream.ExternalUrl;
            if (!string.IsNullOrEmpty(link) && playableUrls.Contains(link))
                continue;

            var type = data?.Type ?? "";
            var name = stream.Name ?? "";
            var occurrence = seen.GetValueOrDefault((type, name));
            seen[(type, name)] = occurrence + 1;

            planned.Add(new SyncEntry(stream, data, true, NoticeGuid(type, name, occurrence)));
        }

        return planned;
    }

    /// <summary>
    /// Only playable versions count as a successful sync. An error-only response must not be
    /// cached as synced, or fixing the addon's config would not show until the cache expired.
    /// </summary>
    public static int PlayableCount(IReadOnlyList<SyncEntry> entries) =>
        entries.Count(e => !e.IsNotice);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamClassifierTests"`
Expected: PASS.

- [ ] **Step 5: Full suite, format, commit**

```bash
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier format Streams/StreamClassifier.cs Gelato.Tests/Streams/StreamClassifierTests.cs
git add Streams/StreamClassifier.cs Gelato.Tests/Streams/StreamClassifierTests.cs
git commit -F - <<'EOF'
feat: classify addon notices and order them after playable versions

Stop dropping url-less streams and AIOStreams errors, statistics and
info entries. They become notices and sort after every playable
version, so a notice is only the default when nothing else is there.
A notice's identity ignores its description, which changes on every
sync for statistics.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 5: Placeholder clip and notice endpoint

**Files:**
- Create: `Assets/notice.mp4`, `Streams/NoticeClip.cs`
- Modify: `Gelato.csproj` (embedded resources ~line 23), `Controllers/GelatoApiController.cs` (after the `stream` action)
- Test: `Gelato.Tests/Streams/NoticeClipTests.cs`

**Interfaces:**
- Produces:
  - `static class NoticeClip { const string ResourceName = "Gelato.Assets.notice.mp4"; const string ContentType = "video/mp4"; const string Container = "mp4"; static Stream Open(); static string PathFor(int httpPort, Guid noticeGuid); static IReadOnlyList<MediaStream> MediaStreams() }`
  - `GET /gelato/notice` serving the clip

- [ ] **Step 1: Generate the clip**

```bash
mkdir -p Assets
ffmpeg -hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:d=4:r=1 \
  -c:v libx264 -preset veryslow -crf 30 -tune stillimage -pix_fmt yuv420p \
  -movflags +faststart -an -y Assets/notice.mp4
ls -l Assets/notice.mp4
```

Expected: about 1,800 bytes; `ffprobe -v error -show_entries stream=codec_name,width,height -of compact Assets/notice.mp4` prints `stream|codec_name=h264|width=640|height=360`.

- [ ] **Step 2: Embed it**

In `Gelato.csproj`, after `<EmbeddedResource Include="Config\config.html" />`, add:

```xml
    <EmbeddedResource Include="Assets\notice.mp4" />
```

- [ ] **Step 3: Write the failing tests**

Create `Gelato.Tests/Streams/NoticeClipTests.cs`:

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~NoticeClipTests"`
Expected: build FAILS with `error CS0103: The name 'NoticeClip' does not exist in the current context`.

- [ ] **Step 5: Implement**

Create `Streams/NoticeClip.cs`:

```csharp
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
```

In `Controllers/GelatoApiController.cs`, add after the `TorrentStream` action (this file is csharpier-clean; format it afterwards):

```csharp
    /// <summary>The placeholder a notice version plays; the id in its path is ignored.</summary>
    [HttpGet("notice")]
    public IActionResult Notice() =>
        File(Gelato.Streams.NoticeClip.Open(), Gelato.Streams.NoticeClip.ContentType, true);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~NoticeClipTests"`
Expected: PASS.

- [ ] **Step 7: Full suite, format, commit**

```bash
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier format Streams/NoticeClip.cs Controllers/GelatoApiController.cs \
  Gelato.Tests/Streams/NoticeClipTests.cs
git add Assets/notice.mp4 Gelato.csproj Streams/NoticeClip.cs \
  Controllers/GelatoApiController.cs Gelato.Tests/Streams/NoticeClipTests.cs
git commit -F - <<'EOF'
feat: serve a placeholder clip for notice versions

Picking a notice version plays a bundled 2 KB clip instead of failing.
The version name carries the message. The clip is served from loopback,
so the reachability rules keep it out of direct play and the stream
redirect.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 6: Save stream-data media info and notices during sync

**Files:**
- Create: `Streams/StreamRowPlanner.cs`
- Modify: `GelatoManager.cs` (constructor; `SyncStreams` filter ~lines 570-592, loop ~lines 636-716, save ~line 719, return ~line 766) — **hand-edit only**
- Test: `Gelato.Tests/Streams/StreamRowPlannerTests.cs`

**Interfaces:**
- Consumes: `StreamMediaInfo.Build` (Task 2); `MediaInfoProvenance.*`, `MediaInfoWrite` (Task 3); `StreamClassifier.PlanSync`, `PlayableCount`, `NoticeKey`, `SyncEntry` (Task 4); `NoticeClip.PathFor`, `MediaStreams`, `Container` (Task 5)
- Produces:
  - `record RowMediaInfoPlan(MediaInfoWrite Decision, IReadOnlyList<MediaStream>? Streams, string? Provenance, string? Container, long? Size, long? RunTimeTicks)`
  - `static class StreamRowPlanner { static RowMediaInfoPlan Plan(bool isNewRow, bool isNotice, string? existingProvenance, Func<bool> hasVideoStream, StreamData? data, string? url, Func<string, string?> toIso6392) }`
  - `GelatoManager` constructor gains `IMediaStreamRepository mediaStreamRepository, ILocalizationManager localization`
  - `SyncStreams` returns the playable count

- [ ] **Step 1: Write the failing planner tests**

Create `Gelato.Tests/Streams/StreamRowPlannerTests.cs`:

```csharp
using Gelato.Streams;
using static Gelato.Streams.MediaInfoProvenance;
using static Gelato.Tests.Streams.StreamDataFactory;

namespace Gelato.Tests.Streams;

/// <summary>
/// The per-row decision SyncStreams applies. The two cases that matter most: an existing
/// library's probed rows are never overwritten by a guess, and re-browsing a title with
/// hundreds of unchanged versions neither rewrites nor looks up anything.
/// </summary>
public class StreamRowPlannerTests
{
    private const string Url = "https://cdn.example.com/dl/movie.mkv";

    private static readonly Func<bool> MustNotQuery = () =>
        throw new InvalidOperationException("the media-stream lookup must not run");

    private static RowMediaInfoPlan Plan(
        bool isNew,
        string? provenance,
        Func<bool> hasVideo,
        StreamData? data,
        bool isNotice = false
    ) => StreamRowPlanner.Plan(isNew, isNotice, provenance, hasVideo, data, Url, Iso);

    [Fact]
    public void NewRow_WithProbedData_WritesItAsProbed()
    {
        var plan = Plan(true, null, MustNotQuery, Data(Probed(), size: 10, durationMs: 60_000));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal(AioStreamsProbed, plan.Provenance);
        Assert.NotEmpty(plan.Streams!);
        Assert.Equal("mkv", plan.Container);
        Assert.Equal(10, plan.Size);
        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, plan.RunTimeTicks);
    }

    [Fact]
    public void NewRow_WithGuessedData_WritesItAsGuessed()
    {
        var plan = Plan(true, null, MustNotQuery, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal(AioStreamsGuessed, plan.Provenance);
    }

    [Fact]
    public void LegacyRowAlreadyProbed_IsStampedNotOverwritten()
    {
        var plan = Plan(false, null, () => true, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.StampFfProbe, plan.Decision);
        Assert.Equal(FfProbe, plan.Provenance);
        Assert.Null(plan.Streams);
    }

    [Fact]
    public void LegacyRowWithoutMediaInfo_GetsStreamData()
    {
        var plan = Plan(false, null, () => false, Data(Parsed(quality: "addon")));

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.NotNull(plan.Streams);
    }

    [Theory]
    [InlineData(FfProbe)]
    [InlineData(AioStreamsProbed)]
    public void RowsWithProvenance_AreNeitherRewrittenNorQueried(string provenance)
    {
        var plan = Plan(false, provenance, MustNotQuery, Data(Probed()));

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
        Assert.Null(plan.Provenance);
        Assert.Null(plan.Streams);
    }

    [Fact]
    public void GuessedRow_UpgradesOnlyToProbedData()
    {
        var upgrade = Plan(false, AioStreamsGuessed, MustNotQuery, Data(Probed()));
        var same = Plan(false, AioStreamsGuessed, MustNotQuery, Data(Parsed(quality: "addon")));

        Assert.Equal(
            (MediaInfoWrite.Write, AioStreamsProbed),
            (upgrade.Decision, upgrade.Provenance)
        );
        Assert.Equal(MediaInfoWrite.Skip, same.Decision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoStreamData_KeepsTodaysBehaviour(bool isNew)
    {
        var plan = Plan(isNew, null, MustNotQuery, null);

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
        Assert.Null(plan.Provenance);
    }

    [Fact]
    public void NewNotice_GetsThePlaceholderMediaInfo_WithoutProvenance()
    {
        var plan = Plan(true, null, MustNotQuery, null, isNotice: true);

        Assert.Equal(MediaInfoWrite.Write, plan.Decision);
        Assert.Equal("h264", Assert.Single(plan.Streams!).Codec);
        Assert.Equal(NoticeClip.Container, plan.Container);
        Assert.Null(plan.Provenance);
        Assert.Null(plan.RunTimeTicks);
    }

    [Fact]
    public void ExistingNotice_IsLeftAlone()
    {
        var plan = Plan(false, null, MustNotQuery, null, isNotice: true);

        Assert.Equal(MediaInfoWrite.Skip, plan.Decision);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamRowPlannerTests"`
Expected: build FAILS with `error CS0103: The name 'StreamRowPlanner' does not exist in the current context`.

- [ ] **Step 3: Implement the planner**

Create `Streams/StreamRowPlanner.cs`:

```csharp
using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

/// <summary>What SyncStreams writes to one stream row; null fields are left as they are.</summary>
public sealed record RowMediaInfoPlan(
    MediaInfoWrite Decision,
    IReadOnlyList<MediaStream>? Streams,
    string? Provenance,
    string? Container,
    long? Size,
    long? RunTimeTicks
);

public static class StreamRowPlanner
{
    private static readonly RowMediaInfoPlan Nothing = new(
        MediaInfoWrite.Skip,
        null,
        null,
        null,
        null,
        null
    );

    public static RowMediaInfoPlan Plan(
        bool isNewRow,
        bool isNotice,
        string? existingProvenance,
        Func<bool> hasVideoStream,
        StreamData? data,
        string? url,
        Func<string, string?> toIso6392
    )
    {
        if (isNotice)
        {
            return isNewRow
                ? new RowMediaInfoPlan(
                    MediaInfoWrite.Write,
                    NoticeClip.MediaStreams(),
                    null,
                    NoticeClip.Container,
                    null,
                    null
                )
                : Nothing;
        }

        // Without stream data, or once a row holds trusted media info, nothing changes - and
        // skipping before the build keeps re-browsing a large title cheap.
        if (data is null || (!isNewRow && MediaInfoProvenance.IsTrusted(existingProvenance)))
            return Nothing;

        var info = StreamMediaInfo.Build(data, url, toIso6392);
        var decision = MediaInfoProvenance.DecideWrite(
            isNewRow,
            existingProvenance,
            hasVideoStream,
            info.IsTrusted
        );

        return decision switch
        {
            MediaInfoWrite.Write => new RowMediaInfoPlan(
                MediaInfoWrite.Write,
                info.Streams,
                MediaInfoProvenance.FromVerdict(info.IsTrusted),
                info.Container,
                info.Size,
                info.RunTimeTicks
            ),
            MediaInfoWrite.StampFfProbe => new RowMediaInfoPlan(
                MediaInfoWrite.StampFfProbe,
                null,
                MediaInfoProvenance.FfProbe,
                null,
                null,
                null
            ),
            _ => Nothing,
        };
    }
}
```

- [ ] **Step 4: Run the planner tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~StreamRowPlannerTests"`
Expected: PASS.

- [ ] **Step 5: Wire `GelatoManager` (hand-edit; do not run csharpier on this file)**

Add usings at the top of `GelatoManager.cs`:

```csharp
using Gelato.Streams;
using MediaBrowser.Model.Globalization;
```

Constructor — replace:

```csharp
    IUserManager userManager,
    IUserDataManager userDataManager
)
```

with:

```csharp
    IUserManager userManager,
    IUserDataManager userDataManager,
    IMediaStreamRepository mediaStreamRepository,
    ILocalizationManager localization
)
```

In `SyncStreams`, replace the whole filter block from `// Filter valid streams` through the
`.ToList();` that ends it with:

```csharp
        // Playable versions first, then notices (addon messages, errors, statistics), so a
        // notice is never the default version while something playable exists.
        var entries = StreamClassifier.PlanSync(streams, cfg.P2PEnabled);
        var noticeCount = entries.Count - StreamClassifier.PlayableCount(entries);
        if (noticeCount > 0)
            _log.LogDebug("SyncStreams: {Count} notice(s) for {Id}", noticeCount, uri.ExternalId);
```

Replace the loop header and path computation — from `var upsertedStreams = new List<Video>();`
through `var streamGuid = s.GetGuid();` — with:

```csharp
        var upsertedStreams = new List<Video>();
        var pendingMediaStreams = new List<(Guid ItemId, IReadOnlyList<MediaStream> Streams)>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var s = entry.Stream;
            var index = i + 1;
            var path = entry.IsNotice
                ? NoticeClip.PathFor(httpPort, entry.Guid)
                : s.IsFile()
                    ? s.Url
                    : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
                        + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                        + (
                            s.Sources is { Count: > 0 }
                                ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
                                : ""
                        );

            var streamGuid = entry.Guid;
```

Directly after `streamItem.SetGelatoData("guid", streamGuid);`, add:

```csharp
            if (entry.IsNotice)
            {
                streamItem.SetGelatoData(StreamClassifier.NoticeKey, true);
                streamItem.RunTimeTicks = null;
            }

            var rowId = streamItem.Id;
            var plan = StreamRowPlanner.Plan(
                isNewStreamItem,
                entry.IsNotice,
                streamItem.GelatoData<string>(MediaInfoProvenance.Key),
                () => HasVideoStream(rowId),
                entry.Data,
                s.Url,
                ToIso6392
            );
            ApplyMediaInfoPlan(streamItem, plan, pendingMediaStreams);
```

Replace `persistence.SaveItems(upsertedStreams, ct);` (the first one, right after the loop) with:

```csharp
        persistence.SaveItems(upsertedStreams, ct);

        // A media stream references its item, so these go in after the rows are saved.
        foreach (var (itemId, mediaStreams) in pendingMediaStreams)
            mediaStreamRepository.SaveMediaStreams(itemId, mediaStreams, ct);
```

Replace `return acceptable.Count;` with:

```csharp
        return StreamClassifier.PlayableCount(entries);
```

Add these members to `GelatoManager` (after `SyncStreams`):

```csharp
    private static void ApplyMediaInfoPlan(
        Video row,
        RowMediaInfoPlan plan,
        List<(Guid ItemId, IReadOnlyList<MediaStream> Streams)> pending
    )
    {
        if (plan.Provenance is not null)
            row.SetGelatoData(MediaInfoProvenance.Key, plan.Provenance);

        if (plan.Decision != MediaInfoWrite.Write || plan.Streams is null)
            return;

        if (plan.Container is not null)
            row.Container = plan.Container;
        if (plan.Size is { } size)
            row.Size = size;
        if (plan.RunTimeTicks is { } ticks)
            row.RunTimeTicks = ticks;

        pending.Add((row.Id, plan.Streams));
    }

    private bool HasVideoStream(Guid itemId) =>
        mediaStreamRepository
            .GetMediaStreams(new MediaStreamQuery { ItemId = itemId, Type = MediaStreamType.Video })
            .Count > 0;

    /// <summary>AIOStreams sends English language names; Jellyfin resolves them.</summary>
    private string? ToIso6392(string language)
    {
        try
        {
            return localization.FindLanguageInfo(language)?.ThreeLetterISOLanguageName;
        }
        catch (Exception)
        {
            return null;
        }
    }
```

- [ ] **Step 6: Verify nothing else references the removed filter**

Run: `grep -n "acceptable\|Invalid stream, skipping" GelatoManager.cs`
Expected: no output.

- [ ] **Step 7: Build, test, check line length**

```bash
dotnet build Gelato.csproj -v q --nologo 2>&1 | grep -E " error |Build succeeded"
dotnet test Gelato.Tests/Gelato.Tests.csproj
git diff -U0 GelatoManager.cs | grep -E "^\+" | grep -v "^+++" | awk 'length > 101'
```

Expected: `Build succeeded.`; all tests pass; the awk prints nothing.

- [ ] **Step 8: Format new files and commit**

```bash
dotnet csharpier format Streams/StreamRowPlanner.cs Gelato.Tests/Streams/StreamRowPlannerTests.cs
git add Streams/StreamRowPlanner.cs GelatoManager.cs Gelato.Tests/Streams/StreamRowPlannerTests.cs
git commit -F - <<'EOF'
feat: save stream data media info and notices during stream sync

Each version gets media info built from AIOStreams' stream data the
first time it is seen, so the version picker shows resolution, HDR and
audio before anything is played. Existing libraries are safe: a row
ffprobe already filled is stamped rather than overwritten, and rows
with provenance are never looked up or rewritten again. Notices are
saved as versions, but only playable versions count towards a
successful sync, so an error-only response is retried on the next view.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 7: Skip ffprobe for versions AIOStreams already probed

**Files:**
- Create: `Streams/ProbeGate.cs`
- Modify: `Decorators/MediaSourceManagerDecorator.cs` (probe call site ~line 406; `ProbeStreamAsync` ~line 682; static `NeedsProbe` ~line 465) — **hand-edit only**
- Test: `Gelato.Tests/Streams/ProbeGateTests.cs`

**Interfaces:**
- Consumes: `MediaInfoProvenance.ShouldProbe`, `AfterProbe`, `Key` (Task 3); `StreamClassifier.NoticeKey` (Task 4); `GelatoData<T>` / `SetGelatoData<T>` (`Common.cs`)
- Produces: `static class ProbeGate { static bool ShouldProbe(BaseItem owner, MediaSourceInfo selected); static void RecordProbe(BaseItem owner, bool succeeded) }`; `ProbeStreamAsync` returns `Task<bool>`

- [ ] **Step 1: Write the failing tests**

Create `Gelato.Tests/Streams/ProbeGateTests.cs`:

```csharp
using Gelato.Streams;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using static Gelato.Streams.MediaInfoProvenance;

namespace Gelato.Tests.Streams;

/// <summary>
/// The probe decision reads the stream row's own provenance and notice flag. These tests
/// prove it reads the keys SyncStreams writes.
/// </summary>
public class ProbeGateTests
{
    private static Movie Row(string? provenance = null, bool notice = false)
    {
        var row = new Movie();
        if (provenance is not null)
            row.SetGelatoData(Key, provenance);
        if (notice)
            row.SetGelatoData(StreamClassifier.NoticeKey, true);
        return row;
    }

    private static MediaSourceInfo Source(bool hasVideo = true, long? minutes = 90) =>
        new()
        {
            MediaStreams = hasVideo
                ? new List<MediaStream> { new() { Type = MediaStreamType.Video } }
                : new List<MediaStream>(),
            RunTimeTicks = minutes is { } m ? TimeSpan.FromMinutes(m).Ticks : null,
        };

    [Fact]
    public void GuessedRow_IsProbed()
    {
        Assert.True(ProbeGate.ShouldProbe(Row(AioStreamsGuessed), Source()));
    }

    [Theory]
    [InlineData(AioStreamsProbed)]
    [InlineData(FfProbe)]
    public void TrustedRow_IsNotProbed(string provenance)
    {
        Assert.False(ProbeGate.ShouldProbe(Row(provenance), Source()));
    }

    [Fact]
    public void LegacyRowWithoutVideo_IsProbed_AsToday()
    {
        Assert.True(ProbeGate.ShouldProbe(Row(), Source(hasVideo: false)));
    }

    [Fact]
    public void Notice_IsNeverProbed()
    {
        Assert.False(ProbeGate.ShouldProbe(Row(notice: true), Source(false, null)));
    }

    [Fact]
    public void SuccessfulProbe_MarksTheRowAsProbed()
    {
        var row = Row(AioStreamsGuessed);

        ProbeGate.RecordProbe(row, succeeded: true);

        Assert.Equal(FfProbe, row.GelatoData<string>(Key));
    }

    [Fact]
    public void FailedProbe_LeavesTheGuessToBeProbedAgain()
    {
        var row = Row(AioStreamsGuessed);

        ProbeGate.RecordProbe(row, succeeded: false);

        Assert.Equal(AioStreamsGuessed, row.GelatoData<string>(Key));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~ProbeGateTests"`
Expected: build FAILS with `error CS0103: The name 'ProbeGate' does not exist in the current context`.

- [ ] **Step 3: Implement the gate**

Create `Streams/ProbeGate.cs`:

```csharp
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Gelato.Streams;

/// <summary>Applies the provenance rules to the stream row a playback request resolves to.</summary>
public static class ProbeGate
{
    public static bool ShouldProbe(BaseItem owner, MediaSourceInfo selected) =>
        MediaInfoProvenance.ShouldProbe(
            owner.GelatoData<bool?>(StreamClassifier.NoticeKey) == true,
            owner.GelatoData<string>(MediaInfoProvenance.Key),
            selected.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) ?? false,
            selected.RunTimeTicks
        );

    public static void RecordProbe(BaseItem owner, bool succeeded)
    {
        var before = owner.GelatoData<string>(MediaInfoProvenance.Key);
        var after = MediaInfoProvenance.AfterProbe(succeeded, before);

        if (after is not null && after != before)
            owner.SetGelatoData(MediaInfoProvenance.Key, after);
    }
}
```

- [ ] **Step 4: Run the gate tests to verify they pass**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~ProbeGateTests"`
Expected: PASS.

- [ ] **Step 5: Make `ProbeStreamAsync` report success (hand-edit; no csharpier)**

In `Decorators/MediaSourceManagerDecorator.cs`, change the signature:

```csharp
    private async Task<bool> ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
```

Inside its `try`, after the closing brace of the `if (probeProvider is not null) { … } else { … }`
block, add:

```csharp
            return true;
```

In its `catch (Exception ex)` block, after `_log.LogError(ex, "Stream probe failed for {Id}", owner.Id);`, add:

```csharp
            return false;
```

- [ ] **Step 6: Use the gate at the call site (hand-edit; no csharpier)**

Replace:

```csharp
        if (NeedsProbe(selected))
```

with:

```csharp
        if (ProbeGate.ShouldProbe(owner, selected))
```

Inside that block, replace:

```csharp
            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);
```

with:

```csharp
            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            // Only a probe that ran marks the row as probed; a failed one is retried next time.
            ProbeGate.RecordProbe(owner, await metadataTask.ConfigureAwait(false));
```

Delete the now-unused local function:

```csharp
        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;
```

Add `using Gelato.Streams;` to the file's usings.

- [ ] **Step 7: Build, test, check line length**

```bash
dotnet build Gelato.csproj -v q --nologo 2>&1 | grep -E " error |Build succeeded"
dotnet test Gelato.Tests/Gelato.Tests.csproj
grep -n "NeedsProbe" Decorators/MediaSourceManagerDecorator.cs
git diff -U0 Decorators/MediaSourceManagerDecorator.cs | grep -E "^\+" | grep -v "^+++" \
  | awk 'length > 101'
```

Expected: `Build succeeded.`; all tests pass; `grep` and `awk` print nothing.

- [ ] **Step 8: Format new files and commit**

```bash
dotnet csharpier format Streams/ProbeGate.cs Gelato.Tests/Streams/ProbeGateTests.cs
git add Streams/ProbeGate.cs Decorators/MediaSourceManagerDecorator.cs \
  Gelato.Tests/Streams/ProbeGateTests.cs
git commit -F - <<'EOF'
feat: skip ffprobe for versions AIOStreams already probed

Playback now probes by provenance. A version whose media info came from
AIOStreams' own probe starts right away. One guessed from a filename is
still probed, and notices never are. ProbeStreamAsync reports whether it
worked, so a failed probe can't mark a guess as real data.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

---

### Task 8: Real-data fixture and live verification

**Files:**
- Create: `scripts/capture-aiostreams-fixture.sh`, `Gelato.Tests/Streams/Fixtures/aiostreams-movie.json` (captured), `Gelato.Tests/Streams/AioStreamsFixtureTests.cs`
- Modify: `Gelato.Tests/Gelato.Tests.csproj`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Write the capture script**

Create `scripts/capture-aiostreams-fixture.sh`:

```bash
#!/usr/bin/env bash
# Capture an AIOStreams stream response as a test fixture, scrubbed of every URL, hash and
# header before it is written. The base URL carries the user's token: it is only passed to
# curl and never printed.
#
# Usage: scripts/capture-aiostreams-fixture.sh <aiostreams-base-url> <out.json> [imdb-id]
#   <aiostreams-base-url> is the addon URL without /manifest.json.
set -euo pipefail

base="${1:?AIOStreams base URL required}"
out="${2:?output path required}"
id="${3:-tt0111161}"
ver=$(sed -n 's/^version: *"\{0,1\}\([^"]*\)"\{0,1\}$/\1/p' build.yaml)
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

curl -fsS -A "AIOStreams-Gelato/${ver:-0.0.0.0}" "${base%/}/stream/movie/${id}.json" -o "$tmp"

python3 - "$tmp" "$out" <<'PY'
import itertools, json, re, sys

src, dst = sys.argv[1], sys.argv[2]
counter = itertools.count()
URL = re.compile(r"https?://[^\s\"']+")

def fake_url():
    return f"https://example.invalid/scrubbed/{next(counter)}"

def scrub(value, key=None):
    if key in ("url", "externalUrl", "nzbUrl"):
        return fake_url() if isinstance(value, str) and value else value
    if key in ("sources", "servers", "rarUrls", "zipUrls", "7zipUrls", "tgzUrls", "tarUrls"):
        return [fake_url() for _ in value] if isinstance(value, list) else None
    if key == "infoHash":
        return "0" * 40 if value else value
    if key == "proxyHeaders":
        return None
    if isinstance(value, dict):
        return {k: scrub(v, k) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v) for v in value]
    if isinstance(value, str):
        return URL.sub(lambda _: fake_url(), value)
    return value

data = scrub(json.load(open(src)))
json.dump(data, open(dst, "w"), indent=2, ensure_ascii=False)
streams = data.get("streams", [])
with_data = sum(1 for s in streams if isinstance(s.get("streamData"), dict))
print(f"wrote {dst}: {len(streams)} streams, {with_data} with streamData")
PY
```

```bash
chmod +x scripts/capture-aiostreams-fixture.sh
```

- [ ] **Step 2: Capture the fixture (needs the user)**

The command needs the user's AIOStreams URL, which contains their token. **Do not ask for the
URL in chat and do not read it from their Jellyfin config.** Ask the user to run this themselves,
using the `!` prefix so the output lands in the session:

```
! scripts/capture-aiostreams-fixture.sh '<your AIOStreams URL without /manifest.json>' Gelato.Tests/Streams/Fixtures/aiostreams-movie.json
```

Expected: `wrote …: N streams, M with streamData` with M > 0. If M is 0, the instance has
`provideStreamData: false` — stop and report it; everything else in this plan still falls back
correctly, but this fixture cannot exercise stream data.

Then verify the scrub before anything else touches the file:

```bash
grep -oE "https?://[^\"]+" Gelato.Tests/Streams/Fixtures/aiostreams-movie.json \
  | grep -v "^https://example.invalid/" | head
```

Expected: no output. If anything prints, delete the fixture and fix the script before retrying.

- [ ] **Step 3: Copy fixtures to the test output**

In `Gelato.Tests/Gelato.Tests.csproj`, add:

```xml
  <ItemGroup>
    <None Update="Streams\Fixtures\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Write the end-to-end test**

Create `Gelato.Tests/Streams/AioStreamsFixtureTests.cs`:

```csharp
using System.Text.Json;
using Gelato.Streams;
using MediaBrowser.Model.Entities;
using Xunit.Abstractions;

namespace Gelato.Tests.Streams;

/// <summary>
/// A real AIOStreams response (scrubbed by scripts/capture-aiostreams-fixture.sh) through the
/// whole pipeline: parse, classify, order, build. It also reports how often AIOStreams' data
/// is probe quality - the spec's open question.
/// </summary>
public class AioStreamsFixtureTests(ITestOutputHelper output)
{
    private static List<StremioStream> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Streams", "Fixtures",
            "aiostreams-movie.json");
        Assert.True(File.Exists(path), $"Capture the fixture first (plan Task 8): {path}");

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<StremioStreamsResponse>(File.ReadAllText(path), opts)!
            .Streams;
    }

    [Fact]
    public void RealResponse_CarriesStreamData()
    {
        Assert.Contains(Load(), s => StreamDataMapper.TryMap(s.StreamData) is not null);
    }

    [Fact]
    public void RealResponse_PlansWithNoticesLast()
    {
        var planned = StreamClassifier.PlanSync(Load(), p2pEnabled: true);

        var firstNotice = planned.ToList().FindIndex(e => e.IsNotice);
        if (firstNotice >= 0)
            Assert.All(planned.Skip(firstNotice), e => Assert.True(e.IsNotice));
        Assert.True(StreamClassifier.PlayableCount(planned) > 0);
    }

    [Fact]
    public void RealResponse_BuildsMediaInfoForEveryPlayableVersion()
    {
        var playable = StreamClassifier
            .PlanSync(Load(), p2pEnabled: true)
            .Where(e => !e.IsNotice && e.Data is not null)
            .ToList();

        var built = playable
            .Select(e => StreamMediaInfo.Build(e.Data!, e.Stream.Url, _ => null))
            .ToList();

        Assert.All(built, b => Assert.Equal(MediaStreamType.Video, b.Streams[0].Type));

        var trusted = built.Count(b => b.IsTrusted);
        output.WriteLine(
            $"{built.Count} playable versions with stream data; {trusted} trusted "
                + $"({(built.Count == 0 ? 0 : 100.0 * trusted / built.Count):F0}%)"
        );
    }
}
```

- [ ] **Step 5: Run it**

Run: `dotnet test Gelato.Tests/Gelato.Tests.csproj --filter "FullyQualifiedName~AioStreamsFixtureTests" --logger "console;verbosity=detailed"`
Expected: PASS, and the output shows the trusted share. Record that number for the PR description.

- [ ] **Step 6: Full verification**

```bash
dotnet build Gelato.csproj -v q --nologo 2>&1 | grep -E " error |Build succeeded"
dotnet test Gelato.Tests/Gelato.Tests.csproj
dotnet csharpier check Streams/ Gelato.Tests/Streams/ Controllers/GelatoApiController.cs
git diff -U0 main -- '*.cs' | grep -E "^\+" | grep -v "^+++" | awk 'length > 101'
```

Expected: `Build succeeded.`; all tests pass; csharpier reports no changes needed; awk prints
nothing.

- [ ] **Step 7: Commit**

```bash
git add scripts/capture-aiostreams-fixture.sh Gelato.Tests/Gelato.Tests.csproj \
  Gelato.Tests/Streams/Fixtures/aiostreams-movie.json Gelato.Tests/Streams/AioStreamsFixtureTests.cs
git commit -F - <<'EOF'
test: verify stream data parsing against a captured AIOStreams response

Add a capture script that scrubs every URL, hash and header before it
writes anything, plus a scrubbed real response that runs through parse,
classify, order and build end to end. The test reports what share of
versions AIOStreams actually probed.

Claude-Session: https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22
EOF
```

- [ ] **Step 8: Hand off for live verification**

Do not release on your own. Push the branch, open a PR (`gh pr create -R survivalizer/Gelato`)
whose body ends with `https://claude.ai/code/session_01BNsH41LtE6XTbXLsFdMq22`, and ask the
user whether to merge and cut a release. After they install it, verify on their server,
reading only — no form submits:

1. Dashboard → Plugins shows Chocolate Gelato **Active** — the new constructor dependencies
   resolved.
2. `GET /Items/{id}?Fields=MediaSources` for a movie browsed for the first time: its versions'
   `MediaStreams` include a video stream with width/height and a non-SDR `VideoRangeType` on an
   HDR version, before anything is played.
3. The server log has no `Invalid stream, skipping` lines for that title.
4. Playing a version whose data was probe quality logs no `Probing stream for` line; playing a
   guessed one does.
