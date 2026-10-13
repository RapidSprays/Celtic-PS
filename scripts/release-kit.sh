#!/usr/bin/env bash
# Rebuild the friend kit and publish it as a new release of the private GitHub repo, so the files can be downloaded
# on any of your machines (signed in to GitHub) instead of being sent around.
#
#   scripts/release-kit.sh [note]     e.g. scripts/release-kit.sh "Fix the passive tree button on Windows"
#
# Tags are kit-<date>-<commit>. Releases of a private repo are only visible to you and people you add to the repo.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
KIT="$HOME/CelticHeroesAnalysis/friend-kit"
NOTE="${1:-}"
[[ -z "$(git -C "$REPO" status --porcelain --untracked-files=no)" ]] || { echo "Commit your changes first, so the release matches a commit." >&2; exit 1; }
git -C "$REPO" push -q
"$REPO/scripts/make-friend-kit.sh"
COMMIT="$(git -C "$REPO" rev-parse --short HEAD)"
TAG="kit-$(date +%Y%m%d-%H%M)-$COMMIT"
assets=("$KIT/CelticHeroesPrivate-Mac.zip" "$KIT/HOW-TO-JOIN.txt")
[[ -f "$KIT/CelticHeroesPrivate.exe" ]] && assets=("$KIT/CelticHeroesPrivate.exe" "${assets[@]}")
gh release create "$TAG" --repo "$(git -C "$REPO" remote get-url origin)" --target "$(git -C "$REPO" rev-parse HEAD)" \
  --title "Friend kit $TAG" --notes "${NOTE:+$NOTE

}Windows: run CelticHeroesPrivate.exe (More info → Run anyway). Mac: unzip and open Celtic Heroes Private. Running a newer file updates your copy. Built from $COMMIT." \
  "${assets[@]}"
