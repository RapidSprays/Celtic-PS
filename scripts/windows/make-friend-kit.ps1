# Build what friends need to join your server over Tailscale (docs/friends.md), with your server address built in.
# Windows version of scripts/make-friend-kit.sh.
#
#   scripts\windows\make-friend-kit.ps1 [-ServerHost <your Tailscale IP>]
#
# Output in ~/CelticHeroesAnalysis/friend-kit/ (outside git):
#   CelticHeroesPrivate.exe   Windows: double-click sets up the private copy the first time and launches the game
#   HOW-TO-JOIN.txt           the instructions to send with it
# The Mac app (CelticHeroesPrivate-Mac.zip) is only built by scripts/make-friend-kit.sh on a Mac: it must be code-signed
# with macOS's codesign to run on Apple Silicon.
param([string]$ServerHost)
. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $ServerHost) { $ServerHost = Get-TailscaleIp }
if (-not $ServerHost) { throw 'No Tailscale address found. Start Tailscale and log in, or pass -ServerHost <address>.' }

$out = Join-Path $Analysis 'friend-kit'
$work = Join-Path $out '.build'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $work | Out-Null

# The client mod is embedded in the program; build it first (needs the pinned client's Unity DLLs).
Invoke-Checked 'dotnet' @('build', '-c', 'Release', (Join-Path $Repo 'server\tools\CH.ClientMods'), '-v', 'q', '-nologo') | Out-Null
# The obfuscated Windows build is patched through its name map (docs/windows-names.md); build it if it's missing.
if (-not (Get-ChildItem (Join-Path $Analysis 'extracted\*\names-*.json') -ErrorAction SilentlyContinue)) {
    & (Join-Path $PSScriptRoot 'map-windows.ps1')
}
Invoke-Checked 'dotnet' @('publish', (Join-Path $Repo 'server\tools\CH.Patcher'), '-c', 'Release', '-r', 'win-x64', '--self-contained',
    '-p:PublishSingleFile=true', '-p:AssemblyName=CelticHeroesPrivate', '-p:DebugType=none', "-p:FriendHost=$ServerHost",
    '-o', (Join-Path $work 'win-x64'), '-v', 'q', '-nologo') | Out-Null
Copy-Item (Join-Path $work 'win-x64\CelticHeroesPrivate.exe') $out
Remove-Item -Recurse -Force $work

@"
Celtic Heroes, on my private server

1. Install Steam and Celtic Heroes (it's free). Start the game once, then quit it.
2. Install Tailscale (tailscale.com/download), make an account and accept the invite I sent you.
3. Run CelticHeroesPrivate.exe. If Windows warns, click "More info", then "Run anyway".
   (On a Mac, ask me for the Mac version.)

The first time it sets itself up (a minute or two), puts "Celtic Heroes Private" on your desktop and starts
the game. After that, just use that shortcut. Keep Tailscale on while you play. Your normal Celtic Heroes
in Steam isn't changed.

The username and password you type the first time become your account on my server.
Chat commands: .help  .who  .tree   Whisper: w Name, message   Party: g message
"@ | Set-Content -Path (Join-Path $out 'HOW-TO-JOIN.txt') -Encoding utf8

Write-Host "Friend kit for $ServerHost in ${out}:"
Get-ChildItem $out | ForEach-Object { Write-Host ("  {0,8:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name) }
