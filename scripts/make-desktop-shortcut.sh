#!/usr/bin/env bash
# Put a "Celtic Heroes Private" app on the Desktop that starts the private game (scripts/play.sh) with one click.
# Run again after moving the repo. It only refers to this repo; nothing of the game is copied into it except the icon.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="$HOME/Desktop/Celtic Heroes Private.app"
ICON="$HOME/CelticHeroesAnalysis/client/24928510/CelticHeroes.app/Contents/Resources/PlayerIcon.icns"

rm -rf "$APP"
osacompile -o "$APP" -e "do shell script \"/bin/bash '$REPO/scripts/play.sh' > /dev/null 2>&1 &\""
if [[ -f "$ICON" ]]; then
  cp "$ICON" "$APP/Contents/Resources/applet.icns"
  # Newer macOS prefers the applet's asset-catalog icon over applet.icns.
  rm -f "$APP/Contents/Resources/Assets.car"
  /usr/libexec/PlistBuddy -c "Delete :CFBundleIconName" "$APP/Contents/Info.plist" 2>/dev/null || true
fi
/usr/libexec/PlistBuddy -c "Set :CFBundleName Celtic Heroes Private" "$APP/Contents/Info.plist" 2>/dev/null || true
codesign --force -s - "$APP" 2>/dev/null # ad hoc again after the edits
touch "$APP" # refresh the Finder icon
echo "Created $APP"
