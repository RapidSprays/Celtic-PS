#!/bin/bash
# The background service's main loop (started by launchd; see scripts/server-service.sh). Not meant to be run by hand.
#
# Keeps the private server running from the published copy in ~/CelticHeroesAnalysis/service/bin:
#   - Tailscale mode when this Mac has a Tailscale address, else localhost only; switches to Tailscale mode once it
#     comes up (restarting the server, which disconnects anyone on it);
#   - restarts the server 5 s after it stops for any reason;
#   - backs up the database once a day while it runs (sqlite online backup), keeping the newest 14.
# Environment overrides (used by tests): CH_SERVICE_DIR, CH_DATA, CH_SERVER_ARGS.
set -uo pipefail

SERVICE="${CH_SERVICE_DIR:-$HOME/CelticHeroesAnalysis/service}"
DATA="${CH_DATA:-$HOME/CelticHeroesAnalysis/server-data}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DOTNET="$DOTNET_ROOT/dotnet"
BACKUPS="$DATA/backups"
read -r -a EXTRA <<< "${CH_SERVER_ARGS:-}"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') [service] $*"; }
ts_ip() { /sbin/ifconfig | awk '/inet 100\./ { split($2, o, "."); if (o[2] >= 64 && o[2] <= 127) { print $2; exit } }'; }

backup_if_due() {
  local db="$DATA/private-server.db"
  [[ -f "$db" ]] || return 0
  mkdir -p "$BACKUPS"
  local newest; newest="$(ls -t "$BACKUPS"/private-server-*.db 2>/dev/null | head -1)"
  if [[ -z "$newest" ]] || [[ -n "$(find "$newest" -mmin +1440 2>/dev/null)" ]]; then
    local out="$BACKUPS/private-server-$(date +%Y%m%d-%H%M%S).db"
    if /usr/bin/sqlite3 "$db" ".backup '$out'"; then log "database backed up to $out"; else log "database backup FAILED"; fi
    ls -t "$BACKUPS"/private-server-*.db 2>/dev/null | tail -n +15 | while read -r old; do rm -f "$old"; done
  fi
}

SERVER=""
stop() {
  log "stopping"
  [[ -n "$SERVER" ]] && kill -TERM "$SERVER" 2>/dev/null && wait "$SERVER" 2>/dev/null
  exit 0
}
trap stop TERM INT

mkdir -p "$DATA"
# No idle sleep while the service runs (released automatically when this script ends).
[[ -z "${CH_NO_CAFFEINATE:-}" ]] && /usr/bin/caffeinate -i -w $$ &
while true; do
  args=(--data "$DATA")
  mode="localhost only"
  if [[ -n "$(ts_ip)" ]]; then args+=(--tailscale); mode="Tailscale $(ts_ip)"; fi
  backup_if_due
  "$DOTNET" "$SERVICE/bin/CH.Server.dll" "${args[@]}" ${EXTRA[@]+"${EXTRA[@]}"} &
  SERVER=$!
  log "server started ($mode, pid $SERVER, revision $(cat "$SERVICE/bin/REVISION" 2>/dev/null || echo '?'))"
  while kill -0 "$SERVER" 2>/dev/null; do
    sleep 5 & wait $!
    if [[ "$mode" == "localhost only" && -n "$(ts_ip)" ]]; then
      log "Tailscale is up: restarting the server so friends can join"
      kill -TERM "$SERVER" 2>/dev/null
      break
    fi
    backup_if_due
  done
  wait "$SERVER" 2>/dev/null
  log "server exited (status $?); restarting in 5 s"
  SERVER=""
  sleep 5
done
