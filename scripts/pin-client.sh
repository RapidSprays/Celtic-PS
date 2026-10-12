#!/usr/bin/env bash
# Copy the whole client .app to ~/CelticHeroesAnalysis/client/<buildid>/ so the private
# server targets one fixed build, independent of Steam updates. The copy starts identical
# to the Steam install (hash-verified); CH.Patcher later modifies only this copy.
#
#   scripts/pin-client.sh [APP_DIR]
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="${1:-$HOME/Library/Application Support/Steam/steamapps/common/Celtic Heroes/CelticHeroes.app}"
BUILD="$(grep -o '"steam_buildid": "[0-9]*"' "$REPO/inventories/build-info.json" | grep -o '[0-9]\+')"
APPID="$(grep -o '"steam_appid": "[0-9]*"' "$REPO/inventories/build-info.json" | grep -o '[0-9]\+')"
DEST="$HOME/CelticHeroesAnalysis/client/$BUILD"

if [[ -d "$DEST/CelticHeroes.app" ]]; then echo "pinned copy already exists: $DEST" >&2; exit 1; fi
mkdir -p "$DEST"
# ditto keeps bundle metadata, extended attributes and the code signature intact
ditto "$APP" "$DEST/CelticHeroes.app"
"$REPO/scripts/inventory.sh" --verify "$DEST/CelticHeroes.app"

# Steamworks reads the app id from steam_appid.txt in the working directory when the game
# isn't launched by Steam itself. This identifies the game to the running Steam client; it
# doesn't bypass ownership checks.
echo "$APPID" > "$DEST/steam_appid.txt"
echo "OK: pinned build $BUILD at $DEST"
