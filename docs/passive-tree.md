# Passive tree

A small tree in the style of Path of Exile, our own addition to the private server.

**Off by default (2026-10-14).** The server only loads the tree when started with `--passive-tree`. It stays off while the base systems are matched to the official data, because its bonuses would skew those numbers. While it's off, no node gives anything, `.tree` says the tree is turned off, and players' chosen nodes stay saved for later.

| Part | Where |
|---|---|
| Rules and bonuses (server) | `server/src/CH.Server/Game/PassiveTree.cs`, `GameServer.Tree.cs` |
| The panel (client mod) | `server/tools/CH.ClientMods/PassiveTree.cs` (patches P14 and P15) |
| Content | `server/content/passive-tree.json`, built by `scripts/gen-passive-tree.py` |

## Rules
- **Points:** one per 5 levels (`pointsPerLevels`), so 50 at level 250. Taking a node costs one point.
- **Starts:** each class has a free start node in its own region. A node can be taken when it links to your start or to a node you already have.
- **Giving back:** right-click (or Alt-click) a node to give it back. This only works if everything else you have stays connected to your start.
- **Reset:** Reset in the panel, or `.tree reset`, gives back everything for free.
- **After re-tuning:** when the tree file changes, nodes that disappeared or no longer connect are refunded automatically when you next log in.

## Using it
The panel opens three ways:
- the **Passives** button in the HUD options menu;
- typing `.tree` in chat;
- `.tree open`, which is what the button sends.

Panel controls: click to take, right-click to give back, drag to move, mouse wheel to zoom. Hovering over a node shows what it does. The left column totals everything you have.

Chat commands work for every player, even without the mod:

| Command | What it does |
|---|---|
| `.tree` | Opens the panel and shows your points |
| `.tree alloc <id>` | Takes a node |
| `.tree refund <id>` | Gives a node back |
| `.tree reset` | Gives back everything |
| `.tree list` | Lists what you have and what you can take next, with ids |

## Shape (96 nodes)
- **Regions:** Warrior at the top, then clockwise Rogue, Ranger, Druid, Mage.
- **Each region:** a start, a middle path of four nodes to the **keystone**, and two side paths to its two **notables**. Each notable also has a short link to the keystone. A keystone is 5 points from its start.
- **Neighbours:** neighbouring regions share a hybrid node in the hub ring and a bridge between their notables.
- **Hub:** a ring of gates and hybrid nodes around the centre. The gates alternate between +2% experience and +2% gold. The centre, **Fortune's Favour**, gives +5% experience and +5% gold.

## Stats a node can give
All of them are applied by the server. The stats page shows them because the client displays the totals the server sends.

| Key | Effect | Applied in |
|---|---|---|
| `str` `dex` `foc` `vit` | Attribute bonus; shown on the stats page as a bonus, not base points | CharacterSheet (combined attributes, so also health, energy, attack and skill damage) |
| `health` `healthPct` `energy` `energyPct` | Maximum health and energy | CharacterSheet |
| `attack` `attackPct` `defence` `defencePct` `armour` `armourPct` | Attack, defence and armour | CharacterSheet. Defence is shown but not used in combat yet |
| `evadePct` | Chance for monsters to miss you (added to the miss chance, capped at 50%) | MobHits, duels |
| `attackSpeedPct` `runSpeedPct` | Faster attacks and running, with no ceiling | LiveStats |
| `healthRegenPct` `energyRegenPct` | % of maximum regenerated every 3 s, in and out of combat | regen tick |
| `critChance` `critMulti` | Critical strikes: base 5% chance and ×2 | PlayerHits, duels |
| `damagePct` `attackDamagePct` `skillDamagePct` `healPct` | Damage and healing | auto-attack, ResolveCast |
| `xpPct` `goldPct` | Kill rewards | Reward |

**Skill mods (`skillMods`).** These apply to a tag, which is either a class name (the skills that class can learn, from skill books' class rules and the class kits) or `heal`. A mod can give:
- `damagePct` (healing, for the `heal` tag);
- `cooldownPct`, which shortens the recharge the server sends in SkillUpdate;
- `costPct`, a lower energy cost;
- `ranks`, extra skill ranks, which the client shows as the compiled rank.

## Keystones

| Keystone | Upside | Downside |
|---|---|---|
| **Iron Hide** (Warrior) | 50% more armour | Enemies never miss you |
| **Assassin's Mark** (Rogue) | Double critical strike chance, and double the extra damage of critical strikes | 25% less maximum health |
| **Windrunner** (Ranger) | 25% faster attacks and running | 30% less armour |
| **Glass Cannon** (Mage) | 30% more skill and auto-attack damage | 30% less maximum health |
| **Life Tap** (Druid) | 5% of the damage you deal heals you | No out-of-combat health regeneration |

## Tuning
1. Edit the tables in `scripts/gen-passive-tree.py`.
2. Run it.
3. Restart the server.

The server sends the layout to the mod. The client caches it by version, so no re-patch and no new friend kit are needed. A new stat key also needs code in `PassiveTree.StatText` and wherever it applies. A new keystone needs `PassiveTree.KnownKeystones` and its rule.

## Not verified in the real client yet
These have only been tested with the scripted client:
- the panel's look and layout;
- the menu button's position;
- right-click on a Mac trackpad, which is a two-finger click. Alt-click works too.
