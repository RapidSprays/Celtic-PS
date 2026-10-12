# Client architecture: build 24928510

Sources:
- Decompiled code at `~/CelticHeroesAnalysis/decompiled/24928510/` (ILSpy 9.1), cited as `File.cs:line` relative to `Assembly-CSharp/` unless marked `st/` (`st-services-unity`).
- Indexes at `~/CelticHeroesAnalysis/indexes/24928510/` (AsmIndex).

Labels: **O** observed · **SI** statically inferred · **EC** experimentally confirmed · **U** unknown.

## Summary
| Area | Finding | Label |
|---|---|---|
| Runtime | Unity 6000.3 with Mono. Game code is `Assembly-CSharp.dll` (1,241 decompiled files) | O |
| Transport to the game server | **UDP via an embedded Lidgren.Network gen3 fork** (`Lidgren.Network/`, 60 files), app identifier `"Chat"` (`NetworkManager.cs:271`) | O |
| Encryption | None at the transport layer: no `NetEncryption` used outside Lidgren. A small AES app-layer cipher applies only to `CommandActionRequest`/`OpenHtmlWindow` payloads (`HyperText/LightweightEncryption.cs`) | O |
| Serialization | Raw Lidgren `NetBuffer` bit-packed reads and writes. **No protobuf, LZ4, Zstd or BouncyCastle in game code** (AsmIndex `refs.csv`: 2,979 Lidgren refs, 0 for those libraries) | O |
| HTTP | `UnityWebRequest` GETs to the "patch server" for environment, world list, patch level, account helpers (see [boot-sequence.md](boot-sequence.md)) | O |
| Anti-cheat | ACTk is shipped, but **no detector is ever started**: 0 hits for `StartDetection`/`*Detector` in all decompiled code. Only `ObscuredFloat`/`ObscuredVector3` movement fields are used (`CCharacter.cs:67-79`) | O |
| File integrity | `SendChecksumFailure` (`NetworkManager.cs:6479`) has **no callers**, and nothing hashes game files | O |
| Firebase | Only analytics, Crashlytics and messaging through SharedTech `Services.Init(..., OptionalServices.None)` (`CController.cs:740`, `st/SharedTech/Services.cs:68-111`). Auth, AppCheck, RemoteConfig and Storage are never called. Login doesn't depend on Firebase | O |
| Steam | Required at startup: `SteamManager.Awake` calls `RestartAppIfNecessary` (`SteamManager.cs:68-77`), and `EnvironmentSetup.SetEnvironment` quits if env is Prod and Steam isn't running (`EnvironmentSetup.cs:165-167`). **No auth ticket**: login sends only the raw SteamID | O |
| Other SDKs | Unity IAP with a custom `SteamStore`, Zendesk, Unity Gaming Services. OneSignal, Facebook, Fyber and IronSource have init code but no callers | O |
| Local state | PlayerPrefs at `~/Library/Preferences/com.onethumbmobile.celticheroes.plist` (holds `USER`, `PASS`, `REFRESH_TOKEN`, `WORLD_ID`, …). `persistentDataPath` = `~/Library/Application Support/com.onethumbmobile.celticheroes/` | O |

## Main classes
| Class | Role |
|---|---|
| `CController` | Top-level game controller: init, balance load, settings, SDK init (`Awake` 740, `Start` 1112, `Init` coroutine around 1650-1710) |
| `EnvironmentSetup` (ScriptableObject in `resources.assets`) | Chooses the environment and patch server URL from a remote request (`EnvironmentSetup.cs:84-169`) |
| `NetworkManager` (6,823 lines) | Connection state machine, HTTP world list, Lidgren client, opcode dispatch `processCommand` (762-1367), send helpers (`createMessage` 4812) |
| `CH_*NetworkHandler`, `Assets.Main.Script.networking_classes/*` | Sub-protocols: party, clan, trade, friend, shop, item shop, mail, PvP, vouchers, auction house, crafting, factions, daily reward, token vendor, barbershop |
| `GamePatcher` | Patch level check and file patching over HTTP (`CPatcher` is an empty leftover) |
| `CWorldData`, `CUI_WorldSelect` | World list entries and Lidgren discovery pings (world "open" status) |
| `CGame` | In-world game state, zone loading (`loadLevel` 3105-3143) |
| `CPlayableCharacter`, `CBasicPC`, `CCharacter` | Local player (sends `PlayerMove`), other players, base character |
| `Balance.*` | Game data model. Client side is loaded from bundled JSON; server-side templates exist **as types only** |

## Game data
- **Client balance** (O): the TextAsset `balance-client` in `sharedassets1.assets`, about 91 MB of Newtonsoft JSON, `"Version": 101`. Loaded via `Balance.Deserialize<BalanceClient>` (`CController.cs:1647-1650`) with `TypeNameHandling.Auto`. It covers zones, zone points, player spawns, items, skills, quests and status effects in client form.
- **Updates** (O): if the server's `VersionCheckReply` says the version isn't OK, the client downloads `string.Format(BalanceCdnUrlFormat, version)` and caches it at `persistentDataPath/balance_saved.json` (`BalanceUpdateController.cs:18-51`).
  - Replying OK skips the download.
  - Because of `TypeNameHandling.Auto`, whoever serves that file can make the client instantiate arbitrary types (SI). **Only serve balance from our own host.**
- **Server balance** (O): the `BalanceServer` types (`Balance.Model/BalanceServer.cs:30-102`) cover mob stats, loot tables, spawn points, combat AI, shops, collision and server-side skill and item values. They are only built by editor tooling (`BalanceProject.cs:290-299`). The shipped JSON has **no values** for them (no `MaxHp`, `LootTables`, `MobSpawnPoints`, `AiScripts`).
  - **So the private server must author all server-side values**, though the schema is fully known.
- **`text.db`** (O): item, quest and tutorial display text keyed by id. See [inventory-summary.md](inventory-summary.md).
- **World geometry** (O): ships with the client in 44 Addressables scene bundles (`StreamingAssets/aa/StandaloneOSX/zoneN_scenes_*`, about 1.1 GB). The catalog is local with no remote updates (`settings.json` `m_DisableCatalogUpdateOnStart:true`). Scene path: `Assets/Zones/<SCENE>/<NAME>.unity` (`Zone.Client/Zone.cs:34-47`).

## Hard-coded or serialized endpoints
| Endpoint | Where | Use |
|---|---|---|
| `https://production.ch.decagames.com/patchserver/PatchServerURL.aspx?iav={0}&platform={1}` | `EnvironmentSetup.remoteEnvironmentRequestUrl` (serialized, `resources.assets`) | **Root of everything**: its reply sets the patch server and balance URL |
| `https://production.ch.decagames.com:443/patchserver/` | `EnvironmentSetup` default patch URL (serialized) | Fallback if the request above fails |
| `http://production.ch.decagames.com:443/patchserver/balancefiles/{0}/balance-client.json` | `EnvironmentSetup` default balance URL (serialized) | Fallback balance CDN |
| `http://127.0.0.1:5555/patchserver/` and `…/balancefiles/{0}/balance-client.json` | `EnvironmentSetup` `local*` fields (serialized, **never read**) | The developers' own local setup |
| `https://www.celtic-heroes.com/server_status_unity_live`, `…/display_server_status_popup` | `CUtilities.cs:21-23` | Server status popup (`ServerStatusTask`) |
| world `hostName` / `ip` + port | worldlist.aspx response | Lidgren UDP game server |
| `m_statsWebsite` | worldlist.aspx response | Leaderboards |
| `http://support.decagames.com/`, facebook.com, appleid.apple.com, `http://www.google.com` | various | Links, Apple sign-in, internet check |

## Leads and next steps
1. The **one-URL redirect**: change `remoteEnvironmentRequestUrl` in the copy so it points to our HTTP server, which then hands out everything else. See [patch-manifest.md](patch-manifest.md).
2. **Separate the private copy's PlayerPrefs** with a new bundle id, so the live account's `USER`/`PASS`/`REFRESH_TOKEN` are never sent to the private server.
3. Server: stock Lidgren gen3 `NetServer` with app id `"Chat"`. The fork's changes are client-side only (address family, a no-op `Deny`, a 90 s timeout), and the wire header looks stock (SI). This needs an experiment to confirm.
4. Build the first-milestone message set: VersionCheck → Login → CharacterList → CreateCharacter → StartGame → zoning → PlayerMove. See [protocol-notes.md](protocol-notes.md).
