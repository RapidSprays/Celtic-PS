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
Seasonal event quests are skipped. Lir's Reach (zone 2) keeps its hand-made camps in ZonePopulation.cs.
"""
import collections, json, math, os, random, re, sqlite3, sys

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

def anchors(zid):
    z = zones[zid]
    pts = [tuple(p["Position"]) for p in z["PlayerSpawnPoints"]]
    pts += [tuple(p["CentrePos"]) for p in z["Points"] if p["CentrePos"][1] > -500]
    pts += zone_points.get(zid, [])
    return pts

result = {"build": build, "approximated": True, "zones": {}}
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
    # Regular monsters: one camp per type (two for the zone's first few), levels rising with quest order.
    for i, mid in enumerate(regular):
        frac = i / max(1, len(regular) - 1)
        level = round(lo + frac * (hi - lo) * 0.85)
        power = mobs[mid]["Power"]
        for _ in range(2 if i < 4 else 1):
            spot = place(15)
            if not spot: break
            camps.append({"x": spot[0], "y": spot[1], "z": spot[2], "radius": 9 if power == 1 else 6, "count": {1: 5, 2: 3, 3: 2}[power],
                          "templates": [mid], "minLevel": max(1, level - 1), "maxLevel": level + 1, "roam": 6})
    for i, mid in enumerate(bosses):
        frac = i / max(1, len(bosses) - 1)
        level = round(lo + frac * (hi - lo)) + 2
        spot = place(25)
        if not spot: continue
        camps.append({"x": spot[0], "y": spot[1], "z": spot[2], "radius": 2, "count": 1, "templates": [mid],
                      "minLevel": level, "maxLevel": level, "roam": 3})
    result["zones"][str(zid)] = {"name": z["ScreenName"], "levels": [lo, hi], "camps": camps}
    print(f"{zid:>3} {z['ScreenName']:<24} {len(regular):>3} types {len(bosses):>3} bosses {sum(c['count'] for c in camps):>4} mobs")

json.dump(result, open(out_path, "w"), indent=1)
print(f"wrote {out_path}")
