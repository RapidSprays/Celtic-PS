# Protocol notes: build 24928510

Line refs are to `Assembly-CSharp/` in the decompiled tree. **O** observed in code · **SI** inferred · **EC** confirmed by experiment · **U** unknown. Secret-like literals are not reproduced; they appear as `[REDACTED: kind]` with their location.

## 1. Transport (O)
- **UDP**, via an embedded Lidgren.Network gen3 fork. App identifier `"Chat"` (`NetworkManager.cs:271`).
- Fork defaults (`Lidgren.Network/NetPeerConfiguration.cs:415-456`): MTU 1408, ping every 4 s, connection timeout 90 s, handshake resend every 3 s × 5.
- Fork changes are client-side only: the socket address family comes from `NetGameConfiguration` (`NetPeer.cs:657`), `Resolve` filters by family, and `Deny` is a no-op (`NetConnection.cs:858`).
- The wire header looks stock gen3 (5 bytes: type, sequence/fragment bits, 16-bit bit length; `NetPeer.cs:937-947`). `NetMessageType` values match upstream. **SI**: a stock gen3 `NetServer` with app id `"Chat"` should interoperate. Not yet confirmed (**U → test in Stage D/E**).
- No transport encryption (O). Plaintext.

## 2. Primitive encodings (O, `Lidgren.Network/NetBuffer.cs`)
| Write | Encoding |
|---|---|
| `WriteVariableUInt32` | 7-bit varint, high bit means continue (1001-1013) |
| `WriteVariableInt32` | zigzag, then varint (1015-1019) |
| `Write(string)` | varuint byte length + UTF-8. Empty or null → single `0` (1081-1092) |
| `Write(bool)` | **1 bit**, not byte-aligned (794-799) |
| `Write(byte[])` | raw bytes, **no length** (831-841) |
| `Write(uint)` / `Write(int)` | 32 bits |
| `Write(float)` | 32 bits |
| `Write(double)` | 64 bits, little-endian (994-997) |
| `Write(byte)` | 8 bits |

The server must use a Lidgren-compatible bit-level writer and reader. Using Lidgren itself is easiest.

## 3. Message framing
### Client → server (O, `createMessage` at `NetworkManager.cs:4812-4827`)
```
varuint opcode                        (NetworkCommandType)
if sessionID != 0:
    byte[32] HMAC-SHA256(key, UTF8("{sessionID}:{opcode}"))   key = SHA256([REDACTED: static signing string, NetworkManager.cs:249])
    uint32  sessionID
payload…
```
- Pre-login messages build the buffer by hand with **no signature**: VersionCheck (6788-6795), Login/LoginAdvanced (4335-4373), CreateAccount (4380-4413), ClearGuestAccount (4279-4289), AccountOptionsChange (4592-4631).
- The signature only proves the sender knows a static key that every client shares. **Our server can skip verifying it**: read the 32 bytes and the uint32, and check that the session id matches the connection.
- Delivery: `ReliableOrdered` on channel 0 for almost everything (154 call sites). **PlayerMove** uses `ReliableSequenced` (`CPlayableCharacter.cs:498`).

### Server → client (O, `HandleMessage` at `NetworkManager.cs:747-764`)
```
byte flag
if flag == 1: uint32 sessionID   (must equal the client's; otherwise the client sends ClientErrorMessage and drops the message)
varuint opcode
payload…
```
There is no signature on server messages. Unknown opcodes are logged and ignored (`processCommand` 1361).

### Lidgren discovery (world status, O, `CUI_WorldSelect.cs:184-210`)
`DiscoveryResponse` payload is `byte loginAvailable (1)`, `byte status (1 = Available)`.

## 4. Message catalogue for milestones E-a to E-d
Opcode values are in [opcode-map.md](opcode-map.md). C→S means client to server, S→C means server to client.

### VersionCheck (0, C→S, unsigned): `varuint 0, varint protocolVersion(=1), varint balanceVersion(=101)`
### VersionCheckReply (1, S→C): `bool ok, varint protocolVersion, varint balanceVersion` (`6797-6822`)
Reply `ok=true` to skip the balance download.

### Login (10001) / LoginAdvanced (10009) (C→S, unsigned, `4335-4373`)
Fields in order:
1. varuint opcode
2. string username
3. string passwordHash: MD5(salt + password), upper-case hex. Salt is [REDACTED: salt, 4778]; `hashString` 4775-4780
4. byte 1
5. string analytics (`model,gen,sysver,platform`)
6. varint patchVersion
7. string [REDACTED: 32-hex client constant, 4350]
8. string deviceId
9. [Advanced only] varint regType
10. string pushToken
11. string `"EN_UK"`
12. [Advanced only] string accessToken
13. varint INTERNAL_APP_VERSION
14. string countryCode
15. string Application.version
16. varuint64 steamId

Private server policy: accept any password hash and never store or log it, nor the device id or SteamID. The client's own PlayerPrefs hold the credentials (see patch-manifest: separate bundle id).

### LoginReply (10002, S→C, `770-818`)
`byte status`. If Accepted: `uint32 sessionId, byte emailFlag, int32 categorySpender, bool deleting [, string …]`. Exact tail is **U**; read lines 770-818 when implementing.

### RequestCharacterList (12001, C→S, signed, no payload) → RequestCharacterListReply (12002, `4133-4254`)
`byte status`; Rejected → `string reason`; Accepted → `varuint maxSlots, varuint count, varint lastSelectedCharId, count × ShortCharacterData`.

**ShortCharacterData** (`ReadShortCharacterData` 4050-4082):
1. `varuint charId, varuint serverCharId, varint worldId`
2. `string name, varuint gmFlag`
3. `string clan`
4. Appearance (4084-4097): `8 × varint` (race, face, skin, skinCol, hair, hairCol, faceAcc, faceAccCol), `float scale, byte gender, byte showHead, byte showFashion`
5. `25 × varint equipmentTemplateId`
6. `varint zone, varint level, varint fishLvl, varint cookLvl, varint class`
7. `float attackRange, varint attackSpeed`
8. `varint pvpLevel, varint pvpRating`
9. `varint dismounted`

### StartGame (13001, C→S, signed, `4787-4797`): `string name, varint 0, varuint playerId, varint 0, string "uuid"`
### StartGameReply (13002, S→C, `2905-2968`)
`byte status`; Rejected → two strings. Accepted:
1. `varint platinum, varint maxAccountLevel`
2. FullCharacterData (3199-3283):
   - ShortCharacterData
   - `float xp, varint pvpXp, float fishXp`
   - abilities, skills and stats sub-readers
   - `varint level, varint fishLvl, varint cookLvl, float cookXp, varint coins`
   - 25 × `{varint invId, varint templateId, varint qty, byte bound, varint charges, byte fav}` (3285-3326)
   - inventory, HUD slots, base stats
3. `float x, y, z, float dir`
4. full quest list
5. `varint n × varint teleportId`
6. clan block
7. `varint n × varint zoneId`
8. `varint unused`

**U**: exact layout of the abilities, skills, stats, inventory and HUD sub-readers. Map these field by field when implementing E-c.

### Zoning and movement
- **EnteringNewZoneUpdate** (13005, S→C, `2259-2307`): `float x,y,z, float _`, quest id lists, `varuint n × CBasicPC`, friend list.
- **AllZoningMessagesSent** (12017, S→C, `1254-1262`): no payload.
- **CharacterZoningIn** (12014, S→C, `2250`): one CBasicPC. **CharacterZoningOut** (12015): `varuint id`.
- **CBasicPC** (`CBasicPC.cs:121-194`): ShortCharacterData-like fields up to pvpRating (gmFlag ≠ 0 prefixes "+GM "), no dismounted field, then `float x,y,z, float direction`.
- **PlayerMove** (13003, C→S, signed, ReliableSequenced, `CPlayableCharacter.cs:470-505`): `double serverTime, float x,y,z, float yAngle, float speed`.
- **BatchZonePositionUpdate** (13004, S→C, `2162-2217`): `double serverTime, varint n × {varuint id, float x,y,z, float yAngle, byte stopped}`.
- **CorrectPlayerPosition** (12013, S→C, `2141-2160`): `varuint zone, varint charId, float x,y,z, float angle`.
- **Monsters**: BatchZoneMonsterUpdate (13006, `2848-2875`, varuint count prefix) and ZoneMonsterAppeared (13008, `2408-2430`). Each entry is `varuint serverId, varuint templateId, float x,y,z, float dirX,dirY, varint opinion,hp,energy,convId,level, float aggroRange, float scale`. ZoneMonsterDisappeared (13007): `varuint`.
- **mobPatrolUpdate** (23008, `2783-2802`), NewEntitiesOfInterest (`2432-2449`), EntityChangedDirection (`2804-2829`).

### Chat
- **GeneralChat** (17001): C→S `varint channel, string message` (`6024-6040`); S→C `varint channel, string, string, varint textId` (`1015-1030`).

## 5. Client-side checks the server should know about (O)
- **ClientErrorMessage** (22001, C→S): one string. Sent on session mismatch, missing item templates and parse failures. Log these: they are a useful debugging signal.
- **ClientChecksumFailure** (22003): dead code, never sent.
- Sub-protocols (party, clan, trade, mail, PvP, …) put a sub-opcode `varint` first in the payload, with their own enums (see the handler classes in `client-architecture.md`).

## 6. Unknowns to resolve
| # | Unknown | How |
|---|---|---|
| U1 | Stock Lidgren `NetServer` compatibility with the fork | Stage D experiment: fake client built from client-fork semantics against our server; then the real client (E-a) |
| U2 | Exact `LoginReply` tail | Read `NetworkManager.cs:770-818` while implementing |
| U3 | FullCharacterData sub-reader layouts | Map `3199-3326` and the sub-readers before E-c |
| U4 | Minimum message set the client needs before it enters the world (beyond StartGameReply + zoning) | E-c iteration with Player.log |
| ~~U5~~ | **Resolved (O)**: env `Test` (0) uses the world `ip` field directly (`NetworkManager.cs:5693`), turns on `"db …"` debug logging to Player.log (`CUtilities.cs:248-266`, `LogManager.cs:9-25`), shows DPS/HPS counters (`GameHUD.cs:711`), and sends client exceptions and crash reports to the game server as ClientErrorMessage (`SimpleCrashReporter.cs:20-46`). No other effects. **The private client uses Test.** | done |
