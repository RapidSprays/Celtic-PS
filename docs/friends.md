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
3. Build the kit once (and again if your Tailscale IP or the game build changes):
   ```bash
   scripts/make-friend-kit.sh
   ```
   It writes `~/CelticHeroesAnalysis/friend-kit/CelticHeroesPrivateKit.zip`. Send that zip to your friends.
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

## Your friends

1. Install Steam, then Celtic Heroes (free). Start it once, then quit it.
2. Install Tailscale, log in, and accept your share link.
3. Unzip the kit.
   - **Mac:** right-click `Setup (Mac).command` → Open.
   - **Windows:** run `windows\Setup.bat`. This only works once Windows support is in the kit (see below).
4. To play, start Steam, then open `Play Celtic Heroes Private` from:
   - Mac: `~/CelticHeroesPrivate`
   - Windows: `%LOCALAPPDATA%\CelticHeroesPrivate`
5. The first username and password they type become their account on your server.

## Game version

The kit only accepts the client build it was verified against (24928510, in `server/tools/CH.Patcher/KnownBuilds.cs`).
If Steam updates Celtic Heroes before a friend runs the setup, the setup refuses and asks for an updated kit. Once a
private copy exists, Steam updates don't touch it.

## Windows support (to do)

The Windows game files differ from the Mac ones. Adding Windows support needs:
- the Windows build's hashes;
- the patch site counts checked against the Windows DLL;
- a separate PlayerPrefs location for the private copy, the Windows equivalent of the Mac bundle-id patch (P1), because
  Windows keys PlayerPrefs on the company and product names.

To get the Windows files onto this Mac without a Windows PC:
1. With Steam running, open `steam://open/console` in a browser.
2. Run `download_depot 2829820 2829821` (2829821 is the expected Windows depot; the Mac one is 2829822).
3. Steam prints the folder it downloaded to, usually `~/Library/Application Support/Steam/steamapps/content/app_2829820/depot_2829821`.
