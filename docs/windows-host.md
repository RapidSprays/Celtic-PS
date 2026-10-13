# Windows host

Development, the always-on server, the database and the friend kit can all live on a Windows PC. The server, the
patcher and the Python data scripts are cross-platform; the scripts that drive them have Windows versions in
`scripts/windows/` (PowerShell 5.1, built into Windows). The macOS scripts in `scripts/` stay as they were.

| macOS | Windows | What changed |
|---|---|---|
| `scripts/prepare-server.sh` | `scripts\windows\prepare-server.ps1` | same steps |
| `scripts/server-service.sh` + `service-run.sh` | `scripts\windows\server-service.ps1` + `service-run.ps1` | Task Scheduler task at logon instead of launchd; backups via `CH.Server backup` instead of `sqlite3`; adds `stop` |
| `scripts/run-private.sh`, `play.sh` | `scripts\windows\play.ps1` | your game is a patched copy of the **Windows** client in `~/CelticHeroesAnalysis/client/24928510-windows`, made from your Steam install exactly like a friend's copy (`CH.Patcher play --host 127.0.0.1`) |
| `scripts/make-desktop-shortcut.sh` | `scripts\windows\make-desktop-shortcut.ps1` | "Celtic Heroes Private (host)" on the Desktop |
| `scripts/make-friend-kit.sh`, `release-kit.sh` | `scripts\windows\make-friend-kit.ps1`, `release-kit.ps1` | Windows `.exe` only (below) |
| `scripts/map-windows.sh` | `scripts\windows\map-windows.ps1` | the Windows build is this PC's own Steam install |

Still Mac-only: `inventory.sh`, `snapshot.sh` and `pin-client.sh` (they work on the Mac client, which is only needed
again when the game updates: see "When the game updates"), and the **Mac friend app**, which must be code-signed with
macOS's `codesign` to run on Apple Silicon. Build it on a Mac with `scripts/make-friend-kit.sh` if a friend plays on a Mac.

## Moving from the Mac

1. **On the Mac**, stop the server so the database stops changing, then pack `~/CelticHeroesAnalysis` without the
   Mac-only parts (`-h` turns the client's symlinks into plain files, which Windows extracts without trouble):
   ```bash
   scripts/server-service.sh uninstall
   cd ~ && tar -czhf ~/Desktop/CelticHeroesAnalysis.tgz --exclude CelticHeroesAnalysis/service \
     --exclude CelticHeroesAnalysis/venv --exclude CelticHeroesAnalysis/friend-kit CelticHeroesAnalysis
   ```
   Copy it to the PC: with both machines on Tailscale, `tailscale file cp ~/Desktop/CelticHeroesAnalysis.tgz <pc-name>:`
   (it lands in the PC's Downloads), or a USB drive.
2. **On the PC** (PowerShell), unpack it into your user folder and recreate the Python environment:
   ```powershell
   tar -xzf "$env:USERPROFILE\Downloads\CelticHeroesAnalysis.tgz" -C $env:USERPROFILE
   py -3 -m venv "$env:USERPROFILE\CelticHeroesAnalysis\venv"
   & "$env:USERPROFILE\CelticHeroesAnalysis\venv\Scripts\python.exe" -m pip install UnityPy
   ```
3. Let PowerShell run the repo's scripts (once per user):
   ```powershell
   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
   ```
4. Let friends reach the server through Windows Firewall. In an **administrator** PowerShell (only Tailscale
   addresses; the server refuses everything else anyway):
   ```powershell
   New-NetFirewallRule -DisplayName "Celtic Heroes Private (HTTP)" -Direction Inbound -Protocol TCP -LocalPort 5555 -RemoteAddress 100.64.0.0/10 -Action Allow
   New-NetFirewallRule -DisplayName "Celtic Heroes Private (game)" -Direction Inbound -Protocol UDP -LocalPort 5556 -RemoteAddress 100.64.0.0/10 -Action Allow
   ```
5. Install the background server and check it:
   ```powershell
   scripts\windows\server-service.ps1 install
   scripts\windows\server-service.ps1 status
   ```
6. In the Tailscale admin console, share **this PC** with each friend (Machines → this PC → Share), like the Mac was.
7. Publish a kit with the PC's address and send friends the new `.exe`. Running it redoes their copy for the new
   address:
   ```powershell
   scripts\windows\release-kit.ps1 "The server moved"
   ```
8. Play yourself with `scripts\windows\play.ps1`, or make the Desktop shortcut once with
   `scripts\windows\make-desktop-shortcut.ps1`. Delete the friend-kit copy (`%LOCALAPPDATA%\CelticHeroesPrivate`) and
   its "Celtic Heroes Private" shortcut if you had installed one on this PC: they point at the old Mac.
9. Git hygiene for this clone: `copy scripts\pre-commit.sh .git\hooks\pre-commit` (Git for Windows runs it with its bash).

## Day to day
- After changing server code: `scripts\windows\server-service.ps1 update` (publishes beside the running server, then
  swaps it in with a few seconds' downtime). `status` says when this is needed.
- Logs: `~/CelticHeroesAnalysis/logs/service.log` (the loop) and `server-<time>.log` per server start;
  `server-service.ps1 logs` follows the current one. The game's own log on Windows is
  `%USERPROFILE%\AppData\LocalLow\One Thumb Mobile Ltd\Celtic Heroes\Player.log`.
- Stopping the service stops the server process outright (Windows has no SIGTERM); SQLite keeps the database consistent.
- Sleep: the service keeps the PC from idle-sleeping while it runs; a manual sleep or shutdown still takes it offline.

## When the game updates
The Mac client is the readable reference the Windows name map is built from (docs/windows-names.md). Without a Mac,
download it with Steam's console (`steam://open/console`): `download_depot 2829820 <Mac depot id>` (SteamDB lists the app's depots; Windows is 2829821), then re-pin and
snapshot it into `~/CelticHeroesAnalysis` by hand (the `.sh` scripts show the layout), run
`scripts\windows\map-windows.ps1`, add the new hashes to `KnownBuilds.cs` and run the tests.
