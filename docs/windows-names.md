# Windows build: name map

The Windows build of Celtic Heroes (depot 2829821, same Steam build 24928510 and game version 6.15.0 as the Mac build) has an **obfuscated** `Assembly-CSharp.dll`:
- About half the types and nearly all methods have scrambled names, for example `NetworkManager` → `CKGBDDIKPME`. MonoBehaviour class names, Unity-serialized fields and the `Balance.Model` data classes keep their names.
- About 50,000 **decoy methods** are added. They are built from scraps of real code, so real string literals turn up in nonsense places. Searching by string alone finds decoys: P2 matched 35 places instead of 6.
- Real method bodies are left **instruction-for-instruction intact**. For example, Mac `NetworkManager.sendChatMessage` and Windows `CKGBDDIKPME.LCIMANMMKCC` have the same IL apart from names.

The Mac build is not obfuscated. All patches and the client mod are written against Mac names.

## How the map is built (`CH.Patcher/NameMatcher.cs`)
1. **Fingerprint** every method: its opcodes with operands normalised. This assembly's own types and members become placeholders; strings, numbers, branch offsets and references to other assemblies (Unity, .NET) stay.
2. **Seed:** a fingerprint that occurs exactly once on each side, with at least 8 instructions, pairs those two methods. Types whose names survived pair by name.
3. **Walk** paired bodies side by side. The methods, fields and types used at the same instruction are pairs too, which resolves short and repeated methods. Repeated fingerprints are settled inside already-paired types. Repeat until nothing new appears.
4. **Then:** enum values pair by constant value; remaining fields and properties of paired types pair by declaration order when the shapes agree.
5. **Conflicts:** anything that would pair one thing with two is dropped. A gap then shows up as a wrong pinned site count rather than a wrong patch.

**Result for build 24928510:** 10,312 of 11,754 methods paired (87.7%), with none ambiguous. That includes every method the patches and the mod use. It takes about 2 s.

## How it's used
- **Building the map:** `scripts/map-windows.sh` writes `~/CelticHeroesAnalysis/extracted/<build>/names-<windows dll sha256>.json`, outside git. It holds names and metadata tokens only, no game code. `CH.Patcher` embeds it, and `make-friend-kit.sh` builds it when missing.
- **Patching:** `AssemblyPatcher` takes a `NameMap` (identity for Mac). Patches name their targets in Mac names. Site counts count only real methods (in the map), so they are the same as on Mac. URL and field rewrites also cover decoys, so no live address is left anywhere.
- **The client mod:** `CHPrivateMods.names.txt` is written next to the mod on Windows. Every reflection lookup goes through `CHPrivateMods.R` (`Field`, `Method`, `Property`, `GameType`, `Enum`, `EnumName`), which walks the type hierarchy and translates names.
- **Tests:** `WindowsBuildTests` (when the depot is present) check four things:
  - the patch set's site counts on the Windows build;
  - no live URLs left;
  - the matcher reproduces the shipped map;
  - each of the 28 members the mod looks up resolves in the Windows build.

## When DECA updates the game
1. Download both depots again.
2. Re-pin the Mac build.
3. Run `scripts/map-windows.sh`.
4. Add the new hashes to `KnownBuilds.cs`.
5. Run the tests.

The matcher needs no hand edits unless the obfuscator's behaviour changes.
