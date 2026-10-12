#!/usr/bin/env bash
# Build the kit friends use to join your server over Tailscale (docs/friends.md).
#
#   scripts/make-friend-kit.sh [--host <your Tailscale IP>]
#
# The kit holds only our setup tool (self-contained, no .NET needed), your server address and instructions. It holds
# nothing of the game: each friend's tool copies and patches their own Steam install.
# Output: ~/CelticHeroesAnalysis/friend-kit/CelticHeroesPrivateKit.zip (outside git).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HOME/CelticHeroesAnalysis/friend-kit"
KIT="$OUT/CelticHeroesPrivateKit"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

HOST=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --host) HOST="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
if [[ -z "$HOST" ]]; then
  # This Mac's Tailscale IPv4 (100.64.0.0/10).
  HOST="$(ifconfig | awk '/inet 100\./ { split($2, o, "."); if (o[2] >= 64 && o[2] <= 127) { print $2; exit } }')"
fi
[[ -n "$HOST" ]] || { echo "No Tailscale address found. Start Tailscale, or pass --host <address>." >&2; exit 1; }

rm -rf "$KIT"
mkdir -p "$KIT"
for rid in osx-arm64 osx-x64; do
  dir="$KIT/mac-${rid#osx-}"
  dotnet publish "$REPO/server/tools/CH.Patcher" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:AssemblyName=CelticHeroesPrivateSetup -p:DebugType=none -o "$dir" >/dev/null
  echo "$HOST" > "$dir/host.txt"
done
# Windows joins the kit once the Windows build is verified (KnownBuilds.cs, docs/friends.md).
if grep -q "ClientPlatform.Windows," "$REPO/server/tools/CH.Patcher/KnownBuilds.cs"; then
  dotnet publish "$REPO/server/tools/CH.Patcher" -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:AssemblyName=CelticHeroesPrivateSetup -p:DebugType=none -o "$KIT/windows" >/dev/null
  echo "$HOST" > "$KIT/windows/host.txt"
  cat > "$KIT/windows/Setup.bat" <<'BAT'
@echo off
cd /d "%~dp0"
CelticHeroesPrivateSetup.exe setup
pause
BAT
fi

cat > "$KIT/Setup (Mac).command" <<'SH'
#!/bin/bash
# Makes your private copy of Celtic Heroes from your own Steam install. Run it again any time to redo the copy.
cd "$(dirname "$0")" || exit 1
xattr -dr com.apple.quarantine . 2>/dev/null
case "$(uname -m)" in arm64) dir=mac-arm64 ;; *) dir=mac-x64 ;; esac
"./$dir/CelticHeroesPrivateSetup" setup
echo
read -r -p "Press Enter to close."
SH
chmod +x "$KIT/Setup (Mac).command"

cat > "$KIT/README.txt" <<TXT
Celtic Heroes private server: joining $HOST

You need:
  1. Steam, with Celtic Heroes installed (it's free). Start it once, then quit it.
  2. Tailscale (tailscale.com/download), logged in and connected to the host's network
     (accept the share invite the host sent you).

Mac:
  Right-click "Setup (Mac).command" and choose Open (macOS asks because the kit isn't from the App Store).
  It copies your Steam install of Celtic Heroes to ~/CelticHeroesPrivate and patches the copy to talk only to
  the host's server. Your Steam install is not changed.
  To play: start Steam, then open ~/CelticHeroesPrivate/"Play Celtic Heroes Private.command".

Windows:
  $( [[ -d "$KIT/windows" ]] && echo 'Open the windows folder and run Setup.bat ("More info" → "Run anyway" if Windows warns).
  To play: start Steam, then run %LOCALAPPDATA%\CelticHeroesPrivate\"Play Celtic Heroes Private.bat".' || echo 'Not supported in this kit yet.')

First login: the username and password you type the first time become your account on this server.
Commands in chat: .help, .who, .where, .zones   Whisper: w Name, message   Party chat: g message

If Steam updates Celtic Heroes before you run the setup, the setup may say your version is different: ask the host
for a new kit. Once your private copy exists, Steam updates don't affect it.
TXT

cat > "$KIT/THIRD-PARTY-NOTICES.txt" <<'TXT'
CelticHeroesPrivateSetup includes:

Mono.Cecil (https://github.com/jbevain/cecil), MIT License
  Copyright (c) 2008 - 2015 Jb Evain
  Copyright (c) 2008 - 2011 Novell, Inc.

.NET runtime (https://github.com/dotnet/runtime), MIT License
  Copyright (c) .NET Foundation and Contributors

MIT License: Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including without
limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
TXT

(cd "$OUT" && rm -f CelticHeroesPrivateKit.zip && zip -qry CelticHeroesPrivateKit.zip CelticHeroesPrivateKit)
echo "Kit for $HOST: $OUT/CelticHeroesPrivateKit.zip ($(du -h "$OUT/CelticHeroesPrivateKit.zip" | cut -f1))"
