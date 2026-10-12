# Celtic Heroes private server research

The aim is a private, non-commercial Celtic Heroes server that a pinned, modified copy of the original macOS client can connect to. Scope and ground rules are in [docs/scope-and-authorization.md](docs/scope-and-authorization.md), and the full plan is in [docs/plan.md](docs/plan.md), and decisions so far are in [docs/decisions.md](docs/decisions.md).

## Status
| Stage | State |
|---|---|
| A: Inventory and pinned client | **done**: build 24928510 inventoried, snapshot taken, pinned copy launches |
| B: Client map | **done**: [client-architecture.md](docs/client-architecture.md) |
| C: Protocol and boot sequence | **first pass done**: [boot-sequence.md](docs/boot-sequence.md), [protocol-notes.md](docs/protocol-notes.md), [opcode-map.md](docs/opcode-map.md). Unknowns U1–U4 are resolved during D/E |
| D: Server foundation | **done**: HTTP patch server + Lidgren game server + SQLite, fake client, 20 tests (see below) |
| E: Real client on the private server | patch set drafted: [patch-manifest.md](docs/patch-manifest.md) |

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
