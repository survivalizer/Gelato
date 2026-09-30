# AIOStreams stream data — design

**Sub-project 1 of 6 in the AIOStreams parity program.**
Date: 2026-09-29 · Status: design approved, spec under review

All AIOStreams file references are pinned to `Viren070/AIOStreams` `main` @
`d029954da44802fbb152ca5583aa2b468c45cdb0`.

## Intent

**Asked for:** expand Gelato so it fully supports AIOStreams in Jellyfin, to the level of
**parity with AIOStreams' own Jellyfin mode**. AIOStreams can now run as a
Jellyfin-compatible server itself (`packages/server/src/routes/jellyfin/`).

**Agreed constraints:** Gelato stays a Jellyfin plugin, keeps working with any Stremio
addon, and is a public release.

Parity covers the content AIOStreams layers on top, not server plumbing. Auth, users,
Quick Connect, websockets, the web client, play state and scheduled tasks all come from the
real Jellyfin that Gelato runs inside.

### The parity program

| # | Sub-project | When |
|---|---|---|
| 1 | Stream data foundation | **this spec** |
| 2 | Playback headers (`proxyHeaders`) | after 1 |
| 3 | Continuity (`bingeGroup`, per-session source memo) | after 2 |
| 4 | Subtitles (per-stream + addon + embedded merge) | after 3 |
| 5 | Segments (AniSkip, Anime-Skip, PMDB beside IntroDB) | independent |
| 6 | Library & discovery (genre catalogs, people, recommendations) | independent |

Sub-project 1 comes first because it introduces the parsed stream model that 2–4 read from.

## Goal

Gelato receives AIOStreams' parsed stream data and uses it the way AIOStreams' Jellyfin mode
does:

- every version shows accurate media info (resolution, codec, HDR type, bit depth, audio
  tracks, subtitle tracks, size) before it is played;
- AIOStreams notices and errors appear in Jellyfin instead of being silently dropped.

**Success criteria**

1. For the same title, before playback, Jellyfin's version picker and media info match what
   AIOStreams' Jellyfin mode shows. (After playback a guessed version may legitimately
   change, because ffprobe replaces the guess with real data.)
2. Playback starts without an ffprobe pass when AIOStreams already probed the file.
3. A misconfigured AIOStreams (e.g. invalid credentials) is visible in the UI, not only in
   the server log.

**Constraints**

- Other addons behave as today, except for the notice entries described below.
- Playback decisions never get worse than today.

### Non-goals

- Request headers (`proxyHeaders`) — sub-project 2.
- `bingeGroup` continuity — sub-project 3.
- Per-stream and addon subtitle merging — sub-project 4.
- Native Stremio Usenet (`nzbUrl`/`servers`) and archive streams (`rarUrls` etc.).
  AIOStreams' built-in Usenet engine already delivers those as plain URLs.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Obtaining stream data | Send `User-Agent: AIOStreams-Gelato/<plugin version>` on addon requests | AIOStreams' `provideStreamData` defaults to `null`, meaning auto-detect: enabled for any User-Agent starting with `AIO` (`packages/server/src/routes/stremio/stream.ts`). Works on any instance, public ones included, with no setup. An operator's explicit `false` still wins. No other AIOStreams behaviour keys off the `AIO` prefix. `Gelato` in the name lets AIOStreams *variants* (User-Agent-conditioned configs) target Jellyfin. |
| Trusting AIOStreams' media info | By source, using `parsedFile.mediaInfoQuality` | AIOStreams' Jellyfin mode never probes; Gelato has ffprobe. Trust `probe` quality; re-probe anything guessed from a filename (`indexer`, `addon`). Display reaches parity and playback decisions are never worse than today. |
| Notices | Shown as versions, after every playable version | Matches AIOStreams' Jellyfin mode. |
| Persisting raw stream data | No | About 2 KB per version and 400+ versions on some titles. Only the built media info (Jellyfin's media-stream table) and a provenance tag are saved. |

## Design

### Definitions

- **Playable stream:** has a valid URL (today's `StremioStream.IsValid()`) or a torrent hash.
  Streams that need request headers remain playable in this sub-project; sub-project 2
  changes that.
- **Notice:** defined under [Notices](#notices).
- A stream that is neither playable nor a notice is dropped, as today.

### New units

1. **Request identity.** `GelatoStremioProvider.NewClient()` sets the User-Agent on every
   addon request. The version comes from
   `GelatoPlugin.Instance.GetType().Assembly.GetName().Version`. The TMDB client in the same
   file is not addon traffic and is unchanged.

2. **`StreamData` model** (new file). `StremioStream` gains `StreamData` as a raw
   `JsonElement?`. A mapper converts it to the typed model **per stream, inside a
   try/catch**. If one stream's data fails to map, that stream's `StreamData` is `null` and
   it falls back to today's behaviour; the rest of the response is unaffected. Every field is
   optional and unknown fields are ignored. `StremioStream` also gains `ExternalUrl`, used
   only by the notice duplicate-link rule. Fields used:

   | Field | Notes |
   |---|---|
   | `type` | `http`, `debrid`, `usenet`, `p2p`, `live`, `info`, `external`, `youtube`, `error`, `statistic`, … |
   | `size` | bytes |
   | `duration` | **milliseconds** (AIOStreams maps it to `durationMs`, `media.ts:228`) |
   | `filename`, `addon`, `message` | strings |
   | `service` | `{ id, cached }` |
   | `parsedFile` | `resolution`, `encode`, `visualTags[]`, `audioTags[]`, `audioChannels[]`, `languages[]`, `subtitles[]`, `audioTracks[]`, `subtitleTracks[]`, `mediaInfoQuality` (`probe` \| `indexer` \| `addon`), `container`, `extension`, `releaseGroup` |
   | track (audio/subtitle) | `lang`, `codec`, `title`, `tag`, `channels`, `default`, `forced`, `commentary`, `dub`, `original`, `hearingImpaired`, `visualImpaired` |

3. **`StreamMediaInfo` builder.** A pure function from `StreamData` to Jellyfin
   `MediaStream`s plus container, runtime ticks and size. It is a port of `buildMediaStreams`
   and its helpers in `packages/core/src/jellyfin/media.ts`, with the mapping tables copied
   verbatim: `ENCODE_CODEC`, `AUDIO_CODEC`, `CHANNEL_COUNT`, `CHANNEL_LAYOUT`,
   `RESOLUTION_SIZE`, `VIDEO_RANGE_TYPE`, `RANGE_PRIORITY`, `NOT_A_TRACK`.

   - **Video:** codec from `ENCODE_CODEC`; width and height from `RESOLUTION_SIZE`;
     `VideoRangeType` from the visual tags via `VIDEO_RANGE_TYPE`, resolved by priority
     DOVI > HDR10Plus > HDR10 > HLG > SDR; `VideoRange` is `SDR` when the type is SDR,
     otherwise `HDR`; `BitDepth` is 10 when the range is not SDR or the tags include
     `10bit`, otherwise 8.
   - **Audio:** one stream per `audioTracks` entry when present (codec is the track's
     codec, else `AUDIO_CODEC[tag]`; channels from `CHANNEL_COUNT` and `CHANNEL_LAYOUT`).
     Otherwise the placeholder and single-stream fallbacks of `audioStreams`.
   - **Subtitles:** embedded `subtitleTracks` only when `mediaInfoQuality` is `probe`;
     otherwise the placeholder languages of `embeddedSubtitleStreams`.
   - **Container:** `parsedFile.container`, then `parsedFile.extension`, then the filename's
     extension, then the URL's; accepted values as in `containerOf`; default `mkv`.
   - **Runtime:** `duration` milliseconds converted to ticks.
   - **Unknown values** leave the field empty. The builder never guesses.

4. **Provenance.** A GelatoData key, `mediaInfoSource`, on each stream row: `ffprobe`,
   `aiostreams-probed` or `aiostreams-guessed`. A pure predicate treats `ffprobe` and
   `aiostreams-probed` as trusted. A missing value means legacy (see
   [Failure handling](#failure-handling-and-compatibility)).

### Changes to existing code

- **`GelatoManager.SyncStreams`.** For each playable stream that has stream data: build the
  media info, save it through `IMediaStreamRepository.SaveMediaStreams`, set container, size
  and runtime, and record provenance (`aiostreams-probed` when `mediaInfoQuality` is `probe`,
  otherwise `aiostreams-guessed`). **If the row's provenance is already `ffprobe`, skip it** —
  a guess must never replace real data on re-sync.
- **`MediaSourceManagerDecorator.NeedsProbe`.** Probe when the row is not a notice and any of:
  no video stream; runtime under 2 minutes; provenance is `aiostreams-guessed`. After
  `ProbeStreamAsync` succeeds, set provenance to `ffprobe`.
- **Notice endpoint.** `GET /gelato/notice` on `GelatoApiController` serves a bundled
  placeholder clip: an embedded resource, an h264 mp4 under 20 KB, generated once with
  ffmpeg and committed.

### Probe decision

| Row | Provenance | Probe? |
|---|---|---|
| Notice | — | Never |
| Legacy, has a video stream, runtime ≥ 2 min | none | No (as today) |
| Legacy, no video stream or runtime < 2 min | none | Yes (as today) |
| Guessed from filename | `aiostreams-guessed` | Yes; the result replaces the guess and provenance becomes `ffprobe` |
| Probed by AIOStreams | `aiostreams-probed` | No, unless runtime < 2 min (as today) |
| Probed by Gelato | `ffprobe` | No, unless runtime < 2 min (as today) |

### Notices

**Classification.** A stream is a notice when either:

- it has stream data and its `type` is `error`, `statistic`, `info`, `external` or
  `youtube`; or
- it has no stream data and no playable locator (no valid URL and no torrent hash).

A notice whose link duplicates a playable version's URL is dropped, as in AIOStreams'
`noticeStreamsOf` (`packages/server/src/routes/jellyfin/resolve.ts`).

AIOStreams' Jellyfin mode lists only `external` and `info` as addon notice types and builds
errors and statistics separately. Over the Stremio protocol all of these arrive as streams,
so Gelato classifies them together. `youtube` is a deliberate addition, approved in design,
so a YouTube-only stream from any addon appears as a notice.

**Presentation.** Each notice becomes a stream row named like any version: name, newline,
description, and marked with the GelatoData key `notice = true`, which is how
`NeedsProbe` and playback recognise it. Notices take `index` values after every playable
version. Versions are already
ordered by `index` (`MediaSourceManagerDecorator.cs:264`), so a notice is never the default
while a playable version exists. When only notices exist, the first one is shown.

**Identity.** `StremioStream.GetGuid()` throws for a stream with no URL, hash, or
`bingeGroup` + filename. A notice derives its identity by hashing the key
`notice|{type}|{name}|{description}` the same way `GetGuid()` hashes its keys (MD5 to a
`Guid`), with `type` empty when there is no stream data. The same notice therefore maps to
the same row across syncs instead of churning.

**Playback.** Path is `http://127.0.0.1:{port}/gelato/notice`. Being loopback, the existing
reachability rules keep it out of direct play and the stream redirect. At sync it is saved
with fixed placeholder media info (one h264 video stream) and no provenance, and it is never
probed.

**Lifecycle.** The existing stale-row cleanup in `SyncStreams` removes a notice once the
addon stops sending it.

**Logging.** The per-sync `Invalid stream, skipping` warning is removed. Notices are logged
at debug level.

## Failure handling and compatibility

- **Malformed stream data:** a per-stream mapping failure falls back to today's behaviour for
  that stream only.
- **Unknown values:** left empty, never guessed.
- **Operator sets `provideStreamData: false`:** no stream data; today's behaviour.
- **Other addons:** the User-Agent is harmless; with no stream data they behave as today,
  except that URL-less streams now appear as notices.
- **Upgrade:** rows without provenance are legacy. Until now ffprobe was the only source of
  media info, so their data is trusted as today, and rows with no media info already trigger
  a probe. Upgrading re-probes nothing and loses nothing.
- **Re-sync** never overwrites `ffprobe` media info with a guess.
- **Jellyfin 12.1:** `IMediaStreamRepository.SaveMediaStreams` is confirmed present in
  `Jellyfin.Controller` 12.1.0.

## Testing

Test-driven: each test is written first and watched fail.

**Pure unit tests**

- `StreamData` mapping: complete, partial, extra fields, wrong types (yields `null`, never
  throws), and per-stream isolation within one response.
- `StreamMediaInfo`: table-driven against the pinned AIOStreams tables; HDR priority; bit
  depth; audio tracks versus the tag fallback; subtitle tracks only at `probe` quality; the
  container fallback chain; milliseconds to ticks; unknown values left empty.
- Provenance trust predicate.
- Notice classification: each type, the no-data locator rule, a root-path URL, the
  duplicate-link drop; identity stability across syncs.
- Every row of the probe decision table.
- User-Agent format.

**Fixtures:** real AIOStreams stream responses carrying stream data, scrubbed of tokens and
URLs, run end to end through parse and build.

**"Never overwrite ffprobe":** tested through pure helpers, following the existing pattern
(`DownloadFilter.TryGetMediaSourceId`, `VideoStreamRedirectFilter.ShouldRedirect`).

**Live verification** on the user's server:

1. The new User-Agent receives `streamData`.
2. The version picker shows media info before anything is played.
3. The share of versions at `probe` quality (see the open question).

## Open question

How often is AIOStreams' data `probe` quality? It depends on the debrid service and addons.
The design is correct either way; the answer only decides how often playback skips ffprobe.
It is measured during live verification.

## Findings carried to later sub-projects

- **Sub-project 2:** AIOStreams' Jellyfin mode treats a stream that needs request or
  response headers as **not playable** unless AIOStreams proxied it (`isPlayable`,
  `media.ts:166`). Gelato currently offers such streams and they fail.
- **Sub-project 3:** `bingeGroup` is already stored on each stream row and read at
  `MediaSourceManagerDecorator.cs:580` into a variable that is never used.
- **Sub-project 4:** AIOStreams merges per-stream `subtitles` with the subtitles resource and
  embedded tracks (`packages/core/src/jellyfin/subtitles.ts`).
