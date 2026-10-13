#!/usr/bin/env bash
# Run the private server in the background on this Mac, so friends can play without you starting it (docs/friends.md).
#
#   scripts/server-service.sh install     build, publish and start it; it then starts by itself whenever you log in
#   scripts/server-service.sh update      after pulling or changing server code: rebuild, republish, restart
#   scripts/server-service.sh restart     restart it (disconnects players)
#   scripts/server-service.sh status      running? which mode, which revision, last backup
#   scripts/server-service.sh logs        follow its log (Ctrl+C to stop following)
#   scripts/server-service.sh uninstall   stop it and remove it from login (keeps the database and backups)
#
# While it runs it keeps the Mac from idle-sleeping (caffeinate), restarts the server if it stops, switches to Tailscale
# mode once Tailscale is up, and backs up the database daily (scripts/service-run.sh). A closed laptop lid still sleeps.
# scripts/run-private.sh notices the service and just launches your game against it.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="local.celticheroes.private-server"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
SERVICE="$HOME/CelticHeroesAnalysis/service"
DATA="$HOME/CelticHeroesAnalysis/server-data"
LOG="$HOME/CelticHeroesAnalysis/logs/service.log"
DOMAIN="gui/$(id -u)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

loaded() { launchctl print "$DOMAIN/$LABEL" >/dev/null 2>&1; }
answering() { curl -sf -o /dev/null --max-time 2 "http://127.0.0.1:5555/ping"; }

wait_until_up() {
  for _ in $(seq 1 60); do answering && return 0; sleep 1; done
  echo "The service didn't answer within 60 s. Last log lines:" >&2
  tail -20 "$LOG" >&2
  return 1
}

publish() {
  echo "Preparing data and publishing the server to $SERVICE/bin ..."
  mkdir -p "$SERVICE"
  "$REPO/scripts/prepare-server.sh" --publish "$SERVICE/bin"
  cp "$REPO/scripts/service-run.sh" "$SERVICE/service-run.sh"
}

write_plist() {
  mkdir -p "$(dirname "$PLIST")" "$(dirname "$LOG")"
  cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>/bin/bash</string><string>$SERVICE/service-run.sh</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>DOTNET_ROOT</key><string>$DOTNET_ROOT</string>
    <key>HOME</key><string>$HOME</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>10</integer>
  <key>StandardOutPath</key><string>$LOG</string>
  <key>StandardErrorPath</key><string>$LOG</string>
</dict>
</plist>
PLIST
}

case "${1:-status}" in
  install)
    if loaded; then echo "Already installed; use 'update' or 'restart'."; exit 0; fi
    if answering; then
      echo "A server is already running on port 5555 (probably scripts/run-private.sh). Quit the game and stop it first." >&2
      exit 1
    fi
    publish
    write_plist
    launchctl bootstrap "$DOMAIN" "$PLIST"
    wait_until_up
    echo "Installed and running. It starts by itself whenever you log in."
    "$0" status
    ;;
  update)
    loaded || { echo "Not installed; run 'install'." >&2; exit 1; }
    publish
    launchctl kickstart -k "$DOMAIN/$LABEL"
    wait_until_up
    echo "Updated to revision $(cat "$SERVICE/bin/REVISION" 2>/dev/null || echo '?')."
    ;;
  restart)
    loaded || { echo "Not installed." >&2; exit 1; }
    launchctl kickstart -k "$DOMAIN/$LABEL"
    wait_until_up
    echo "Restarted."
    ;;
  status)
    if ! loaded; then echo "Not installed."; exit 0; fi
    if answering; then echo "Running."; else echo "Installed, but not answering right now (starting, or failing: see 'logs')."; fi
    grep "\[service\] server started" "$LOG" 2>/dev/null | tail -1 | sed 's/^/  last start: /'
    echo "  data: $DATA"
    echo "  last backup: $(ls -t "$DATA"/backups/private-server-*.db 2>/dev/null | head -1 || true)"
    [[ "$(cat "$SERVICE/bin/REVISION" 2>/dev/null)" != "$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null)" ]] \
      && echo "  The repo has newer code than the service: run '$0 update'." || true
    ;;
  logs)
    tail -n 40 -f "$LOG"
    ;;
  uninstall)
    if loaded; then launchctl bootout "$DOMAIN/$LABEL" || true; fi
    rm -f "$PLIST"
    echo "Stopped and removed from login. The database and backups are kept in $DATA."
    ;;
  *)
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
