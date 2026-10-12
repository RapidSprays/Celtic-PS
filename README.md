# Celtic Heroes private server research

The aim is a private, non-commercial Celtic Heroes server that a pinned, modified copy of the original macOS client can connect to. Scope and ground rules are in [docs/scope-and-authorization.md](docs/scope-and-authorization.md), and the full plan is in [docs/plan.md](docs/plan.md), and decisions so far are in [docs/decisions.md](docs/decisions.md).

## Status
| Stage | State |
|---|---|
| A: Inventory and pinned client | **done**: build 24928510 inventoried, snapshot taken, pinned copy launches |
| B: Client map | next: needs `ilspycmd` and REA installed |
| C: Protocol and boot sequence | not started; first boot observations in [docs/inventory-summary.md](docs/inventory-summary.md) |
| D: Server foundation | not started |
| E: Real client on the private server | not started |

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
