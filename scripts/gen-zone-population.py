#!/usr/bin/env python3
"""Generate monster camps for every live zone from the client's quest data (APPROXIMATED, see docs/decisions.md).

    scripts/gen-zone-population.py [BUILD]

Reads the extracted balance JSON and the snapshot's text.db, writes
~/CelticHeroesAnalysis/extracted/<build>/zone-population.json (outside git: derived from client data).

The client ships no spawn tables, so:
  * which monsters live in a zone comes from quests set there: kill objectives, mob-set kills, and monster names in
    quest text ("Recover two Command Totems from the Beastwalkers"). A quest's zone is the zone its text names most,
    or the zone of its "travel to" stage;
  * positions are around walkable points the data does give: leystones, zone exits and "travel to" coordinates
    (the client drops every model onto the ground, so only x/z matter);
  * levels follow a per-zone range and the order quests introduce each monster.
Seasonal event quests are skipped. Lir's Reach (zone 2) keeps its hand-picked camp list (LIRS_REACH below).

Where the zone's map mask gives the real walkable ground (scripts/extract-zone-worlds.py, docs/world-placement.md),
camps go only on open walkable land clear of towns, leystones and exits; camp-dwelling monsters (raiders, bandits)
take the world's own tents and campfires; lower levels sit nearer the zone's first leystone; and every monster gets
its own walkable home point. Zones without a usable mask keep the older placement around leystones and exits.
"""
import collections, json, math, os, random, re, sqlite3, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import worldplace  # noqa: E402

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
home = os.path.expanduser("~/CelticHeroesAnalysis")
balance = json.load(open(f"{home}/extracted/{build}/balance-client.json", encoding="utf-8-sig"))
db = sqlite3.connect(f"file:{home}/snapshots/{build}/Contents/Resources/Data/StreamingAssets/text.db?mode=ro", uri=True)
out_path = f"{home}/extracted/{build}/zone-population.json"

def entries(name):
    return [v for k, v in balance[name].items() if k != "$id"]

mobs = {m["Id"]: m for m in entries("MobTemplates")}
zones = {z["ZoneId"]: z for z in entries("Zones")}
mob_sets = {}
for k, v in balance["MobSets"].items():
    if k != "$id" and isinstance(v, list): mob_sets[int(k)] = v

# Approximated level bands, in the order players reach the zones. Hubs (Farcrag Castle, Tavern) stay monster-free.
LEVELS = {
    22: (3, 8), 4: (8, 14), 5: (12, 20), 6: (16, 30), 3: (25, 45), 11: (35, 55), 10: (45, 70),
    12: (60, 80), 13: (70, 90), 7: (80, 120), 15: (100, 110), 16: (108, 118), 17: (116, 126),
    18: (124, 134), 19: (132, 142), 20: (140, 150), 24: (150, 180), 27: (180, 220),
}
SEASONAL = re.compile(r"midwinter|winter|samhain|beltane|harvest|christmas|yule|easter|valentine|halloween|"
                      r"lughnasa|imbolc|frost festival|snow|anniversary|pumpkin|spooky|event", re.I)
JUNK = re.compile(r"^(quest |aa |ct |z\d+ |do not use|overwrite)|\[|dummy|skill shard|test|placeholder|nexus$|"
                  r"waters$|barrel|nest$|obelisk|totem$|beacon|chest|crate", re.I)

def usable(m):
    return m["Type"] == 0 and 1 <= m["Power"] <= 6 and not JUNK.search(m["Name"])

# Monster names → lowest template id with that name (same-name templates are variants; their differences are unknown).
by_name = {}
for mid in sorted(mobs):
    m = mobs[mid]
    if usable(m): by_name.setdefault(m["Name"].lower(), mid)
def plurals(n):
    out = {n, n + "s", n + "es"}
    if n.endswith("f"): out.add(n[:-1] + "ves")
    if n.endswith("y"): out.add(n[:-1] + "ies")
    if n.endswith("man"): out.add(n[:-3] + "men")
    return out
name_re = []
for n, mid in by_name.items():
    if len(n) < 5: continue
    for form in plurals(n): name_re.append((form, mid))
name_re.sort(key=lambda t: -len(t[0]))
big = re.compile(r"\b(" + "|".join(re.escape(f) for f, _ in name_re) + r")\b")
form_to_mob = dict(name_re)

quests = {q: (n or "", d or "") for q, n, d in db.execute("select quest_id, quest_name, quest_desc from quest_templates")}
stages = collections.defaultdict(list)
for q, st, desc, ct, det in db.execute(
        "select quest_id, stage_id, stage_desc, completion_type, completion_details from quest_stage_templates order by quest_id, stage_id"):
    stages[q].append((desc or "", ct, det or ""))

live = [z for z, v in zones.items() if v["Accessibility"] == "L" and z in LEVELS]
keywords = {}
for zid in live:
    z = zones[zid]
    for n in [z["ScreenName"]] + [p["Description"].replace("\\n", " ").replace("(Free Travel)", "").strip() for p in z["PlayerSpawnPoints"]]:
        if len(n) >= 6: keywords.setdefault(n.lower(), set()).add(zid)
keywords = {k: next(iter(v)) for k, v in keywords.items() if len(v) == 1}
keywords.update({"highshore": 2, "lir's reach": 2, "stonevale": 3, "crookback": 4, "dustwither": 5, "shalemont": 6,
                 "otherworld": 7, "carrowmore": 10, "fingal": 11, "dunskeig": 12, "murky vault": 13,
                 "bridge of despair": 15, "infernal armour": 16, "alchemical quarter": 17, "summoning chamber": 18,
                 "forbidden hall": 19, "arcane sanctum": 20, "heroes landing": 22, "heroes' landing": 22,
                 "corrupted garden": 24, "valley of the ancients": 27, "maccroin": 6, "greygorge": 6,
                 "loch dorcha": 11, "fianna": 10})

def quest_zone(q, text):
    for desc, ct, det in stages[q]:
        if ct == 2:
            zid = int(det.split(",")[0])
            if zid in LEVELS or zid == 2: return zid
    hits = collections.Counter()
    t = text.lower()
    for k, zid in keywords.items():
        c = t.count(k)
        if c: hits[zid] += c * len(k)
    return hits.most_common(1)[0][0] if hits else None

# zone → {mob template: first quest id that involves it}; zone → walkable "travel to" points
zone_mobs = collections.defaultdict(dict)
zone_points = collections.defaultdict(list)
for q in sorted(quests):
    name, desc = quests[q]
    text = " ".join([name, desc] + [s[0] for s in stages[q]])
    if SEASONAL.search(name + " " + desc): continue
    zid = quest_zone(q, text)
    if zid is None: continue
    involved = set()
    for sdesc, ct, det in stages[q]:
        m = re.match(r"(\d+)\s*,\s*(\d+)", det)
        if ct == 0 and m: involved.add(int(m.group(1)))
        if ct == 12 and m: involved.update(mob_sets.get(int(m.group(1)), []))
        if ct == 2:
            p = det.split(",")
            if int(p[0]) == zid: zone_points[zid].append((float(p[1]), float(p[2]), float(p[3])))
    for match in big.finditer(text.lower()):
        involved.add(form_to_mob[match.group(1)])
    for mid in involved:
        if mid in mobs and usable(mobs[mid]): zone_mobs[zid].setdefault(mid, q)

# Lir's Reach (zone 2): the hand-picked monster mix per area (leystone id, offset x/z, radius, count, templates, levels,
# roam). Centres move onto the nearest open ground when the mask says the offset point isn't.
LIRS_REACH = [
    (2003, 45, 30, 12, 5, [70003, 70004], 1, 2, 6),        # scrawny wolves outside the village
    (2002, 25, 35, 12, 5, [70000], 2, 3, 6),               # direhounds by the guard tower
    (2002, 40, 10, 8, 2, [70002], 4, 4, 6),                # beastwalkers
    (2004, 30, 25, 15, 6, [70108, 70118], 3, 5, 6),        # coast and grey wolves, western road
    (2004, 45, 40, 4, 1, [70020], 6, 6, 3),                # Alpha Snarl
    (2006, 30, -25, 14, 6, [70129, 70130], 5, 7, 6),       # grave robbers at the temple
    (2006, 20, -40, 6, 2, [70128], 6, 6, 6),               # tomb burglars
    (2006, 40, -45, 4, 1, [70132], 8, 8, 3),               # pillager
    (2005, 30, 30, 15, 7, [70009, 70122, 70167], 6, 9, 6), # Flintblade camp, southern pass
    (2005, 45, 45, 4, 1, [70124], 10, 10, 3),              # cutthroat
    (2007, -35, 25, 15, 6, [70030, 70126], 8, 11, 6),      # Overlook outlaws, eastern dock
    (2007, -50, 40, 4, 1, [70031], 12, 12, 3),             # outlaw brute
    (2008, -30, -30, 15, 6, [70110, 70120], 6, 8, 6),      # prowlers, northern peninsula
    (2008, 30, 30, 12, 5, [70015, 70016, 70133], 10, 13, 6),  # Skullcrusher picts
    (2008, 40, 20, 6, 2, [70171], 10, 10, 6),              # warhounds
    (2008, 50, -40, 15, 4, [70087, 70116], 13, 16, 6),     # Jotun
    (2008, 70, -55, 3, 1, [70022], 20, 20, 0),             # Jotun Warbringer (boss)
]

worlds = worldplace.load(build)
TOWN = worldplace.VILLAGE_OBJECTS
# NPCs from gen-world.py (run it first): camps keep clear of them, as of towns.
world_path = f"{home}/extracted/{build}/world.json"
npc_spots = collections.defaultdict(list)
if os.path.exists(world_path):
    for n in json.load(open(world_path)).get("npcs", []): npc_spots[n["zone"]].append((n["x"], n["z"]))

def ground_ok(world, x, z, radius, roam, safe, min_dist, town, camps, spacing, open_share=0.85):
    if not world.walkable(x, z): return False
    if world.open_fraction(x, z, radius + roam * 0.5) < open_share: return False
    if any(math.hypot(x - sx, z - sz) < min_dist for sx, sz in safe): return False
    if any(math.hypot(x - tx, z - tz) < 22 for tx, tz in town): return False
    return all(math.hypot(x - c["x"], z - c["z"]) >= spacing for c in camps)

def home_points(world, rng, x, z, radius, count):
    """A walkable home for each monster of a camp, spread over its radius."""
    pts = []
    for _ in range(count * 30):
        if len(pts) == count: break
        a, d = rng.random() * math.tau, math.sqrt(rng.random()) * radius
        px, pz = x + math.cos(a) * d, z + math.sin(a) * d
        if world.walkable(px, pz) and all(math.hypot(px - qx, pz - qz) >= 2 for qx, qz in pts): pts.append((round(px, 2), round(pz, 2)))
    while len(pts) < count: pts.append((round(x, 2), round(z, 2)))
    return pts

def anchors(zid):
    z = zones[zid]
    pts = [tuple(p["Position"]) for p in z["PlayerSpawnPoints"]]
    pts += [tuple(p["CentrePos"]) for p in z["Points"] if p["CentrePos"][1] > -500]
    pts += zone_points.get(zid, [])
    return pts

result = {"build": build, "approximated": True, "zones": {}}

# Lir's Reach: the hand-picked camps, moved onto open ground.
z2, w2 = zones[2], worlds.get(2)
if w2 and w2.has_mask:
    stones2 = {p["Id"]: p["Position"] for p in z2["PlayerSpawnPoints"]}
    safe2 = [(p[0], p[2]) for p in stones2.values()]
    town2 = [(o[1], o[3]) for o in w2.find(TOWN)] + npc_spots[2]
    rng2 = random.Random(2 * 7919)
    camps2 = []
    for anchor, ox, oz, radius, count, templates, lo2, hi2, roam in LIRS_REACH:
        ax, ay, az = stones2[anchor]
        want = (ax + ox, az + oz)
        spot = None
        for r in range(0, 90, 3):
            for k in range(max(1, r // 2)):
                a = k / max(1, r // 2) * math.tau
                cx, cz = want[0] + r * math.cos(a), want[1] + r * math.sin(a)
                if ground_ok(w2, cx, cz, radius, roam, safe2, 20, town2, camps2, radius + 6): spot = (cx, cz); break
            if spot: break
        if spot is None: continue
        camp = {"x": round(spot[0], 2), "y": round(ay, 2), "z": round(spot[1], 2), "radius": radius, "count": count, "templates": templates,
                "minLevel": lo2, "maxLevel": hi2, "roam": roam}
        camp["points"] = home_points(w2, rng2, spot[0], spot[1], radius, count)
        camps2.append(camp)
    result["zones"]["2"] = {"name": z2["ScreenName"], "levels": [1, 20], "camps": camps2}
    print(f"  2 {z2['ScreenName']:<24} hand-picked camps on real ground: {len(camps2)} of {len(LIRS_REACH)}")

for zid in sorted(LEVELS):
    found = zone_mobs.get(zid, {})
    if not found: continue
    z = zones[zid]
    lo, hi = LEVELS[zid]
    order, names = [], set()
    for mid in sorted(found, key=lambda mid: (found[mid], mid)):
        n = mobs[mid]["Name"].strip().lower()
        if n not in names: names.add(n); order.append(mid)
    def spread(ids, cap):  # keep the quest-order spread (and so the level spread) when trimming
        return ids if len(ids) <= cap else [ids[round(i * (len(ids) - 1) / (cap - 1))] for i in range(cap)]
    regular = spread([m for m in order if mobs[m]["Power"] <= 3], 40)
    bosses = spread([m for m in order if mobs[m]["Power"] >= 4], 12)
    safe = [(p["Position"][0], p["Position"][2]) for p in z["PlayerSpawnPoints"]]
    safe += [(p["CentrePos"][0], p["CentrePos"][2]) for p in z["Points"] if p["CentrePos"][1] > -500]
    pts = anchors(zid)
    rng = random.Random(zid * 7919)
    (minx, minz), (maxx, maxz) = z["MinPos"], z["MaxPos"]
    def place(min_dist):
        for _ in range(200):
            ax, ay, az = pts[rng.randrange(len(pts))]
            ang, dist = rng.random() * math.tau, rng.uniform(18, 45)
            x, zz = ax + math.cos(ang) * dist, az + math.sin(ang) * dist
            if not (minx + 5 < x < maxx - 5 and minz + 5 < zz < maxz - 5): continue
            if any(math.hypot(x - sx, zz - sz) < min_dist for sx, sz in safe): continue
            if any(math.hypot(x - c["x"], zz - c["z"]) < 14 for c in camps): continue
            return round(x, 2), round(ay, 2), round(zz, 2)
        return None
    camps = []
    world = worlds.get(zid)
    if world and world.has_mask:
        # Real ground: a pool of open, spaced spots ordered by distance from the zone's first leystone, so levels rise
        # going outward; camp dwellers take the world's tents and campfires first.
        hub = z["PlayerSpawnPoints"][0]["Position"] if z["PlayerSpawnPoints"] else pts[0]
        town = [(o[1], o[3]) for o in world.find(TOWN)] + npc_spots[zid]
        pool = []
        for _ in range(4000):
            spot = world.random_walkable(rng)
            if spot and ground_ok(world, spot[0], spot[1], 3, 2, safe, 24, town, [{"x": p[0], "z": p[1]} for p in pool], 12, 0.6): pool.append(spot)
        pool.sort(key=lambda p: math.hypot(p[0] - hub[0], p[1] - hub[2]))
        camp_spots = [(o[1], o[3]) for o in world.find(worldplace.CAMP_OBJECTS)]
        y_of = lambda x, zz: min(pts, key=lambda p: math.hypot(p[0] - x, p[2] - zz))[1]
        # The zone's first few monster types get two camps each, as in the older placement.
        doubled = [m for i, m in enumerate(regular) for _ in range(2 if i < 4 else 1)]
        regular_count = len(doubled)
        wanted = [(mid, False) for mid in doubled] + [(mid, True) for mid in bosses]
        for i, (mid, boss) in enumerate(wanted):
            if not pool: break
            power = mobs[mid]["Power"]
            radius, count, roam = (2, 1, 3) if boss else (9 if power == 1 else 6, {1: 5, 2: 3, 3: 2}.get(power, 2), 6)
            frac = (i - regular_count) / max(1, len(bosses) - 1) if boss else i / max(1, regular_count - 1)
            level = round(lo + frac * (hi - lo)) + 2 if boss else round(lo + frac * (hi - lo) * 0.85)
            spot = None
            if worldplace.CAMP_DWELLERS.search(mobs[mid]["Name"]):
                for cx, cz in camp_spots:
                    near = world.nearest_walkable(cx, cz, max_radius=20)
                    if near and ground_ok(world, near[0], near[1], radius, roam, safe, 28, town, camps, radius + 14):
                        spot = near
                        break
            if spot is None:
                # Spread over the pool by level: low levels near the hub, bosses at the far end. Narrow ground (dungeon
                # corridors) gets smaller, tighter camps rather than none.
                index = min(len(pool) - 1, int((0.7 + 0.3 * frac if boss else frac * 0.85) * (len(pool) - 1)))
                for tier_radius, open_share, spacing in ((radius, 0.85, radius + 14), (min(radius, 5), 0.75, 14), (3, 0.6, 10)):
                    for j in sorted(range(len(pool)), key=lambda j: abs(j - index)):
                        if ground_ok(world, pool[j][0], pool[j][1], tier_radius, min(roam, tier_radius), safe, 24, town, camps, spacing, open_share):
                            spot = pool.pop(j)
                            radius, roam = tier_radius, min(roam, tier_radius)
                            break
                    if spot: break
            if spot is None: continue
            camp = {"x": round(spot[0], 2), "y": round(y_of(*spot), 2), "z": round(spot[1], 2), "radius": radius, "count": count,
                    "templates": [mid], "minLevel": level if boss else max(1, level - 1), "maxLevel": level if boss else level + 1, "roam": roam}
            camp["points"] = home_points(world, rng, spot[0], spot[1], radius, count)
            camps.append(camp)
        result["zones"][str(zid)] = {"name": z["ScreenName"], "levels": [lo, hi], "camps": camps}
        print(f"{zid:>3} {z['ScreenName']:<24} {len(regular):>3} types {len(bosses):>3} bosses {sum(c['count'] for c in camps):>4} mobs (real ground)")
        continue
    # Regular monsters: one camp per type (two for the zone's first few), levels rising with quest order.
    for i, mid in enumerate(regular):
        frac = i / max(1, len(regular) - 1)
        level = round(lo + frac * (hi - lo) * 0.85)
        power = mobs[mid]["Power"]
        for _ in range(2 if i < 4 else 1):
            spot = place(28)  # clear of the NPC rings around leystones (gen-world.py)
            if not spot: break
            camps.append({"x": spot[0], "y": spot[1], "z": spot[2], "radius": 9 if power == 1 else 6, "count": {1: 5, 2: 3, 3: 2}[power],
                          "templates": [mid], "minLevel": max(1, level - 1), "maxLevel": level + 1, "roam": 6})
    for i, mid in enumerate(bosses):
        frac = i / max(1, len(bosses) - 1)
        level = round(lo + frac * (hi - lo)) + 2
        spot = place(35)
        if not spot: continue
        camps.append({"x": spot[0], "y": spot[1], "z": spot[2], "radius": 2, "count": 1, "templates": [mid],
                      "minLevel": level, "maxLevel": level, "roam": 3})
    result["zones"][str(zid)] = {"name": z["ScreenName"], "levels": [lo, hi], "camps": camps}
    print(f"{zid:>3} {z['ScreenName']:<24} {len(regular):>3} types {len(bosses):>3} bosses {sum(c['count'] for c in camps):>4} mobs")

json.dump(result, open(out_path, "w"), indent=1)
print(f"wrote {out_path}")
