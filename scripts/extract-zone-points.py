#!/usr/bin/env python3
"""Extract the client's zone-exit tables (TextAssets "Zone<N>_zone_points") for the server.

    scripts/extract-zone-points.py [BUILD]

The client reads them in CZonePointManager.loadZonePointObjects from the Addressables bundle
StreamingAssets/aa/StandaloneOSX/textfiles_assets_all_*.bundle (UnityFS, LZ4). Each CSV line is
    id,minLevel,destZoneId,CYLINDER,x,y,z,radius,halfHeight,questCompletedId
Writes ~/CelticHeroesAnalysis/extracted/<build>/zone-points.json (outside git: it is client data).
"""
import glob, json, os, re, struct, sys

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
home = os.path.expanduser("~/CelticHeroesAnalysis")
# Zone exit tables (Zones/Zone<N>_zone_points) live in the default bundle of the Windows game copy; the text bundle holds
# Zone27's (the Mac build's text bundle has only that one).
windows = os.environ.get("CH_GAME_DATA", os.path.expanduser("~/AppData/Local/CelticHeroesPrivate/game/CelticHeroes_Data"))
bundles = (glob.glob(f"{windows}/StreamingAssets/aa/StandaloneWindows64/default_assets_all_*.bundle")
           + glob.glob(f"{windows}/StreamingAssets/aa/StandaloneWindows64/textfiles_assets_all_*.bundle")
           or glob.glob(f"{home}/client/{build}/CelticHeroes.app/Contents/Resources/Data/StreamingAssets/aa/StandaloneOSX/textfiles_assets_all_*.bundle"))
if not bundles: sys.exit("no zone-points bundle found")

def lz4_block(src):
    dst, i, n = bytearray(), 0, len(src)
    while i < n:
        token = src[i]; i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255: break
        dst += src[i:i + lit]; i += lit
        if i >= n: break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255: break
        ml += 4
        start = len(dst) - off
        for k in range(ml): dst.append(dst[start + k])
    return bytes(dst)

zones = {}
def read_bundle(path):
    f = open(path, "rb").read()
    if not f.startswith(b"UnityFS\0"): return
    p = 8 + 4
    for _ in range(2): p = f.index(b"\0", p) + 1
    _, cbi, ubi, flags = struct.unpack(">qIII", f[p:p + 20]); p += 20
    if flags & 0x80: sys.exit("blocks info at end of file is not supported")
    p = (p + 15) & ~15
    info = f[p:p + cbi] if flags & 0x3F == 0 else lz4_block(f[p:p + cbi]); p += cbi
    if flags & 0x200: p = (p + 15) & ~15
    q = 16
    count = struct.unpack(">i", info[q:q + 4])[0]; q += 4
    data = bytearray()
    for _ in range(count):
        usize, csize, bflags = struct.unpack(">IIH", info[q:q + 10]); q += 10
        chunk = f[p:p + csize]; p += csize
        data += lz4_block(chunk) if bflags & 0x3F in (2, 3) else chunk
    data = bytes(data)

    # TextAsset: int32 name length, name (4-byte aligned), int32 script length, script bytes.
    for m in re.finditer(rb"(?i)zone(\d+)_zone_points", data):
        i = m.start()
        if i < 4 or struct.unpack_from("<i", data, i - 4)[0] != len(m.group(0)): continue
        j = (m.end() + 3) & ~3
        size = struct.unpack_from("<i", data, j)[0]
        if not 0 <= size < 1_000_000: continue
        text = data[j + 4:j + 4 + size].decode("utf-8-sig")
        rows = []
        for line in text.splitlines():
            cols = [c.strip() for c in line.split(",")]
            if len(cols) < 10 or not cols[0].lstrip("-").isdigit(): continue
            if cols[3].upper() != "CYLINDER": sys.exit(f"unsupported zone point shape: {line}")
            rows.append({"id": int(cols[0]), "minLevel": int(cols[1]), "destZone": int(cols[2]),
                         "x": float(cols[4]), "y": float(cols[5]), "z": float(cols[6]),
                         "radius": float(cols[7]), "halfHeight": float(cols[8]), "quest": int(cols[9])})
        zones[int(m.group(1))] = rows

for path in bundles: read_bundle(path)

if 2 not in zones or not zones[2]: sys.exit("zone 2 exits not found")
out = f"{home}/extracted/{build}/zone-points.json"
json.dump({"build": build, "zones": {str(k): v for k, v in sorted(zones.items())}}, open(out, "w"), indent=1)
print(f"wrote {sum(len(v) for v in zones.values())} exits in {len(zones)} zones to {out}")
