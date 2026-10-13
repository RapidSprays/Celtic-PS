#!/usr/bin/env python3
"""Fishing spots and catches for every zone with water (APPROXIMATED, see docs/gathering-notes.md).

    scripts/gen-fishing.py [BUILD]

The client ships the fishing spot templates (Race FishingSpot, Type 1), the rods and the fish, but not where spots
are or what they catch (server-only; the scenes' map-icon list, MapNodeHelper.GatheringNodes, is empty in this
build). So:
  * only zones with water have spots: those whose own fishing quests are set there (not the Tavern, whose quest is
    for the sea outside), and Heroes Landing's coast;
  * spots stand on the shore: walkable ground (map mask "1") beside a large body of water (a "0" region of at least
    2,500 m²; smaller ones are buildings and walls), spread at least 45 m apart, at most 8 per zone;
  * catches come from the zone's fishing quests ("Catch 10 Sprat"): its common fish (mesh 257) and rare ones (258,
    eels 274, rays 275, sharks 273) and relics (263). Zones with no fishing quests take the nearest zone's by level;
  * spot levels follow the zone's level band.
Writes ~/CelticHeroesAnalysis/extracted/<build>/fishing.json (outside git: derived from client data).
"""
import collections, json, math, os, random, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chdata, worldplace  # noqa: E402

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
bal = chdata.balance(build)
db = chdata.textdb(build)
items = {int(k): v for k, v in bal["ItemTemplates"].items() if k != "$id"}
zones = {z["ZoneId"]: z for z in chdata.entries(bal, "Zones")}
world = json.load(open(f"{chdata.HOME}/extracted/{build}/world.json"))
worlds = worldplace.load(build)
out_path = f"{chdata.HOME}/extracted/{build}/fishing.json"

SPOTS = sorted(m["Id"] for m in chdata.entries(bal, "MobTemplates") if m.get("Race") == "FishingSpot" and m["Name"] == "Rippling Waters")
RARE_SPOTS = sorted(m["Id"] for m in chdata.entries(bal, "MobTemplates") if m.get("Race") == "FishingSpot" and m["Name"] == "Shimmering Waters")
JUNK = sorted(i for i, t in items.items() if t.get("Subtype") == "FishingItem" and t.get("Mesh3dsID") == 262)
CLAMS = sorted(i for i, t in items.items() if t.get("Subtype") == "FishingItem" and t.get("Mesh3dsID") == 261)

# Fish named by each zone's fishing quests (quest_type 1).
quest_type = {q: t for q, t in db.execute("select quest_id, quest_type from quest_templates")}
by_zone = collections.defaultdict(set)
for qid, q in world["quests"].items():
    if quest_type.get(int(qid)) != 1 or q["zone"] is None: continue
    for s in q["stages"]:
        if s["type"] in (1, 6) and s["details"]:
            item = int(s["details"].split(",")[0])
            if items.get(item, {}).get("Subtype") == "FishingItem": by_zone[q["zone"]].add(item)

def catches(zid):
    lo = chdata.LEVELS[zid][0]
    source = zid if by_zone.get(zid) else min(by_zone, key=lambda z: abs(chdata.LEVELS.get(z, (999,))[0] - lo))
    fish = by_zone[source]
    mesh = lambda i: items[i].get("Mesh3dsID")
    return {"common": sorted(i for i in fish if mesh(i) == 257),
            "rare": sorted(i for i in fish if mesh(i) in (258, 273, 274, 275)),
            "relics": sorted(i for i in fish if mesh(i) == 263),
            "junk": JUNK, "clams": CLAMS}

def water_regions(w):
    """Pixels of "0" regions large enough to be water."""
    n, rows = w.n, w.rows
    seen = [[False] * n for _ in range(n)]
    water = set()
    min_px = 2500 / (w.metres_per_pixel ** 2)
    for v0 in range(n):
        for u0 in range(n):
            if seen[v0][u0] or rows[v0][u0] != "0": continue
            stack, region = [(u0, v0)], []
            seen[v0][u0] = True
            while stack:
                u, v = stack.pop()
                region.append((u, v))
                for du, dv in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    a, b = u + du, v + dv
                    if 0 <= a < n and 0 <= b < n and not seen[b][a] and rows[b][a] == "0":
                        seen[b][a] = True
                        stack.append((a, b))
            if len(region) >= min_px: water.update(region)
    return water

FISHING_ZONES = (set(by_zone) - {9}) | {22}

result = {"build": build, "zones": {}}
for zid, z in sorted(zones.items()):
    w = worlds.get(zid)
    if zid not in FISHING_ZONES or zid not in chdata.LEVELS or not w or not w.has_mask: continue
    water = water_regions(w)
    if not water: continue
    reach = max(1, round(3 / w.metres_per_pixel))
    shore = [(u, v) for v in range(w.n) for u in range(w.n) if w.rows[v][u] == "1"
             and any((u + du, v + dv) in water for du in range(-reach, reach + 1) for dv in range(-reach, reach + 1))]
    if not shore: continue
    rng = random.Random(zid)
    rng.shuffle(shore)
    stones = [(p["Position"][0], p["Position"][2]) for p in z["PlayerSpawnPoints"]]
    y = min((p["Position"][1] for p in z["PlayerSpawnPoints"]), default=0.0)
    spots = []
    for u, v in shore:
        x, zz = w._world(u, v)
        if any(math.hypot(x - sx, zz - sz) < 45 for sx, sz, _ in spots): continue
        if any(math.hypot(x - sx, zz - sz) < 15 for sx, sz in stones): continue
        spots.append((x, zz, y))
        if len(spots) >= 8: break
    lo, hi = chdata.LEVELS[zid]
    result["zones"][str(zid)] = {
        "name": z.get("ScreenName"), "minLevel": max(1, lo), "maxLevel": max(1, hi),
        "spots": [{"x": round(x, 2), "y": round(yy, 2), "z": round(zz, 2)} for x, zz, yy in spots],
        "templates": SPOTS, "rareTemplates": RARE_SPOTS, "catches": catches(zid),
    }
    print(f"{zid:>3} {z.get('ScreenName') or '':<24} spots {len(spots)}  fish {len(result['zones'][str(zid)]['catches']['common'])}")

json.dump(result, open(out_path, "w"), indent=1)
print(f"wrote {out_path}")
