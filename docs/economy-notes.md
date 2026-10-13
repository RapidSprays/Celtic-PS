# Economy notes: build 24928510 (researched 2026-10-12)

Paths relative to `decompiled/24928510/Assembly-CSharp/`. Types: varint = zigzag `WriteVariableInt32`, varuint, byte, bool (1 bit), float, string. `readInventory` (NM:3329-3402) = `varint n × {varint invId, varint templateId, varint qty, byte bound, varint charges, byte favourite}`; InventoryWithMoney = `varint coins` + readInventory.

## Server-only (must be approximated)
Item prices (server ItemTemplate BuyPrice/SellPrice), shop stock (server Shop: MobTemplateId, Stock[], SubtypeMultipliers), loot sets/tables/chances, mob level and coin ranges, item spawn points, token-vendor stock and costs (cooking ingredients), how recipes are learnt.

## Merchants (CH_ShopNetworkHandler.cs)
- Conversation XML action `SHOP <shopId>` → RequestShop **18002** C→S `varint npcTemplateId, varint shopId, distinctTemplateList(varint n × varint templateId)`.
- PurchaseItem **18004** C→S `npcTemplateId, shopId, templateId, qty, distinctTemplateList`. SellItem **18005** C→S `npcTemplateId, shopId, templateId, invId, qty, distinctTemplateList`.
- RequestShopReply **18003** S→C: `byte status`; Accepted: `byte hasHeader [varint npcTemplateId, varint shopId]`; Rejected: `string error` and stop ("shop not found" closes the screen). Then InventoryWithMoney; `varint n × {shopEntryId, templateId, stockQty}`; `varint n × {templateId, buyPrice, sellPrice}` (send prices for the stock and every template the client listed). Entries with buyPrice 0 are hidden. No separate buy/sell reply: re-send 18003 Accepted with hasHeader 0 (inferred). No buyback, no repair.
- **Our own fixed-price stock:** `server/content/shop-extras.json` adds items to chosen shops at exact prices (`ShopCatalog.LoadExtras`), on top of the generated stock. Currently Glamora the Warrior, the skill merchant in Highshore Village (Lir's Reach; all seven of her class shops), sells Sigils of Health and Sigils of Energy Crates for 100,000 gold each. She is the only Highshore NPC whose conversation opens a shop. Edit the file and restart the server to change it; no re-patch is needed.
- Conversation XML (textfiles bundle, 197 `cv_*` files) has 686 `SHOP n` actions with labels and conditions (CLASS, LEVEL_GREATER_THAN, FACTION_GREATER_THAN, QUEST_*). SpawnIconInfoCollections has 313 `Shop` icons (Param2 = shopId, Param3 = class gate). NPC name tags: `[Armour Trader]`, `[Item Trader]`, `[Banker]`, ...

## Loot
- WonCombat **13017**: `varint mobTemplateId, mobServerId, killerId, levelType, xp, luckyXp, float xpPercent, varint coins, luckyGold, varint n × {templateId, qty}, InventoryWithMoney`. Loot goes straight into the inventory: no corpse window, no need/greed.
- Ground items: ItemSpawns **15009** `varint n × {spawnId, templateId, float x,y,z}`, ItemRespawn 15010 (one), ItemDespawn 15011 `spawnId`. PickupItem **15013** C→S `varint spawnId` (client requires ≤1 m). PickupItemReply **15016** `byte status; rejected: string; else varint templateId, qty, InventoryWithMoney`.
- Item fields for drop pools: `EquipSlot`, `Subtype`, `RequirementTypes[{LevelRequirement, Amount}]` (10,242 items, levels 0-230), `ClassTypes`, `StarLevel` (−1 normal), `Tradeable`, `ImportantItem`, `Stackable`, `MaxCharges`, `IconColour` (rarity tint). No item level, rarity or droppable flag.

## Bank (personal)
- Conversation action `BANK` → BankingMessage **18006** C→S `byte isTransfer; if 1: byte toBank, varint invId, varint templateId, varint qty` (0 = open/refresh).
- BankingMessageReply **18007** S→C `byte includesPlayerInv; [readInventory player (no coins)]; readInventory bank`. Capacity = `soloBankItemLimit`, the last varint of the stats block. No gold in the bank.

## Trade, TradeMessage 17013 (CH_TradeNetworkHandler.cs), varint sub-op first, ids are character ids
- C→S 0 `{varint otherCharId, byte isReply}` → S→C to the other player 1 `{varint requesterId, byte silent}`. Accepting re-sends 0 with isReply 1.
- S→C 10 `{varint otherCharId}` opens the window (send to both).
- C→S 2 `{varint otherCharId}` cancels → S→C 3 `{varint charId}` to the other.
- C→S 4 `{byte isMoney; 0: byte toTrade, varint templateId, invId, qty; 1: varint coins}` → S→C 5 `{byte status; 0: string; then 3 × InventoryWithMoney: own inventory, own offer, their offer}` (resets ready flags).
- C→S 6 `{byte ready}` → S→C 7 `{byte}` to the other. C→S 9 `{varint otherCharId}` confirm; when both confirm, S→C 8 `{3 × InventoryWithMoney}`.

## Crafting (cooking) and token vendors
- CraftingTemplates (185): RecipeName, Success/Critical/Master Min/Max chance, MinAbility, Difficulty, RecipeLevel, RecipeXp, Failure/Success/Critical/MasterItemReward, CraftingTime, CostId (server-only ingredients), OptionalID (booster).
- Conversation `COOKING <vendorId>` → CraftingMessage **23001** `varint 1` and TokenVendorMessage **23006** `varint 1, varint vendorId`.
- S→C CraftingMessage sub-op is a **varuint**:
  - 2 `{varint n × {recipeId, bool fav}, varint cookingLevel, float progress}`
  - 4 `{varint resultTemplateId, varint cookingLevel, float progress, varint xp}`, sent after CraftingTime (+7 s when cookingLevel ≤ RecipeLevel+20).
- C→S crafting:
  - 3 `{recipeId, optionalInvId|-1}`
  - 5 `{bool}` interrupt
  - 6 `{recipeId, bool fav}`
- TokenVendor S→C `varint 2 {byte hasId [vendorId], varint n × {stockId, templateId, qty, costId} (n=0: string error), varint m × {costId, varint k × {templateId, qty}}}`. Purchase C→S 3 `{vendorId, stockId, qty}` → S→C 4 `{byte ok; 0: string; else varint newInvId, InventoryWithMoney}`.

## Other
Stack size: only a `Stackable` flag. The inventory is unlimited (`fastTravelItemLimit` only gates fast travel). Money is a single varint shown as gold. Platinum goes through CurrencyInfo 15021 `byte has; varint platinum`.

## Platinum item shop (ItemShop 18001, CH_ItemShopNetworkHandler)
- C→S `vi 0` asks for the catalogue (sent when the shop screen opens); `vi 2, vi stockId, vi count` buys.
- S→C catalogue: `vi 1, u8 status (0 closes the shop), vi n, n × (vi stockId, vi templateId, vi quantity, vi price, str name, str description, vi category)`.
  - Categories: 0 Gold, 1 Items and Upgrades, 2 Potions and Scrolls, 3 Elixirs, 4 Platinum, 5 Bundled Offers.
  - The template must exist in the client data (else a NullReferenceException), and each category holds at most 128 entries.
- S→C purchase: `vi 3, u8 success, str chat text, vi platinum, [success: InventoryWithMoney]`. Then CurrencyInfo 15021: `u8 1, vi platinum`.
- PlatinumBundleInfo 19005 with no cash bundles: `vi 0, u8 0 (no buy-platinum buttons), vi 0 offers`. A nonzero bundle count smaller than the client's widget array throws.
- Ours: `server/content/item-shop.json`, with prices our own. Bags of Gold (14) become 100 gold each and Auction House Slots (93) add a listing slot, both on receipt.

## Factions (FactionsMessage 23002, server → client only)
- `vu type`. 1 currentFactions: `vi n × (vi faction, vi points)`. 3 pointsChanged: `vi faction, vi points, vi delta`. 4 levelChanged: `vi faction, vi points, u8 up, str name, str title`.
- Dropped before CGame.Ready, so it's sent after zoning.
- The client works out levels itself (FactionTemplate.GetLevel, mirrored in `FactionInfo.LevelAt`). A faction missing from the list fails most FACTION_GREATER_THAN tests, so 4 Liches, 5 Reavers and 6 Hiddenfolks are always sent.
- The point sources are server-only. Ours are in `server/content/factions.json`:
  - Liches quests (3002-3006, 3038) and Reavers quests (3007-3011, 3037, 3088) each give +1500 to their own side and −750 to the other.
  - Valley of the Ancients quests and Ancient bounties give Hiddenfolks points, and so does each kill in the Valley (+5).

## Bounty boards
- The boards are NPCs with conversation 440 (normal) or 800 (Ancient), filename `bb`, of type Dynamic.
  - gen-world places them: the board (spawn 2653) beside Robert the Bruised in Farcrag Castle, and the Ancient board (13311) at the Valley hub.
- Flow:
  1. Opening a board sends ReqServerDynamicData 22008 `str "BB-DATA"` / `"ABB-DATA"`.
  2. The reply is RecServerDynamicData 22009: `u8, str "BB-DATA~token~maxToday~maxConcurrent~numFree~numPaid~minLevel~Name^quest^cost^status~..."`.
  3. Statuses are 0 NotReady, 1 Purchasable, 2 Available, 3 Started, 4 Completed, 5 HandedIn, 6 Dropped.
- Accepting is QuestStart. Abandoning is BountyBoard 23004: `u8 ancient, vi 1, vi quest`.
- "Get More Bounties" runs DELETE_ITEM on the pledges, then sends `"BB-BUY"`.
- The client never asks again, so the board is pushed after every start, hand-in, drop and purchase.
- Ours (invented):
  - The day's board has 3 free bounties and 2 more for 1 Bounty Pledge each (217; Ancient 71040), one at a time.
  - The minimum level is 10 (Ancient 180).
  - Bounties are drawn per character per UTC day from those whose kill targets spawn on our server, pitched at that spawn level, within −12/+3 of the player's level when possible.
  - A bounty in progress carries over to the next day.

## Auction house (AuctionHouse 23003)
- The auctioneer's actions are VIEWAUCTION (screen 27), BIDSAUCTION (28) and MANAGEAUCTION (26).
- C→S sub-ops:

  | Sub-op | Name | Fields |
  |---|---|---|
  | 0 | cancel | `vi id` |
  | 1 | list | `vi template, vi inventoryId, vi qty, vi charges, vi duration 0/1/2, vi characterId, vi startBid, vi buyout (−1 none)` |
  | 2 | bid | `vi id, vi characterId, vi amount` |
  | 3 | buyout | `vi id, vi characterId` |
  | 4 / 6 | search / my bids | `vi filter, str query, vi sort, vi dir, vi minLevel, vi maxLevel, vi page (from 1), vi perPage` |
  | 5 | my listings | `vi characterId, vi page, vi perPage` |

  - The query string is `"{words} {+slot} {+Class}"`.
- S→C: `vi type` (AHServerMessageType 0-19).
  - Text types send `str`.
  - 10 and 12 send `vi total, vi n, listings`. 11 sends the same plus `vi used, vi extra slots`. 16 sends `vi used, vi extra`.
  - A listing is `vi id, vi template, vi qty, str "d.hh:mm:ss" left, str seller, vi current bid (start bid if none), str bidder ("NONE"), vi buyout`.
- Rules (client params and the auctioneer's words): 12/24/48 h, deposit 1/2/4% of the start bid (min 50, returned unless cancelled), next bid ≥ 5% up, 5% fee, 5 free slots up to 30 with bought slots.
- Ours:
  - The listed item and the highest bidder's gold are held.
  - Expired listings settle within 15 s.
  - Proceeds and items go straight into the backpack, online or not (there is no collect message).
  - Items come back without their charges.
