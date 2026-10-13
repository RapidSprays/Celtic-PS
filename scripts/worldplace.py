"""Placement on the real ground of a zone (docs/world-placement.md), from extract-zone-worlds.py's zone-worlds.json.

Used by gen-world.py (NPCs) and gen-zone-population.py (monster camps). A zone without a usable mask (missing, or
blank: Palace, Valley of the Ancients) answers "walkable" everywhere, so callers fall back to their old placement.
"""
import json, math, os, re

import chdata


class ZoneWorld:
    def __init__(self, zid, data):
        self.zid = zid
        self.name = data.get("name")
        minx, minz, maxx, maxz = data["bounds"]
        self.minx, self.minz, self.w, self.h = minx, minz, maxx - minx, maxz - minz
        rows = data.get("mask") or []
        walk = sum(r.count("1") for r in rows) / max(1, len(rows) * len(rows[0])) if rows else 0
        self.rows = rows if rows and 0.02 < walk < 0.98 else None
        self.n = len(rows) if rows else 0
        self.objects = [(o[0], o[1], o[2], o[3]) for o in data.get("objects", [])]

    @property
    def has_mask(self):
        return self.rows is not None

    @property
    def metres_per_pixel(self):
        return self.w / self.n if self.n else 1.0

    def _px(self, x, z):
        return int((x - self.minx) / self.w * self.n), int((1 - (z - self.minz) / self.h) * self.n)

    def _world(self, u, v):
        return self.minx + (u + 0.5) / self.n * self.w, self.minz + (1 - (v + 0.5) / self.n) * self.h

    def walkable(self, x, z):
        if not self.rows: return True
        u, v = self._px(x, z)
        return 0 <= u < self.n and 0 <= v < self.n and self.rows[v][u] == "1"

    def open_fraction(self, x, z, radius):
        """Share of the ground within radius that is walkable (1.0 without a mask)."""
        if not self.rows: return 1.0
        step = max(1.5, self.metres_per_pixel)
        total = ok = 0
        r = 0.0
        while r <= radius:
            count = max(1, int(2 * math.pi * r / step)) if r else 1
            for i in range(count):
                a = 2 * math.pi * i / count
                total += 1
                ok += self.walkable(x + r * math.cos(a), z + r * math.sin(a))
            r += step
        return ok / total

    def nearest_walkable(self, x, z, max_radius=40.0, min_radius=0.0, avoid=(), spacing=0.0, toward=None):
        """The walkable spot nearest (x, z), at least min_radius away and spacing from every point in avoid. With
        toward, ties prefer the side facing that point (a building's front towards its village)."""
        step = max(1.0, self.metres_per_pixel / 2)
        r = min_radius
        while r <= max_radius:
            count = max(1, int(2 * math.pi * r / step)) if r else 1
            candidates = []
            for i in range(count):
                a = 2 * math.pi * i / count
                cx, cz = x + r * math.cos(a), z + r * math.sin(a)
                if not self.walkable(cx, cz): continue
                # Not on the edge of a footprint: the neighbouring ground is walkable too.
                if self.rows and not all(self.walkable(cx + dx, cz + dz) for dx, dz in ((1.5, 0), (-1.5, 0), (0, 1.5), (0, -1.5))): continue
                if any(math.hypot(cx - ax, cz - az) < spacing for ax, az in avoid): continue
                score = math.hypot(cx - toward[0], cz - toward[1]) if toward else 0
                candidates.append((score, cx, cz))
            if candidates:
                _, cx, cz = min(candidates)
                return round(cx, 2), round(cz, 2)
            r += step
        return None

    def random_walkable(self, rng, tries=400):
        if not self.rows: return None
        for _ in range(tries):
            u, v = rng.randrange(self.n), rng.randrange(self.n)
            if self.rows[v][u] == "1": return self._world(u, v)
        return None

    def find(self, pattern, near=None, within=None):
        """Named objects matching a regex (case-insensitive), nearest to `near` first."""
        rx = re.compile(pattern, re.I)
        hits = [(n, x, y, z) for n, x, y, z in self.objects if rx.search(n)]
        if near is not None:
            hits.sort(key=lambda o: math.hypot(o[1] - near[0], o[3] - near[1]))
            if within is not None: hits = [o for o in hits if math.hypot(o[1] - near[0], o[3] - near[1]) <= within]
        return hits


def load(build):
    path = f"{chdata.HOME}/extracted/{build}/zone-worlds.json"
    if not os.path.exists(path): return {}
    data = json.load(open(path))
    return {int(zid): ZoneWorld(int(zid), z) for zid, z in data["zones"].items()}


# NPC name or title → world objects they belong beside. First match wins; order from specific to general.
NPC_PLACES = [
    (r"lighthouse", r"lighthouse"),
    (r"\bmiller\b", r"mill"),
    (r"\bbaker\b", r"bak|oven|bread"),
    (r"statue", r"statue"),
    (r"leystone", r"^leystone$|int_leystone"),
    (r"blacksmith|\bsmith\b|armourer|weaponsmith", r"forge|anvil|smith"),
    (r"banker|\bbank\b", r"bank|mailbox|vault"),
    (r"stable|horse|mount|saddle", r"stable|horse|hay"),
    (r"captain|sailor|seafarer|fisher|boat|harbour|dock|pirate", r"boathouse|pier|dock|jetty|boat|ship"),
    (r"fishing", r"pier|dock|jetty|fish"),
    (r"\bcook\b|culinar|chef", r"kitchen|cauldron|cook|tavern"),
    (r"innkeeper|barkeep|bard|tavern|brewer", r"tavern|\binn\b|bar_|barrel"),
    (r"farmer|farmhand", r"farm|field|wheat|barn|crop|plough"),
    (r"herder|shepherd|swineherd", r"pen|fence|barn|sheep|pig"),
    (r"druid", r"stone_?circle|standing|menhir|druid|altar|temple|grove"),
    (r"priest|monk|temple", r"temple|shrine|altar|church"),
    (r"sergeant|guard|soldier|watch|sentry|warden|militia", r"watchtower|tower|gate|barracks|guard"),
    (r"merchant|trader|vendor|\bshop|skill merchant|seller|jeweller|tailor", r"stall|market|vendor|shop|table"),
    (r"barber", r"barber"),
    (r"archer|ranger|hunt", r"^ranger|archery|target|hunt|camp"),
]

# Buildings and village furniture NPCs without a better match stand beside, one per object, around their village.
VILLAGE_OBJECTS = r"cottage|house|hut|barn|stable|silo|stage|bench|lamppost|lamp_?post|well|watch_?tower|stall|tent|campfire|buildings_village|shed|cart|fountain"


def npc_target(name):
    for npc_rx, obj_rx in NPC_PLACES:
        if re.search(npc_rx, name, re.I): return obj_rx
    return None


# Monsters that live in camps (tents, campfires) rather than roaming open ground.
CAMP_DWELLERS = re.compile(r"raider|bandit|brigand|draken|thief|thieves|smuggler|pirate|cultist|outlaw|marauder|deserter|mercenar|soldier|warrior|poacher|rogue|scout|zealot|fomor|goblin|bogle|kobold", re.I)
CAMP_OBJECTS = r"camp|tent|campfire|fire_?pit|bedroll"
