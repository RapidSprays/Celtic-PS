#!/usr/bin/env bash
# Start the private server and launch the patched pinned client against it.
#
#   scripts/run-private.sh            server + client; Ctrl+C stops both
#   scripts/run-private.sh --server   server only
#
# Requires: Steam running, scripts/pin-client.sh done, CH.Patcher applied (patch-state.json present).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
BUILD=24928510
CLIENT_DIR="$HOME/CelticHeroesAnalysis/client/$BUILD"
DATA="$HOME/CelticHeroesAnalysis/server-data"
LOGS="$HOME/CelticHeroesAnalysis/logs"
STAMP="$(date +%Y%m%d-%H%M%S)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

[[ -f "$CLIENT_DIR/patch-state.json" ]] || { echo "pinned client is not patched: run CH.Patcher first" >&2; exit 1; }
patched="$(grep -o '"patchedSha256": "[0-9a-f]*"' "$CLIENT_DIR/patch-state.json" | grep -o '[0-9a-f]\{64\}')"
actual="$(shasum -a 256 "$CLIENT_DIR/CelticHeroes.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll" | cut -c1-64)"
[[ "$patched" == "$actual" ]] || { echo "client DLL differs from patch-state.json; re-run CH.Patcher" >&2; exit 1; }
pgrep -x steam_osx >/dev/null || { echo "Steam is not running" >&2; exit 1; }

mkdir -p "$LOGS"
dotnet build -c Release "$REPO/server/src/CH.Server" >/dev/null
dotnet "$REPO/server/src/CH.Server/bin/Release/net8.0/CH.Server.dll" --data "$DATA" > "$LOGS/server-$STAMP.log" 2>&1 &
SERVER=$!
CLIENT=""
cleanup() {
  [[ -n "$CLIENT" ]] && kill "$CLIENT" 2>/dev/null || true
  kill -TERM "$SERVER" 2>/dev/null || true
  wait "$SERVER" 2>/dev/null || true
  echo "Stopped. Server log: $LOGS/server-$STAMP.log"
}
trap cleanup EXIT INT TERM
sleep 2
kill -0 "$SERVER" 2>/dev/null || { cat "$LOGS/server-$STAMP.log"; exit 1; }
echo "Server up (pid $SERVER). Log: $LOGS/server-$STAMP.log"

if [[ "${1:-}" == "--server" ]]; then
  wait "$SERVER"
  exit 0
fi

# steam_appid.txt in the working directory identifies the game to the running Steam client.
(cd "$CLIENT_DIR" && exec "CelticHeroes.app/Contents/MacOS/Celtic Heroes") > "$LOGS/client-stdout-$STAMP.log" 2>&1 &
CLIENT=$!
echo "Client up (pid $CLIENT). Player.log: ~/Library/Logs/One Thumb Mobile Ltd/Celtic Heroes/Player.log"
echo "Isolation check: lsof -a -p $CLIENT -i -n -P   (only 127.0.0.1 and Steam loopback expected)"
wait "$CLIENT" || true
