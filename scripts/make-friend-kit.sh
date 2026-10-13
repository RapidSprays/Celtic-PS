#!/usr/bin/env bash
# Build what friends need to join your server over Tailscale (docs/friends.md): one file per platform, with your server
# address built in. Double-clicking it sets everything up the first time and launches the game every time after.
#
#   scripts/make-friend-kit.sh [--host <your Tailscale IP>]
#
# Output in ~/CelticHeroesAnalysis/friend-kit/ (outside git):
#   CelticHeroesPrivate-Mac.zip   "Celtic Heroes Private.app" (Apple Silicon and Intel)
#   CelticHeroesPrivate.exe       Windows, once the Windows build is verified (KnownBuilds.cs)
#   HOW-TO-JOIN.txt               the instructions to send with it
# Nothing of the game is inside: each friend's copy is made from their own Steam install.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HOME/CelticHeroesAnalysis/friend-kit"
WORK="$OUT/.build"
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

rm -rf "$OUT" && mkdir -p "$WORK"
# The client mod is embedded in the program; build it first (needs the pinned client's Unity DLLs).
dotnet build -c Release "$REPO/server/tools/CH.ClientMods" >/dev/null
publish() { # rid outdir
  dotnet publish "$REPO/server/tools/CH.Patcher" -c Release -r "$1" --self-contained -p:PublishSingleFile=true \
    -p:AssemblyName=CelticHeroesPrivate -p:DebugType=none -p:FriendHost="$HOST" -o "$2" >/dev/null
}

# ---- Mac: one app with both architectures; a tiny launcher picks the right one ----------------------------------
APP="$WORK/Celtic Heroes Private.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
for arch in arm64 x64; do
  publish "osx-$arch" "$WORK/osx-$arch"
  mkdir -p "$APP/Contents/Resources/$arch"
  cp "$WORK/osx-$arch/CelticHeroesPrivate" "$APP/Contents/Resources/$arch/"
done
cp "$REPO/server/tools/CH.Patcher/THIRD-PARTY-NOTICES.txt" "$APP/Contents/Resources/"
cat > "$APP/Contents/MacOS/launcher" <<'SH'
#!/bin/bash
# Celtic Heroes Private: runs the right build for this Mac. Output goes to ~/Library/Logs/CelticHeroesPrivate.log.
here="$(cd "$(dirname "$0")/.." && pwd)"
xattr -dr com.apple.quarantine "$here/.." 2>/dev/null
case "$(uname -m)" in arm64) arch=arm64 ;; *) arch=x64 ;; esac
exec "$here/Resources/$arch/CelticHeroesPrivate" play >> "$HOME/Library/Logs/CelticHeroesPrivate.log" 2>&1
SH
chmod +x "$APP/Contents/MacOS/launcher"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Celtic Heroes Private</string>
  <key>CFBundleDisplayName</key><string>Celtic Heroes Private</string>
  <key>CFBundleIdentifier</key><string>local.celticheroes.private.launcher</string>
  <key>CFBundleExecutable</key><string>launcher</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo 1)</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST
codesign --force --deep --sign - "$APP" 2>/dev/null
(cd "$WORK" && ditto -c -k --keepParent "Celtic Heroes Private.app" "$OUT/CelticHeroesPrivate-Mac.zip")

# ---- Windows: one exe (once the Windows build is verified) --------------------------------------------------------
WINDOWS=0
if grep -q "ClientPlatform.Windows," "$REPO/server/tools/CH.Patcher/KnownBuilds.cs"; then
  # The obfuscated Windows build is patched through its name map; build it if this machine doesn't have it yet.
  ls "$HOME"/CelticHeroesAnalysis/extracted/*/names-*.json >/dev/null 2>&1 || "$REPO/scripts/map-windows.sh"
  publish win-x64 "$WORK/win-x64"
  cp "$WORK/win-x64/CelticHeroesPrivate.exe" "$OUT/"
  WINDOWS=1
fi
rm -rf "$WORK"

cat > "$OUT/HOW-TO-JOIN.txt" <<TXT
Celtic Heroes, on my private server

1. Install Steam and Celtic Heroes (it's free). Start the game once, then quit it.
2. Install Tailscale (tailscale.com/download), make an account and accept the invite I sent you.
3. Run the file I sent:
$( [[ $WINDOWS == 1 ]] && echo '   Windows: CelticHeroesPrivate.exe. If Windows warns, click "More info", then "Run anyway".' || echo '   Windows: not ready yet, coming soon.')
   Mac: unzip CelticHeroesPrivate-Mac.zip and open "Celtic Heroes Private". If macOS says it can't check it,
        open System Settings > Privacy & Security, click "Open Anyway", then open it again.

The first time it sets itself up (a minute or two), puts "Celtic Heroes Private" on your desktop and starts
the game. After that, just use that shortcut. Keep Tailscale on while you play. Your normal Celtic Heroes
in Steam isn't changed.

The username and password you type the first time become your account on my server.
Chat commands: .help  .who  .tree   Whisper: w Name, message   Party: g message
TXT

echo "Friend kit for $HOST in $OUT:"
ls -lh "$OUT" | awk 'NR > 1 { print "  " $5 "  " $9, $10, $11 }'
