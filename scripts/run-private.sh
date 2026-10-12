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
if lsof -nP -iTCP:5555 -sTCP:LISTEN >/dev/null 2>&1 || lsof -nP -iUDP:5556 >/dev/null 2>&1; then
  echo "A private server is already running (ports 5555/5556 in use):" >&2
  pgrep -fl 'CH.Server.dll' >&2 || true
  echo "Quit the game and stop the other run-private.sh (Ctrl+C in its terminal) first." >&2
  exit 1
fi

mkdir -p "$LOGS"
dotnet build -c Release "$REPO/server/src/CH.Server" >/dev/null
dotnet "$REPO/server/src/CH.Server/bin/Release/net8.0/CH.Server.dll" --data "$DATA" > "$LOGS/server-$STAMP.log" 2>&1 &
SERVER=$!
CLIENT=""
cleanup() {
  [[ -n "$CLIENT" ]] && kill "$CLIENT" 2>/dev/null || true
  [[ -n "$SERVER" ]] && kill -TERM "$SERVER" 2>/dev/null || true
  [[ -n "$SERVER" ]] && wait "$SERVER" 2>/dev/null || true
  echo "Stopped. Server log: $LOGS/server-$STAMP.log"
}
trap cleanup EXIT INT TERM
# Loading the client balance data takes a few seconds; wait until the server actually answers.
for _ in $(seq 1 60); do
  kill -0 "$SERVER" 2>/dev/null || { echo "Server failed to start:" >&2; tail -20 "$LOGS/server-$STAMP.log" >&2; SERVER=""; exit 1; }
  curl -sf -o /dev/null "http://127.0.0.1:5555/ping" && break
  sleep 0.5
done
curl -sf -o /dev/null "http://127.0.0.1:5555/ping" || { echo "Server did not answer within 30s; see $LOGS/server-$STAMP.log" >&2; exit 1; }
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
