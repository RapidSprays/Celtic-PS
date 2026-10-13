# Gathering and crafting notes: build 24928510 (researched 2026-10-13)

This covers fishing, ground items and cooking: their wire formats and what the server decides. The server code is in `GameServer.Fishing.cs`, `GameServer.GroundItems.cs` and `GameServer.Cooking.cs`. The data generators are `scripts/gen-fishing.py`, `gen-item-spawns.py` and `gen-cooking.py`; they write JSON beside the balance file, outside git.

**Notation:** `vi` = WriteVariableInt32 (zigzag), `vu` = WriteVariableUInt32 (plain), `bit` = Write(bool), which is one bit.

## Fishing (ordinary combat messages)
- **What a spot is:** a mob of a FishingSpot template (Race FishingSpot, **Type 1**; 668 templates) spawned with **opinion −15**.
  - With a rod (subtype FishingRod) equipped, the action button is in Fish mode and sends StartAttackingNPC.
  - The selection range is 12 m.
  - If the spot is in combat with someone else, the client prints "Fishing spot claimed by another!".
- **Fish skills:** Gather Fish 5000 and Reel In 8056 have CastTarget Fish. They need the spot to be in combat (EntityCombatStateChanged) and targeting the angler (AttackState spot→player).
- **Damage:**
  - Player→spot CombatDamage is shown as gathering.
  - Spot→player is shown as concentration damage. The real value travels in **PlayerVitalsUpdate 13047 `vi concentration`**.
  - The HUD bars are concentration / max and spot health / max.
  - At 0 concentration the client plays the failure itself.
- **Landing a fish:** WonCombat with **level type 1**. The XP percent there is fishing XP, and each loot item's mesh picks the catch animation (258 rare, 262 boot, 263 sack, 273 shark, 274 eel, 275 ray).
- **Fishing level-up:** LevelUp `vi 1, vi level, lists`.
- **Rods:** a rod's LevelRequirement is checked against fishing level; its DamageTypes are FishingDamage (Beginner's 6 … Grandmaster's 290).
- **Server-only, so ours:**
  - Spot positions: the scenes' MapNodeHelper.GatheringNodes is empty in this build. We use shore points beside large water areas in zones whose fishing quests are set there, at most 8 per zone.
  - Spot health: 25 + 12·level.
  - Bites: 1 + level/20, more when the spot is far above you.
  - Damage: the rod plus half your fishing level, halved 15+ levels up.
  - Catches: the zone's quest fish, about 6% rare (25% at Shimmering spots), plus relics, junk and clams.
  - XP: 10 + 3·level.
  - Curve: 40 + 12·L^1.4.
  - Concentration: 50 + gear, regenerating 10 per tick when not fishing.

## Ground items
| Message | Direction | Fields |
|---|---|---|
| ItemSpawns 15009 | S→C | `vi n × {vi spawnId, vi template, float x, y, z}`; send on every zone entry, because the client clears them on zone unload |
| ItemRespawn 15010 | S→C | one entry, with a grow-in effect |
| ItemDespawn 15011 | S→C | `vi spawnId` |
| PickupItem 13015 | C→S | `vi spawnId`; the client sends it only from within 1 m, after its pickup walk |
| PickupItemReply 13016 | S→C | `u8 1, vi template, vi qty, InventoryWithMoney`, or `u8 0, str reason` |

- **Drawing:** an item is drawn only if its Mesh3dsID is a PropMesh3dConfig with PickupPrefab (67 meshes: herbs, mushrooms, crystals, presents, eggs, crops). The client raycasts down from 100 m above the point, so only x/z matter.
- **Ours:**
  - Every LootItem quest stage whose item has a pickup mesh and no monster source gets 6 points near its quest giver.
  - Crops grow in Lir's Reach's fields; vegetables also grow in Stonevale; King Bolete grows in Stonevale and Shalemont.
  - Each point grows back after 60-120 s.

## Cooking (CraftingMessage 23001 + token vendor 221)
- **Opening:** the oven (mob 923, conversation 706) or the chef (705) run `COOKING 221`. Ovens are placed by gen-world: beside the chef, in Farcrag Castle and in the Tavern.
- **On open the page sends, in order:**
  1. Crafting sub 1.
  2. TokenVendor `vi 1, vi 221`.
  3. Crafting sub 5 (interrupt false).
  - The known-recipes reply must come first, because the vendor reply is filtered by it.
- **C→S Crafting:**
  - 1 known recipes
  - 3 craft `vi recipeId, vi optional item template or −1`
  - 5 interrupt `bit`
  - 6 favourite `vi recipeId, bit`
- **S→C Crafting (sub-op as `vu`):**
  - 2 `vi n × {vi recipeId, bit favourite}, vi cookingLevel, float progress%`
  - 4 `vi resultItem, vi cookingLevel, float progress%, vi xp`
- **Vendor 221 stock:**
  - Each entry is `{stockId, SuccessItemReward, 1, costId}`, and **costId must be the recipe's CraftingTemplate.CostId**, or the client gets a null recipe.
  - The cost lists are the ingredients.
  - After each result the client "buys" the matching stock entry (stock 0 for a burnt dish). We answer `vi 4, u8 1, vi itemId, InventoryWithMoney` without charging.
- **Outcome chance:** the client's own curve, `min + max(0, a − MinAbility) / (that + Difficulty) × (max − min)`.
  - Success uses Cooking Proficiency (38); critical and master use Cooking Mastery (39).
  - The optional booster makes success 100% and doubles the critical chance.
  - The reply comes after CraftingTime, plus 7 s unless the cook is 20+ levels above the recipe.
- **Ours:**
  - Ingredient lists come from recipe names and kinds (`gen-cooking.py`).
  - Level-20 recipes are known from the start; the rest come from recipe books.
  - Food merchants sell the bought ingredients, standard rods, concentration potions and recipe books.
  - The cooking XP curve is the same as fishing's. Burnt dishes, and dishes 20+ levels below the cook, give a quarter of the XP.
