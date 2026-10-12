# Private-client patch manifest: build 24928510 (DRAFT, not yet applied)

These patches apply **only** to the pinned copy at `~/CelticHeroesAnalysis/client/24928510/`. They will be applied by `server/tools/CH.Patcher` (Mono.Cecil), which refuses to run unless the input hashes match `inventories/sha256.txt`. It then re-signs the copy ad hoc. The Steam install is never touched.

Stage B findings made this set much smaller than planned. There are **no active anti-cheat detectors and no file-integrity checks to strip** (see [client-architecture.md](client-architecture.md)).

| # | Target | Change | Why | Evidence |
|---|---|---|---|---|
| P1 | `Info.plist` `CFBundleIdentifier` | `com.onethumbmobile.celticheroes` → `com.onethumbmobile.celticheroes.private` | **Separates PlayerPrefs and persistentDataPath from the live game.** The live prefs hold `USER`, `PASS`, `REFRESH_TOKEN` and `WORLD_ID`, and the private client must never send those to any server, ours included. It also stops the private client from overwriting live settings | Shared plist observed (`~/Library/Preferences/com.onethumbmobile.celticheroes.plist`). Unity on macOS keys PlayerPrefs by bundle id (SI) |
| P2 | `EnvironmentSetup.Init` (`EnvironmentSetup.cs:93`) | Use a **local environment URL** (`http://127.0.0.1:<httpPort>/patchserver/PatchServerURL.aspx?iav={0}&platform={1}`, from a config file next to the app) instead of the serialized `remoteEnvironmentRequestUrl` | The single root endpoint. Our reply `0,http://127.0.0.1:<httpPort>/patchserver/,0,http://127.0.0.1:<httpPort>/patchserver/balancefiles/{0}/balance-client.json` sends every patch server, world list and balance request to us and selects env Test | `boot-sequence.md` steps 3 and 9 |
| P3 | `EnvironmentSetup` defaults (`defaultPatchServerUrl`, `defaultBalanceCdnUrlFormat`) | Point them to the same local URLs | If our env endpoint is down, the client must fail over to localhost, never to production | `EnvironmentSetup.cs:150-157` |
| P4 | `CUtilities` static ctor (`CUtilities.cs:21-23`) | Point the server-status URLs at the local HTTP server | Removes the `celtic-heroes.com` live calls | AsmIndex `endpoints.csv` |
| P5 | `CController.Awake` `Services.Init(...)` (`CController.cs:740`) | Skip it, and invoke its completion callback directly | Stops Firebase analytics, Crashlytics and FCM telemetry to the live project | `st/SharedTech/Services.cs:68-111`. The callback contract still needs checking before patching |
| P6 | `UnityServices.InitializeAsync` (`CController.cs:1151`), Zendesk init (`CController.cs:1663`) | No-op | Telemetry and support SDKs talk to live projects | — |
| P7 | IAP: `CController.InitialisePurchaseManager` (`CController.cs:1409`) | No-op | No store calls or purchases from the private client | `InAppPurchaseManager.cs:27-44`, `SteamStore.cs:59-124` |

**Not patched:**
- Steam (`SteamManager`, ownership, `RestartAppIfNecessary`). Steam stays running and there is no DRM bypass.
- ACTk `Obscured*` types (in-memory encoding only).
- The HMAC signing on client messages (our server simply doesn't verify it).
- `http://www.google.com` internet check (not a DECA endpoint).
- External links (support, Facebook), which only open when clicked.

## Isolation check (run after patching)
1. Search the patched `Assembly-CSharp.dll`, `resources.assets` overrides and `Info.plist` for `decagames.com` and `celtic-heroes.com`. The literals in `resources.assets` stay, but P2 and P3 make them unreachable from code. The check lists them as "present, unreferenced".
2. While the private client runs: `lsof -a -p <pid> -i -n -P` must show only `127.0.0.1` and the Steam IPC loopback.
3. `scripts/inventory.sh --verify` on the Steam install must still pass.
