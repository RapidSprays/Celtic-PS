#!/usr/bin/env bash
# Get the private server ready to run: client balance data, derived world data and a Release build.
#
#   scripts/prepare-server.sh                 build into server/src/CH.Server/bin/Release (run-private.sh)
#   scripts/prepare-server.sh --publish DIR   also publish a self-contained copy of the server to DIR (the background service)
#
# Derived data is (re)generated when missing or when its script (or the shared chdata.py) changed.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
BUILD=24928510
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DOTNET="$DOTNET_ROOT/dotnet"
PUBLISH=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --publish) PUBLISH="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

EXTRACTED="$HOME/CelticHeroesAnalysis/extracted/$BUILD"
[[ -f "$EXTRACTED/balance-client.json" ]] || python3 "$REPO/scripts/extract-balance.py" "$BUILD"
regen() { local out="$EXTRACTED/$1" script="$REPO/scripts/$2"
  if [[ ! -f "$out" || "$script" -nt "$out" || "$REPO/scripts/chdata.py" -nt "$out" ]]; then python3 "$script" "$BUILD" >/dev/null; fi; }
regen zone-points.json extract-zone-points.py
regen zone-population.json gen-zone-population.py
regen world.json gen-world.py

if [[ -n "$PUBLISH" ]]; then
  "$DOTNET" publish -c Release "$REPO/server/src/CH.Server" -o "$PUBLISH" >/dev/null
  git -C "$REPO" rev-parse --short HEAD > "$PUBLISH/REVISION" 2>/dev/null || true
else
  "$DOTNET" build -c Release "$REPO/server/src/CH.Server" >/dev/null
fi
