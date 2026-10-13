#!/usr/bin/env python3
"""Read each zone's walkable area and named world objects from the pinned client (read-only; docs/world-placement.md).

  ~/CelticHeroesAnalysis/venv/bin/python scripts/extract-zone-worlds.py [build]

Needs UnityPy (installed in ~/CelticHeroesAnalysis/venv, never in the repo). Writes
~/CelticHeroesAnalysis/extracted/<build>/zone-worlds.json (outside git):
  zones.<id>.bounds   [minX, minZ, maxX, maxZ]: the client's map bounds (CMapManager: Zones MinPos/MaxPos)
  zones.<id>.mask     rows top to bottom of the map mask (<map>_mask.png, the map screen's fog mask), one character per
                      pixel: "1" walkable ground, "2" cliffs or edges, "0" sea, walls and building footprints
  zones.<id>.objects  [name, x, y, z] for every named object in the zone's scene (world positions; Transform chains)
Map pixel (u, v) of world (x, z): u = (x - minX) / width, v = 1 - (z - minZ) / height (MapScreen.SetupPlayerIcon).
"""
import glob, json, math, os, re, sys

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chdata  # noqa: E402

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
bundles = f"{chdata.HOME}/client/{build}/CelticHeroes.app/Contents/Resources/Data/StreamingAssets/aa/StandaloneOSX/"
out_path = f"{chdata.HOME}/extracted/{build}/zone-worlds.json"
bal = chdata.balance(build)

# Auto-generated modelling names carry no meaning (pCube999, polySurface4902, Box074, Plane).
NOISE = re.compile(r"^(p[A-Z][a-z]+\d*|polySurface\d+|Box\d+|Plane\d*|Cylinder\d*|Sphere\d*|group\d*|null\d*|transform\d*|Object\d+|default)$")


def mask_rows(map_file):
    name = map_file.replace(".png", "")
    paths = glob.glob(f"{bundles}*maptextures_{name}_mask.png_*.bundle")
    if not paths: return None
    for obj in UnityPy.load(paths[0]).objects:
        if obj.type.name == "Texture2D":
            img = obj.read().image.convert("L")
            w, h = img.size
            px = img.load()
            # Light grey (~204) walkable, dark grey (~74) cliffs and edges, black blocked.
            return ["".join("1" if px[x, y] > 150 else "2" if px[x, y] > 40 else "0" for x in range(w)) for y in range(h)]
    return None


def quat_rotate(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    ix, iy, iz, iw = w * vx + y * vz - z * vy, w * vy + z * vx - x * vz, w * vz + x * vy - y * vx, -x * vx - y * vy - z * vz
    return (ix * w + iw * -x + iy * -z - iz * -y, iy * w + iw * -y + iz * -x - ix * -z, iz * w + iw * -z + ix * -y - iy * -x)


def quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def scene_objects(scene):
    paths = [p for p in glob.glob(f"{bundles}{scene}_scenes_*.bundle") if "lowend" not in p]
    if not paths: return []
    env = UnityPy.load(paths[0])
    transforms, names = {}, {}
    for obj in env.objects:
        if obj.type.name == "GameObject": names[obj.path_id] = obj.read().m_Name
        elif obj.type.name == "Transform": transforms[obj.path_id] = obj.read()
    cache = {}

    def world(pid):
        if pid in cache: return cache[pid]
        stack = []
        while pid in transforms and pid not in cache:
            stack.append(pid)
            pid = transforms[pid].m_Father.path_id
        for p in reversed(stack):
            t = transforms[p]
            lp, lr, ls = t.m_LocalPosition, t.m_LocalRotation, t.m_LocalScale
            rot, scale = (lr.x, lr.y, lr.z, lr.w), (ls.x, ls.y, ls.z)
            father = cache.get(t.m_Father.path_id)
            if father:
                (fp, fr, fs) = father
                v = quat_rotate(fr, (lp.x * fs[0], lp.y * fs[1], lp.z * fs[2]))
                cache[p] = ((fp[0] + v[0], fp[1] + v[1], fp[2] + v[2]), quat_mul(fr, rot), (fs[0] * scale[0], fs[1] * scale[1], fs[2] * scale[2]))
            else:
                cache[p] = ((lp.x, lp.y, lp.z), rot, scale)
        return cache[stack[0]] if stack else cache[pid]

    found = []
    for pid, t in transforms.items():
        name = names.get(t.m_GameObject.path_id, "")
        if not name or NOISE.match(name): continue
        pos = world(pid)[0]
        if pos == (0.0, 0.0, 0.0): continue  # containers at the origin; their children carry the positions
        found.append([name, round(pos[0], 2), round(pos[1], 2), round(pos[2], 2)])
    return found


result = {"build": build, "zones": {}}
for z in chdata.entries(bal, "Zones"):
    zid, scene, map_file = z["ZoneId"], z.get("SceneFilename"), z.get("MapFilename") or ""
    (minx, minz), (maxx, maxz) = z["MinPos"], z["MaxPos"]
    rows = mask_rows(map_file) if map_file else None
    objects = scene_objects(scene) if scene else []
    result["zones"][str(zid)] = {"name": z.get("ScreenName"), "scene": scene, "bounds": [minx, minz, maxx, maxz], "mask": rows, "objects": objects}
    walk = sum(r.count("1") for r in rows) / (len(rows) * len(rows[0])) if rows else 0
    # Self-check: the zone's leystones (real walkable points) must land on walkable pixels.
    on_ground = 0
    for p in z.get("PlayerSpawnPoints", []) if rows else []:
        u = int((p["Position"][0] - minx) / (maxx - minx) * len(rows))
        v = int((1 - (p["Position"][2] - minz) / (maxz - minz)) * len(rows))
        on_ground += 0 <= u < len(rows) and 0 <= v < len(rows) and rows[v][u] == "1"
    stones = len(z.get("PlayerSpawnPoints", [])) if rows else 0
    print(f"{zid:>3} {z.get('ScreenName') or '':<24} mask {'%3d%% walkable' % (walk * 100) if rows else 'none':<14} "
          f"objects {len(objects):>5}  leystones on walkable ground {on_ground}/{stones}")

json.dump(result, open(out_path, "w"))
print(f"wrote {out_path} ({os.path.getsize(out_path) // 1024} KB)")
