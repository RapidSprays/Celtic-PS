#!/usr/bin/env bash
# (Re)build the Windows name map (docs/windows-names.md): pairs the obfuscated Windows Assembly-CSharp with the Mac
# build's readable one by code shape. Needs the Windows depot, downloaded with Steam's console:
#   download_depot 2829820 2829821
# Writes ~/CelticHeroesAnalysis/extracted/<build>/names-<windows dll sha256>.json (outside git), which CH.Patcher embeds.
#
#   scripts/map-windows.sh [--depot DIR]
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
BUILD=24928510
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DEPOT=""
[[ "${1:-}" == "--depot" ]] && DEPOT="$2"
if [[ -z "$DEPOT" ]]; then
  for d in "$HOME/Library/Application Support/Steam/Steam.AppBundle/Steam/Contents/MacOS/steamapps/content/app_2829820/depot_2829821" \
           "$HOME/Library/Application Support/Steam/steamapps/content/app_2829820/depot_2829821"; do
    [[ -f "$d/CelticHeroes_Data/Managed/Assembly-CSharp.dll" ]] && { DEPOT="$d"; break; }
  done
fi
[[ -n "$DEPOT" ]] || { echo "Windows depot not found. In Steam's console (steam://open/console): download_depot 2829820 2829821" >&2; exit 1; }
WIN="$DEPOT/CelticHeroes_Data/Managed/Assembly-CSharp.dll"
MAC="$HOME/CelticHeroesAnalysis/snapshots/$BUILD/Contents/Resources/Data/Managed/Assembly-CSharp.dll"
OUT="$HOME/CelticHeroesAnalysis/extracted/$BUILD/names-$(shasum -a 256 "$WIN" | cut -c1-64).json"
mkdir -p "$(dirname "$OUT")"
dotnet run --project "$REPO/server/tools/CH.Patcher" -- map --mac "$MAC" --windows "$WIN" --out "$OUT"
echo "Wrote $OUT"
dotnet run --project "$REPO/server/tools/CH.Patcher" -- verify --game-dir "$DEPOT" --names "$OUT" | grep -E "site|URLs|names"
