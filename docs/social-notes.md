# Social notes: build 24928510 (researched 2026-10-13)

Friends, the block list, mail and clans: the wire formats and what the server decides. Our server code is in `GameServer.Social.cs`, `GameServer.Mail.cs` and `GameServer.Clans.cs`.

**Notation:** `vi` = WriteVariableInt32, `str` = string, `u8` = byte, `bit` = Write(bool), which is one bit.

## Friends and blocks
| Opcode | Direction | Fields |
|---|---|---|
| ClientFriendRequest 17004 | C→S | `vi target` |
| ServerFriendRequest 17006 | S→target | `vi requester, str name`; the client shows a 30 s popup |
| ClientFriendReply 17005 | C→S | `vi requester, vi reply` (HW_FRIEND_REPLY: 0 error, 1 accept, 2 reject, 3 timeout) |
| ServerFriendReply 17007 | S→requester | `str name, vi reply` |
| RemoveFriend 17010 | C→S | `vi id` |
| RequestSocialLists 17008 | C→S | empty; sent when the Social screen opens, then every 15 s while it stays open |
| FriendList 17009 | S→C | `vi n × {str name, vi id, u8 online, vi zone, vi level, vi class}`; the same block ends EnteringNewZoneUpdate |
| BlockCharacter 12021 | C→S | `vi id` |
| UnblockCharacter 12022 | C→S | `vi characterId (−1 for the global list), vi serverId` |
| BlockedList 12023 | S→C | `vi n × {vi id, str name}`, then `vi m × {vi id, str name, vi world}` (the cross-world list; always 0 here) |

- **Timeouts:** a popup that times out sends nothing, so the server times friend requests out itself.
- **List updates:** the client never edits its lists itself, so the server sends the whole list after every change. It also pushes it to friends and clanmates when someone logs in, logs out or changes zone.
- **Blocking:** the client never filters anything by its block list. The server drops a blocked player's whispers, zone and world chat, friend, party and clan invitations, trades, duels and mail.

## Mail (MailMessage 17012, `vi subtype` first)
**C→S sub-types:**
- 5 open mailbox
- 6 read `vi id`
- 7 delete `vi id`
- 8 return `vi id`
- 9 take attachments `vi id` (−1 = all)
- 10 send details `str name, vi length, vi gold, vi itemCount`
- 11 send `vi postage, vi recipient, str subject, str body, vi gold, vi n × {vi inventoryId, vi template, vi qty}` (−1s for empty slots)
- 12 closed

**S→C sub-types:**
- 1 list: header `vi maxName, vi maxSubject, vi maxMessage, vi maxItems, vi minLevel`, then `vi n × summary`
- 2 unread notice
- 3 update `u8 1, summary, str body, vi n × {vi id, vi template, vi qty, u8 bound, vi charges}`; `u8 0, vi id` is ignored by the client, so after a delete the server resends the list
- 4 send info: echoes the four fields of sub-type 10, then `u8 ok`, then, if ok, the 7-field social record and `vi postage`. A mismatched echo is ignored by the client.

**Summary:** `vi id, vi senderId (<0 system: no reply), str sender, str subject, vi gold, u8 hasItems, u8 read, vi daysToExpiry`.

**Rules and the UI:**
- Composing needs at least one Postage Stamp (21948). The UI shows a cost of 1 + items, and attachments are limited by the client's `maximum_items_to_trade` (2).
- Ours: postage is recomputed and taken by the server, mail lasts 30 days, and system mail is used for server rewards.

## Clans (ClanMessage 17003, `vi subtype` first)
**C→S sub-types:**
- 0 create `str name`; needs a Clan Banner (6), which the server consumes
- 2 invite `vi target`
- 3 invite reply `vi clan, vi reply, vi inviter`
- 4 promote / 5 demote `vi target, vi rank`. Demoting a Recruit is a kick, and demoting yourself as a Recruit is leaving.
- 10 disband
- 11 message `str` (< 300)
- 12 permissions: 5 ranks × 7 bits

**S→C sub-types:**
- 1 roster, as ClanBlock: `vi id` (−1 = none, nothing follows), `str name, vi n × {social record, vi rank}, str message, 35 bits`
- 8 invite `vi clan, str clan, vi inviter, str inviter`; the popup is 30 s and silent on timeout

**Notes:**
- **Ranks:** 0 Recruit, 1 Member, 2 Noble, 3 General, 4 Leader. Permission bits per rank: view bank, inspect, donate, withdraw, buy slots, set permissions, see logs.
- **StartGameReply:** it also carries a clan block. Its permission reader dereferences the player, so we send −1 there and the roster after zoning.
- **Nameplates:** other players see `(Clan)` from the character summary. A live change is PlayerAppearanceUpdate slot 74 = `str`.
- **Clan chat:** channel 2, with the `"c "` prefix.
- **Clan bank** (CLAN_BANK action):
  - 20001 `u8 transfer [u8 toBank, vi id, vi template, vi qty]`
  - 20002 `u8 includesBackpack, vi status [str error], backpack, bank, slots (vi slots, vi n offers {vi id, vi slots, vi platinum, vi gold, vi charges, str reset}, vi m limited offers), 35 bits`. The m list must be written, because the bits follow it.
  - Log: 20003 / 20004 `vi status, vi n × {str name, u8 donated, vi template, vi qty, str utc "yyyy-MM-ddTHH:mm:ss"}`
  - Buy slots: 20005 `vi offer, vi platinum` / 20006 `vi status [str], vi platinum, vi coins, slots`
- **Ours (invented):**
  - Nobles and above invite; you promote or demote only those below you, and a Leader promoting a General hands over the clan.
  - Default permissions: Recruits view and donate; Nobles also withdraw; Generals also buy slots and see logs.
  - The bank starts at 20 slots, with +10 for 100,000 gold or 50 platinum.
- **Clan Battleground:** zone 14. The client has no clan-battle logic, so the rules are the server's (see the PvP zones).
