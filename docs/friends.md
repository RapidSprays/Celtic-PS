# Playing with friends (Tailscale)

Friends join your private server over [Tailscale](https://tailscale.com), an encrypted private network. Nothing is opened
on your router, and the server refuses every address that isn't this Mac or on your tailnet (`--tailscale` mode,
`NetworkAccess.cs`). The game's own traffic is unencrypted, so don't expose the server any other way.

**No game files are shared.** Each friend installs Celtic Heroes from Steam (it's free), and the kit's setup tool copies
and patches *their own* install. The kit contains only our setup tool, your address and instructions.

## You (the host)

1. Install Tailscale on this Mac and log in.
2. Share this Mac with each friend: Tailscale admin console → Machines → this Mac → Share, and send them the link.
   Sharing gives them this one machine only, not your whole network.
3. Build the friend files once (again if your Tailscale IP changes, or to send friends an update):
   ```bash
   scripts/make-friend-kit.sh
   ```
   It writes to `~/CelticHeroesAnalysis/friend-kit/`:
   - `CelticHeroesPrivate-Mac.zip`
   - `CelticHeroesPrivate.exe` (once Windows is verified)
   - `HOW-TO-JOIN.txt` (the instructions to paste to friends)

   Each is one self-contained file with your server address built in. Double-clicking it does everything: the first time it makes and patches the player's private copy from their own Steam install, installs itself and puts a **Celtic Heroes Private** shortcut on their desktop. Every time, it starts Steam if needed, waits for Steam to sign in, checks your server is reachable (with plain messages when Tailscale is off or the server is down) and launches the game. Running a newer file redoes the copy, so updates need no extra steps (`CH.Patcher/FriendApp.cs`).
4. Run the server in Tailscale mode, with your own client as usual:
   ```bash
   scripts/run-private.sh --tailscale
   ```
   Use `--server --tailscale` to run the server without starting your own client.
   If macOS asks whether `dotnet` may accept incoming connections, allow it.
5. In game you are a GM automatically, because you play on the server's machine. Useful commands:
   - `.who` lists who is online.
   - `.gm <account>` / `.gm <account> off` grants or removes GM rights.
   - `.resetpassword <account>` helps a friend who forgot their password: their next login sets a new one.

## Releases
The repo is private on GitHub (`ChickenExe/Celtic-Chickens`). The friend files are published as releases, so you can download them on any of your machines. `scripts/release-kit.sh "what changed"` rebuilds the kit and publishes a new release, after you've committed. Releases of a private repo are only visible to you and people you add to the repo.

## Always on: the background service
So friends can play without you starting anything, run the server as a background service on this Mac:
```bash
scripts/server-service.sh install
```
It then starts by itself whenever you log in. While it runs, it:
- restarts the server within seconds if the server stops;
- keeps the Mac from idle-sleeping (a closed laptop lid still sleeps);
- uses Tailscale mode as soon as Tailscale is up;
- backs up the database daily to `~/CelticHeroesAnalysis/server-data/backups`, keeping the last 14.

`scripts/run-private.sh` then only launches your game against it. For one-click play, `scripts/make-desktop-shortcut.sh` puts a **Celtic Heroes Private** app on your Desktop. It starts Steam if needed, then the game (`scripts/play.sh`), and shows a dialog if something goes wrong. The log is at `~/CelticHeroesAnalysis/logs/launcher.log`.

| Command | What it does |
|---|---|
| `scripts/server-service.sh update` | Rebuilds and restarts after server code changes. `status` tells you when this is needed. |
| `scripts/server-service.sh status` | Shows whether it's running, which mode, and the last backup. |
| `scripts/server-service.sh logs` | Follows its log. |
| `scripts/server-service.sh uninstall` | Stops it and removes it from login. The database and backups are kept. |

Friends can play whenever this Mac is on, awake, online and signed in to Tailscale.

## Your friends
Send them `HOW-TO-JOIN.txt` and their file. In short:
1. Install Steam and Celtic Heroes (free). Start it once, then quit.
2. Install Tailscale, make an account and accept your share invite.
3. Run the file:
   - **Windows:** `CelticHeroesPrivate.exe`. It isn't code-signed, so SmartScreen asks: More info → Run anyway.
   - **Mac:** unzip and open **Celtic Heroes Private**. On first open, macOS may need System Settings → Privacy & Security → Open Anyway.
4. From then on, use the **Celtic Heroes Private** desktop shortcut.

Their private copy lives in `~/CelticHeroesPrivate` (Mac) or `%LOCALAPPDATA%\CelticHeroesPrivate` (Windows). The launcher logs to `~/Library/Logs/CelticHeroesPrivate.log` on a Mac. The first username and password they type become their account on your server.

## Game version

The kit only accepts the client build it was verified against (24928510, in `server/tools/CH.Patcher/KnownBuilds.cs`).
If Steam updates Celtic Heroes before a friend runs the setup, the setup refuses and asks for an updated kit. Once a
private copy exists, Steam updates don't touch it.

## Windows support
The Windows `.exe` is in the kit. The Windows game code is obfuscated, so the setup patches it through a name map built from the Mac and Windows builds (docs/windows-names.md, `scripts/map-windows.sh`). The setup path has been rehearsed on the Mac against the downloaded Windows depot, with all patch site counts the same as on Mac and every mod lookup resolved. **Not yet run on a real Windows PC.**
