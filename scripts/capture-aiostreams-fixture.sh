#!/usr/bin/env bash
# Capture an AIOStreams stream response as a test fixture, scrubbed of every URL, hash and
# header before it is written. The base URL carries the user's token: it is only passed to
# curl and is never printed, echoed, or written anywhere.
#
# Usage: scripts/capture-aiostreams-fixture.sh <out.json> [imdb-id]
#   <out.json>  fixture output path.
#   [imdb-id]   defaults to tt0111161.
#
# The AIOStreams base URL (the addon URL without /manifest.json) comes from the
# AIOSTREAMS_URL environment variable. If that is unset or empty, you are prompted for it on
# the terminal instead, with input hidden (not echoed).
#
#   AIOSTREAMS_URL='https://.../stremio/<token>' scripts/capture-aiostreams-fixture.sh out.json
#   scripts/capture-aiostreams-fixture.sh out.json   # prompts for the URL instead
set -euo pipefail

out="${1:?output path required}"
id="${2:-tt0111161}"

base="${AIOSTREAMS_URL:-}"
if [ -z "$base" ]; then
  read -rs -p "AIOStreams URL (without /manifest.json): " base
  echo >&2
fi
base="${base%/manifest.json}"
base="${base%/}"
if [ -z "$base" ]; then
  echo "AIOStreams base URL required (set AIOSTREAMS_URL or enter it at the prompt)" >&2
  exit 1
fi

ver=$(sed -n 's/^version: *"\{0,1\}\([^"]*\)"\{0,1\}$/\1/p' build.yaml)
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
mkdir -p "$(dirname "$out")"

curl -fsS -A "AIOStreams-Gelato/${ver:-0.0.0.0}" "${base}/stream/movie/${id}.json" -o "$tmp"

python3 - "$tmp" "$out" <<'PY'
import itertools, json, re, sys

src, dst = sys.argv[1], sys.argv[2]
counter = itertools.count()
URL = re.compile(r"https?://[^\s\"']+")
HEX40 = re.compile(r"(?<![0-9a-fA-F])[0-9a-fA-F]{40}(?![0-9a-fA-F])")

def fake_url():
    return f"https://example.invalid/scrubbed/{next(counter)}"

def scrub_hex(text):
    return HEX40.sub("0" * 40, text)

def scrub(value, key=None):
    if key in ("url", "externalUrl", "nzbUrl"):
        return fake_url() if isinstance(value, str) and value else value
    if key in ("sources", "servers", "rarUrls", "zipUrls", "7zipUrls", "tgzUrls", "tarUrls"):
        return [fake_url() for _ in value] if isinstance(value, list) else None
    if key == "infoHash":
        return "0" * 40 if value else value
    if key in ("proxyHeaders", "headers", "request", "response"):
        return None
    if isinstance(value, dict):
        return {k: scrub(v, k) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v) for v in value]
    if isinstance(value, str):
        return scrub_hex(URL.sub(lambda _: fake_url(), value))
    return value

data = scrub(json.load(open(src)))
json.dump(data, open(dst, "w"), indent=2, ensure_ascii=False)
streams = data.get("streams", [])
with_data = sum(1 for s in streams if isinstance(s.get("streamData"), dict))
print(f"wrote {dst}: {len(streams)} streams, {with_data} with streamData")
PY
