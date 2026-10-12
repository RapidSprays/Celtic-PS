# Multiplayer notes: build 24928510 (researched 2026-10-12, not yet implemented)

From the decompiled client. NM = NetworkManager.cs. Labels: everything here is **statically inferred** until implemented and tested with two clients.

## IDs
Keep `serverCharId == charId` (the server already does). Runtime messages look players up by serverId; party/friend code mixes both ids.

## Other players in a zone
- **CBasicPC** (`CBasicPC.cs:121-194`) = ShortCharacterData **without** the trailing `vi dismounted`, then `f32 x,y,z, f32 dir`. Needs its own writer (OtherPlayer in World.cs already matches).
- `AddOtherPlayerCharacter` (`CGame.cs:4885`) uses `Dictionary.Add`: **never send a duplicate ZoningIn**; caches are cleared only on zone load. Never send a client its own CBasicPC.
- **CharacterZoningIn 12014** = one CBasicPC; **CharacterZoningOut 12015** = `vu id`. **EnteringNewZoneUpdate 13005** carries the CBasicPCs already present.
- **BatchZonePositionUpdate 13004** (NM:2162): `f64 serverTime; vi n × {vu id; f32 x,y,z; f32 yAngleDeg; u8 stopped}`. It is a **full snapshot**: any player not listed is hidden and its model destroyed. The stamp is the arrival deadline − 1 s (client adds 1.0); send server `NetTime.Now`. Send about 5 Hz with every other player in the zone, excluding the recipient. `stopped = 1` when the last PlayerMove had speed 0. Clients send PlayerMove every 0.2 s while moving, keep-alive every 5 s.
- **CorrectPlayerPosition 12013** and **NewEntitiesOfInterest 21002** snap others but don't change their destination; follow with a batch (`stopped = 1`) for teleports.
- **EntityChangedDirection 13030** turns in place. **ZoneLagList 13034**: `vi n × vi id` (lag icons).

## Appearance
**PlayerAppearanceUpdate 15004** (NM:5774, others only): `vu serverId; vi k × {vi slot; value}`.
- Slots 0-24: `vi` item template id (−1 empty).
- 50-56, 60 gender, 61 race, 70 class, 71 attack speed, 69 level, 72/73 PvP: all `vi`.
- 63/64 show headgear/fashion: `u8`. 62 attack range and 65 scale: `f32`. 67 name and 74 clan: `str`.
- **Never send 57/58/59/66/68.** Only send it to clients that already know that player; the skip parser has no case for slots 20/21/22 and would misread.

**AppearanceOnlyUpdate 12009** is self only.

## Combat seen by others
- **AttackState 16004**: `u8 start; u8 attackerIsMob; vi attackerId; u8 defenderIsMob; vi defenderId`. Broadcast start for players too; the client animates swings. Send stop to the attacker only when the server really ended it.
- **CombatDamage 13019**:
  - messageType 0 PC→mob, 1 mob→PC, 3 PC→PC.
  - Shown only if both models exist on that client.
  - attackType 1 from another caster plays their skill animation.
  - Trailing health updates are **per recipient**: absolute for the recipient's own id, percent (0-100) for other players. dps/hps go only to the attacker.
- **Death**: there is no message; a final damage entry plus health 0 is enough.
- **RespawnPlayer 12012**: health is absolute (others' max is 50, so send 50 to others, real HP to the player). Follow with a percent health update and a batch.
- **OthersStatusEffect 15018**: `vi n1 × {u8 isMob; vu id; StatusList}`, then `vi n2` (send 0).
- **PlayerHateList 17011**: `vi n × vu id` (PvP enemies).

## Emotes, chat, PvP
- **CharacterPlayingEmote 12018**:
  - C→S: `bit loop; vi emote`.
  - S→C: `vu id; vi anim; bit loop`.
  - Echo to the **whole zone including the sender** (the local player waits for it). Sit = 66, looping.
- **Chat 17001** keeps prefixes: `s ` zone (8), `g ` party (1), `c ` clan (2), `w Name, msg` whisper (7).
  - Whisper target: channel 6 with name = sender. Echo to sender: channel 7 with name = target.
- **PvP duels** use PVPMessage 13027, sub-types 0-5 (see the research report).

## Parties and friends
- **PartyMessage 17002** sub-opcodes:
  - 0 invite C→S `vi charId`
  - 1 prompt S→invitee `vi inviterId` (inviter must be known to the client)
  - 2 reply C→S `vi requester; vi reply` (1 accept, 2 reject)
  - 3 result S→inviter `vi responder; vi reply`
  - 4 roster S→each member `vi n × {str name; vi charId; vi zone; vi level; vi class}` (send to all after any change; n = 0 clears)
  - 5 leave C→S
- **Friends**:
  - Requests and replies: 17004/17005/17006/17007; RemoveFriend 17010.
  - RequestSocialLists 17008 is answered with FriendList 17009 `vi n × {str name; vi id; u8 online; vi zone; vi level; vi class}` and BlockedList 12023.

## Two clients on one Mac
- Nothing in the client enforces a single instance (no lock files or mutexes; Unity's single-instance setting is Windows/Linux).
- **Always launch the binary directly from `~/CelticHeroesAnalysis/client/24928510`** so `steam_appid.txt` is in the working directory. `open`/Finder would make Steam restart the *live* game instead.
- Second client:
  ```bash
  cd ~/CelticHeroesAnalysis/client/24928510 && "CelticHeroes.app/Contents/MacOS/Celtic Heroes" -logFile ~/CelticHeroesAnalysis/logs/client2.log -screen-fullscreen 0 -screen-width 1280 -screen-height 720
  ```
- Both copies share PlayerPrefs (use two username/password accounts) and persistentDataPath (start the second after the first reaches login).
- **Server rules**:
  - Sessions are keyed by connection.
  - Never allow the same character in the world twice.
  - Build per-recipient batches and health.
  - Send ZoningIn/Out on enter, leave and disconnect.
