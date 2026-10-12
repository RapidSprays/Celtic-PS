# Private-client patch manifest: build 24928510 (APPLIED 2026-10-12)

These patches apply **only** to the pinned copy at `~/CelticHeroesAnalysis/client/24928510/`. They are applied by `server/tools/CH.Patcher` (Mono.Cecil):
```bash
cd server && dotnet run --project tools/CH.Patcher            # patch (add --http-port N to use another port)
dotnet run --project tools/CH.Patcher -- --dry-run            # check without writing
dotnet run --project tools/CH.Patcher -- --inspect out.dll    # write a patched DLL elsewhere for decompiling
```

How the patcher protects the inputs:
- It always starts from pristine inputs, hash-verified against `inventories/sha256.txt`:
  - the snapshot's `Assembly-CSharp.dll`
  - `client/<build>/pristine/globalgamemanagers`, backed up on the first run
- Every patch has a **pinned site count**. A different client build fails the run rather than leaving the copy half-patched.
- After patching it re-signs the copy ad hoc and writes `client/<build>/patch-state.json` with hashes and the patch list.
- The Steam install is refused as a target and never touched.

| # | Target | Change | Sites | Why |
|---|---|---|---|---|
| P1 | `Info.plist` `CFBundleIdentifier` | → `com.onethumbmobile.celticheroes.private` | 1 | Gives the private client its own PlayerPrefs and persistentDataPath. The live prefs hold `USER`, `PASS` and `REFRESH_TOKEN`; the private client never sees them. |
| P2 | `EnvironmentSetup`: every `ldfld` of `remoteEnvironmentRequestUrl`, `defaultPatchServerUrl`, `defaultBalanceCdnUrlFormat` and the `local*` fields | → localhost constants (`http://127.0.0.1:<port>/patchserver/…`) | 6 | The single root endpoint plus all fallbacks, so the client can never fall back to production. |
| P4 | Live URL literals (`CUtilities` server status ×2, `XMLPopUpUGUI` support link) | → `http://127.0.0.1:<port>/external/<host><path>` | 3 | Removes the remaining DECA and celtic-heroes.com endpoints. |
| P4b | `NetworkManager.CheckInternetSettings` `"http://www.google.com"` | → `http://127.0.0.1:<port>/ping` | 1 | Full network isolation (the server answers 200). |
| P5 | `CController.Awake`: `SharedTech.Services::Init(...)` | call removed (arguments popped) | 1 | No Firebase analytics, Crashlytics or FCM. |
| P6a | `CController.Start`: `UnityServices.InitializeAsync()` | removed | 1 | No Unity Gaming Services. |
| P6b | `CController.Init` coroutine: `ZendeskSdk.InitializeAsync(...).ContinueWith(...)` | both calls removed | 1 | No Zendesk SDK (support and analytics). |
| P7 | `InAppPurchaseManager..ctor`: `UnityPurchasing.Initialize(...)` | removed | 1 | The store never initializes, so no purchases. The manager object still exists, which avoids null references. |
| P8 | `globalgamemanagers` (UnityConnectSettings) | 7 Unity service URLs (analytics config and events, crash, performance, dashboard) → same-length `http://127.0.0.1:<port>/u/…` | 7 | Unity engine telemetry starts natively before any script. Same-length rewrite, so no re-serialization is needed. |

Patch IDs P3 and P6 from the draft were folded into P2 and P6a/P6b.

**Not patched:**
- Steam (`SteamManager`, ownership, `RestartAppIfNecessary`): no DRM bypass.
- ACTk: no detectors run.
- ObscuredTypes (in-memory only).
- Client HMAC signing: the server ignores it.

## IL safety
Edited methods are expanded with `SimplifyMacros` before editing and re-optimized with `OptimizeMacros` afterwards. The first real-client run hit an `InvalidProgramException` in `EnvironmentSetup.OnEnvironmentRequestCompleted`, where a short branch had been pushed out of range. That is fixed, and `PatcherTests.Edited_methods_have_valid_branch_targets_after_round_trip` guards against it.

## Isolation check (experimentally confirmed 2026-10-12)
Patched client running at the main menu: `lsof -a -p <pid> -i TCP` shows only `127.0.0.1:5555` (our server) and the Steam IPC loopback.
- The server log shows Unity telemetry POSTs arriving at `/u/…` and being refused.
- The environment, privacy, patch-level and server-status requests are all served locally.
- `scripts/inventory.sh --verify` on the Steam install still passes.

## P9: Windows company name (friend copies on Windows only)
`globalgamemanagers` PlayerSettings.companyName `One Thumb Mobile Ltd` → `CelticHeroes Private`, a same-length, length-prefixed rewrite. Unity on Windows keys registry PlayerPrefs and `persistentDataPath` on the company name, so the private copy never reads the live game's saved login. It's the Windows counterpart of P1, the bundle id. Expected: 1 site.

## P10: "Max" button on the skill popup
- **Code:** `CSkillPopup..ctor` gets a call at its end, inside try/catch, to a new private method `CHPrivate_AttachMax`. That method calls `CHPrivateMods.SkillMaxButton.Attach(this)`.
- **Mod assembly:** `CHPrivateMods.dll` is built from `server/tools/CH.ClientMods`, embedded in CH.Patcher, and copied next to Assembly-CSharp.dll.
- **What the mod does:**
  - copies the Upgrade button, labels it "Max" and places it above;
  - on tap, calls the popup's own `SetLevelToUpgradeTo(int.MaxValue)` (which clamps to the rank cap and free points) and then `ConfirmButtonPressed()`;
  - keeps its visibility in step with Upgrade.
- **Server side:** it accepts multi-rank BuySkill requests and clamps again.
- **Failure safety:** if the mod were missing or failed, the try/catch leaves the popup unchanged.
- Expected: 1 site.

## P11: custom items
- **Hook:** the start of `CItemManager.Init(BalanceClient)` calls `CHPrivateMods.CustomItems.Register(balance)`, guarded like P10.
- **What it does:** for each entry in `server/content/custom-items.json`, the mod copies the base `ItemTemplate` (MemberwiseClone) under the new ID, Name and Description, and adds it to the client's template dictionary before the shop cache is built. Model, icon, class rules and animations come from the base.
- **Where the list comes from:** CH.Patcher writes it next to the mod as `CHPrivateMods.items.txt`. The server applies the same file (`Game/CustomItems.cs`).
- **Skill ranks, same hook:** `CHPrivateMods.SkillRanks.Extend` first extends every player skill's `Levels` and `PvpLevels` (ability ≥ 0, more than one rank) to 260 ranks and clears their `MinLevel`. That removes the rank caps without a bounds error in `HUDSkillButtons` / `NetworkManager` (both index `Levels[rank]`). The rule matches the server's `Game/SkillLevels.cs`.
- Expected: 1 site.

## P12: rainbow weapons
- **Hook:** `CMD5_Model.checkDownloadComplete`, right after `m_isFullyLoaded = true`, calls `CHPrivateMods.RainbowWeapons.OnModelLoaded(this)`, guarded.
- **What it does:** if the model's visible weapon is an item with `effect: "rainbow"`, each weapon mesh gets a `RainbowCycler` that cycles hue every frame:
  - the body through the game's own `PrimaryColour`, so fades still work;
  - the additive glow mesh through `_TintColor`.
- This is a script-driven effect on the existing shaders. A real new shader would need the matching Unity Editor (6000.3.10f1) to compile it per platform.
- Expected: 1 site.

## P13: custom item models
- **Hook:** the start of `CModelIDTemplateManager.Init(BalanceClient)` calls `CHPrivateMods.CustomItems.RegisterModels(balance)`, guarded. The model manager initialises before the item manager, so models can't wait for P11.
- **What it does:** for items whose JSON has a `model`, the mod copies the base weapon model (`EquipmentModelIDTemplates[Weapon][base]`) under the new model id. It appends particle components built like the game's fashion particles (`ModelIDParticleComponent`: effect id, bone, offset, scale). The item's `ModelID` points at it. The client then attaches the particles itself, as it does for fashion.
- Expected: 1 site.
