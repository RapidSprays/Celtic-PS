#!/usr/bin/env bash
# Read-only inventory of a Celtic Heroes client install.
#
#   scripts/inventory.sh [APP_DIR] [OUT_DIR]   write file list, candidates, hashes, build info
#   scripts/inventory.sh --verify [APP_DIR]    check APP_DIR against inventories/sha256.txt
#
# APP_DIR defaults to the Steam install's CelticHeroes.app. Paths in the outputs are
# relative to APP_DIR, so the same sha256.txt verifies the Steam install, the snapshot
# and the pinned copy.
set -euo pipefail

STEAM="$HOME/Library/Application Support/Steam/steamapps"
APPID=2829820
DEFAULT_APP="$STEAM/common/Celtic Heroes/CelticHeroes.app"
REPO="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "${1:-}" == "--verify" ]]; then
  APP="${2:-$DEFAULT_APP}"
  cd "$APP"
  shasum -a 256 --quiet -c "$REPO/inventories/sha256.txt"
  echo "OK: $(wc -l < "$REPO/inventories/sha256.txt" | tr -d ' ') files match in $APP"
  exit 0
fi

APP="${1:-$DEFAULT_APP}"
OUT="${2:-$REPO/inventories}"
mkdir -p "$OUT"
[[ -d "$APP/Contents" ]] || { echo "not an .app bundle: $APP" >&2; exit 1; }
cd "$APP"

# file-list.txt: "<bytes>\t<relative path>", sorted for stable diffs
find . -type f -print0 | xargs -0 stat -f '%z%t%N' | sed 's#\t\./#\t#' | LC_ALL=C sort -t$'\t' -k2 > "$OUT/file-list.txt"

# candidates.txt: runtime, code, config and data files worth analysing
cut -f2 "$OUT/file-list.txt" | grep -Ei \
  '(^|/)(Managed|il2cpp_data|StreamingAssets|MonoBleedingEdge)/|GameAssembly|\.(dll|dylib|exe|assets|manifest|json|db|config|info)$|/MacOS/|boot\.config$|globalgamemanagers|Info\.plist$' \
  | grep -v '/aa/' > "$OUT/candidates.txt" || true

# sha256.txt: every file, relative paths
find . -type f -print0 | LC_ALL=C sort -z | xargs -0 shasum -a 256 | sed 's#  \./#  #' > "$OUT/sha256.txt"

# build-info.json
MANIFEST="$STEAM/appmanifest_$APPID.acf"
acf() { grep -E "^\s*\"$1\"" "$MANIFEST" | head -1 | awk -F'"' '{print $4}'; }
plist() { plutil -extract "$1" raw -o - Contents/Info.plist 2>/dev/null || echo ""; }
BUILDGUID="$(grep -E '^build-guid=' Contents/Resources/Data/boot.config | cut -d= -f2 || true)"
if [[ -f Contents/Resources/Data/Managed/Assembly-CSharp.dll ]]; then BACKEND=mono
elif ls Contents/Frameworks/GameAssembly* >/dev/null 2>&1; then BACKEND=il2cpp
else BACKEND=unknown; fi
cat > "$OUT/build-info.json" <<EOF
{
  "steam_appid": "$APPID",
  "steam_buildid": "$(acf buildid)",
  "steam_name": "$(acf name)",
  "steam_last_updated_unix": "$(acf LastUpdated)",
  "bundle_id": "$(plist CFBundleIdentifier)",
  "app_version": "$(plist CFBundleShortVersionString)",
  "bundle_version": "$(plist CFBundleVersion)",
  "unity_version": "$(plist CFBundleGetInfoString | grep -oE '[0-9]{4}\.[0-9]+\.[0-9]+[a-z][0-9]+' | head -1)",
  "unity_build_guid": "$BUILDGUID",
  "scripting_backend": "$BACKEND",
  "file_count": $(wc -l < "$OUT/file-list.txt" | tr -d ' '),
  "total_bytes": $(awk -F'\t' '{s+=$1} END {printf "%d", s}' "$OUT/file-list.txt"),
  "source_path": "$APP",
  "generated_utc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF
echo "Wrote inventory for $APP to $OUT"
cat "$OUT/build-info.json"
