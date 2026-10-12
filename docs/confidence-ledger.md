# Confidence ledger

Labels: **O** observed · **SI** statically inferred · **EC** experimentally confirmed · **U** unknown. Confidence: high / medium / low.

| # | Claim | Label | Conf. | Evidence | Alternatives |
|---|---|---|---|---|---|
| 1 | Build 24928510 is v6.15.0 (213) on Unity 6000.3.10f1 | O | high | `inventories/build-info.json` | — |
| 2 | Scripting backend is Mono | O | high | `Data/Managed/Assembly-CSharp.dll`, `MonoBleedingEdge/`, Player.log "Mono path[0]" | — |
| 3 | The pinned copy runs from its own path with Steam running | EC | high | `lsof` txt mapping to the `client/24928510` path; Player.log Mono path | — |
| 4 | At startup the client checks a patch server at `production.ch.decagames.com:443/patchserver/` | O | high | `logs/pin-launch-Player.log` | — |
| 5 | Client patch level is 143; the server reported 60 | O | high | same log | The two numbers may measure different things. Not important. |
| 6 | Firebase starts before SteamAPI_Init and a "token" is retrieved | O | high | same log, `[SharedTech]` lines | The token may be Firebase Auth, AppCheck or a SharedTech token (U) |
| 7 | `st-services-unity.dll` is the "SharedTech" services layer | SI | low | name plus log tag | To check in Stage B |
| 8 | `text.db` holds item and quest display text keyed by id | O | high | `sqlite3 .schema` | — |
| 9 | The server refers to items and quests by these ids | SI | medium | the schema has text only | The ids could be client-local (U until Stage C) |
| 10 | ~~Game server transport and port: unknown~~ → see #11 | — | — | — | — |
| 11 | Game transport is plaintext UDP via an embedded Lidgren gen3 fork, app id "Chat" | O | high | `NetworkManager.cs:271, 362-369`; no NetEncryption outside `Lidgren.Network/` | — |
| 12 | Port comes from worldlist.aspx; the address comes from `hostName` via DNS, or `ip` in env Test | O | high | `NetworkManager.cs:5598-5610, 5691-5704` | — |
| 13 | Message header: varuint opcode; post-login C→S adds a 32-byte HMAC + uint32 session; S→C starts with a flag byte (+ session) | O | high | `NetworkManager.cs:4812-4827, 747-764`; spot-checked 2026-10-12 | — |
| 14 | No protobuf, LZ4, Zstd or BouncyCastle in game code | O | high | AsmIndex `refs.csv` category counts (0) | They could be used via reflection (unlikely; no string evidence) |
| 15 | One environment URL decides every other endpoint | O | high | `EnvironmentSetup.cs:84-169` spot-checked; `CUtilities.cs:29` | — |
| 16 | No ACTk detectors are started | O | medium | 0 grep hits in decompiled code; `RuntimeInitializeOnLoads.json` | Detectors could sit in Addressables scenes (bundles not scanned) |
| 17 | No file-integrity check (`SendChecksumFailure` has no callers) | O | high | grep for callers | — |
| 18 | Login sends no Steam ticket and no Firebase/AppCheck token | O | high | `NetworkManager.cs:4335-4373` | — |
| 19 | Server-side game data (mob stats, loot, spawns, AI) isn't shipped; only the schema is | O | high | `BalanceServer.cs`, no matching keys in bundled `balance-client` JSON | It could arrive through a server message (no evidence) |
| 20 | Zone geometry ships in 44 local Addressables scene bundles; no remote catalog | O | high | `StreamingAssets/aa/StandaloneOSX`, `settings.json` | — |
| 21 | Env Test (0) uses the world `ip`, turns on debug logging, and sends client exceptions to the game server | O | high | `CUtilities.cs:248-266`, `LogManager.cs`, `SimpleCrashReporter.cs`, `NetworkManager.cs:5693` | — |
| 22 | The live PlayerPrefs (shared with the pinned copy) hold USER, PASS and REFRESH_TOKEN | O | high | key names in the plist (values not read) | — |
| 23 | A stock Lidgren gen3 NetServer can talk to the client fork | SI | medium | the fork's diffs are client-side; header and message types match | Hidden wire changes → test in Stage D |
| 24 | `st-services-unity` is the SharedTech services layer (Firebase analytics, Crashlytics, messaging) | O | high | `st/SharedTech/Services.cs:68-111` | (resolves #7) |
| 25 | Upstream lidgren-network-gen3 matches the client fork's wire code (message types, constants, packet header encode, handshake, ping/pong) | O | high | side-by-side diff 2026-10-12 (`vendor/Lidgren.Network/VENDOR.md`) | Non-wire differences only (address family, Deny, null-check fix) |
| 26 | Our server and fake client interoperate through the full login → world → move → restart flow | EC | high | `EndToEndTests` (6 consecutive green runs) | Shows self-consistency only; the real client is the final check (E-a) |
| 27 | LoginReply must go out without the session flag (the client's session id is still 0 when it arrives) | SI | high | `NetworkManager.cs:747-757` + `770-778` | — |
| 28 | The patched client takes all its endpoints from our server and boots to the main menu in env Test | EC | high | Player.log `db ENVIRONMENT:Test`, server log, 2026-10-12 | — |
| 29 | Unity engine telemetry (config.uca / cdp.cloud.unity3d.com) is started natively from UnityConnectSettings in globalgamemanagers | EC | high | `dig` on the connection IPs + POSTs arriving at `/u/…` after P8 | — |
| 30 | The patched client opens no non-local TCP connections at the menu | EC | high | `lsof -a -p <pid> -i TCP` | Later screens may open others; re-check per milestone |
| 31 | The real client logs in, creates a character, enters zone 2 and moves on our server | EC | high | Owner's play session 2026-10-12; server log (Login, Created character, entering zone 2), frame log | — |
| 32 | Stock upstream Lidgren interoperates with the client's fork (resolves U1) | EC | high | #31 | — |
| 33 | The client ships the WorldPlacer spawn-editor *model* (MobSpawnPoint: position, angle, respawn, patrol, movement AI) but no loader, UI or data | O | high | no implementers or constructors of ISpawnPointsLoader/MobSpawnPointManager in any assembly | Data may sit in editor-only tooling we don't have |
| 34 | Client balance gives mob/NPC identities (9,077 MobTemplates), class starting equipment, skills and zone exits, but no spawn positions | O | high | `extracted/24928510/balance-client.json` | — |
