# Environment

Audited 2026-10-12 (read-only).

| Item | Value | How checked |
|---|---|---|
| Hardware | Apple M5, arm64 | `uname -m`, Player.log Metal device line |
| OS | macOS 26.6.2 (25G83) | `sw_vers` |
| Xcode | `/Applications/Xcode.app/Contents/Developer` | `xcode-select -p` |
| Node / npm | v26.5.0 / 11.17.0 (REA needs Node ≥22.19) | `--version` |
| .NET SDK | 8.0.423 | `dotnet --version` |
| git | 2.54.0 (Apple Git-157) | `git --version` |
| Homebrew | 7.0.9 | `brew --version` |
| Python | 3.9.6 (system) | `python3 --version` |
| Java | **missing** (needed only for Ghidra) | `java -version` |
| ILSpy (`ilspycmd`) | 9.1.0.7988 (installed 2026-10-12; latest 11.x needs a newer .NET). Needs `export DOTNET_ROOT=$HOME/.dotnet` | `ilspycmd --version` |
| REA | 6.4.0 via `npx -y rea-agents@latest` (CLI works; Claude Code MCP registration waits for owner confirmation) | `rea doctor` |
| Wireshark | missing, not needed | `command -v` |
| Steam | running, library at `~/Library/Application Support/Steam` | `pgrep`, `libraryfolders.vdf` |

## Paths
| What | Path |
|---|---|
| Steam install (never modified) | `~/Library/Application Support/Steam/steamapps/common/Celtic Heroes/CelticHeroes.app` |
| Burst debug info (outside the .app, not inventoried) | `…/common/Celtic Heroes/Celtic Heroes_BurstDebugInformation_DoNotShip/` |
| Snapshot (read-only) | `~/CelticHeroesAnalysis/snapshots/<buildid>/` |
| Pinned client copy | `~/CelticHeroesAnalysis/client/<buildid>/CelticHeroes.app` |
| Client log | `~/Library/Logs/One Thumb Mobile Ltd/Celtic Heroes/Player.log` (shared by Steam install and pinned copy; overwritten each launch) |
| Saved logs | `~/CelticHeroesAnalysis/logs/` |
| Decompiled source (outside git) | `~/CelticHeroesAnalysis/decompiled/<buildid>/<assembly>/` |
| Assembly indexes (outside git) | `~/CelticHeroesAnalysis/indexes/<buildid>/<assembly>/` |
