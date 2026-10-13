#!/usr/bin/env python3
"""Ground items: quest pickups and cooking crops (APPROXIMATED, see docs/gathering-notes.md).

    scripts/gen-item-spawns.py [BUILD]

The live game's item spawn points (Zone.ItemSpawnPoints) are server-only. The client can only draw an item on the
ground if its mesh is a pickup prop (PropMesh3dConfigs PickupPrefab), so:
  * quest pickups: every "collect" stage (LootItem) whose item has a pickup mesh and that no monster drops gets six
    points in its quest's zone, on walkable ground 20-90 m from the quest giver (else the zone's hub leystone);
  * cooking crops (gen-cooking.py "ground"): grain and pulses in Lir's Reach's fields (near farm objects, else around
    Highshore), vegetables there and in Stonevale, King Bolete mushrooms in Stonevale and Shalemont.
Each point holds one item and grows back 60-120 s after it's picked. Writes
~/CelticHeroesAnalysis/extracted/<build>/item-spawns.json (outside git: derived from client data).
"""
import collections, json, math, os, random, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chdata, worldplace  # noqa: E402

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
bal = chdata.balance(build)
items = {int(k): v for k, v in bal["ItemTemplates"].items() if k != "$id"}
zones = {z["ZoneId"]: z for z in chdata.entries(bal, "Zones")}
world = json.load(open(f"{chdata.HOME}/extracted/{build}/world.json"))
cooking = json.load(open(f"{chdata.HOME}/extracted/{build}/cooking.json"))
worlds = worldplace.load(build)
out_path = f"{chdata.HOME}/extracted/{build}/item-spawns.json"
HUB = {2: 2003, 93: 9093, 3: 3001, 4: 4002, 5: 5001, 6: 6001, 7: 7002, 9: 9002, 10: 10001, 11: 11002, 12: 12003,
       13: 13001, 22: 2201, 24: 24003, 27: 26008}

pickup_meshes = {int(v.get("Id", k)) for k, v in bal["PropMesh3dConfigs"].items() if k != "$id" and isinstance(v, dict) and v.get("PickupPrefab")}
pickup_meshes.discard(76)  # the generic chest: nearly every item uses it

points = collections.defaultdict(list)  # zone → [{x, y, z, items, respawn}]

def hub(zid):
    z = zones[zid]
    return next((p for p in z["PlayerSpawnPoints"] if p["Id"] == HUB.get(zid)), None) or (z["PlayerSpawnPoints"] or [None])[0]

def place(zid, item, near, count, rng, lo=20, hi=90):
    w = worlds.get(zid)
    taken = [(p["x"], p["z"]) for p in points[zid]]
    placed = 0
    for _ in range(count * 40):
        if placed >= count: break
        a, r = rng.random() * 2 * math.pi, lo + rng.random() * (hi - lo)
        x, z = near[0] + r * math.cos(a), near[2] + r * math.sin(a)
        if w and w.has_mask and not (w.walkable(x, z) and w.open_fraction(x, z, 3) > 0.8): continue
        if any(math.hypot(x - tx, z - tz) < 6 for tx, tz in taken): continue
        points[zid].append({"x": round(x, 2), "y": round(near[1], 2), "z": round(z, 2), "items": [[item, 1]], "respawn": [60, 120]})
        taken.append((x, z))
        placed += 1
    return placed

rng = random.Random(7)
givers = {}
for n in world["npcs"]: givers.setdefault(n["conversation"], (n["x"], n["y"], n["z"], n["zone"]))
quest_items = 0
for qid, q in sorted(world["quests"].items(), key=lambda kv: int(kv[0])):
    if q["seasonal"] or q["zone"] is None or q["zone"] not in zones: continue
    for s in q["stages"]:
        if s["type"] != 1 or s["sources"]: continue
        item = int(s["details"].split(",")[0]) if s["details"] else -1
        if items.get(item, {}).get("Mesh3dsID") not in pickup_meshes: continue
        if any(p["items"][0][0] == item for p in points[q["zone"]]): continue
        g = givers.get(q["giver"])
        anchor = (g[0], g[1], g[2]) if g and g[3] == q["zone"] else (tuple(hub(q["zone"])["Position"]) if hub(q["zone"]) else None)
        if anchor is None: continue
        quest_items += place(q["zone"], item, anchor, 6, rng) > 0

# Cooking crops.
GRAIN, VEG, MUSHROOM = [60541, 60542, 60543, 60544, 60547], [60545, 60552, 60556, 60557, 60560], [60558]
lirs = worlds.get(2)
fields = lirs.find(r"field|wheat|farm|crop|hay|barn|plough", near=tuple(hub(2)["Position"][::2]), within=250) if lirs else []
for i, crop in enumerate(GRAIN + VEG):
    near = (fields[i % len(fields)][1], hub(2)["Position"][1], fields[i % len(fields)][3]) if fields else tuple(hub(2)["Position"])
    place(2, crop, near, 5, rng, lo=4 if fields else 40, hi=30 if fields else 140)
for crop in VEG: place(3, crop, tuple(hub(3)["Position"]), 3, rng, lo=40, hi=150)
for zid in (3, 6):
    for crop in MUSHROOM: place(zid, crop, tuple(hub(zid)["Position"]), 6, rng, lo=40, hi=160)

json.dump({"build": build, "zones": {str(z): p for z, p in sorted(points.items())}}, open(out_path, "w"), indent=1)
print(f"{quest_items} quest pickup items, {sum(len(p) for p in points.values())} points in {len(points)} zones; wrote {out_path}")
for z, p in sorted(points.items()): print(f"  {z:>3} {zones[z].get('ScreenName')}: {len(p)}")
