#!/bin/bash
# One-click start for the private game (the "Celtic Heroes Private" desktop shortcut runs this; see
# scripts/make-desktop-shortcut.sh). Starts Steam if needed, then scripts/run-private.sh, which uses the background
# server if it's installed. Problems are shown in a dialog; the full output goes to ~/CelticHeroesAnalysis/logs/launcher.log.
REPO="$(cd "$(dirname "$0")/.." && pwd)"
LOG="$HOME/CelticHeroesAnalysis/logs/launcher.log"
mkdir -p "$(dirname "$LOG")"
exec >> "$LOG" 2>&1
echo "=== $(date '+%Y-%m-%d %H:%M:%S') launching"
export PATH="/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

fail() {
  local text="${1//\"/\'}"
  /usr/bin/osascript -e "display dialog \"$text\" with title \"Celtic Heroes Private\" buttons {\"OK\"} default button 1 with icon caution" >/dev/null
  exit 1
}

if pgrep -f "client/24928510/CelticHeroes.app/Contents/MacOS/Celtic Heroes" >/dev/null; then
  fail "Celtic Heroes Private is already running."
fi
if ! pgrep -x steam_osx >/dev/null; then
  echo "starting Steam"
  open -g -a Steam || fail "Steam isn't installed."
  for _ in $(seq 1 90); do pgrep -x steam_osx >/dev/null && break; sleep 1; done
  pgrep -x steam_osx >/dev/null || fail "Steam didn't start. Start Steam, then try again."
fi

"$REPO/scripts/run-private.sh"
status=$?
if [[ $status -ne 0 ]]; then
  fail "The game couldn't start:

$(grep -v '^===' "$LOG" | tail -4)

Full log: ~/CelticHeroesAnalysis/logs/launcher.log"
fi
