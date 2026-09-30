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
| Dolby Vision mapping | More precise than AIOStreams (DV + HDR10 → DOVI with HDR10) | Jellyfin's direct-play decisions distinguish DV profiles and Gelato trusts probe data without re-probing; see the builder. |
| Persisting raw stream data | No | About 2 KB per version and 400+ versions on some titles. Only the built media info (Jellyfin's media-stream table) and a provenance tag are saved. |

## Design

### Definitions

- **Playable stream:** exactly what today's filter accepts. It has a valid URL
  (`StremioStream.IsValid()`), and it is skipped if it carries a torrent hash while P2P is
  disabled. Streams that need request headers remain playable in this sub-project;
  sub-project 2 changes that. Torrent-only streams (a hash, no URL) are rejected by
  `IsValid()` today and stay rejected here (see the findings).
- **Notice:** defined under [Notices](#notices).
- A stream that is neither playable nor a notice is dropped, as today.

### New units

1. **Request identity.** `GelatoStremioProvider.NewClient()` sets the User-Agent on every
   addon request. The version comes from
   `GelatoPlugin.Instance.GetType().Assembly.GetName().Version`. The TMDB client in the same
   file is not addon traffic and is unchanged.

2. **`StreamData` model** (new file). `StremioStream` gains `StreamData` as a raw
   `JsonElement?`. A mapper converts it to the typed model **per stream, inside a
   try/catch**, reading each field only when it has the expected JSON type. A wrong-typed
   field is dropped and the rest still maps; if `streamData` is not an object at all, that
   stream's `StreamData` is `null` and it falls back to today's behaviour. The rest of the
   response is unaffected either way. Every field is optional and unknown fields are
   ignored. `StremioStream` also gains `ExternalUrl`, used
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
   `MediaStream`s plus container, runtime ticks, size and a trust verdict. It ports
   `buildMediaStreams` and its helpers in `packages/core/src/jellyfin/media.ts`, with the
   mapping tables copied verbatim: `ENCODE_CODEC`, `AUDIO_CODEC`, `CHANNEL_COUNT`,
   `CHANNEL_LAYOUT`, `RESOLUTION_SIZE`, `NOT_A_TRACK`, and the container list of
   `containerOf`.

   **It sets Jellyfin's inputs, not its outputs.** AIOStreams emits JSON and sets
   `VideoRange`, `VideoRangeType`, `DisplayTitle` and `IsTextSubtitleStream` directly. In
   Jellyfin 12.1 these are read-only, computed by `MediaStream.GetVideoColorRange()` and
   friends (`MediaBrowser.Model/Entities/MediaStream.cs` @ `v12.1`). The builder sets the
   fields they are computed from:

   | Intended range | Fields set |
   |---|---|
   | SDR | none |
   | HDR10 | `ColorTransfer=smpte2084`, `ColorPrimaries=bt2020`, `ColorSpace=bt2020nc` |
   | HDR10+ | HDR10 fields + `Hdr10PlusPresentFlag=true` |
   | HLG | `ColorTransfer=arib-std-b67`, `ColorPrimaries=bt2020`, `ColorSpace=bt2020nc` |
   | DOVI (profile 5) | `DvProfile=5`, `RpuPresentFlag=1`, `BlPresentFlag=1`, `DvBlSignalCompatibilityId=0` |
   | DOVI with HDR10 | `DvProfile=8`, `RpuPresentFlag=1`, `BlPresentFlag=1`, `DvBlSignalCompatibilityId=1` + HDR10 fields |
   | DOVI with HDR10+ | DOVI-with-HDR10 fields + `Hdr10PlusPresentFlag=true` |
   | DOVI with HLG | `DvProfile=8`, `RpuPresentFlag=1`, `BlPresentFlag=1`, `DvBlSignalCompatibilityId=4` + HLG fields |

   Track titles and flag labels (Forced, Commentary, …) go in `Title`, which Jellyfin folds
   into its computed `DisplayTitle`. A subtitle's text/image kind follows from its `Codec`.

   **Dolby Vision is mapped more precisely than AIOStreams does.** The parser emits a DV file
   with an HDR10 base as `visualTags: ["DV", "HDR10"]`; AIOStreams' priority rule collapses
   that to `DOVI`. In Jellyfin, `DOVI` (profile 5, no fallback) and `DOVIWithHDR10` (profile
   8.1, playable by HDR10 devices) lead to different direct-play decisions, and Gelato trusts
   probe-quality data without re-probing. Collapsing would make decisions worse than
   ffprobe's, which the constraints forbid. (`HDR+DV`, `DV Only` and `HDR Only` are filter-only
   tags that the parser never emits.) Rules, first match wins:

   | Visual tags contain | Range |
   |---|---|
   | `DV` and `HDR10+` | DOVI with HDR10+ |
   | `DV` and (`HDR10` or `HDR`) | DOVI with HDR10 |
   | `DV` and `HLG` | DOVI with HLG |
   | `DV` | DOVI (profile 5) |
   | `HDR10+` | HDR10+ |
   | `HDR10` or `HDR` | HDR10 |
   | `HLG` | HLG |
   | otherwise | SDR |

   `BitDepth` is 10 when the range is not SDR or the tags include `10bit`, otherwise 8.

   - **Video:** codec from `ENCODE_CODEC`; width and height from `RESOLUTION_SIZE`; range
     fields as above.
   - **Audio:** one stream per `audioTracks` entry when present (codec is the track's codec,
     else `AUDIO_CODEC[tag]`; channels from `CHANNEL_COUNT` and `CHANNEL_LAYOUT`). Otherwise,
     on guessed data only, the placeholder and single-stream fallbacks of `audioStreams`.
   - **Subtitles:** embedded `subtitleTracks` when `mediaInfoQuality` is `probe`; otherwise,
     on guessed data only, the placeholder languages of `embeddedSubtitleStreams`. At probe
     quality an empty track list means AIOStreams probed the file and found no subtitles;
     placeholders (built from the filename's language list) would advertise subtitles the
     file doesn't have, on a row that is never re-probed. Guessed rows are re-probed before
     playback, so placeholders there are display-only.
   - **Languages:** resolved through Jellyfin's
     `ILocalizationManager.FindLanguageInfo(name).ThreeLetterISOLanguageName` (AIOStreams
     sends English names). Unresolved names leave `Language` empty.
   - **Container:** `parsedFile.container`, then `parsedFile.extension`, then the filename's
     extension, then the URL's; accepted values as in `containerOf`; default `mkv`.
   - **Runtime:** a positive `duration` in milliseconds converted to ticks. Missing, zero or
     negative leaves runtime empty rather than zero, since a zero runtime would force a probe
     on every play.
   - **Missing `parsedFile`** yields a video stream and a single audio stream, neither with a
     codec or dimensions, untrusted (as AIOStreams' own fallbacks produce).
   - **Bitrate:** when size and a positive duration are both known, the video stream's
     `BitRate` is the average `size × 8 ÷ seconds`, capped at `int.MaxValue`. Jellyfin's
     remote bitrate limit reads it, and ffprobe would otherwise supply it.
   - **Unknown values** leave the field empty. The builder never guesses.

   **Trust verdict.** The result is trusted (`aiostreams-probed`) only when
   `mediaInfoQuality` is `probe` **and** the video codec and resolution are both known
   **and** there is at least one audio track and every audio track has a codec. Anything
   less is `aiostreams-guessed` and gets probed before playback, so a thin probe result can
   never stand in for ffprobe.

4. **Provenance.** A GelatoData key, `mediaInfoSource`, on each stream row: `ffprobe`,
   `aiostreams-probed` or `aiostreams-guessed`. A pure predicate treats `ffprobe` and
   `aiostreams-probed` as trusted. A missing value means legacy (see
   [Failure handling](#failure-handling-and-compatibility)).

### Changes to existing code

- **`GelatoManager.SyncStreams`.** For each playable stream that has stream data: build the
  media info, set container, size and runtime, and record provenance from the trust
  verdict. Media streams are saved through `IMediaStreamRepository.SaveMediaStreams` **after**
  `SaveItems`, because a media stream references its item. Whether to write follows the
  write rules below; a guess never replaces real data, and an unchanged version is never
  rewritten.
- **`MediaSourceManagerDecorator.NeedsProbe`.** Probe when the row is not a notice and any of:
  no video stream; runtime under 2 minutes; provenance is `aiostreams-guessed`.
  `ProbeStreamAsync` catches its own exceptions today, so the caller can't tell whether it
  worked; it changes to return `bool`. Provenance becomes `ffprobe` only when it returns
  `true`, so a failed probe never passes a guess off as real data.

### Media-info write rules

Decided per stream row on every sync:

| Row | Existing provenance | New data | Action |
|---|---|---|---|
| New | — | any | Write; provenance from the trust verdict |
| Existing | none (legacy), has a video stream | any | Don't write; stamp provenance `ffprobe` (before this release ffprobe was the only writer) |
| Existing | none (legacy), no video stream | any | Write |
| Existing | `aiostreams-guessed` | trusted | Write (upgrade) |
| Existing | `aiostreams-guessed` | guessed | Don't write |
| Existing | `aiostreams-probed` or `ffprobe` | any | Don't write |

The video-stream check for a legacy row runs once: afterwards the row has provenance. A
title whose 400+ versions have not changed therefore causes no media-stream writes when it
is browsed again.
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
`noticeStreamsOf` (`packages/server/src/routes/jellyfin/resolve.ts`). A notice with no
name, description or title is dropped: it would be a blank entry with nothing to say.

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
`notice|{type}|{name}|{occurrence}` the same way `GetGuid()` hashes its keys (MD5 to a
`Guid`). `type` is empty when there is no stream data; `occurrence` counts earlier notices in
the same response with the same type and name, so two identical-looking notices get two
rows. The description is deliberately left out: statistics and many errors carry timings or
counts that change on every sync, and keying on them would delete and recreate the row
each time. The row's name and description are updated in place instead.

**Playback.** Path is `http://127.0.0.1:{port}/gelato/notice?id={guid:N}`. The query makes
each notice's path unique, which matters because a new row's `Id` is derived from its path;
the endpoint ignores it. Being loopback, the existing reachability rules keep it out of
direct play and the stream redirect. At sync it is saved with fixed placeholder media info
(one h264 video stream, container `mp4`), no provenance and no runtime, and it is never
probed. The clip is black, 640×360, 4 seconds, about 2 KB: the version name already carries
the message, and the local ffmpeg build has no text filter.

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
  media info, so a legacy row that has a video stream is stamped `ffprobe` and kept; one
  without media info receives stream data (see the write rules). Upgrading re-probes nothing
  and loses nothing.
- **Failed probe:** provenance stays as it was, so a guessed row is probed again next time.
- **Re-sync** never overwrites `ffprobe` media info with a guess.
- **Jellyfin 12.1:** `IMediaStreamRepository.SaveMediaStreams` is confirmed present in
  `Jellyfin.Controller` 12.1.0.

## Testing

Test-driven: each test is written first and watched fail.

**Pure unit tests**

- `StreamData` mapping: complete, partial, extra fields; a wrong-typed field is dropped while
  the rest maps; a non-object `streamData` yields `null`; never throws; per-stream
  isolation within one response.
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

- **Standalone bug, not part of parity:** since upstream commit `1ef6fdf` (2026-02-21, "code
  cleanup and null checks"), `StremioStream.IsValid()` requires a URL, so torrent-only
  streams are rejected before `SyncStreams` builds their torrent-proxy path. That branch is
  unreachable for them, and Gelato's torrent engine never sees a pure torrent stream.
  Fixing it changes which versions users see, so it gets its own change.

- **Sub-project 2:** AIOStreams' Jellyfin mode treats a stream that needs request or
  response headers as **not playable** unless AIOStreams proxied it (`isPlayable`,
  `media.ts:166`). Gelato currently offers such streams and they fail.
- **Sub-project 3:** `bingeGroup` is already stored on each stream row and read at
  `MediaSourceManagerDecorator.cs:580` into a variable that is never used.
- **Sub-project 4:** AIOStreams merges per-stream `subtitles` with the subtitles resource and
  embedded tracks (`packages/core/src/jellyfin/subtitles.ts`).
