# Pets, mounts and token vendors: build 24928510 (researched 2026-10-12)

Paths are relative to `decompiled/24928510/Assembly-CSharp/`; NM = NetworkManager.cs.

## Pets
- **How a pet appears.** It is entirely client-side. The client draws a pet when equipment slot 20 (Companion) is filled and the owner is not "hungry".
  - The model comes from `PetAppearances[item.ModelID]` (CompanionManager.cs:61-77).
  - Other players' pets come from their equipment block.
- **Hungry rule** (CompanionManager.Hungry 116-131): the owner has **no** status effects, or has **any** effect of EffectType 48. So a fed pet needs a "Well Fed" effect in 15017 (own) and 15018 (others).
- **Effect pairs** (Hungry type 48 / Well Fed):
  - Dog 5588/5589, Rabbit 5601/5602, Bear 5713/5711, Spirit 5714/5712, Wolf 5716/5717
  - Spider 5731/5732, Boar 5733/5734, Chicken 5974/5975, Hatchling 5976/5977, Eagle 5999/6000
  - Phoenix 6136/6137, Pig 6171/6172, Seedling 6177/6178, Reindeer 6270/6271, Imp 6303/6304
  - "New Fed Dog" 5647 is fed by every "New Pet Food" (skill 7900).
- **Feeding** is a normal UseItem (Self target); the reply is UseItemReply with a 3 s recharge.
  - Food skill → Well Fed effect: 7582→5589, 7870→5602, 8036→5711, 8037→5712, 8039→5717, 8059→5732, 8060→5734, 8149→5975, 8150→5977, 8176→6000, 8321→6137, 8361→6172, 8365→6178, 8430→6271, 8467→6304.
  - Typed food of level L feeds pets up to StarLevel L+1.
- **Growth is breeding at a token vendor:** two identical pets give the next size (Tiny→Giant, StarLevel 1-6), and two Giants give the next colour tier.
- **Skill toggles.** CompanionSkillToggle 16007 / MountSkillToggle 16008 S→C are `int32 skillId, bit enabled`. They grey out the pet or mount skill, and every skill-list message resets them.

## Mounts
- The client draws a mount while slot 22 is filled and there is no effect of EffectType 51. Effect 20007 "Dismounted" is type 51, one level, 3600 s.
- There is no mount toggle message: mounting is equipping. Dismounted is cleared by:
  - the Mount Whistle (skill link 8066, a normal UseItem);
  - the stablemaster's CASTSKILL 8065 <gold> → CastSkillOnPlayer 13014 `vi npcServerId, vi skillId, vi gold`;
  - expiry.

## Token vendors, TokenVendorMessage 23006
- C→S:
  - `1 {vendorId}`: stock
  - `3 {vendorId, stockId, qty}`: buy
- S→C stock: `2 {byte hasId [vendorId], varint n × {stockId, template, qty, costId}, varint m × {costId, varint k × {template, qty}}}`. When n = 0, a string follows, then a trailing `varint 0`. Every costId used must be in the cost table.
- S→C buy: `4 {byte ok; 0: string; else varint newInvId, InventoryWithMoney}`.
- The only client data is `token_vendor_descriptions` (id, title, body). Stock and costs are server-only.
- Vendors:
  - pet shop `cv_pet_vendor`, pet breeder `cv_pet_breeder`
  - mount vendor `cv_mount_vendor`, mount breeder `cv_mount_breeder`, barding `cv_saddle_vendor`
  - bounty vendor (Hunter's tokens)
- Currencies:
  - Pet Token 56051 / Uncommon 56052 / Rare 56053; Mount Token 60145
  - "Bag of N … Tokens" (skill links 7874 / 8067) open like chests.
