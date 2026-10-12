# Celtic Heroes private server research

The aim is a private, non-commercial Celtic Heroes server that a pinned, modified copy of the original macOS client can connect to. Scope and ground rules are in [docs/scope-and-authorization.md](docs/scope-and-authorization.md), and the full plan is in [docs/plan.md](docs/plan.md), and decisions so far are in [docs/decisions.md](docs/decisions.md).

## Status
| Stage | State |
|---|---|
| A: Inventory and pinned client | **done**: build 24928510 inventoried, snapshot taken, pinned copy launches |
| B: Client map | **done**: [client-architecture.md](docs/client-architecture.md) |
| C: Protocol and boot sequence | **first pass done**: [boot-sequence.md](docs/boot-sequence.md), [protocol-notes.md](docs/protocol-notes.md), [opcode-map.md](docs/opcode-map.md). Unknowns U1–U4 are resolved during D/E |
| D: Server foundation | **done**: HTTP patch server + Lidgren game server + SQLite, fake client, 20 tests (see below) |
| E: Real client on the private server | **E-a to E-c done (2026-10-12)**: the real patched client logs in, creates a character, enters Lir's Reach and walks around, fully isolated. The world is empty (no server-side spawn data); Stage F next |

## Layout
- `docs/`: research notes. Every claim is labelled and tracked in [docs/confidence-ledger.md](docs/confidence-ledger.md).
- `inventories/`: file list, candidates, SHA-256 of every client file, build info.
- `scripts/`: reproducible steps (below).
- `server/`: the .NET 8 server, fake client, patcher and tests (Stage D onwards).

Outside git, in `~/CelticHeroesAnalysis/`:
- `snapshots/<build>/`: read-only copy of the client's code and config
- `client/<build>/`: the pinned client copy
- `decompiled/<build>/`
- `logs/`

## Reproduce
Steam must be running and Celtic Heroes installed.
```bash
scripts/inventory.sh            # file list, candidates, hashes, build-info.json → inventories/
scripts/snapshot.sh             # read-only snapshot of Managed/ + config → ~/CelticHeroesAnalysis/snapshots/<build>/
scripts/pin-client.sh           # full hash-checked copy → ~/CelticHeroesAnalysis/client/<build>/
scripts/inventory.sh --verify   # confirm the Steam install is still unmodified
scripts/inventory.sh --verify ~/CelticHeroesAnalysis/client/24928510/CelticHeroes.app
```
To launch the pinned copy (it reads `steam_appid.txt` from the working directory):
```bash
cd ~/CelticHeroesAnalysis/client/24928510 && "CelticHeroes.app/Contents/MacOS/Celtic Heroes"
```
The client log is at `~/Library/Logs/One Thumb Mobile Ltd/Celtic Heroes/Player.log`.

## Git hygiene
```bash
ln -sf ../../scripts/pre-commit.sh .git/hooks/pre-commit
```
The hook blocks client binaries, databases, decompiled code, files over 5 MB, and token-like strings.

## Stage B tools
```bash
export DOTNET_ROOT=$HOME/.dotnet
B=24928510; M=~/CelticHeroesAnalysis/snapshots/$B/Contents/Resources/Data/Managed
ilspycmd -p -r "$M" -o ~/CelticHeroesAnalysis/decompiled/$B/Assembly-CSharp "$M/Assembly-CSharp.dll"
dotnet run -c Release --project scripts/tools/AsmIndex -- "$M/Assembly-CSharp.dll" ~/CelticHeroesAnalysis/indexes/$B/Assembly-CSharp
```

## Server (Stage D)
```bash
cd server
export DOTNET_ROOT=$HOME/.dotnet
dotnet test                                    # 20 tests: wire format, persistence, redaction, loopback guard, end-to-end
dotnet run --project src/CH.Server -- --data ~/CelticHeroesAnalysis/server-data
```
- Listens on `127.0.0.1` only: HTTP patch server on 5555, Lidgren UDP game server on 5556. `--bind` with a non-loopback address is refused unless `--allow-remote` is given.
- Data goes to `--data`: `private-server.db` (SQLite; accounts keyed by username, **no passwords, device ids or Steam ids**) and `logs/frames-*.jsonl` (every message in and out; login bodies redacted).
- Implemented: environment, world list, account-conflict, privacy and patch pages; discovery; VersionCheck; Login; character list and create; RequestTweak; StartGame with full character data; FinishedZoning → EnteringNewZoneUpdate + AllZoningMessagesSent; PlayerMove (persisted).
- New characters start in Lir's Reach (zone 2) at the Highshore Village spawn, taken from the client balance data (approximated).
- `server/vendor/Lidgren.Network`: upstream lidgren-network-gen3 (MIT), wire-compared with the client's fork. See its `VENDOR.md` for the two local changes.

## Private client (Stage E)
```bash
cd server && dotnet run --project tools/CH.Patcher   # patch the pinned copy (once, or after changing the port)
cd .. && scripts/run-private.sh                       # start server + patched client; Ctrl+C stops both
```

## Playing (Stage F)
New characters get their class's starter gear, skills, potions and skill books. Lir's Reach is populated with
monster camps around its leystones (auto-placed; see `server/src/CH.Server/Game/ZonePopulation.cs`). Combat,
XP, coins, potion drops, level-ups, death and respawn at the nearest leystone all work. Buffs and debuffs
(status effects) show with icons and timers and do what they say: armour/defence/attack/speed/regen changes,
Hide and Camouflage against aggro, energy and damage shields, damage over time on mobs. Levelling grants
attribute and skill points to spend on the stats and skills pages.

**Equipment:** every item's stats apply: primary stat bonuses, Health/Energy/Attack/Defence bonuses, ability and
skill bonuses, avoidances; level/stat/gender requirements are enforced; two-handers block the offhand; equip skills
(e.g. Virulent Sunder) join the skill list while worn; weapon procs fire on hit.

**Chests:** all 315 openable containers in the client data work (`server/src/CH.Server/Game/Chests.cs`):
70 Mystery Chests open on the client's own chest-opening screen ("Next Chest" opens the next); pirate,
smuggler and fashion boxes give one random matching piece; crates, sets, token bags and starter kits give what
their descriptions say; seasonal gifts, bounty bags, clams, fishing/cooking caches and tarot bags draw from
themed pools. Platinum is tracked. Kills have a small chance to drop a chest. The real loot tables were never
shipped, so contents are reconstructed from each chest's own description.

GM commands are typed in normal chat with a `.` prefix (`/` commands are client-encrypted and not used):

| Command | What it does |
|---|---|
| `.help` | list commands |
| `.where` | zone and coordinates |
| `.find <name>` | search mob templates (e.g. `.find duncan`) |
| `.npc <id or name> [conv <id>]` | place a friendly NPC here, facing you; its conversation is matched by name (saved) |
| `.spawn <id or name> [level]` | place a hostile mob here (saved) |
| `.near [radius]` | list mobs and saved spawns around you |
| `.delete <spawn id>` / `.delete nearest` | remove a saved spawn |
| `.level <n>` / `.level +<n>` / `.level -<n>` | set or change your level |
| `.xp <amount>` | gain experience (levels up as it goes) |
| `.give <item id or name> [qty]` (or `.item`) | put items in your backpack, e.g. `.give minor health potion 5` |
| `.finditem <name>` | search item templates (several can share a name; pick by id) |
| `.gearset [level]` | equip the best gear your class can wear at that level (Rogue: daggers + leather) |
| `.wear <item name or id>` | give and equip an item (GM override of requirements) |
| `.chest [name] [qty]` | a random legacy chest, or chests by name (`.chest mystery chest 5`, `.chest pirate`, `.chest bounty`) |
| `.heal`, `.learn <skill id>` | testing helpers |
| `.respawn` | respawn every mob in the zone |
