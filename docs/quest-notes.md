# NPCs, conversations and quests: build 24928510 (researched 2026-10-12)

Paths are relative to `decompiled/24928510/Assembly-CSharp/`; NM = NetworkManager.cs. Types: vi = zigzag varint, vu = varuint, i32 = fixed 32-bit, bit = 1-bit bool, byte, f32, str. Status byte: 0 = Rejected (then `str` reason), 1 = Accepted.

## NPC positions
- **No client file gives NPC positions.** Map icons come from live mobs. `SpawnIconInfoCollections` is `spawnPointId → IconsInfoPerMob[mobTemplateId] → [{Type, Param1..4}]`, looked up by the mob's server id, with no position.
  - Types: QuestAdvancer 5948, QuestGiver 1923, Token 678, Shop 313, Mail 13, Trainer 10, Banker/Auctioner/Barber/ClanBanker 1 each.
  - QuestGiver Param1 = quest; QuestAdvancer Param1/2 = quest/stage; Shop Param2 = shopId, Param3 = class gate.
- Approximations, best first:
  1. Role props in scene files: Farcrag Castle `-banker_collider` (-39.63,-91.34,-0.11), blacksmith (5.69,-111.23,122.28), stables (12.66,-101.77,98.54), barbershop (39.29,-152.22,102.62), bounty board (-10.33,-100.31,-36.87), mailboxes. Lir's Reach mailbox (-104.85,-5.43,-29.34).
  2. text.db `tutorial_conditions`: type 11 = InRangeOfPosition `r;x;y;z`, type 12 = TargetSelected by spawn id.
  3. Quest Position stages (66).
  4. Stage text and `Location:` comments in conversation XMLs.
  5. Leystones.

## Conversations
- **The client runs them itself.** Clicking an NPC sends nothing; `ConversationHUD.LoadConversation(convId)` runs locally. An NPC needs MobSpawn convId ≥ 0; monsters use −1.
- Trees are TextAssets `cv_*.xml` in the textfiles bundle (893 files; ConversationLookupsTemplates id → Filename). The client evaluates conditions against its quest lists and items, and runs the actions.
- Actions:
  - `QUEST_ADD q` → QuestStart 14001
  - `STAGE_COMPLETE q s` → QuestStageComplete 14003
  - `SHOP n`, `BANK`, `TOKENVENDOR n`
  - `LEARN_ABILITY a coins` → 16005/16006
  - `COOKING n`, `MAIL`, `BARBERSHOP`, `CASTSKILL s cost`, `TAKE_ITEM`, `DELETE_ITEM`, `SERVER_MESSAGE s` (bounty board, 22008/22009)
- Condition `QUEST_AVAILABLE q` → an NPC offers a quest only if q is in the server's available list.
- Spawn → conversation: join the QuestGiver icon quests with `QUEST_ADD`, and the QuestAdvancer (q,s) icons with `STAGE_COMPLETE q s`.

## Quest messages
| Msg | Dir | Layout |
|---|---|---|
| QuestStart 14001 | C→S | `vi questId` |
| QuestStartReply 14002 | S→C | `byte status`; ok: `vi questId, vi nStages, bit _, nStages×{byte taskDone, byte active}`, AvailLists |
| QuestStageComplete 14003 | C→S | `vi questId, vi stageIdx` (talk and hand-in stages only) |
| QuestStageProgressReply 14004 | S→C | `byte status`; ok: `vi mode, vi questId, vi stageIdx`; mode ≤ 0: `vi completionType` [types 0, 1, 8, 12: `vi _, vi remaining`], `vi stageId`; mode > 0: `vi firstNewStage, vi count` (−1 none); then `byte hasInv` [InvWithMoney] |
| QuestCompleteReply 14005 | S→C | `byte status`; ok: `vi questId, vi xp, f32 newXpPercent, vi coins, vi _, vi _, vi n×{templateId, qty}, byte _`, InvWithMoney, AvailLists |
| QuestRefreshMessage 14006 | S→C | FullQuestList + AvailLists |
| DeleteQuest 14007 | C→S | `i32 questId` → reply 14006 |
| RequestAccurateQuestData 14008 | C→S empty / S→C | `vi n×{vi questId, bit tracked, vi nStages, nStages×{vi stageIdx, vi completionSum}}` (also safe to push unsolicited to refresh icons) |
| PreviewQuestRewards 14009 | C→S `bit fromLog, i32 questId` / S→C | `bit fromLog, vi questId, vi xp, str xpText, vi coins, vi xpType, vi itemOrSkillId, bit isSkill, bit isRandomLoot, vi itemQty, vi factionId, vi factionAmt` |
| TrackQuest 14010 / TrackAllQuests 14011 | C→S | `i32 questId, bit` / `bit` |
| QuestsInZoneRefresh 13036 | S→C | AvailLists |

- **AvailLists** = `vi n × vi questId` (available) + `vi n × vi questId` (not yet available). Sent in EnteringNewZoneUpdate (before mobs), LevelUp, and 14002/14005/13036.
- **FullQuestList** (in StartGameReply) = `vi nCompleted × vi questId` + `vi nActive × {vi questId, vi nStages, bit oldEvent, nStages×{byte done, byte active}}`.
- The server detects Position, kill, loot, cast and mob-set stages itself. Rewards, prerequisites and levels are **server-only** and have to be approximated.
