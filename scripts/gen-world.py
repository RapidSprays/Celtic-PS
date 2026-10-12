#!/usr/bin/env python3
"""Generate the quest table and NPC placements from the client data (APPROXIMATED, see docs/quest-notes.md).

    scripts/gen-world.py [BUILD]

Writes ~/CelticHeroesAnalysis/extracted/<build>/world.json (outside git: derived from client data):
  quests: id → name, zone, level, prerequisites (QUEST_COMPLETE conditions on the way to the giver's QUEST_ADD, plus
          CompletedQuest stages), min level (LEVEL_GREATER_THAN conditions, else the zone band), stages with their
          completion type, details and next stage.
  npcs:   official spawn id (so the client's quest/shop icons apply), mob template, conversation id, zone and a
          position. The client ships no NPC positions: NPCs stand in rings around the leystone their quests or
          conversation notes name (else the zone's hub leystone); a few Farcrag Castle roles use scene props.
"""
import collections, json, math, os, re, sys

sys.path.insert(0, os.path.dirname(__file__))
import chdata

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
bal = chdata.balance(build)
db = chdata.textdb(build)
zones = {z["ZoneId"]: z for z in chdata.entries(bal, "Zones")}
mobs = {m["Id"]: m for m in chdata.entries(bal, "MobTemplates")}
keywords = chdata.zone_keywords(zones)

# ---- conversations ----------------------------------------------------------------------------------------------
lookups = {c["ConversationId"]: c["Filename"] for c in bal["ConversationLookupsTemplates"] if c.get("ConvType") == "Static"}
assets = chdata.text_assets(chdata.textfiles_bundle(build), rb"cv_[A-Za-z0-9_]+")

def tag(block, name):
    return [m.strip() for m in re.findall(rf"<{name}>(.*?)</{name}>", block, re.S)]

convs = {}
for cid, filename in lookups.items():
    xml = assets.get(filename) or assets.get(filename.replace(".xml", ""))
    if not xml: continue
    location = (re.search(r"Location:\s*(.*)", xml) or [None, ""])[1].strip()
    body = re.sub(r"<!--.*?-->", "", xml, flags=re.S)
    starts = [(tag(b, "startCondition"), (tag(b, "startGoto") or [""])[0]) for b in re.findall(r"<start>(.*?)</start>", body, re.S)]
    lines = {}
    for b in re.findall(r"<line>(.*?)</line>", body, re.S):
        choices = [(tag(c, "choiceCondition"), " ".join(tag(c, "choiceAction")), (tag(c, "choiceGoto") or ["-1"])[0],
                    (tag(c, "choiceText") or [""])[0])
                   for c in re.findall(r"<choice>(.*?)</choice>", b, re.S)]
        lines[(tag(b, "lineTag") or [""])[0]] = choices
    convs[cid] = {"file": filename, "location": location, "starts": starts, "lines": lines,
                  "actions": [a for cs in lines.values() for _, a, _, _ in cs if a.strip()]}

def conditions(conds, kind):
    out = []
    for c in conds:
        p = c.split()
        if len(p) >= 2 and p[0] == kind and p[1].lstrip("-").isdigit(): out.append(int(p[1]))
    return out

# Prerequisites: walk each conversation from its start blocks; a QUEST_ADD inherits the QUEST_COMPLETE (and
# LEVEL_GREATER_THAN) conditions met on the first path that reaches it.
prereqs, min_levels, giver = {}, {}, {}
quest_conv = collections.defaultdict(set)      # quest → conversations that add or advance it
stage_conv = {}                                # (quest, stage) → conversation with STAGE_COMPLETE
shop_conv = collections.defaultdict(set)
for cid, c in sorted(convs.items()):
    seen = {}
    queue = collections.deque()
    for conds, goto in c["starts"]:
        queue.append((goto, frozenset(conditions(conds, "QUEST_COMPLETE")), max(conditions(conds, "LEVEL_GREATER_THAN") or [0])))
    while queue:
        node, done, lvl = queue.popleft()
        if node in seen or node not in c["lines"]: continue
        seen[node] = (done, lvl)
        for conds, action, goto, _ in c["lines"][node]:
            d2 = done | frozenset(conditions(conds, "QUEST_COMPLETE"))
            l2 = max([lvl] + conditions(conds, "LEVEL_GREATER_THAN"))
            for m in re.finditer(r"QUEST_ADD\s+(\d+)", action):
                q = int(m.group(1))
                quest_conv[q].add(cid)
                if q not in giver:
                    giver[q] = cid
                    prereqs[q] = sorted(d2 - {q})
                    min_levels[q] = l2
            for m in re.finditer(r"STAGE_COMPLETE\s+(\d+)\s+(\d+)", action):
                quest_conv[int(m.group(1))].add(cid)
                stage_conv.setdefault((int(m.group(1)), int(m.group(2))), cid)
            for m in re.finditer(r"SHOP\s+(\d+)", action):
                shop_conv[int(m.group(1))].add(cid)
            queue.append((goto, d2, l2))

# ---- quests -----------------------------------------------------------------------------------------------------
names = {q: (n or "", d or "", rep, qt) for q, n, d, rep, qt in db.execute(
    "select quest_id, quest_name, quest_desc, repeatable, quest_type from quest_templates")}
stages = collections.defaultdict(list)
for q, st, desc, ct, det, nxt in db.execute(
        "select quest_id, stage_id, stage_desc, completion_type, completion_details, next_stage from quest_stage_templates order by quest_id, stage_id"):
    stages[q].append({"type": ct, "details": det or "", "next": nxt, "text": desc or ""})
totals = {}
for qt in chdata.entries(bal, "QuestTemplates"):
    totals[qt["Id"]] = [s.get("CompletionTotalSum", 0) for s in qt.get("Stages", [])]

quest_zone = {}
for q in names:
    zid = None
    for s in stages[q]:
        if s["type"] == 2:
            z = int(s["details"].split(",")[0])
            if z in chdata.LEVELS: zid = z; break
    if zid is None:
        text = " ".join([names[q][0], names[q][1]] + [s["text"] for s in stages[q]]
                        + [convs[c]["location"] for c in quest_conv.get(q, ())])
        votes = chdata.zone_votes(text, keywords)
        zid = votes.most_common(1)[0][0] if votes else None
    quest_zone[q] = zid

by_zone = collections.defaultdict(list)
for q, z in quest_zone.items():
    if z is not None: by_zone[z].append(q)
quest_level = {}
for z, qs in by_zone.items():
    lo, hi = chdata.LEVELS[z]
    qs.sort()
    for i, q in enumerate(qs):
        quest_level[q] = round(lo + (hi - lo) * i / max(1, len(qs) - 1) * 0.9)

# Monsters named in a stage's text ("Recover two Command Totems from the Beastwalkers") drop its quest items.
name_to_ids = collections.defaultdict(list)
for mid in sorted(mobs):
    m = mobs[mid]
    if m["Type"] == 0 and m["Power"] >= 1 and len(m["Name"].strip()) >= 4 and "[" not in m["Name"]:
        name_to_ids[m["Name"].strip().lower()].append(mid)
forms = {}
for n, ids in name_to_ids.items():
    for f in {n, n + "s", n + "es", n[:-1] + "ves" if n.endswith("f") else n, n[:-1] + "ies" if n.endswith("y") else n,
              n[:-3] + "men" if n.endswith("man") else n}:
        forms.setdefault(f, ids)
form_re = re.compile(r"\b(" + "|".join(re.escape(f) for f in sorted(forms, key=len, reverse=True)) + r")\b")

def sources(text):
    ids = []
    for m in form_re.finditer(text.lower()):
        ids += forms[m.group(1)]
    return sorted(set(ids))[:40]

quests = {}
for q, (name, desc, rep, qtype) in names.items():
    if not stages[q]: continue
    seasonal = bool(chdata.SEASONAL.search(name + " " + desc))
    pre = set(prereqs.get(q, []))
    pre |= {int(s["details"]) for s in stages[q] if s["type"] == 5 and s["details"].isdigit()}
    level = quest_level.get(q, 1)
    tot = totals.get(q, [])
    quests[str(q)] = {
        "name": name, "zone": quest_zone[q], "level": level,
        "minLevel": max(min_levels.get(q, 0) + 1 if min_levels.get(q) else 0, max(1, level - 3)),
        "prereqs": sorted(p for p in pre if p != q and p in names), "repeatable": rep != 0, "type": qtype,
        "seasonal": seasonal, "giver": giver.get(q),
        "stages": [{"type": s["type"], "details": s["details"], "next": s["next"],
                    "total": tot[i] if i < len(tot) else 0,
                    "sources": sources(s["text"]) if s["type"] == 1 else []} for i, s in enumerate(stages[q])],
    }

# ---- NPCs -------------------------------------------------------------------------------------------------------
HUB = {2: 2003, 93: 9093, 3: 3001, 4: 4002, 5: 5001, 6: 6001, 7: 7002, 9: 9002, 10: 10001, 11: 11002, 12: 12003,
       13: 13001, 22: 2201, 24: 24003, 27: 26008}
# Farcrag Castle scene props (scene Transforms): the role NPC stands next to the prop.
PROPS = {"Banker": (-39.63, -91.34, -0.11), "Blacksmith": (5.69, -111.23, 122.28), "Stable": (12.66, -101.77, 98.54),
         "Barber": (39.29, -152.22, 102.62)}

def best_conv(spawn_icons, template):
    score = collections.Counter()
    for icon in spawn_icons:
        t, p1, p2 = icon.get("Type"), icon.get("Param1", -1), icon.get("Param2", -1)
        if t == "QuestGiver":
            for c in quest_conv.get(p1, ()):
                if f"QUEST_ADD {p1}" in " ".join(convs[c]["actions"]): score[c] += 3
        elif t == "QuestAdvancer" and (p1, p2) in stage_conv:
            score[stage_conv[(p1, p2)]] += 2
        elif t == "Shop":
            for c in shop_conv.get(p2, ()): score[c] += 2
    if not score: return None
    top = max(score.values())
    best = sorted((c for c, v in score.items() if v == top), key=lambda c: (convs[c]["file"].startswith("cv_ct_"), c))
    return best[0]

npcs, unplaced = [], 0
ring = collections.Counter()
for key, coll in bal["SpawnIconInfoCollections"].items():
    if key == "$id": continue
    spawn_id = int(key)
    per_mob = {int(k): v for k, v in coll.get("IconsInfoPerMob", {}).items() if k != "$id"}
    for template, icons in per_mob.items():
        if template not in mobs: continue
        conv = best_conv(icons, template)
        if conv is None: continue  # quest targets and props without a conversation are not NPCs
        qs = {i.get("Param1") for i in icons if i.get("Type") in ("QuestGiver", "QuestAdvancer")}
        votes = collections.Counter(quest_zone[q] for q in qs if quest_zone.get(q) is not None)
        if not votes:
            votes = chdata.zone_votes(convs[conv]["location"] + " " + mobs[template]["Name"], keywords)
        if not votes:
            unplaced += 1
            continue
        zid = votes.most_common(1)[0][0]
        z = zones[zid]
        stones = {chdata.leystone_name(p).lower(): p for p in z["PlayerSpawnPoints"]}
        text = (convs[conv]["location"] + " " + " ".join(s["text"] for q in qs if q in quests for s in stages[q])).lower()
        anchor = next((p for n, p in stones.items() if n and n in text), None)
        if anchor is None:
            anchor = next((p for p in z["PlayerSpawnPoints"] if p["Id"] == HUB.get(zid)), None) or (z["PlayerSpawnPoints"] or [None])[0]
        if anchor is None:
            unplaced += 1
            continue
        name = mobs[template]["Name"]
        prop = next((pos for role, pos in PROPS.items() if zid == 93 and role.lower() in name.lower()), None)
        if prop:
            x, y, zz = prop[0] + 1.5, prop[1], prop[2] + 1.5
            facing = 0.0
        else:
            ax, ay, az = anchor["Position"]
            i = ring[anchor["Id"]]; ring[anchor["Id"]] += 1
            radius = 7 + 2.5 * (i // 10)
            angle = i * 36 + (i // 10) * 18
            x = ax + radius * math.sin(math.radians(angle))
            zz = az + radius * math.cos(math.radians(angle))
            y = ay
            facing = (angle + 180) % 360  # face the leystone
        npcs.append({"spawnId": spawn_id, "template": template, "name": name, "conversation": conv, "zone": zid,
                     "x": round(x, 2), "y": round(y, 2), "z": round(zz, 2), "direction": round(facing, 1),
                     "roles": sorted({i.get("Type") for i in icons})})

# ---- shops ------------------------------------------------------------------------------------------------------
# The client knows which shop id a conversation opens, never what it stocks (server-only). Each shop gets a category
# from its menu label, file and NPC name; the server stocks it from the item templates for the NPC's zone level.
CATEGORIES = [
    ("stamps", r"stamp"), ("fashion", r"fashion|shoe|charm|carpet|disguise|costume|\bhat|dye|tailor"),
    ("mount", r"mount|horse|steed|stable|saddle"), ("book", r"book|manual|tome|skill|spell|train"),
    ("armour", r"armou?r|plate|leather"), ("weapon", r"weapon|blacksmith|smith|sword|offhand|bow|dagger|axe|staff|wand|spear|totem"),
    ("jewellery", r"jewel|ring|amulet|necklace|bangle|bracelet|trinket"), ("food", r"food|grocer|cook|bait|fish|ingredient|culinar|baker"),
    ("potion", r"potion|alchem|apothecar|herb|suppl"),
]
conv_zone = {}
conv_npc = {}
for n in npcs:
    conv_zone.setdefault(n["conversation"], n["zone"])
    conv_npc.setdefault(n["conversation"], n["name"])
shops = {}
for cid, c in convs.items():
    for choices in c["lines"].values():
        for conds, action, _, text in choices:
            for m in re.finditer(r"SHOP\s+(\d+)", action):
                sid = int(m.group(1))
                if str(sid) in shops and shops[str(sid)]["zone"] is not None: continue
                classes = sorted({p.split()[1].title() for p in conds if p.startswith("CLASS ") and len(p.split()) > 1})
                hay = " ".join([text, c["file"], conv_npc.get(cid, "")]).lower()
                category = next((cat for cat, rx in CATEGORIES if re.search(rx, hay)), "general")
                # Zone and conversation from a placed NPC that sells this shop (several conversations may).
                seller = next((c2 for c2 in sorted(shop_conv.get(sid, ())) if c2 in conv_zone), cid)
                cid_for_shop, zone = seller, conv_zone.get(seller)
                if zone is None:
                    votes = chdata.zone_votes(c["location"], keywords)
                    zone = votes.most_common(1)[0][0] if votes else None
                shops[str(sid)] = {"category": category, "classes": classes, "zone": zone, "label": text[:60], "conversation": cid_for_shop}

out = f"{chdata.HOME}/extracted/{build}/world.json"
json.dump({"build": build, "approximated": True, "quests": quests, "npcs": npcs, "shops": shops}, open(out, "w"), indent=0)
print(f"shops: {len(shops)}; categories: {dict(collections.Counter(v['category'] for v in shops.values()))}")
per_zone = collections.Counter(n["zone"] for n in npcs)
print(f"quests: {len(quests)} ({sum(1 for q in quests.values() if q['giver'] is not None)} with a giver, "
      f"{sum(1 for q in quests.values() if q['zone'] is not None)} with a zone)")
print(f"npcs: {len(npcs)} placed, {unplaced} without a zone; per zone: {dict(sorted(per_zone.items()))}")
print(f"wrote {out}")
