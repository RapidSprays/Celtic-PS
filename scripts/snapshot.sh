#!/usr/bin/env bash
# Read-only analysis snapshot of the client's code and config (not the 2.5 GB asset bundles;
# those live in the pinned copy). Lands in ~/CelticHeroesAnalysis/snapshots/<buildid>/.
#
#   scripts/snapshot.sh [APP_DIR]
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="${1:-$HOME/Library/Application Support/Steam/steamapps/common/Celtic Heroes/CelticHeroes.app}"
BUILD="$(grep -o '"steam_buildid": "[0-9]*"' "$REPO/inventories/build-info.json" | grep -o '[0-9]\+')"
DEST="$HOME/CelticHeroesAnalysis/snapshots/$BUILD"
DATA="Contents/Resources/Data"

if [[ -d "$DEST" ]]; then echo "snapshot already exists: $DEST (delete it first to retake)" >&2; exit 1; fi
mkdir -p "$DEST"
cd "$APP"
# Managed code, Unity config, and StreamingAssets minus the asset bundles
rsync -a --relative \
  "$DATA/Managed" "$DATA/boot.config" "$DATA/app.info" \
  "$DATA/RuntimeInitializeOnLoads.json" "$DATA/ScriptingAssemblies.json" \
  Contents/Info.plist "$DEST/"
rsync -a --relative --exclude 'aa/' --exclude 'AndroidAssets/' "$DATA/StreamingAssets" "$DEST/"

# Verify every copied file against the inventory, then make the snapshot read-only
cd "$DEST"
FILES="$(mktemp)"
find . -type f | sed 's#^\./##' > "$FILES"
# sha256.txt lines are "<64 hex>  <path>": match on the exact path
awk 'NR==FNR {want[$0]; next} substr($0, 67) in want' "$FILES" "$REPO/inventories/sha256.txt" > "$FILES.sums"
N="$(wc -l < "$FILES" | tr -d ' ')"
[[ "$(wc -l < "$FILES.sums" | tr -d ' ')" == "$N" ]] || { echo "snapshot has files missing from the inventory" >&2; exit 1; }
shasum -a 256 --quiet -c "$FILES.sums"
rm -f "$FILES" "$FILES.sums"
chmod -R a-w "$DEST"
echo "OK: snapshot of $N files at $DEST, hashes match, made read-only"
