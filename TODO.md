# TODO

Last updated 2026-10-13. State at the time of writing:
- Porting sections 1-6 are done (`docs/porting-status.md`).
- The live server runs revision `c0809cd`.
- Friend kit `v0.1.1` (local relay) is released.

Development is moving from the Mac to Windows. Read "Moving to Windows" first.

## Moving to Windows

These things live **outside git** and must be redone or copied on the new machine:

| What | Where on the Mac | How to get it on Windows |
|---|---|---|
| Client balance data and generated world files (`balance-client.json`, `world.json`, `zone-population.json`, `zone-worlds.json`, `fishing.json`, `cooking.json`, `item-spawns.json`, `zone-points.json`, Windows name map) | `~/CelticHeroesAnalysis/extracted/24928510/` | Copy the folder, or regenerate with `scripts/prepare-server.sh` and the `scripts/gen-*.py` scripts. `extract-zone-worlds.py` needs UnityPy in a venv outside the repo |
| Decompiled client, for reading only | `~/CelticHeroesAnalysis/decompiled/24928510/` | Regenerate locally. Never commit it |
| Pinned game build and patched private copy | `~/CelticHeroesAnalysis/client/24928510/` | On Windows, use the Windows depot (build 24928510). The kit patches it with the name map (`docs/windows-names.md`) |
| Server database (accounts, characters, clans, mail…) | `~/CelticHeroesAnalysis/server-data/private-server.db`; daily backups in `backups/` | Copy it if the server moves to Windows. **The live server still runs on the Mac** (launchd service, Tailscale `100.69.4.85`) |

- **If the server moves off the Mac,** the friend kit has the host address built in. Build a new kit with the new Tailscale IP (`scripts/make-friend-kit.sh`, then `RELEASE_TAG=v0.x.y scripts/release-kit.sh "…"`). The Mac scripts (`server-service.sh`, `run-private.sh`, `make-desktop-shortcut.sh`) are bash and macOS-only; Windows needs a service equivalent, such as a scheduled task or NSSM.
- **Building and testing:** install the .NET 8 SDK, then run `dotnet test server/CH.Server.sln`. Tests skip themselves without the extracted data.
- **Ground rules still apply** (`docs/scope-and-authorization.md`):
  - No live-server traffic capture.
  - No game binaries or decompiled code in git.
  - Game files are never distributed.
  - Commits stay private.

## Bug-fix pass: bugs DECA never fixed

Sources:
- This server's own logs: client error reports and the Mac client's `Player.log` output.
- Public player reports: Steam discussions (app 2829820), DECA's public Trello bug tracker (https://trello.com/b/LrY9ScOh), and the 2026 patch notes.
- Reddit, Discord and the old forum were unreachable.

Many of DECA's server bugs don't apply here, because we run our own server: the crit penalty against lower-level mobs, dead item bonuses, elixir stacking and ghost sessions. What we inherit is their **client** (6.15, which already includes their 2026 fixes).

**Client fixes** go through the client mod and patcher (`server/tools/CH.ClientMods`, `CH.Patcher`, `docs/patch-manifest.md`). They need a re-patch of the host copy and a new friend kit; ship them together as v0.1.2.

- [ ] **1. Stuck screens.**
  - The death screen has no OK button (respawn impossible). About 5 Steam reports in 2025-26, no DECA reply: https://steamcommunity.com/app/2829820/discussions/0/601911983260065020/
  - The tutorial overlay blocks everything and survives a relog: https://steamcommunity.com/app/2829820/discussions/0/530970010564210538/
  - Our friend's UI locked up twice: the tutorial, and the calendar (below).
  - Fix: repair the popup layer, plus a guaranteed close key (Escape) for any stuck popup or overlay.
- [ ] **2. Calendar Claim button crashes.**
  - Cause: a NullReferenceException in `LivingCalendarManager.SendAction`, so the screen never closes. Seen in our logs from the Windows friend.
  - Likely the manager keeps a `networkManager` reference taken before the network manager existed.
  - The server works around it today: it never opens the screen and gives the daily reward at login instead (`GameServer.Calendar.cs`).
  - Fix the client, then let the server open the calendar screen again.
- [ ] **3. Tutorial overlay crashes every frame.**
  - Cause: `GUIQuestLog.FirstQuestIconRect` throws ArgumentOutOfRangeException with an empty quest log.
  - Tutorials are off by default (`.tutorial on` opts in). Fix the client so tutorials are usable, then consider turning them back on for new players.
- [ ] **4. Camera.**
  - NullReferenceException in `InputManager.UpdateCamera` from `CGame.ScreenClicked`: 9 times in the Mac logs. Fix: null guard.
  - Camera spins on its own when a controller or HID device drifts: https://steamcommunity.com/app/2829820/discussions/0/4625855423768058964/ Fix: add a joystick deadzone.
- [ ] **5. Mobile controls on PC.**
  - No key rebinding, which makes the game unplayable on non-English keyboard layouts.
  - Camera tilt is limited to about 30-60°.
  - About 1 second of movement continues after releasing a key.
  - No click-to-move.
  - Reported in about 6 Steam threads with no DECA reply: https://steamcommunity.com/app/2829820/discussions/0/601891224286146358/, https://steamcommunity.com/app/2829820/discussions/0/592891465952156764/, https://steamcommunity.com/app/2829820/discussions/0/601900196106917061/
- [ ] **6. List scrolling.** The bank and clan bank won't scroll with a touchpad or mouse wheel (Steam, 2025). They likely share one list/scroll widget, so a single fix may cover every list.
- [ ] **7. Missing-icon error every session.** The client throws `InvalidKeyException: No Location found for Key=.png` (an empty icon name) while creating an entity in `CGame`. Seen on both Mac and Windows. Fix: guard in the icon loader, or find which template has the empty icon.
- [ ] **8. Ghost and clone players.** Quitting from an NPC while food is cooking "clones" your character, and inspecting players quickly makes their models jump to mid-map (Steam, Apr 2025): https://steamcommunity.com/app/2829820/discussions/0/601901034049194688/ Check whether it reproduces here first.

**Server fixes** take effect for everyone as soon as the server restarts (`scripts/server-service.sh update` on the host).

- [ ] **9. `CommandActionRequest` (24000) is unhandled.** Seen when using `.log`. Find what the client expects back (`docs/opcode-map.md`).
- [ ] **10. Unstick.**
  - Add a `.stuck` command (to the nearest leystone).
  - Add an automatic unstick for new characters that spawn spinning or unable to move: https://steamcommunity.com/app/2829820/discussions/0/601905642111771378/
  - Also check that our own CharacterStuck handling works.
- [ ] **11. Bosses don't reset after a player dies or uses a resurrection idol** (old forum, about 2012). Make sure our leash/return logic (`GameServer.World.cs`, `StartReturn`) also runs when the target dies or revives elsewhere.
- [ ] **12. A new character died about 22 s after first spawning** (friend's first login, 2026-10-13). Find out what killed him; monster camps should keep clear of the starting point.
- [x] Idle logout: DECA logs players out after about 10 minutes; ours never does. Nothing to do.
- [x] Transaction atomicity (DECA's history of dupes): bank, trade, mail, auction and clan bank all move items in single transactions.

**Suggested order:**
1. Items 1, 2, 3, 4 and 7 (crashes and soft-locks players hit today).
2. Item 9.
3. Item 5, as one input and camera quality-of-life pass.
4. Item 6.

## Not yet verified in the real client

Everything from porting sections 1-6 passed the automated tests but hasn't been played:
- Fishing, cooking and ground pickups.
- Bounty boards, auction house and item shop.
- Mail, friends and clans with the clan bank.
- Arena and Clan Battleground PvP.
- Leaderboards and tarot.
- The passive tree panel.

## Later

- **Rebalance and quality-of-life pass.** Every invented number is listed in `docs/decisions.md`. The shop, factions, calendar, passive tree and custom items are plain content files under `server/content/`.
- **A Windows host service**, if the server moves off the Mac.
