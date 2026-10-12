#!/usr/bin/env python3
"""Build server/content/passive-tree.json: the private server's Path-of-Exile-style passive tree (docs/passive-tree.md).

  scripts/gen-passive-tree.py            # writes server/content/passive-tree.json

The tree is our own design (no client data is read). Five class regions sit around a hub; each region is one template
rotated into place, filled from the per-class tables below. Edit the tables, re-run, and restart the server: the server
sends the layout to the client mod, so tuning needs no re-patch.

Skill-mod tags: a class name (skills that class can learn, from skill books' class rules and the class kits) or "heal".

Layout per region (u = outward from the hub, v = sideways), ids are 100*(region+1) + n:
           A ─ a3 ─ a2 ─ a1                       a = notable A's path, b = notable B's path, m = the middle path
          /                \\
  K ─ k1                    \\
  │      m4 ─ m3 ─ m2 ─ m1 ─ S ─ gate ─ (hub ring) ─ centre
  K ─ k2                    /
          \\                /
           B ─ b3 ─ b2 ─ b1
Notable B of one region and notable A of the next are joined by a bridge node, which makes an outer ring.
"""
import json, math, os

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "server", "content", "passive-tree.json")
SCALE = 110  # layout units → panel pixels

def s(stat, amount):
    return {"stat": stat, "amount": amount}

# Regions clockwise from the top; neighbours share a hybrid hub node and a bridge. Class ids are the client's.
REGIONS = [
    {
        "class": 1, "name": "Warrior", "attr": "str", "attr2": "vit",
        "a": ("Bulwark", [s("armourPct", 12), s("healthPct", 8)], [s("armourPct", 5)] * 3),
        "b": ("Cleaving Fury", [], [s("attackPct", 3)] * 3,
              [{"tag": "warrior", "damagePct": 20, "cooldownPct": 10, "ranks": 2}]),
        "m": [s("str", 15), s("vit", 15), s("healthPct", 4), s("str", 15)],
        "k": [s("healthPct", 4), s("armourPct", 5)],
        "keystone": ("Iron Hide", "ironHide"),
    },
    {
        "class": 5, "name": "Rogue", "attr": "dex", "attr2": "str",
        "a": ("Opportunist", [s("critChance", 3), s("critMulti", 25)], [s("critChance", 1)] * 3),
        "b": ("Flurry", [s("attackSpeedPct", 10), s("attackDamagePct", 10)], [s("attackSpeedPct", 3)] * 3),
        "m": [s("dex", 15), s("critMulti", 10), s("dex", 15), s("attackDamagePct", 4)],
        "k": [s("critMulti", 10), s("critChance", 1)],
        "keystone": ("Assassin's Mark", "assassinsMark"),
    },
    {
        "class": 4, "name": "Ranger", "attr": "dex", "attr2": "vit",
        "a": ("Deadeye", [], [s("attackDamagePct", 4)] * 3,
              [{"tag": "ranger", "damagePct": 20, "costPct": 15, "ranks": 2}]),
        "b": ("Fleet Foot", [s("runSpeedPct", 10), s("attackSpeedPct", 6)], [s("runSpeedPct", 3)] * 3),
        "m": [s("dex", 15), s("evadePct", 1), s("dex", 15), s("attackSpeedPct", 3)],
        "k": [s("runSpeedPct", 3), s("evadePct", 1)],
        "keystone": ("Windrunner", "windrunner"),
    },
    {
        "class": 2, "name": "Druid", "attr": "vit", "attr2": "foc",
        "a": ("Verdant Grace", [], [s("healPct", 5)] * 3,
              [{"tag": "heal", "damagePct": 20, "cooldownPct": 10, "ranks": 2}]),
        "b": ("Deep Roots", [s("healthRegenPct", 1.5), s("healthPct", 8)], [s("healthRegenPct", 0.5)] * 3),
        "m": [s("vit", 15), s("foc", 15), s("healthPct", 4), s("healPct", 5)],
        "k": [s("healthRegenPct", 0.5), s("vit", 15)],
        "keystone": ("Life Tap", "lifeTap"),
    },
    {
        "class": 3, "name": "Mage", "attr": "foc", "attr2": "vit",
        "a": ("Arcane Flow", [], [s("energyRegenPct", 0.5)] * 3,
              [{"tag": "mage", "cooldownPct": 15, "costPct": 10, "ranks": 2}]),
        "b": ("Spellweaver", [s("skillDamagePct", 15), s("energyPct", 10)], [s("skillDamagePct", 4)] * 3),
        "m": [s("foc", 15), s("energyPct", 4), s("foc", 15), s("skillDamagePct", 4)],
        "k": [s("energyPct", 4), s("skillDamagePct", 4)],
        "keystone": ("Glass Cannon", "glassCannon"),
    },
]

ATTR_NAMES = {"str": "Strength", "dex": "Dexterity", "foc": "Focus", "vit": "Vitality"}

nodes = {}

def add(nid, kind, name, u, v, angle, stats=(), **extra):
    x = u * math.cos(angle) - v * math.sin(angle)
    y = u * math.sin(angle) + v * math.cos(angle)
    node = {"id": nid, "kind": kind, "name": name, "x": round(x * SCALE), "y": round(y * SCALE), "stats": list(stats), "links": []}
    node.update({k: v for k, v in extra.items() if v is not None})
    nodes[nid] = node
    return nid

def link(a, b):
    if b not in nodes[a]["links"]:
        nodes[a]["links"].append(b)
        nodes[b]["links"].append(a)

def small_name(stats):
    st = stats[0]["stat"]
    if st in ATTR_NAMES:
        return ATTR_NAMES[st]
    return {
        "healthPct": "Vigour", "energyPct": "Clarity", "armourPct": "Plating", "attackPct": "Might", "defencePct": "Guard", "evadePct": "Evasion",
        "attackSpeedPct": "Haste", "runSpeedPct": "Swiftness", "critChance": "Precision", "critMulti": "Ruthlessness",
        "attackDamagePct": "Sharpness", "skillDamagePct": "Potency", "healPct": "Mending", "healthRegenPct": "Renewal",
        "energyRegenPct": "Focus Flow", "xpPct": "Learning", "goldPct": "Fortune",
    }[st]

CENTRE = add(1, "notable", "Fortune's Favour", 0, 0, 0, [s("xpPct", 5), s("goldPct", 5)])
n = len(REGIONS)
for k, r in enumerate(REGIONS):
    angle = -math.pi / 2 + k * 2 * math.pi / n  # top first, clockwise on screen (y down)
    base = 100 * (k + 1)
    # The hub: a gate per region (alternating experience / gold), a hybrid node between neighbours, the centre.
    gate = add(10 + k, "small", "Learning" if k % 2 == 0 else "Fortune", 2.0, 0, angle,
               [s("xpPct", 2)] if k % 2 == 0 else [s("goldPct", 2)])
    nxt = REGIONS[(k + 1) % n]
    mid = add(20 + k, "small", f"{ATTR_NAMES[r['attr']]} and {ATTR_NAMES[nxt['attr']]}", 2.0, 0, angle + math.pi / n,
              [s(r["attr"], 8), s(nxt["attr"], 8)] if r["attr"] != nxt["attr"] else [s(r["attr"], 8), s(nxt["attr2"], 8)])
    start = add(base, "start", f"{r['name']}'s Path", 3.2, 0, angle, [], **{"class": r["class"]})
    link(start, gate)
    # Middle path to the keystone.
    prev = start
    for i, st in enumerate(r["m"]):
        prev_id = add(base + 1 + i, "small", small_name([st]), 4.2 + i, 0, angle, [st])
        link(prev, prev_id)
        prev = prev_id
    kname, kkey = r["keystone"]
    keystone = add(base + 20, "keystone", kname, 9.0, 0, angle, [], keystone=kkey)
    link(prev, keystone)
    # Notable paths A (v < 0) and B (v > 0).
    for side, key, off in ((-1, "a", 10), (1, "b", 14)):
        spec = r[key]
        name, nstats, path = spec[0], spec[1], spec[2]
        mods = spec[3] if len(spec) > 3 else None
        prev = start
        for i, st in enumerate(path):
            pid = add(base + off + i, "small", small_name([st]), 4.0 + i, side * (1.0 + 0.55 * i), angle, [st])
            link(prev, pid)
            prev = pid
        notable = add(base + off + 3, "notable", name, 7.0, side * 2.4, angle, nstats, skillMods=mods)
        link(prev, notable)
        # A short way from the notable towards the keystone.
        kn = add(base + 18 + (0 if side < 0 else 1), "small", small_name([r["k"][0 if side < 0 else 1]]), 8.2, side * 1.3, angle,
                 [r["k"][0 if side < 0 else 1]])
        link(notable, kn)
        link(kn, keystone)

for k in range(n):
    link(10 + k, 20 + k)
    link(20 + k, 10 + (k + 1) % n)
    link(20 + k, CENTRE)
    # Outer bridge: notable B of region k to notable A of region k+1.
    angle = -math.pi / 2 + (k + 0.5) * 2 * math.pi / n
    r, nxt = REGIONS[k], REGIONS[(k + 1) % n]
    bridge = add(30 + k, "small", f"{r['name']} and {nxt['name']}", 7.6, 0, angle, [s(r["attr2"], 10), s(nxt["attr2"], 10)])
    link(bridge, 100 * (k + 1) + 17)
    link(bridge, 100 * ((k + 1) % n + 1) + 13)

KEYSTONES = {
    "ironHide": "50% more armour, but enemies never miss you",
    "assassinsMark": "Double critical strike chance and double the extra damage of critical strikes, but 25% less maximum health",
    "windrunner": "25% faster attacks and running, but 30% less armour",
    "glassCannon": "30% more skill and auto-attack damage, but 30% less maximum health",
    "lifeTap": "5% of the damage you deal heals you, but you no longer regenerate health out of combat",
}

tree = {
    "version": 1,
    "pointsPerLevels": 5,
    "keystones": KEYSTONES,
    "nodes": [nodes[i] for i in sorted(nodes)],
}
with open(OUT, "w") as f:
    json.dump(tree, f, indent=1)
    f.write("\n")
print(f"{OUT}: {len(nodes)} nodes, {sum(len(v['links']) for v in nodes.values()) // 2} links")
