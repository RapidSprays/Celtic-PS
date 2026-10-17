#!/usr/bin/env python3
"""Replace the approximated NPC and monster positions with the official spawn points from Master's Grimoire.

    scripts/import-grimoire.py <Grimoire folder> <output folder> [BUILD]

Master's Grimoire (a community search tool, data as of 2022-09) ships the live server's spawn table in
Resources/Database/spawnpoints.txt, one spawn point per line:
    id~zone~respawn~x~y~z~movement~template|weight;template|weight…~scale~direction
movement is "stand", "random <radius> …" or "patrol x|y|z|wait;…". Mob levels come from its moblist.txt (column 6).

Run it after scripts/gen-world.py and scripts/gen-zone-population.py. It never changes their output: it copies every
data file from ~/CelticHeroesAnalysis/extracted/<build>/ into the output folder, keeps an untouched copy of the
originals in <output folder>/original, and writes the new world.json and zone-population.json there. Start the server
with --balance <output folder>/balance-client.json to use it (its other files are read from beside that).
  * world.json: every NPC whose spawn id (or, failing that, template in the same zone) is in the table moves to that
    spawn point's position and facing.
  * zone-population.json: each zone the table covers gets one camp per hostile or passive spawn point, at its exact
    position, with its template mix and weights (the server picks one by weight at every respawn, so rare spawns
    turn up at their official odds) and roam radius. Zones it doesn't cover keep the generated camps.
  * loot.json: the official loot sets and tables (loot section below).
  * mob-stats.json: each monster's official level and health (moblist.txt).
Outputs stay outside git (they are derived from game data).
"""
import json, math, os, re, shutil, sys
from collections import Counter

if len(sys.argv) < 3: sys.exit(__doc__)
grimoire_db = os.path.join(sys.argv[1], "Resources", "Database")
out = os.path.abspath(sys.argv[2])
build = sys.argv[3] if len(sys.argv) > 3 else "24928510"
extracted = os.path.expanduser(f"~/CelticHeroesAnalysis/extracted/{build}")
os.makedirs(os.path.join(out, "original"), exist_ok=True)
for name in os.listdir(extracted):
    if name.endswith(".json"):
        shutil.copyfile(os.path.join(extracted, name), os.path.join(out, name))
        if name != "balance-client.json": shutil.copyfile(os.path.join(extracted, name), os.path.join(out, "original", name))


def rows(name):
    with open(os.path.join(grimoire_db, name), encoding="utf-8-sig") as f:
        return [line.rstrip("\r\n").split("~") for line in f if line.strip()]


def generated(name):
    """The generator's own output (read from extracted/, never changed) and where the new version goes."""
    return json.load(open(os.path.join(extracted, name + ".json"), encoding="utf-8")), os.path.join(out, name + ".json")


balance = json.load(open(os.path.join(out, "balance-client.json"), encoding="utf-8-sig"))
known_mobs = {int(k) for k in balance["MobTemplates"] if k != "$id"}
known_zones = {int(k) for k in balance["Zones"] if k != "$id"}
moblist = {int(r[0]): r for r in rows("moblist.txt")}
zone_names = {int(r[0]): r[1] for r in rows("zones.txt")}


def level(template):
    r = moblist.get(template)
    return max(1, int(r[5])) if r and r[5].lstrip("-").isdigit() else 1


def friendly(template):
    r = moblist.get(template)
    return r is not None and r[4] == "Friendly"


spawns = []
for r in rows("spawnpoints.txt"):
    mix = [(int(t), int(w)) for t, w in (p.split("|") for p in r[7].split(";"))]
    mix = [(t, w) for t, w in mix if t in known_mobs and w > 0]
    if not mix or int(r[1]) not in known_zones: continue
    spawns.append({"id": int(r[0]), "zone": int(r[1]), "x": float(r[3]), "y": float(r[4]), "z": float(r[5]),
                   "movement": r[6].strip(), "mix": mix, "count": 1, "direction": float(r[9])})
by_id = {s["id"]: s for s in spawns}


def roam(s):
    kind, _, rest = s["movement"].partition(" ")
    if kind == "random":
        return min(20.0, float(rest.split()[0]))
    if kind == "patrol":
        pts = [tuple(map(float, p.split("|")[:3])) for p in rest.split(";") if p.count("|") >= 2]
        return min(20.0, max((math.hypot(x - s["x"], z - s["z"]) for x, _, z in pts), default=0.0))
    return 0.0


# ---- NPCs -------------------------------------------------------------------------------------------------------
world, world_path = generated("world")
moved = by_template = 0
for npc in world["npcs"]:
    s = by_id.get(npc["spawnId"])
    if s is None or npc["template"] not in (t for t, _ in s["mix"]):
        candidates = [c for c in spawns if c["zone"] == npc["zone"] and any(t == npc["template"] for t, _ in c["mix"])]
        s = min(candidates, key=lambda c: math.hypot(c["x"] - npc["x"], c["z"] - npc["z"]), default=None)
        if s is None: continue
        by_template += 1
    npc.update(zone=s["zone"], x=s["x"], y=s["y"], z=s["z"], direction=s["direction"])
    moved += 1
world["grimoire"] = True
json.dump(world, open(world_path, "w", encoding="utf-8"), indent=1)
print(f"NPCs: {moved} of {len(world['npcs'])} moved to official spawn points ({by_template} matched by template)")

# ---- Monsters ---------------------------------------------------------------------------------------------------
population, population_path = generated("zone-population")
zones = Counter()
for zone_id in sorted({s["zone"] for s in spawns}):
    camps = []
    for s in (s for s in spawns if s["zone"] == zone_id):
        hostile = [(t, w) for t, w in s["mix"] if not friendly(t)]
        if not hostile: continue
        templates = [t for t, _ in hostile]
        weights = [w for _, w in hostile]
        levels = [level(t) for t in set(templates)]
        camps.append({"x": round(s["x"], 2), "y": round(s["y"], 2), "z": round(s["z"], 2), "radius": 0,
                      "count": s["count"], "templates": templates, "weights": weights, "minLevel": min(levels), "maxLevel": max(levels),
                      "roam": round(roam(s), 1), "points": [[round(s["x"], 2), round(s["z"], 2)]] * s["count"],
                      "spawnId": s["id"]})
    if not camps: continue
    name = population["zones"].get(str(zone_id), {}).get("name") or zone_names.get(zone_id, f"Zone {zone_id}")
    population["zones"][str(zone_id)] = {"name": name, "camps": camps, "source": "grimoire"}
    zones[zone_id] = sum(c["count"] for c in camps)
population["grimoire"] = True
json.dump(population, open(population_path, "w", encoding="utf-8"), indent=1)
print(f"Monsters: {sum(zones.values())} from official spawn points in {len(zones)} zones")
for zone_id, n in sorted(zones.items()):
    print(f"  {zone_id:>3} {population['zones'][str(zone_id)]['name']:<26} {n}")

# ---- Loot -------------------------------------------------------------------------------------------------------
# A monster rolls each of its loot sets (moblootsetlist: mob~set~times). A set picks one loot table by weight
# (lootsetlist: set~table~weight; table -1 is "Nothing"), and the table picks one item by weight
# (loottableitemlist: table~item~quantity~weight). Items the current client doesn't know are kept, so the odds stay
# the same; the server drops nothing for them.
known_items = {int(k) for k in balance["ItemTemplates"] if k != "$id"}
loot = {"source": "Master's Grimoire (2022-09)", "mobs": {}, "sets": {}, "tables": {}}
for mob, loot_set, times in rows("moblootsetlist.txt"):
    loot["mobs"].setdefault(mob, []).append([int(loot_set), int(times)])
for loot_set, table, weight in rows("lootsetlist.txt"):
    loot["sets"].setdefault(loot_set, []).append([int(table), int(weight)])
unknown = set()
for table, item, quantity, weight in rows("loottableitemlist.txt"):
    if table == "-1": continue
    if int(item) not in known_items: unknown.add(int(item))
    loot["tables"].setdefault(table, []).append([int(item), int(quantity), int(weight)])
json.dump(loot, open(os.path.join(out, "loot.json"), "w", encoding="utf-8"))
print(f"Loot: {len(loot['mobs'])} monsters, {len(loot['sets'])} loot sets, {len(loot['tables'])} tables "
      f"({len(unknown)} items not in this client, never dropped)")

# ---- Monster stats ----------------------------------------------------------------------------------------------
# moblist.txt columns (as Master's Grimoire reads them): id, name, aggro range, follow range, opinion, level, health,
# gold, attack, defence, attack speed, energy, radius, damage, resists, stars, attack range, missile speed, ai, xp,
# evade, hidden. The server takes each monster's own level, health, attack, defence and damage from here. Damage is
# "type,amount" pairs; types: 0 pierce, 1 slash, 2 crush, 3 heat, 4 cold, 5 magic, 6 poison, 7 divine, 8 chaos, 9 true.
stats = {}
for r in rows("moblist.txt"):
    if len(r) > 6 and r[5].isdigit() and r[6].isdigit() and int(r[0]) in known_mobs and int(r[5]) > 0 and int(r[6]) > 0:
        damage = [[int(t), int(a)] for t, a in (d.split(",") for d in r[13].split(";") if d.count(",") == 1)
                  if t.isdigit() and a.isdigit()]
        # Resists "type,value": a number is a flat resist, "50%" lets half through, "Immune" none (as the Grimoire reads them).
        resists = []
        for part in r[14].split(";"):
            t, _, v = part.partition(",")
            if not t.isdigit() or not v: continue
            if v.lower() == "immune": resists.append([int(t), 0, 0.0])
            elif v.endswith("%") and v[:-1].lstrip("-").isdigit(): resists.append([int(t), 0, max(0.0, (100 - int(v[:-1])) / 100)])
            elif v.lstrip("-").isdigit(): resists.append([int(t), int(v), 1.0])
        # Gold: "25 - 52" (a range) or a single number; XP: column 20.
        gold = [int(g) for g in re.findall(r"\d+", r[7])][:2] or [0]
        # Evasion against players' skills, per avoidance type (0 physical, 1 spell, 2 movement, 3 wounding, 4 weakening,
        # 5 mental): column 21, "type,value" pairs.
        evade = {t: int(v) for t, _, v in (p.partition(",") for p in (r[20].split(";") if len(r) > 20 else []))
                 if t.isdigit() and v.isdigit()}
        stats[r[0]] = {"level": int(r[5]), "health": int(r[6]), "attack": int(r[8]) if r[8].isdigit() else 0,
                       "defence": int(r[9]) if r[9].isdigit() else 0, "damage": damage, "resists": resists,
                       "xp": int(r[19]) if r[19].isdigit() else 0, "gold": [gold[0], gold[-1]], "evade": evade}
json.dump({"source": "Master's Grimoire (2022-09)", "mobs": stats}, open(os.path.join(out, "mob-stats.json"), "w", encoding="utf-8"))
print(f"Monster stats: official level, health, attack, defence, damage, XP and gold for {len(stats)} monsters")

# ---- Live respawn times ------------------------------------------------------------------------------------------
# spawnpoints.txt column 3: "2 min - 3 min", "10 sec", "5 hr". The server uses them when spawn-tuning.json says so.
def _respawn_seconds(text):
    return sum(int(n) * {"hr": 3600, "min": 60, "sec": 1}[u] for n, u in re.findall(r"(\d+)\s*(hr|min|sec)", text))
respawn_times = {}
for cols in rows("spawnpoints.txt"):
    if len(cols) < 3 or not cols[0].isdigit(): continue
    parts = cols[2].split(" - ")
    lo, hi = _respawn_seconds(parts[0]), _respawn_seconds(parts[-1])
    if lo > 0: respawn_times[cols[0]] = [lo, max(lo, hi)]
json.dump({"source": "Master's Grimoire (2022-09) spawnpoints.txt", "respawns": respawn_times}, open(os.path.join(out, "spawn-respawns.json"), "w", encoding="utf-8"))
print(f"Live respawn times: {len(respawn_times)} spawn points")

# ---- Vendor sell prices -----------------------------------------------------------------------------------------
# itemlist.txt column 7 is what a vendor pays for the item (live, 2026-10-16: Sapphire 500, Diamond 1,500).
prices = {}
with open(os.path.join(grimoire_db, "itemlist.txt"), encoding="utf-8-sig") as f:
    for line in f:
        cols = line.rstrip("\n").split("~")
        if len(cols) > 7 and cols[0].isdigit() and cols[7].strip().isdigit() and int(cols[7]) > 0:
            prices[cols[0]] = int(cols[7])
json.dump({"source": "Master's Grimoire (2022-09)", "sellPrices": prices}, open(os.path.join(out, "item-prices.json"), "w", encoding="utf-8"))
print(f"Vendor sell prices: {len(prices)} items")

# ---- Equipment sets ---------------------------------------------------------------------------------------------
# setbonusitem.txt: set~piece item; setbonusreward.txt: set~reward item~pieces needed. The reward is an unequippable
# item ("Warden Armour Set Bonus Ranger") holding the bonus; the server's own aura values are in
# server/content/set-bonuses.json.
pieces = {}
for set_id, item in rows("setbonusitem.txt"):
    pieces.setdefault(int(set_id), []).append(int(item))
effect_ids = {}
for k, v in balance["StatusEffects"].items():
    if k != "$id": effect_ids.setdefault(v.get("Name", "").strip(), int(k))
item_names = {int(k): v.get("Name", "").strip() for k, v in balance["ItemTemplates"].items() if k != "$id"}


def aura_effect(reward, first_piece):
    """The client's status effect for a set bonus (the icon under the health bar), found by name: "Lesser Bloodthorn
    Aura Set Bonus" → "Lesser Bloodthorn Aura", "Warden Armour Set Bonus Ranger" (Greythorn pieces) → "Greythorn
    Aura", "Doch Gul Set Bonus Ranger" → "Doch Gul Ranger Aura", "Exalted Dragon…" → "Exalted Aura"."""
    name, piece = item_names.get(reward, ""), item_names.get(first_piece, "")
    base = name.replace(" set Bonus", "").replace(" Set Bonus", "").strip()
    words = piece.replace("'s", "").split()
    candidates = [base, base + " Aura", " ".join(name.split()[:2]) + " " + name.split()[-1] + " Aura",
                  name.split()[0] + " Aura"] + [" ".join(words[:n]) + " Aura" for n in (1, 2)]
    return next((effect_ids[c] for c in candidates if c in effect_ids), None)


sets = [{"id": int(s), "pieces": pieces.get(int(s), []), "needed": int(n), "reward": int(r),
         "effect": aura_effect(int(r), pieces.get(int(s), [0])[0])}
        for s, r, n in rows("setbonusreward.txt") if int(r) in known_items and pieces.get(int(s))
        and not any("test" in item_names.get(p, "").lower() for p in pieces[int(s)])]  # set 67: a developer test set
json.dump({"source": "Master's Grimoire (2022-09)", "sets": sets}, open(os.path.join(out, "item-sets.json"), "w", encoding="utf-8"))
print(f"Equipment sets: {len(sets)} ({sum(1 for x in sets if x['effect'])} with an aura icon)")
for x in sets:
    print(f"  set {x['id']:>2} {item_names.get(x['reward'], '?'):<45} -> "
          f"{balance['StatusEffects'][str(x['effect'])]['Name'] if x['effect'] else 'NO ICON'}")

# ---- Skill damage types -------------------------------------------------------------------------------------------
# skilltemplates.txt column 7 is the skill's damage type; the server cuts skill damage by the monster's resist to it.
type_ids = {"Pierce": 0, "Slash": 1, "Crush": 2, "Heat": 3, "Cold": 4, "Magic": 5, "Poison": 6, "Divine": 7, "Chaos": 8, "True": 9}
skill_types = {r[0]: type_ids[r[6]] for r in rows("skilltemplates.txt") if len(r) > 6 and r[6] in type_ids}
json.dump({"source": "Master's Grimoire (2022-09)", "skills": skill_types}, open(os.path.join(out, "skill-types.json"), "w", encoding="utf-8"))
print(f"Skill damage types: {len(skill_types)} skills")

# ---- Monster skills and AI ----------------------------------------------------------------------------------------
# mobskillsets.txt: mob~skill set~AI (-1: the mob's own, moblist column 19); skillsetweight.txt: set~skill~weight~kind
# (0 attack, 1 heal, 2 buff, 3 debuff); mobskills.txt: mob~skill~rank; mobabilities.txt: mob~ability~level;
# ai.txt: id~type~self-heal %~other-heal %~decisions~skill set, decisions "kind,weight;…" with kinds 1 auto-attack,
# 2 attack skill, 3 debuff, 4 heal self, 5 buff, 6 heal other (as Master's Grimoire reads them). A mob can have several
# skill sets, each tagged with a slot (mobskillsets column 3); an AI profile's last column says which slot it fights
# with (-1: the mob's default sets), so bosses that switch AI in a script also switch skills.
set_skills = {}
for set_id, skill, weight, kind in rows("skillsetweight.txt"):
    if skill.lstrip("-").isdigit() and int(skill) > 0: set_skills.setdefault(set_id, []).append([int(skill), int(weight), int(kind)])
ranks = {(m, s): int(r) for m, s, r in rows("mobskills.txt")}
abilities = {}
for m, a, l in rows("mobabilities.txt"): abilities.setdefault(m, {})[a] = int(l)
ai = {r[0]: {"type": int(r[1]), "selfHeal": int(r[2]), "otherHeal": int(r[3]),
             "decisions": [[int(k), int(w)] for k, w in (d.split(",") for d in r[4].split(";") if d.count(",") == 1)],
             "slot": int(r[5]) if len(r) > 5 and r[5].lstrip("-").isdigit() and int(r[5]) >= -1 else -1}
      for r in rows("ai.txt")}
mob_sets = {}
for m, set_id, slot in rows("mobskillsets.txt"):
    mob_sets.setdefault(m, []).append((set_id, int(slot)))
mob_ranks = {}
for (m, s), r in ranks.items():
    mob_ranks.setdefault(m, {})[s] = r

# ---- Monster scripts ------------------------------------------------------------------------------------------------
# scripts.txt: id~script~announcement~priority. A script is "1|triggers;2|failure conditions;3|actions;", each list
# "code^arg^arg|code^arg…". Every trigger must hold and no failure condition, then the actions run in order (codes as
# Master's Grimoire describes them; GameServer.Scripts runs them). mobscripts.txt: mob~AI type~script, the script only
# running while the mob fights with that AI (-1: any).
def number(v):
    try:
        f = float(v)
        return int(f) if f.is_integer() and "." not in v else f
    except ValueError:
        return v


scripts = {}
for r in rows("scripts.txt"):
    if not r[0].isdigit() or len(r) < 4: continue
    parts = {"1": [], "2": [], "3": []}
    for piece in r[1].split(";"):
        kind, _, rest = piece.partition("|")
        if kind in parts:
            parts[kind] += [[number(a) for a in x.split("^")] for x in rest.split("|") if x and x.split("^")[0].isdigit()]
    priority = r[-1] if r[-1].isdigit() else "1"
    scripts[r[0]] = {"when": parts["1"], "unless": parts["2"], "do": parts["3"], "say": "~".join(r[2:-1]).strip(),
                     "priority": int(priority)}
mob_scripts = {}
for m, ai_type, script in rows("mobscripts.txt"):
    if script in scripts and int(m) in known_mobs: mob_scripts.setdefault(m, []).append([int(ai_type), int(script)])
used = {s for links in mob_scripts.values() for _, s in links}
json.dump({"source": "Master's Grimoire (2022-09)", "scripts": {k: v for k, v in scripts.items() if int(k) in used},
           "mobs": mob_scripts}, open(os.path.join(out, "mob-scripts.json"), "w", encoding="utf-8"))
print(f"Monster scripts: {len(used)} scripts on {len(mob_scripts)} monsters")

brains = {}
for mob, r in moblist.items():
    if mob not in known_mobs or (str(mob) not in mob_sets and str(mob) not in mob_scripts): continue
    ai_id = r[18] if len(r) > 18 else "-1"
    slots = {}
    for set_id, slot in mob_sets.get(str(mob), []):
        slots.setdefault(str(slot), []).extend([s, w, k, ranks.get((str(mob), str(s)), 0)] for s, w, k in set_skills.get(set_id, []))
    skills = slots.pop("-1", [])
    if ai_id not in ai or not (skills or slots or str(mob) in mob_scripts): continue
    brains[str(mob)] = {"ai": ai_id, "skills": skills, "slots": slots, "ranks": mob_ranks.get(str(mob), {}),
                        "abilities": abilities.get(str(mob), {})}
json.dump({"source": "Master's Grimoire (2022-09)", "mobs": brains, "ai": ai}, open(os.path.join(out, "mob-skills.json"), "w", encoding="utf-8"))
print(f"Monster skills: {len(brains)} monsters with skill sets, {len(ai)} AI profiles")

# ---- Status effects: tick time and damage type ---------------------------------------------------------------------
# statuseffecttemplates.txt: column 4 is the damage type of damage over time, column 10 the seconds between ticks.
status_info = {}
for r in rows("statuseffecttemplates.txt"):
    if len(r) < 10: continue
    entry = {}
    if r[9].isdigit() and int(r[9]) > 0: entry["tick"] = int(r[9])
    if r[3] in type_ids: entry["type"] = type_ids[r[3]]
    if entry: status_info[r[0]] = entry
json.dump({"source": "Master's Grimoire (2022-09)", "statusEffects": status_info}, open(os.path.join(out, "status-effects.json"), "w", encoding="utf-8"))
print(f"Status effects: tick time or damage type for {len(status_info)}")

# ---- Skill aggro ---------------------------------------------------------------------------------------------------
# skilltemplatelevels.txt: skill~rank~initial~cast~cooldown~energy~interrupt~requirement~pvp~base~aggro. The aggro column
# is the threat a cast adds on top of its damage (heals and buffs too); PvE rows only (pvp 0).
aggro = {}
for r in rows("skilltemplatelevels.txt"):
    if len(r) > 10 and r[8] == "0" and r[1].isdigit() and r[10].lstrip("-").replace(".", "", 1).isdigit():
        aggro.setdefault(r[0], {})[int(r[1])] = float(r[10])
aggro = {s: [ranks[k] for k in sorted(ranks)] for s, ranks in aggro.items() if any(v for v in ranks.values())}
json.dump({"source": "Master's Grimoire (2022-09)", "skills": aggro}, open(os.path.join(out, "skill-aggro.json"), "w", encoding="utf-8"))
print(f"Skill aggro: {len(aggro)} skills")
