# Porting status

What's still to port of the client's feature set, from the client message list (218 message types) and its UI. Each item is ticked when it's done and tested. "Invented" means DECA keeps that system's rules server-side, so ours are our own (see docs/decisions.md).

## 1. Combat
- [x] Friendly skills on other players (heals and buffs), and group skills on party members within the skill's radius
- [x] Area skills: everything within the template's AreaOfEffect radius around the target, at most 10
- [x] Ability progression: AbilityUpdate; gains invented
- [x] Cast interrupts from the skill rank's ChargingProtection; monster tagging (CombatEntityLocked: owner/party get the kill)
- [x] Monster special attacks: elites and bosses, with a themed client skill when one matches (invented)

## 2. Character and quality of life
- [x] Delete character (logout was already handled)
- [x] Barbershop: free; others see the new look
- [x] Backpack and Bank Expansions, applied on receipt. Bank +10 slots each; backpack +5 carry before over-encumbrance, counted but no effect yet, since encumbrance isn't penalised
- [x] Settings report: phone push settings only, acknowledged and ignored
- Not needed: CustomiseCharacter (the client never sends it); permanent buff type 19 is extra action-bar slots, and no item grants it
- Moved to §4: the block list (it shares the friends list's social data)

## 3. Gathering and crafting
- [x] Fishing: spots on real shorelines, rods, concentration, catches from each zone's fish, fishing XP and levels; numbers invented
- [x] Ground items and pickups: quest pickups near their givers, cooking crops in the fields, regrowth; positions invented
- [x] Cooking: ovens, the client's crafting page, known recipes and recipe books, the client's success curve, cooking XP and levels; ingredient lists invented. Food merchants sell ingredients, rods and recipe books

## 4. Social
- [x] Friends list and block list: mutual friends with the client's request popup; blocks enforced by the server (whispers, chat, invitations, trade, duels, mail)
- [x] Mail: Postage Stamps, gold and up to 2 items, returns, take-all, 30-day expiry, system mail for server rewards
- [x] Clans and clan bank: banner to found, invite, promote, demote, kick, leave, hand over, message, permissions, chat, nameplates, bank with log and slot purchases; rank rules invented
- [x] Clan Battleground (zone 14): clan against clan, entered with `.battleground` (§6)

## 5. Economy
- [x] Auction house: listings, search, bids, buyouts, cancels, expiry; deposits and the 5% fee
- [x] Bounty board: a daily board per character (3 free, 2 for pledges); board selection invented
- [x] Factions and reputation: Liches, Reavers and Hiddenfolks sent at login; point sources invented (`server/content/factions.json`)
- [x] Platinum item shop: our catalogue (`server/content/item-shop.json`), no cash offers; Bags of Gold and Auction House Slots apply on receipt. Platinum comes from chests and GM `.platinum`

## 6. Content systems
- [x] Leaderboards: the client's JSON web service on the patch server, from character columns and running totals (boss boards empty). Achievements: nothing to port, the client ignores them
- [x] Tarot: the Fortune Teller's three decks, attempts, extra moves, prizes by mail; costs invented
- [x] Living calendar: a 28-day daily login track (`server/content/calendar.json`); rewards invented. Seasonal events stay off (seasonal quests closed)
- [x] PvP: Global Arena free-for-all and Clan Battleground (`.battleground`) through hate lists, real deaths, PvP rank and rating; formulas invented
- [x] Tutorial and first-time hints below level 20, completions kept, `.tutorial off`

## Not porting
- Real-money purchases (Steam, app stores), offerwall, "rate us", support tickets.
- The cross-server global layer: 11xxx messages that hand a character between DECA's separate worlds and a shared global server. We run one world, so global zones are ordinary zones.
- Checksums, debug and performance reports.
- Vouchers (redeem codes).
