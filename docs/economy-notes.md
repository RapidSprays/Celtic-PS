# Economy notes: build 24928510 (researched 2026-10-12)

Paths relative to `decompiled/24928510/Assembly-CSharp/`. Types: varint = zigzag `WriteVariableInt32`, varuint, byte, bool (1 bit), float, string. `readInventory` (NM:3329-3402) = `varint n × {varint invId, varint templateId, varint qty, byte bound, varint charges, byte favourite}`; InventoryWithMoney = `varint coins` + readInventory.

## Server-only (must be approximated)
Item prices (server ItemTemplate BuyPrice/SellPrice), shop stock (server Shop: MobTemplateId, Stock[], SubtypeMultipliers), loot sets/tables/chances, mob level and coin ranges, item spawn points, token-vendor stock and costs (cooking ingredients), how recipes are learnt.

## Merchants (CH_ShopNetworkHandler.cs)
- Conversation XML action `SHOP <shopId>` → RequestShop **18002** C→S `varint npcTemplateId, varint shopId, distinctTemplateList(varint n × varint templateId)`.
- PurchaseItem **18004** C→S `npcTemplateId, shopId, templateId, qty, distinctTemplateList`. SellItem **18005** C→S `npcTemplateId, shopId, templateId, invId, qty, distinctTemplateList`.
- RequestShopReply **18003** S→C: `byte status`; Accepted: `byte hasHeader [varint npcTemplateId, varint shopId]`; Rejected: `string error` and stop ("shop not found" closes the screen). Then InventoryWithMoney; `varint n × {shopEntryId, templateId, stockQty}`; `varint n × {templateId, buyPrice, sellPrice}` (send prices for the stock and every template the client listed). Entries with buyPrice 0 are hidden. No separate buy/sell reply: re-send 18003 Accepted with hasHeader 0 (inferred). No buyback, no repair.
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
