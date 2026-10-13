# Content systems notes: build 24928510 (researched 2026-10-13)

This covers achievements, leaderboards, tarot, the living calendar, PvP zones and tutorials: their wire formats and what the server decides. The server code is in `GameServer.PvpZones.cs`, `Leaderboards.cs` (served by `PatchServer`), `GameServer.Tarot.cs`, `GameServer.Calendar.cs` and `GameServer.Tutorials.cs`.

**Notation:** `vi` = WriteVariableInt32, `vu` = WriteVariableUInt32, `vl` = WriteVariableInt64, `bit` = Write(bool), which is one bit, `i32`/`i64` = fixed width.

## Achievements: nothing to port
- AchievementUpdate 13044 (`vi, vi, str, str, float`) is read and thrown away by the client.
- The client never sends AchievementUpdateSuccess 13045.
- The achievement templates exist only in BalanceServer.
- LeaderBoardUpdate 13042/13043 are dead in the same way.

## Leaderboards (HTTP JSON, not the game connection)
- **Where:** the Social screen's stats page fetches `{stats URL from the world list}` + page. We serve it at `http://host:port/stats/`.
- **Parsing:** Unity JsonUtility, so field names must be exact (we use PascalCase, no camelCase policy) and each container is an object.
- **Blocking:** requests are synchronous and block the client for up to 50 s, so answers must be quick.

| Page | Query | Reply |
|---|---|---|
| `GlobalPage.aspx` | (none) | `{WorldDictionary:[{Key,Value}], ClassDictionary:[…]}` |
| `RankingPage.aspx` | `?type=ranking_*&playerWorldId&charId[&filterWorldId][&classId][&clanId][&sortCol][&sortDir][&page][&numRecs]` | `{RankingDataList, PlayerRankingData (Rank 0 = not ranked), TotalResults, ExtraColumn, IsDataFromCache}` |
| `TheClanPage.aspx` | `type` = `total_levels_30d` / `total_members_7d` / `total_members_30d` | `TheClanContainer` |
| `BossRanking.ashx?board=general/detailed/fight/individual` | (none) | boss containers; ours are empty, since no fight records are kept |
| `TheClanDict.aspx` | (none) | `{ClanDictionary:[{Key,Value}]}` |

- **RankingData fields:** `Index, Rank, WorldId, WorldName, CharacterId, ClanId, ClanName, Name, Level, ClassId, ClassName, RankingValue (double)`.
- **Our values:**
  - Character columns: level, coins, fishing, cooking and PvP rank.
  - Running totals in `character_stats`: kills, deaths, heal, melee_hit, skills_hit, quests, star_1..6 (kills by monster stars), pvp_kills, pvp_deaths.
  - Two ratios: PvE and PvP kills per death.

## Tarot (TarotGameMessage 21030), the Fortune Teller's pairs game
- **C→S:**
  - `vi 1` decks
  - `vi 2, vi deck` start or continue
  - `vi 3, vi deck, vi board 0..17` flip
  - `vi 4, vi deck, bit accept` extra moves
- **S→C:**
  1. Decks: `vi n × {vi id, str name, vi assetsTemplate, vl nextRefreshTicks, vi attemptsLeft, vi costItem (−1 free), vi costQty, bit inProgress, bit rewardOnLoss, vi extraMoves, vi extraMovesGold}`. The refresh must lie in the future, or the client asks every frame.
  2. Start: `vi deck, bit ok, [str error] | bit new, [bit extraBought, 18 × vi face (−1 down)], vi moves, [new: inventory without money]`.
  3. Flip: `vi deck, vi board1, vi face1, bit second, [vi board2, vi face2, bit match, bit finished, bit won, vi moves]`.
  4. Extra moves: `bit ok, vi moves, InventoryWithMoney | str error`.
- **Rules (client and NPC):** 18 cards, 9 pairs, a move is two flips, 18 moves. Prizes are mailed. Out of moves, the client offers extra moves once.
- **Ours:**
  - Minor Arcana: free, daily.
  - Major Arcana: weekly, for 1 Tarot Token (the NPC names a seasonal token instead).
  - Premium Arcana: 5 a day, for 1 Tarot Token, with a Bad Luck token on a loss.
  - Prizes are the Arcana Bags. 6 extra moves cost 5,000 gold.
  - Tarot Tokens come from the item shop and the calendar.
  - The Fortune Teller (142359) is in Farcrag Castle.

## Living calendar (LivingCalendarInfo 21016 / LivingCalendarAction 21017)
- **C→S** (`u8 action`): 0 request, 1 claim `vu track`, 2 redeem missed `vu track`.
- **S→C** (`u8 type`): 0 full, 1 track updates, 2 error `str`, 3 reward granted.
  - **Full** (type 0, which **opens the screen by itself**): `vu n × {vu id, u8 type, str name, str desc, vi day+1, vi _, vi v3, vi missed, vl _, vl nextReward (ToBinary), vi maxTokens, vi usedTokens, vi n × {vi _, u8 0 item, vi template, vi qty, bit premium}}`. The client treats the day as claimed when `missed + v3 < day`.
  - **Update blocks** use fixed width: `vu track, i32 day, bit claimed, i32 missed, i64 nextTicks, i32 usedTokens`.
- **Ours:** one "Daily Rewards" track (`server/content/calendar.json`) with 28 rewards, one claim per UTC day and no missed-day penalty. It is offered at login when today's reward is waiting.

## PvP zones and progress
- **Duels and hostility:** PVPMessage 13027 is duels only. Hostility to players is entirely the PlayerHateList (17011: `vi n × vu id`).
- **Ours:**
  - The Global Arena (zone 8, through Farcrag Castle's exit) is free-for-all except your party.
  - The Clan Battleground (zone 14, via `.battleground` for clan members) is everyone outside your clan.
  - Deaths there are real. Arena respawns are at its entrance.
- **Rank and rating:**
  - Rank (0-29, class titles) and rating (0-12, Pathetic … Invincible) travel in the character summary and in AppearanceOnlyUpdate 12009. The client announces any change, so the real values must always be sent.
  - Others see them through PlayerAppearanceUpdate slots 72/73. The stats page bar is stat 31 (percent × 100).
  - Ours:
    - Kill experience: 20 + 4·clamp(victim level − killer level + 5, 0, 20) + 5·victim rank.
    - Next rank: 100·(rank+1)^1.3.
    - Rating: 4 + 4·log2((kills+5)/(deaths+5)), clamped to 0-12.
- **AreaBattleUpdate 23007** is an animation snapshot of who attacks whom, not PvP; it isn't used.

## Tutorials (Tutorial 13037, FirstTime 13038)
- **Tutorial 13037:**
  - S→C `vi 1, vi id` starts tutorial 91-94; `vi 2, vi id` ends one. Ending the running one crashes the client, so we never send it.
  - C→S `vi 3, vi id` marks a tutorial complete.
- **FirstTime 13038:**
  - S→C `vi id` makes a hint available. 100 starts the chain; 101 destroys it.
  - C→S `vi id` marks a hint done.
- **Ours:** below level 20 we send the unfinished tutorials and hints plus 100, once per session. Completions are kept. `.tutorial off` stops it.
