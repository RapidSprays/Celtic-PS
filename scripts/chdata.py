"""Shared readers for the client data under ~/CelticHeroesAnalysis (used by the gen-*/extract-* scripts).

Nothing here writes files; callers write their outputs to ~/CelticHeroesAnalysis/extracted/<build>/ (outside git).
"""
import collections, glob, json, os, re, sqlite3, struct

HOME = os.path.expanduser("~/CelticHeroesAnalysis")


def balance(build):
    return json.load(open(f"{HOME}/extracted/{build}/balance-client.json", encoding="utf-8-sig"))


def entries(bal, name):
    return [v for k, v in bal[name].items() if k != "$id"]


def textdb(build):
    return sqlite3.connect(f"file:{HOME}/snapshots/{build}/Contents/Resources/Data/StreamingAssets/text.db?mode=ro", uri=True)


# ---- Addressables textfiles bundle (UnityFS, LZ4) ---------------------------------------------------------------

def _lz4_block(src):
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


def textfiles_bundle(build):
    """The decompressed bytes of textfiles_assets_all_*.bundle (zone points, conversation XML, ...)."""
    paths = glob.glob(f"{HOME}/client/{build}/CelticHeroes.app/Contents/Resources/Data/StreamingAssets/aa/StandaloneOSX/textfiles_assets_all_*.bundle")
    if len(paths) != 1: raise SystemExit(f"expected one textfiles bundle, found {len(paths)}")
    f = open(paths[0], "rb").read()
    if not f.startswith(b"UnityFS\0"): raise SystemExit("not a UnityFS bundle")
    p = 8 + 4
    for _ in range(2): p = f.index(b"\0", p) + 1
    _, cbi, ubi, flags = struct.unpack(">qIII", f[p:p + 20]); p += 20
    if flags & 0x80: raise SystemExit("blocks info at end of file is not supported")
    p = (p + 15) & ~15
    info = f[p:p + cbi] if flags & 0x3F == 0 else _lz4_block(f[p:p + cbi]); p += cbi
    if flags & 0x200: p = (p + 15) & ~15
    q = 16
    count = struct.unpack(">i", info[q:q + 4])[0]; q += 4
    data = bytearray()
    for _ in range(count):
        usize, csize, bflags = struct.unpack(">IIH", info[q:q + 10]); q += 10
        chunk = f[p:p + csize]; p += csize
        data += _lz4_block(chunk) if bflags & 0x3F in (2, 3) else chunk
    return bytes(data)


def text_assets(data, name_pattern=rb"[A-Za-z0-9_]+"):
    """TextAssets (int32 name length, name, 4-byte align, int32 size, bytes) whose name matches name_pattern."""
    found = {}
    for m in re.finditer(name_pattern, data):
        i = m.start()
        if i < 4 or struct.unpack_from("<i", data, i - 4)[0] != len(m.group(0)): continue
        j = (m.end() + 3) & ~3
        if j + 4 > len(data): continue
        size = struct.unpack_from("<i", data, j)[0]
        if not 0 <= size < 2_000_000: continue
        found.setdefault(m.group(0).decode(), data[j + 4:j + 4 + size].decode("utf-8-sig", "replace"))
    return found


# ---- which zone a quest belongs to ------------------------------------------------------------------------------

# Approximated level bands, in the order players reach the zones.
LEVELS = {
    2: (1, 15), 93: (5, 20), 9: (15, 35), 22: (3, 8), 4: (8, 14), 5: (12, 20), 6: (16, 30), 3: (25, 45), 11: (35, 55),
    10: (45, 70), 12: (60, 80), 13: (70, 90), 7: (80, 120), 15: (100, 110), 16: (108, 118), 17: (116, 126),
    18: (124, 134), 19: (132, 142), 20: (140, 150), 24: (150, 180), 27: (180, 220),
}

EXTRA_KEYWORDS = {
    "highshore": 2, "lir's reach": 2, "lyr's reach": 2, "farcrag": 93, "tavern": 9, "stonevale": 3, "crookback": 4,
    "dustwither": 5, "shalemont": 6, "otherworld": 7, "carrowmore": 10, "fingal": 11, "dunskeig": 12, "murky vault": 13,
    "bridge of despair": 15, "infernal armour": 16, "alchemical quarter": 17, "summoning chamber": 18,
    "forbidden hall": 19, "arcane sanctum": 20, "heroes landing": 22, "heroes' landing": 22, "corrupted garden": 24,
    "valley of the ancients": 27, "maccroin": 6, "greygorge": 6, "loch dorcha": 11, "fianna": 10,
}

SEASONAL = re.compile(r"midwinter|winter|samhain|beltane|harvest|christmas|yule|easter|valentine|halloween|"
                      r"lughnasa|imbolc|frost festival|snow|anniversary|pumpkin|spooky|event", re.I)


def zone_keywords(zones):
    """Lowercase place names (zone names, leystone names) → zone id, for names that belong to one zone only."""
    kw = {}
    for zid, z in zones.items():
        if zid not in LEVELS: continue
        for n in [z["ScreenName"]] + [leystone_name(p) for p in z["PlayerSpawnPoints"]]:
            if len(n) >= 6: kw.setdefault(n.lower(), set()).add(zid)
    kw = {k: next(iter(v)) for k, v in kw.items() if len(v) == 1}
    kw.update(EXTRA_KEYWORDS)
    return kw


def leystone_name(p):
    return p["Description"].replace("\\n", " ").replace("\n", " ").replace("(Free Travel)", "").strip()


def zone_votes(text, keywords):
    hits = collections.Counter()
    t = text.lower()
    for k, zid in keywords.items():
        c = t.count(k)
        if c: hits[zid] += c * len(k)
    return hits
