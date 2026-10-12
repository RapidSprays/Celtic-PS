# Celtic Heroes: plan for a private server the real client can connect to

## Context
The goal is a **working private Celtic Heroes server, for non-commercial use, that the original client connects to** (Stage E in the handoff brief `~/Downloads/Celtic_Heroes_REA_Claude_Handoff.pdf`). The user has stated that the project is authorized. Stages A–D exist to make E possible. The brief's evidence discipline still applies: every claim is labelled observed, statically inferred, experimentally confirmed or unknown. Production stays untouched. This plan is based on what this Mac actually has, which I checked read-only during planning.

## Already verified (read-only, during planning)
| Fact | Evidence | Label |
|---|---|---|
| arm64, macOS 26.6.2, Xcode CLT | `uname -m`, `sw_vers`, `xcode-select -p` | observed |
| Node 26.5 / npm 11.17 (meets REA's Node ≥22.19 requirement), .NET SDK 8.0.423, git, Homebrew, Python 3.9 | `--version` | observed |
| **No Java** (so no Ghidra yet); no ILSpy, Wireshark, REA | `command -v` | observed |
| Game installed natively: Steam app **2829820**, buildid **24928510**, at `~/Library/Application Support/Steam/steamapps/common/Celtic Heroes/CelticHeroes.app` (3.0 GB, 21,670 files) | appmanifest, `du`, `find` | observed |
| v6.15.0 (213), bundle id `com.onethumbmobile.celticheroes`, **Unity 6000.3.10f1** | Info.plist | observed |
| **Mono backend, not IL2CPP**: `Contents/Resources/Data/Managed/Assembly-CSharp.dll` (2.5 MB) + 221 DLLs, `MonoBleedingEdge/` | file tree | observed |
| Clue libraries: Google.Protobuf, BouncyCastle.Crypto, K4os LZ4, ZstdNet, System.Net.Sockets, Steamworks.NET, Firebase (Auth, AppCheck, RemoteConfig), **ACTk anti-cheat**, sqlitekit, `st-services-unity.dll` | `Managed/` listing | observed (clues only) |
| The client changes often: it was updated today, and 7.0.0 is in beta | file mtime, news | observed / external |
| REA is an MCP server (`npx rea-agents setup`) with built-in .NET analysis | github.com/morluto/rea | observed |

**What follows from these facts:**
- Mono means ILSpy produces near-source C#. Both the client's *send* code and its *receive/parse* code can be read directly, and the receive side is exactly what the server has to satisfy.
- Because the client updates often, the server targets **one pinned client build** (a copy), not the live Steam install.

## Ground rules
- Record the user's authorization statement in `docs/scope-and-authorization.md`, with the date (2026-10-12) and a field for the authorizing party that the user fills in.
- **The Steam install is never modified.** All runs and changes use a pinned copy at `~/CelticHeroesAnalysis/client/<buildid>/CelticHeroes.app`.
- **The private server never contacts DECA, Steam or Firebase back-ends.** Tokens the client sends (Steam ticket, Firebase ID token) are accepted without validation, never stored, and logged as `[REDACTED]`. Anything the client contacts on its own during normal startup is observed and documented; we never induce or proxy it.
- No other players' data, and no capture or decryption of live-server traffic beyond passive metadata from the user's own session.
- Anti-cheat and the other outside services are stripped **only from the pinned copy**, and that copy also has every live endpoint removed. A client with no anti-cheat therefore can never connect to production. This is checked in Verification.
- **Scope caveat:** server-side game data and logic aren't in the client. That includes mob stats, drop tables, spawns, quest and combat resolution, and most likely the authoritative item stats. Stage B checks how much ships with the client (`StreamingAssets`, `sqlitekit` databases, `resources.assets` via AssetRipper). Anything missing has to be written by us or approximated, and labelled as such.
- The server listens on localhost by default. Opening it to the LAN or internet for friends is a separate decision, documented when it's made.
- Git holds only my notes, scripts, server code, hashes and protocol fixtures. Client binaries, patched binaries, decompiled code and asset dumps stay out of git and are never distributed.

## Layout
- **Repo** (local git; a remote only if the user asks, and it must be private): `~/Coding/celtic-heroes-research/`
  ```
  README.md  .gitignore
  docs/  scope-and-authorization.md environment.md inventory-summary.md client-architecture.md
         boot-sequence.md protocol-notes.md opcode-map.md confidence-ledger.md decisions.md
         risk-register.md expansion-plan.md handoff.md
  inventories/  file-list.txt candidates.txt sha256.txt build-info.json
  scripts/  inventory.sh snapshot.sh pin-client.sh run-private.sh  tools/AsmIndex (Mono.Cecil)
  server/  CH.Server.sln  src/{CH.Protocol,CH.Persistence,CH.Server,CH.FakeClient}  tools/CH.Patcher  tests/CH.Tests (+ fixtures/)
  docs/patch-manifest.md
  ```
- **Workspace** (outside git): `~/CelticHeroesAnalysis/{snapshots,decompiled,client,logs}/<buildid>/`

## Stage A: Inventory and a pinned client (≈½–1 day)
1. Set up the repo skeleton, `environment.md` and `scope-and-authorization.md`.
2. `scripts/inventory.sh` produces the file list, candidates, SHA-256 hashes and `build-info.json` (from the appmanifest and Info.plist).
3. `scripts/snapshot.sh` makes a read-only, hash-verified copy of `Data/Managed`, the config JSONs and `StreamingAssets`.
4. `scripts/pin-client.sh` copies the whole `.app` to `client/24928510/` and checks that the copy launches unmodified with Steam running. Steam DRM stays intact: the user owns the game and Steam keeps running.
- **Exit:** inventory and copy are hash-verified, and the copy launches. **Checkpoint 1.**

## Stage B: Client map (≈2–4 days)
1. With approval, install `dotnet tool install -g ilspycmd` and REA (`npx rea-agents@latest setup`). I'll review the Claude Code MCP config it writes, and confirm with `rea --help` and a test call.
2. Run `ilspycmd -p` on Assembly-CSharp, `-firstpass` and `st-services-unity.dll`, writing to `decompiled/24928510/`.
3. Build `tools/AsmIndex` (Mono.Cecil). It writes reproducible JSON indexes of:
   - types and methods that use sockets, WebSocket, HttpClient, protobuf `IMessage`/`MessageParser`, BouncyCastle, LZ4, Zstd, `CodeStage.AntiCheat.*`, Firebase and Steamworks
   - large enums (opcode candidates)
   - endpoint-like string literals
4. Cross-check the findings with REA's .NET analysis.
5. Write `client-architecture.md`. Every claim cites `Type::Method`, with a label and confidence.
- **Exit:** the architecture report and ledger are written. **Checkpoint 2.**

## Stage C: Protocol and boot sequence (≈1–3 weeks, ongoing)
- **`boot-sequence.md`:** the order of everything the client contacts at startup (Steam init, Firebase Auth/AppCheck/RemoteConfig, any HTTP server list or news call, the game socket). It also records **where the game server host and port come from**: a constant, PlayerPrefs, RemoteConfig, an HTTP server list, or a launch argument. This decides the redirect method in Stage E.
- **`protocol-notes.md` and `opcode-map.md`** cover:
  - transport, TLS or custom crypto, and certificate pinning
  - framing (header layout, length, endianness)
  - when compression applies
  - serialization (protobuf descriptors → local `.proto` files, or hand-written binary readers)
  - the session state machine
  - **the parse code for every server→client message the client needs to reach character select and enter the world**
- **Exit:** the messages for the first Stage E milestone are documented, with unknowns listed. **Checkpoint 3.**

## Stage D: Server foundation (starts in parallel with C)
.NET 8 in C#, the same language as the client, using `Google.Protobuf` and `Microsoft.Data.Sqlite`, with xUnit tests.
- `CH.Protocol`: an `IFrameCodec` interface. The first implementation is the **Celtic Heroes codec built from Stage C evidence**, not a made-up format. Until then a stub codec lets the rest be built and tested. Unit tests cover partial reads, oversized frames and malformed input.
- `CH.Server`: a `TcpListener`, plus a TLS listener if the client uses TLS. It refuses a non-loopback bind unless `--allow-remote` is passed. Other parts:
  - session state machine
  - login handler (accepts any token, redacts it)
  - account, character and world services
  - **a frame logger for every inbound frame** (decoded where known, hex plus opcode where not) to `logs/`
  - a handler for unknown opcodes that logs and leaves the session open
- `CH.Persistence`: SQLite with numbered migrations (`accounts`, `characters`, `positions`, later `inventory`).
- `CH.FakeClient`: speaks the reconstructed protocol, so automated tests don't need the real client.
- **Exit:** `dotnet test` passes, including a test where the fake client logs in, creates a character, moves, the server restarts, and the position is still there.

## Stage E: The real client connects (core goal, ≈1–3 months)
**The live server is never needed.** Every server→client message is rebuilt from the client's decompiled receive/parse code and confirmed by watching the client react.

1. **Private-client patch set (`server/tools/CH.Patcher`, Mono.Cecil, in the repo).** It patches only the pinned copy. It is reproducible and idempotent. It refuses to run unless the input DLL hash matches the pinned build. Afterwards it re-signs the copy ad hoc with `codesign --force --deep -s -`. Each patch is listed in `docs/patch-manifest.md` (target `Type::Method`, reason, effect).
   - **Endpoints:** every DECA game, API and HTTP host is replaced with one local endpoint read from a config file (default `127.0.0.1:<port>`). **Live hostnames are removed**, so the private client cannot reach production.
   - **Anti-cheat:** ACTk detectors and their callbacks are disabled. ObscuredTypes, ACTk's in-memory value encoding, is left alone unless it gets in the way.
   - **Firebase:** Auth, AppCheck and RemoteConfig are stubbed with local defaults. Analytics, Crashlytics and Messaging are disabled, so no telemetry goes to DECA's Firebase.
   - **Purchases and ads:** Unity IAP, Fyber, Facebook and OneSignal are stubbed out, so no real purchases or ad calls can happen.
   - **TLS:** if the game socket uses TLS, either drop it to plain TCP in the copy or trust a local certificate, whichever touches less code.
   - **Steam:** `steam_api` and ownership checks stay as they are (no DRM bypass), and Steam runs alongside. The server ignores the Steam ticket it receives.
   - **Fallback:** if a patch can't be written cleanly as an IL edit, a BepInEx/Harmony plugin applies the same change at runtime.
2. **Login on the private server.** A trivial login: any token is accepted and redacted. Accounts and characters live in local SQLite.
3. **Shortcut to look at during Stage B:** if the messages are protobuf, rebuild the `.proto` files from the client's embedded descriptors with a script. The output goes to the workspace, outside git, and the server's codec is generated from it. That gives exact wire compatibility without hand-writing every message.
4. **Iteration loop.** Each pass goes:
   - launch the pinned copy
   - read the server frame log and the Unity `Player.log` (path to be confirmed, likely `~/Library/Logs/<company>/Celtic Heroes/Player.log`)
   - find the next message the client waits for, in its decompiled receive handler
   - implement that response
   - add the captured client frames as **test fixtures**, and a fake-client test that replays them
5. Boot sequence on the private client: Steam init, then (once patched) directly to our server. `boot-sequence.md` records what the unpatched client does, so each patch has evidence behind it.
4. **Milestones:**
   - **E-a:** handshake and login accepted.
   - **E-b:** character list shown, with characters from our database.
   - **E-c:** enter the world at a saved position.
   - **E-d:** move, then restart the server, and the position persists.

   Each milestone gets a screenshot or log proof and a repeatable script, `scripts/run-private.sh`, which starts the server, checks the copy is patched, and launches it.
- **Exit:** E-d can be repeated from a clean checkout plus the pinned copy.

## Stage F: Expansion and handoff
Next features, in order: inventory, abilities/combat, NPCs/mobs, world state, chat, multiplayer (two clients), and LAN hosting (a separate decision). Each feature follows the same loop: receive handler evidence → codec → handler → fixture test. Write `risk-register.md` covering client updates breaking compatibility (pin the build, diff opcode maps per build with AsmIndex), TLS pinning, ACTk and Firebase dependence. Write `handoff.md` with completed work, blockers, decisions needed and the next three actions.

## Reused rather than built
ILSpy (`ilspycmd`), Mono.Cecil (indexing and copy-only patching), REA built-in .NET analysis, Google.Protobuf (the same library the client ships, so wire formats match), Microsoft.Data.Sqlite, xUnit, and BepInEx/Harmony only as the last-resort rung. There is no existing code in `~/Coding` to reuse.

## Verification
- **A:** `shasum -c` against both the Steam install (it must be unchanged) and the snapshot. The pinned copy launches.
- **B and C:** spot-check 10 random ledger claims against the decompiled code, and cross-check with REA.
- **D:** `dotnet test server/CH.Server.sln` passes. `lsof -iTCP -sTCP:LISTEN` shows only 127.0.0.1.
- **E:** each milestone has a scripted run, a screenshot of the client screen, a server frame log, and replay tests from fixtures passing. Check the Steam install hash again after every Stage E session.
- **Production isolation check:** search the patched DLLs for live hostnames (there must be none). While the private client runs, `lsof -i -a -p <pid>` must show only localhost and Steam connections.
- **Always:** a pre-commit check rejects binaries, `decompiled/`, `client/` and token-like strings.

## Approvals I'll ask for when I reach them
1. Installing `ilspycmd` and REA (REA changes Claude Code MCP config).
2. The first run of CH.Patcher on the pinned copy (after reviewing `patch-manifest.md`).
3. Later, if needed: AssetRipper, BepInEx, Java/Ghidra. Wireshark is no longer needed because the live server plays no part.
4. Any git remote (private), or exposing the server beyond localhost.
