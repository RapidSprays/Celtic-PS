# Boot and connection sequence: build 24928510

Order of everything the client contacts, from launch to in-world. Line refs are to `Assembly-CSharp/` in the decompiled tree. **O** observed in code · **EC** confirmed in Player.log (2026-10-12 launch of the unmodified pinned copy) · **SI** inferred.

| # | Step | Contacts | Ref | Label |
|---|---|---|---|---|
| 1 | `SteamManager.Awake`: `RestartAppIfNecessary(2829820)`, then `SteamAPI_Init` | local Steam client (IPC) | `SteamManager.cs:68-77` | O, EC |
| 2 | `CController.Awake` → SharedTech `Services.Init(OptionalServices.None)`: Firebase analytics, Crashlytics, messaging (FCM token) | Firebase / Google | `CController.cs:740`, `st/SharedTech/Services.cs:68-111` | O, EC ("Token retrieved successfully") |
| 3 | `EnvironmentSetup.Init`: **GET `remoteEnvironmentRequestUrl`** formatted with (desktop internal app version, `"standalone"`), 20 s timeout | `production.ch.decagames.com` | `EnvironmentSetup.cs:84-157` | O |
| 3a | Reply is CSV `env,patchServerUrl[,redeemEnabled[,balanceCdnUrlFormat]]`, with env `-1` Unknown, `0` Test, `1` Beta, `2` Prod, `3` Local. On error, defaults to Prod and the serialized default URLs | — | `EnvironmentSetup.cs:115-157`, `TargetEnvironment.cs` | O |
| 3b | If env is Prod and Steam isn't initialized → `Application.Quit()` | — | `EnvironmentSetup.cs:165-167` | O |
| 4 | `UnityServices.InitializeAsync()` | Unity Gaming Services | `CController.cs:1151` | O |
| 5 | Zendesk SDK init | Zendesk | `CController.cs:1663` | O, EC |
| 6 | Privacy policy request: `PATCH_SERVER_ROOT + privacypolicy.aspx` | patch server | `CController.cs:1206, 1594` | O |
| 7 | IAP init: `UnityPurchasing.Initialize` with `SteamStore`, which GETs `PurchasesList.aspx?steamId=` | patch server | `CController.cs:1409, 1710`, `SteamStore.cs:71` | O, EC (product lookups) |
| 8 | Patch check: GET `PatchInfoRequest.aspx` → bare integer (server patch level). Files are patched only if server > client (143) | patch server | `GamePatcher.cs:133-167` | O, EC ("Server Patch Level : 60 , Client Patch Level : 143") |
| 9 | **World list**: GET `worldlist.aspx?uname=<acct>&regt=<type>&cver=<ver>` (or `?cver=` only) | patch server | `NetworkManager.cs:5537-5639` | O |
| 10 | World select screen: Lidgren `DiscoverKnownPeer` to each world every 5 s. A world is selectable only after a discovery response `byte loginAvailable, byte status(1=Available)` | **game server (UDP)** | `CWorldData.cs`, `CUI_WorldSelect.cs:184-210` | O |
| 11 | Account conflict check: GET `CheckAccountConflictState.aspx?params=` → `0`-`3`. **If it fails, the client never sends a login** | patch server | `AccountConflictHelper.cs:58-91`, `NetworkManager.cs:619-622` | O |
| 12 | Lidgren `Connect(host, port)` with app id `"Chat"` | game server (UDP) | `NetworkManager.cs:362-369` | O |
| 13 | On Connected → **VersionCheck** (op 0). The reply sets the balance version: OK → login; not OK → balance download from `BalanceCdnUrlFormat` | game server; balance CDN | `NetworkManager.cs:735-739, 6788-6822` | O |
| 14 | **Login** (10001) or LoginAdvanced (10009) → LoginReply (10002) gives the session id | game server | `NetworkManager.cs:592-646, 4335-4373, 770-818` | O |
| 15 | RequestCharacterList (12001) → reply (12002). Then CreateCharacter / SelectCharacter / StartGame (13001) → StartGameReply (13002) | game server | `CController.cs:905-913`, `NetworkManager.cs:4133-4254, 2905-2968` | O |
| 16 | Zone load from local Addressables. Zoning messages (12014/12015/12017, 13005). In world: PlayerMove (13003), batch position and monster updates | game server | `CGame.cs:3105-3143`, `NetworkManager.cs:2141-2875` | O |

## World list format (`worldlist.aspx`, `NetworkManager.cs:5580-5639`)
Lines are split on CR/LF, with empty lines dropped:
```
ok
<serverPatchVersion>,<minClientVersion>        # needs minClientVersion <= INTERNAL_APP_VERSION
<statsWebsiteUrl>
<numMaxCharacters>
<N>
<worldID>,<name>,<ip>,<port>,<blocked 0|1>,<private 0|1>,<unused 0|1>,<hostName>,<globalServerZone -1=normal>,<numCharacters>   × N
[<experimentLevel>]
```
- Address resolution (`NetworkManager.cs:5691-5704`): if env is **Test** (0), the `ip` field is used directly. Otherwise `hostName` is resolved by DNS and the first address of the chosen family is cached.
- `setSelectedWorld` uses PlayerPrefs `WORLD_ID`. If that is -1, it picks the first world with id > 1 that isn't global and isn't blocked (`NetworkManager.cs:414-478`).
- `IPNetworkManager.GetHost` takes the host part up to the first `:`. **A patch root URL must include an explicit port** (`IPNetworkManager.cs:63-66`).

## HTTP request details
`CreatePatchHttpRequest` (`NetworkManager.cs:5706-5716`) builds a GET with no redirects, no custom headers and a 60 s timeout. `RemoteCertificateValidationCallback` is strict, but it only applies to `System.Net`. UnityWebRequest uses platform validation. **For a local server use `http://`**, which needs no certificate.

## What the private server must serve (minimum for login)
- **HTTP** at `http://127.0.0.1:<port>/patchserver/`:
  - `PatchServerURL.aspx` (env CSV)
  - `PatchInfoRequest.aspx` (an integer ≤ 143)
  - `worldlist.aspx`
  - `CheckAccountConflictState.aspx` (`0`)
  - `privacypolicy.aspx`
  - `PurchasesList.aspx` (empty)
  - account creation: `accountAvailability.aspx`, `createAccount.aspx`, guest variants, `NameGenerator.aspx`
- **UDP Lidgren** at `127.0.0.1:<port>`, app id `"Chat"`:
  - discovery response
  - VersionCheckReply (OK)
  - LoginReply
  - the character list, creation, start-game and zoning messages
