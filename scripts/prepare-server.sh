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
# The real ground per zone (needs UnityPy in ~/CelticHeroesAnalysis/venv; skipped without it). NPCs are placed before
# monster camps, which keep clear of them (docs/world-placement.md).
VENV_PY="$HOME/CelticHeroesAnalysis/venv/bin/python"
if [[ -x "$VENV_PY" ]] && [[ ! -f "$EXTRACTED/zone-worlds.json" || "$REPO/scripts/extract-zone-worlds.py" -nt "$EXTRACTED/zone-worlds.json" ]]; then
  "$VENV_PY" "$REPO/scripts/extract-zone-worlds.py" "$BUILD" >/dev/null
fi
regen zone-points.json extract-zone-points.py
world_inputs_newer() { [[ "$EXTRACTED/zone-worlds.json" -nt "$EXTRACTED/$1" || "$REPO/scripts/worldplace.py" -nt "$EXTRACTED/$1" ]]; }
if world_inputs_newer world.json; then python3 "$REPO/scripts/gen-world.py" "$BUILD" >/dev/null; fi
regen world.json gen-world.py
if world_inputs_newer zone-population.json || [[ "$EXTRACTED/world.json" -nt "$EXTRACTED/zone-population.json" ]]; then
  python3 "$REPO/scripts/gen-zone-population.py" "$BUILD" >/dev/null
fi
regen zone-population.json gen-zone-population.py
# Gathering and cooking (docs/gathering-notes.md): fishing spots and ground items follow the world and its NPCs.
regen cooking.json gen-cooking.py
for pair in "fishing.json gen-fishing.py" "item-spawns.json gen-item-spawns.py"; do
  set -- $pair
  if world_inputs_newer "$1" || [[ "$EXTRACTED/world.json" -nt "$EXTRACTED/$1" || "$EXTRACTED/cooking.json" -nt "$EXTRACTED/$1" ]]; then
    python3 "$REPO/scripts/$2" "$BUILD" >/dev/null
  fi
  regen "$1" "$2"
done

if [[ -n "$PUBLISH" ]]; then
  "$DOTNET" publish -c Release "$REPO/server/src/CH.Server" -o "$PUBLISH" >/dev/null
  git -C "$REPO" rev-parse --short HEAD > "$PUBLISH/REVISION" 2>/dev/null || true
else
  "$DOTNET" build -c Release "$REPO/server/src/CH.Server" >/dev/null
fi
