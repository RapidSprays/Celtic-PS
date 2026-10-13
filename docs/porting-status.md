# Porting status

What's still to port of the client's feature set, from the client message list (218 message types) and its UI. Each item is ticked when it's done and tested. "Invented" means DECA keeps that system's rules server-side, so ours are our own (see docs/decisions.md).

## 1. Combat
- [x] Friendly skills on other players (heals and buffs), and group skills on party members within the skill's radius
- [x] Area skills: everything within the template's AreaOfEffect radius around the target, at most 10
- [x] Ability progression: AbilityUpdate; gains invented
- [x] Cast interrupts from the skill rank's ChargingProtection; monster tagging (CombatEntityLocked: owner/party get the kill)
- [x] Monster special attacks: elites and bosses, with a themed client skill when one matches (invented)

## 2. Character and quality of life
- [ ] Delete character, logout back to character select
- [ ] Barbershop and appearance change
- [ ] Settings sync, permanent buffs (bag expansions), block list

## 3. Gathering and crafting
- [ ] Fishing
- [ ] Ground items and pickups, item and resource spawns
- [ ] Cooking and crafting (185 client recipes)

## 4. Social
- [ ] Friends list
- [ ] Mail
- [ ] Clans, clan bank, Clan Battleground

## 5. Economy
- [ ] Auction house
- [ ] Bounty board
- [ ] Factions and reputation
- [ ] Platinum item shop (private-server platinum)

## 6. Content systems
- [ ] Achievements, leaderboards
- [ ] Tarot mini-game
- [ ] Living calendar and seasonal events
- [ ] PvP area battles and arena rating
- [ ] Tutorial and first-time flags

## Not porting
- Real-money purchases (Steam, app stores), offerwall, "rate us", support tickets.
- The cross-server global layer: 11xxx messages that hand a character between DECA's separate worlds and a shared global server. We run one world, so global zones are ordinary zones.
- Checksums, debug and performance reports.
- Vouchers (redeem codes).
