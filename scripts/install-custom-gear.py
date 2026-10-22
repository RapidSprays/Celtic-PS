#!/usr/bin/env python3
"""Puts server/content/custom-gear.json into the game: writes CHPrivateMods.gear.txt next to the client mod.

    python scripts/install-custom-gear.py [balance-client.json] [game Managed folder]

The server reads custom-gear.json itself (Game/CustomGear.cs); the client mod (CHPrivateMods.CustomItems) reads this
file at start-up, so run it with the game closed. Names are resolved the same way as the server does, and every stat
list is written out in full, so nothing is kept from the item the look is copied from.
"""
import json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
GEAR = os.path.join(HERE, "..", "server", "content", "custom-gear.json")
BALANCE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "..", "grimoire-world", "balance-client.json")
MANAGED = sys.argv[2] if len(sys.argv) > 2 else os.path.expandvars(r"%LOCALAPPDATA%\CelticHeroesPrivate\game\CelticHeroes_Data\Managed")

DEFAULT_LOOKS = {"ring": 59705, "necklace": 58282, "bracelet": 17802, "charm": 35386}
STATS = {n.lower(): n for n in ("Strength", "Dexterity", "Focus", "Vitality")}
BONUSES = {"health": "HealthBonus", "energy": "EnergyBonus", "attack": "AttackBonus", "defence": "DefenceBonus",
           "defense": "DefenceBonus", "concentration": "ConcentrationBonus"}
RESISTS = {"heat": "HeatResist", "fire": "HeatResist", "cold": "ColdResist", "ice": "ColdResist", "magic": "MagicResist",
           "poison": "PoisonResist", "divine": "DivineResist", "chaos": "ChaosResist", "pierce": "PiercingResist",
           "piercing": "PiercingResist", "slash": "SlashingResist", "slashing": "SlashingResist", "crush": "CrushingResist",
           "crushing": "CrushingResist"}
DAMAGE = {"pierce": "PiercingDamage", "piercing": "PiercingDamage", "slash": "SlashingDamage", "slashing": "SlashingDamage",
          "crush": "CrushingDamage", "crushing": "CrushingDamage", "heat": "HeatDamage", "fire": "HeatDamage",
          "cold": "ColdDamage", "ice": "ColdDamage", "magic": "MagicDamage", "poison": "PoisonDamage",
          "divine": "DivineDamage", "chaos": "ChaosDamage", "true": "TrueDamage"}
AVOIDANCE = {"physical": 0, "spell": 1, "movement": 2, "wounding": 3, "weakening": 4, "mental": 5}


def fail(msg):
    print("ERROR: " + msg)
    sys.exit(1)


def game_running():
    try:
        out = subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()
        return "celtic" in out
    except OSError:
        return False


if game_running():
    fail("the game is running. Close Celtic Heroes first, then run this again.")
bal = json.load(open(BALANCE, encoding="utf-8-sig"))
def entries(name):
    v = bal[name]
    return [x for x in (v.values() if isinstance(v, dict) else v) if isinstance(x, dict)]
items = {int(k): v for k, v in bal["ItemTemplates"].items() if isinstance(v, dict)}
abilities = {}
for a in entries("Abilities"):
    abilities.setdefault(a["Name"].lower(), a["Id"])
skills = {}
for key, s in bal["SkillTemplates"].items():
    if not isinstance(s, dict):
        continue
    sid = s.get("SkillId", int(key))
    skills.setdefault(s.get("Name", "").lower(), []).append((s.get("AbilityId", -1) < 0, sid))
doc = json.load(open(GEAR, encoding="utf-8-sig"))

def clean(s): return str(s).replace("|", "/").replace("\n", " ").replace(";", ",")

lines = []
for g in doc.get("gear", []):
    name = g["name"]
    where = f"custom-gear.json, {name}"
    def resolve(table, field):
        out = []
        for k, v in (g.get(field) or {}).items():
            if k.lower() not in table:
                fail(f"{where}: unknown {field} \"{k}\" (use {', '.join(sorted(set(table)))})")
            out.append((table[k.lower()], int(v)))
        return out
    def ability(k):
        if str(k).isdigit(): return int(k)
        if k.lower() not in abilities: fail(f"{where}: unknown ability \"{k}\"")
        return abilities[k.lower()]
    def skill(k):
        if isinstance(k, int) or str(k).isdigit(): return int(k)
        if k.lower() not in skills: fail(f"{where}: unknown skill \"{k}\"")
        return sorted(skills[k.lower()])[0][1]
    if "look" in g:
        look = g["look"]
        if not isinstance(look, int):
            ids = [i for i, v in items.items() if v["Name"].lower() == str(look).lower()]
            if not ids: fail(f"{where}: no item called \"{look}\" to copy the look of")
            look = min(ids)
    elif str(g.get("type", "")).lower() in DEFAULT_LOOKS:
        look = DEFAULT_LOOKS[g["type"].lower()]
    else:
        fail(f"{where}: needs a \"look\" (an item id or name), or \"type\": ring, necklace, bracelet or charm")
    if look not in items: fail(f"{where}: look {look} isn't an item")
    if g["id"] in items: fail(f"{where}: id {g['id']} is already the game's {items[g['id']]['Name']}")

    def pairs(lst): return ",".join(f"{k}:{v}" for k, v in lst)
    # skillDamage and regen add lines to the description, the same as Game/CustomGear.cs ExtraText
    sdmg = [(k, int(v)) for k, v in (g.get("skillDamage") or {}).items()]
    text = [g["description"]] if g.get("description") else []
    text += [f"Increases the damage of your {k} skill by {v}." for k, v in sdmg]
    rg = {k.lower(): int(v) for k, v in (g.get("regen") or {}).items()}
    h, en = rg.get("health", 0), rg.get("energy", 0)
    if h > 0 and en > 0: text.append(f"Restores {h} health and {en} energy per tick.")
    elif h > 0: text.append(f"Restores {h} health per tick.")
    elif en > 0: text.append(f"Restores {en} energy per tick.")
    g = dict(g, description=" ".join(text))
    over = [
        ("Armour", g.get("armour", 0)),
        ("BonusTypes", pairs(resolve(BONUSES, "bonuses") + resolve(RESISTS, "resists"))),
        ("DamageTypes", pairs(resolve(DAMAGE, "damage"))),
        ("PrimaryStatBonuses", pairs(resolve(STATS, "stats"))),
        ("AbilityBonuses", pairs((ability(k), int(v)) for k, v in (g.get("abilities") or {}).items())),
        ("AvoidanceBonuses", pairs(resolve(AVOIDANCE, "avoidance"))),
        ("SkillBonuses", pairs([(skill(k), int(v)) for k, v in (g.get("skills") or {}).items()] + [(skill(k), v) for k, v in sdmg])),
        ("RequirementTypes", pairs([("LevelRequirement", g["level"])] if "level" in g else [])),
        ("ClassTypes", ",".join(g.get("classes") or [])),
        ("StarLevel", g.get("stars", -1)),
        ("Tradeable", 0 if g.get("tradeable") is False else 1),
        ("NoTrade", 1 if g.get("tradeable") is False else 0),
        ("ImportantItem", 0),
        ("ProcSkillID", skill(g["proc"]["skill"]) if g.get("proc") else 0),
        ("ProcSkillLevel", g["proc"].get("level", 1) if g.get("proc") else 0),
        ("ProcSkillChance", g["proc"].get("chance", 0) / 100 if g.get("proc") else 0),
        ("EquipSkillID", skill(g["equipSkill"]) if g.get("equipSkill") else 0),
    ]
    if "speed" in g: over.append(("Speed", g["speed"]))
    if "range" in g: over.append(("Range", g["range"]))
    lines.append("|".join([str(g["id"]), str(look), clean(name), clean(g.get("description", "")),
                           ";".join(f"{k}={v}" for k, v in over)]))
    print(f"  {g['id']}  {name}  (look: {items[look]['Name']})")

if not os.path.isdir(MANAGED): fail(f"no game folder at {MANAGED}")
with open(os.path.join(MANAGED, "CHPrivateMods.gear.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + ("\n" if lines else ""))
print(f"Installed {len(lines)} custom gear item(s) into the game.")

# ---- the owner's monsters and quests (custom-monsters.json, custom-quests.json) -> CHPrivateMods.extras.txt
CONTENT = os.path.join(HERE, "..", "server", "content")
def optional(name):
    p = os.path.join(CONTENT, name)
    return json.load(open(p, encoding="utf-8-sig")) if os.path.exists(p) else {}
mobs = {int(k): v for k, v in bal["MobTemplates"].items() if isinstance(v, dict)}
quest_ids = {v["Id"] for v in entries("QuestTemplates")}
extras = []
for m in optional("custom-monsters.json").get("monsters", []):
    if m["base"] not in mobs: fail(f"custom-monsters.json, {m['name']}: base {m['base']} isn't a monster")
    if m["id"] in mobs: fail(f"custom-monsters.json, {m['name']}: id {m['id']} is already the game's {mobs[m['id']]['Name']}")
    extras.append(f"MOB|{m['id']}|{m['base']}|{clean(m['name'])}")
    print(f"  monster {m['id']}  {m['name']}  (look: {mobs[m['base']]['Name']})")
    if m.get("colour"): extras.append(f"TINT|{m['id']}|{m['colour']}")
    if m.get("petLook"):
        pets = {x.get("PrefabName") for x in entries("PetAppearances")}
        if m["petLook"] not in pets: fail(f"custom-monsters.json, {m['name']}: petLook {m['petLook']} isn't one of the game's pets")
        extras.append(f"LOOK|{m['id']}|{m['petLook']}")
        print(f"     drawn as the pet {m['petLook']}")
    skills_by_id = {int(k): v for k, v in bal["SkillTemplates"].items() if isinstance(v, dict)}
    for a in m.get("abilities", []):
        look = skills_by_id.get(a.get("look", 0))
        if look is None: fail(f"custom-monsters.json, {m['name']}, {a.get('name')}: look {a.get('look')} isn't a skill")
        if a.get("skillId"):
            if a["skillId"] in skills_by_id: fail(f"custom-monsters.json, {a['name']}: skillId {a['skillId']} is already the game's {skills_by_id[a['skillId']]['Name']}")
            extras.append(f"SKILL|{a['skillId']}|{a['look']}|{clean(a['name'])}")
        if a.get("effectId") and look.get("StatusEffectId"):
            extras.append(f"EFFECT|{a['effectId']}|{look['StatusEffectId']}|{clean(a['name'])}")
        print(f"     ability {a['name']} ({a['kind']}, looks like {look['Name']})")
for o in optional("monster-overrides.json").get("monsters", []):  # the game's own monsters' colours
    if o.get("colour"):
        extras.append(f"TINT|{o['id']}|{o['colour']}")
        print(f"  colour {o['colour']} on {o.get('name') or o['id']}")
conv_files = {x["ConversationId"]: x["Filename"] for x in entries("ConversationLookupsTemplates")}
for n in optional("npc-extras.json").get("npcs", []):  # the owner's trade NPCs: their conversation becomes a greeting and trades
    if not n.get("trades"): continue
    if n["conversation"] >= 20000: file = f"#{n['conversation']}"  # a made-up conversation id (the mod adds it)
    else:
        file = conv_files.get(n["conversation"])
        if not file: fail(f"npc-extras.json, {n.get('name')}: conversation {n['conversation']} isn't one of the game's")
    trades = "|".join(f"{clean(t['text'])}|{t['vendor']}" for t in n["trades"])
    extras.append(f"TRADER|{file}|{clean(n.get('greeting', 'Hello, [name].'))}|{trades}")
    print(f"  trader {n.get('name')} ({file}): " + ", ".join(t["text"] for t in n["trades"]))
for q in optional("custom-quests.json").get("quests", []):
    if q["base"] not in quest_ids: fail(f"custom-quests.json: base {q['base']} isn't a quest")
    if q["id"] in quest_ids: fail(f"custom-quests.json: id {q['id']} is already one of the game's quests")
    swaps = ";".join(f"{clean(k)}={clean(v)}" for k, v in (q.get("replace") or {}).items())
    extras.append(f"QUEST|{q['id']}|{q['base']}|{swaps}")
    if q.get("conversation"): extras.append(f"CONV|{q['conversation']}|{q['base']}|{q['id']}|{swaps}")
    print(f"  quest {q['id']}  (a copy of quest {q['base']}" + (f", with its lines in {q['conversation']})" if q.get("conversation") else ")"))
with open(os.path.join(MANAGED, "CHPrivateMods.extras.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(extras) + ("\n" if extras else ""))
print(f"Installed {len(extras)} custom monster/quest line(s). Restart the server, then .give <id> for gear in game.")
