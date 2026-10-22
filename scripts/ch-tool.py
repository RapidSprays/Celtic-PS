#!/usr/bin/env python3
"""CH Server Tool: a window for the owner's own content, so nothing has to be typed into JSON by hand.

    python scripts/ch-tool.py        (or double-click chSERVER\\ch-tool.cmd)

Tabs:
  Spawns    the saved spawns (server database): add, move, delete; copy a character's last position.
  Monsters  server/content/custom-monsters.json: copies of a monster with your own numbers and boss abilities.
  Game monsters  server/content/monster-overrides.json: the game's own monsters (armour bosses, legacy 5/6 stars,
                 raid bosses...): level, health, attack, defence, resists, evasion, XP, gold, size; drops in spawn-tuning.json.
  Crits & skills  server/content/crit-chances.json and ability-gain.json: crit chances and how fast the crit abilities
                 train; skill-overrides.json: how often a skill lands on monsters (evasionPercent, minLandChance).
  Gear      server/content/custom-gear.json: rings, necklaces, bracelets, charms, armour, weapons.
  Install   puts gear, monsters and quests into the game copy (scripts/install-custom-gear.py).

Every save keeps the previous file in server/content/.backup (and the database in server-data/.backup). Restart the
server after changes; Install needs the game closed.
"""
import datetime, json, os, re, shutil, sqlite3, subprocess, sys
import tkinter as tk
from tkinter import ttk, messagebox, colorchooser
try:
    from PIL import Image, ImageTk  # the zone maps (Master's Grimoire's Resources/Maps)
except ImportError:
    Image = ImageTk = None

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))                 # chSERVER
CONTENT = os.path.join(HERE, "..", "server", "content")
BALANCE = os.path.join(ROOT, "grimoire-world", "balance-client.json")
DB = os.path.expanduser(r"~\CelticHeroesAnalysis\server-data\private-server.db")
MONSTERS = os.path.join(CONTENT, "custom-monsters.json")
GEAR = os.path.join(CONTENT, "custom-gear.json")
TUNING = os.path.join(CONTENT, "spawn-tuning.json")
MAPS = os.path.join(ROOT, "master", "Resources", "Maps")
WORLD = os.path.join(ROOT, "grimoire-world", "world.json")
POPULATION = os.path.join(ROOT, "grimoire-world", "zone-population.json")
MOB_STATS = os.path.join(ROOT, "grimoire-world", "mob-stats.json")
LOOT = os.path.join(ROOT, "grimoire-world", "loot.json")
OVERRIDES = os.path.join(CONTENT, "monster-overrides.json")
CRITS = os.path.join(CONTENT, "crit-chances.json")
SKILL_OVERRIDES = os.path.join(CONTENT, "skill-overrides.json")
ABILITY_GAIN = os.path.join(CONTENT, "ability-gain.json")
# Raid bosses (the test copies 200182/200183 left out)
RAID_BOSSES = [70722, 73000, 73708, 100002, 102982, 141103, 141966, 142027]

DAMAGE_TYPES = ["Pierce", "Slash", "Crush", "Heat", "Cold", "Magic", "Poison", "Divine", "Chaos", "True"]
RESIST_TYPES = ["Pierce", "Slash", "Crush", "Heat", "Cold", "Magic", "Poison", "Divine", "Chaos"]
EVASIONS = ["Physical", "Spell", "Movement", "Wounding", "Weakening", "Mental"]
STATS = ["Strength", "Dexterity", "Focus", "Vitality"]
BONUSES = ["Health", "Energy", "Attack", "Defence", "Concentration"]
CLASSES = ["Warrior", "Druid", "Mage", "Ranger", "Rogue"]
# Gear templates: what to copy the look of (model, icon, slot, weapon animation). Jewellery uses the default looks.
TEMPLATES = [
    ("Main hand - Bow", 72430), ("Main hand - Dagger", 72439), ("Main hand - Spear", 72448), ("Main hand - Sword", 72385),
    ("Main hand - Greatsword", 72028), ("Main hand - Axe", 72026), ("Main hand - Greataxe", 72029), ("Main hand - Hammer", 72027),
    ("Main hand - Great Hammer", 72030), ("Main hand - Staff", 72035), ("Main hand - Wand", 72421), ("Main hand - Totem", 72412),
    ("Offhand - Dagger", 72038), ("Offhand - Blade", 34996), ("Offhand - Quiver", 63404), ("Offhand - Shield", 72040),
    ("Offhand - Grimoire", 63406),
    ("Armour - Head", ("slot", "Head")), ("Armour - Chest", ("slot", "Chest")), ("Armour - Legs", ("slot", "Leg")),
    ("Armour - Hands", ("slot", "Hands")), ("Armour - Feet", ("slot", "Feet")), ("Offhand - any (choose)", ("slot", "Offhand")),
    ("Main hand - any weapon (choose)", ("slot", "Weapon")),
    ("Jewellery - Ring", "ring"), ("Jewellery - Amulet", "necklace"), ("Jewellery - Bracer", "bracelet"), ("Jewellery - Misc (charm)", "charm"),
]

KINDS = {
    "weaponBuff": "Adds damage to its auto-attacks for a while (Venom Coat)",
    "selfHot": "Heals itself every few seconds while it lasts (Morrigan's Embrace)",
    "selfHeal": "A cast heal at health thresholds, can be interrupted (Dark Touch)",
    "aoeStun": "Area damage and stun every so often (Priestess's Mezmorize)",
}
KIND_FIELDS = {  # field, label, default
    "weaponBuff": [("damage", "Damage per hit", 75), ("damageType", "Damage type", "Poison"), ("duration", "Lasts (s)", 60), ("recastAfter", "Recast after it ends (s)", 30)],
    "selfHot": [("heal", "Heal per tick", 50), ("every", "Every (s)", 5), ("duration", "Lasts (s)", 45)],
    "selfHeal": [("heal", "Heal", 5000), ("castTime", "Cast time (s)", 2), ("atHealth", "At health % (comma list)", "85, 60, 40, 20, 10"), ("interruptedBy", "Interrupted by skills (comma list)", "Shield Bash")],
    "aoeStun": [("damage", "Damage", 150), ("damageType", "Damage type", "Magic"), ("radius", "Radius (m)", 8), ("stunSeconds", "Stun (s)", 4), ("cooldown", "Every (s)", 45), ("evadedBy", "Evaded by", "Movement")],
}


# ---------------------------------------------------------------- data helpers
def lenient_load(path, default):
    if not os.path.exists(path): return default
    text = open(path, encoding="utf-8-sig").read()
    return json.loads(re.sub(r",(\s*[\]\}])", r"\1", text))


def backup(path):
    if not os.path.exists(path): return
    folder = os.path.join(os.path.dirname(path), ".backup")
    os.makedirs(folder, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    shutil.copyfile(path, os.path.join(folder, f"{os.path.basename(path)}.{stamp}"))


def save_json(path, data, indent=1):
    backup(path)
    with open(path, "w", encoding="utf-8") as f: json.dump(data, f, indent=indent, ensure_ascii=False)


def server_running():
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command",
                              "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { $_.CommandLine -like '*CH.Server.dll*' } | Measure-Object | Select-Object -ExpandProperty Count"],
                             capture_output=True, text=True, timeout=20).stdout.strip()
        return out not in ("", "0")
    except Exception:
        return False


class Game:
    """Names from the game data (monsters, items, skills, zones), loaded once."""
    def __init__(self):
        b = json.load(open(BALANCE, encoding="utf-8-sig"))
        def ent(name):
            v = b[name]
            return [x for x in (v.values() if isinstance(v, dict) else v) if isinstance(x, dict)]
        self.mobs = {int(k): v["Name"] for k, v in b["MobTemplates"].items() if isinstance(v, dict)}
        self.items = {int(k): v["Name"] for k, v in b["ItemTemplates"].items() if isinstance(v, dict)}
        self.item_slot = {int(k): v.get("EquipSlot") for k, v in b["ItemTemplates"].items() if isinstance(v, dict)}
        self.item_raw = {int(k): v for k, v in b["ItemTemplates"].items() if isinstance(v, dict)}  # for "Start from a game item"
        self.abilities = {a["Id"]: a["Name"] for a in ent("Abilities") if "Id" in a and "Name" in a}
        self.skills = {int(k): v.get("Name", "") for k, v in b["SkillTemplates"].items() if isinstance(v, dict)}
        self.effects = {v["Id"] for v in ent("StatusEffects")}
        # pets a monster can be drawn as (client mod MobPetBody): prefab -> name
        self.pets = {v["PrefabName"]: v.get("Name", v["PrefabName"]) for v in ent("PetAppearances") if v.get("PrefabName")}
        self.bounds = {int(z["ZoneId"]): (z["MinPos"], z["MaxPos"]) for z in ent("Zones") if "MinPos" in z and "ZoneId" in z}
        self.zones = {}
        try:  # names from the zone population file (the client data's zones have no readable names)
            pop = json.load(open(os.path.join(ROOT, "grimoire-world", "zone-population.json"), encoding="utf-8-sig"))["zones"]
            self.zones = {int(k): v.get("name", k) for k, v in pop.items()}
        except Exception:
            pass


# ---------------------------------------------------------------- picker dialog
def pick(parent, title, entries):
    """A searchable list; returns (id, name) or None."""
    win = tk.Toplevel(parent)
    win.title(title)
    win.geometry("520x480")
    win.transient(parent)
    q = tk.StringVar()
    ttk.Entry(win, textvariable=q).pack(fill="x", padx=8, pady=6)
    lb = tk.Listbox(win, activestyle="dotbox")
    lb.pack(fill="both", expand=True, padx=8)
    shown = []
    result = {}
    def refresh(*_):
        lb.delete(0, "end")
        shown.clear()
        needle = q.get().lower().strip()
        for i, n in entries:
            if not needle or needle in n.lower() or needle == str(i):
                shown.append((i, n))
                lb.insert("end", f"{i}  {n}")
                if len(shown) >= 400: break
    def ok(*_):
        sel = lb.curselection()
        if sel: result["v"] = shown[sel[0]]
        win.destroy()
    q.trace_add("write", refresh)
    lb.bind("<Double-Button-1>", ok)
    ttk.Button(win, text="Choose", command=ok).pack(pady=6)
    refresh()
    win.grab_set()
    parent.wait_window(win)
    return result.get("v")


def kv_text(d):
    return ", ".join(f"{k}: {v}" for k, v in (d or {}).items())


def parse_kv(text, allowed=None, what="value"):
    out = {}
    for part in [p.strip() for p in text.split(",") if p.strip()]:
        if ":" not in part: raise ValueError(f"'{part}' needs a colon, like Strength: 40")
        k, v = [x.strip() for x in part.split(":", 1)]
        if allowed:
            match = [a for a in allowed if a.lower() == k.lower()]
            if not match: raise ValueError(f"unknown {what} '{k}' (use {', '.join(allowed)})")
            k = match[0]
        out[k] = int(float(v))
    return out


# ---------------------------------------------------------------- the app
class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("CH Server Tool")
        self.geometry("1100x720")
        try: self.game = Game()
        except Exception as e:
            messagebox.showerror("CH Server Tool", f"Couldn't read the game data:\n{BALANCE}\n\n{e}")
            raise SystemExit(1)
        nb = ttk.Notebook(self)
        nb.pack(fill="both", expand=True)
        self.spawns = SpawnsTab(nb, self)
        self.monsters = MonstersTab(nb, self)
        self.game_mobs = GameMonstersTab(nb, self)
        self.crits = CritsTab(nb, self)
        self.gear = GearTab(nb, self)
        self.install = InstallTab(nb, self)
        nb.add(self.spawns, text="Spawns")
        nb.add(self.monsters, text="Monsters")
        nb.add(self.game_mobs, text="Game monsters")
        nb.add(self.crits, text="Crits & skills")
        nb.add(self.gear, text="Gear")
        nb.add(self.install, text="Install")

    def monster_name(self, tid):
        custom = {m["id"]: m["name"] for m in lenient_load(MONSTERS, {"monsters": []}).get("monsters", [])}
        return custom.get(tid) or self.game.mobs.get(tid) or f"monster {tid}"

    def all_monsters(self):
        custom = [(m["id"], m["name"] + "  (custom)") for m in lenient_load(MONSTERS, {"monsters": []}).get("monsters", [])]
        return custom + sorted(self.game.mobs.items(), key=lambda x: x[1].lower())


def colour_row(frame, row, var, col=0):
    """A "Colour" field: #RRGGBB (blank = the model's own), a Pick… button and a swatch."""
    ttk.Label(frame, text="Colour (blank = its own)").grid(row=row, column=col, sticky="w", padx=4, pady=2)
    box = ttk.Frame(frame)
    box.grid(row=row, column=col + 1, sticky="w", padx=4, pady=2)
    ttk.Entry(box, textvariable=var, width=10).pack(side="left")
    swatch = tk.Label(box, width=3, relief="groove")
    swatch.pack(side="left", padx=4)
    def show(*_):
        v = var.get().strip()
        try: swatch.config(bg=v if re.fullmatch(r"#[0-9a-fA-F]{6}", v) else "SystemButtonFace")
        except tk.TclError: pass
    def choose():
        got = colorchooser.askcolor(color=var.get().strip() or "#ffffff", title="Monster colour")
        if got and got[1]: var.set(got[1].upper())
    ttk.Button(box, text="Pick…", command=choose).pack(side="left")
    ttk.Button(box, text="Clear", command=lambda: var.set("")).pack(side="left", padx=2)
    var.trace_add("write", show)
    show()


def check_hit(dmg, kind):
    """Auto-attack damage (whole number) and its type (blank = the copied monster's)."""
    out = {}
    if dmg.strip():
        out["hitDamage"] = int(float(dmg.replace(",", "")))
        if out["hitDamage"] < 0: raise ValueError("auto-attack damage is 0 or more")
    if kind.strip():
        match = [t for t in DAMAGE_TYPES if t.lower() == kind.strip().lower()]
        if not match: raise ValueError(f"damage type is one of {', '.join(DAMAGE_TYPES)}")
        out["hitDamageType"] = match[0]
    return out


def check_colour(v):
    v = v.strip()
    if v and not re.fullmatch(r"#[0-9a-fA-F]{6}", v): raise ValueError("colour is like #FF4020 (or blank)")
    return v.upper()


def form_row(frame, row, label, var, width=28, col=0):
    ttk.Label(frame, text=label).grid(row=row, column=col, sticky="w", padx=4, pady=2)
    e = ttk.Entry(frame, textvariable=var, width=width)
    e.grid(row=row, column=col + 1, sticky="we", padx=4, pady=2)
    return e


# ---------------------------------------------------------------- map picker
class MapPicker(tk.Toplevel):
    """A zone's map (Master's Grimoire) with its monster spots (grey), NPCs (blue) and your saved spawns (red). Click to
    pick a spot: x and z come from the click, the height from the nearest known spot. Wheel zooms, right-drag moves."""

    def __init__(self, parent, app, on_pick, zone=None):
        super().__init__(parent)
        self.app, self.on_pick = app, on_pick
        self.title("Pick a spot on the map")
        self.geometry("1150x860")
        if Image is None:
            ttk.Label(self, text="The map needs the Pillow library (pip install pillow).").pack(padx=20, pady=20)
            return
        self.maps = {int(re.match(r"z(\d+)_map", f, re.I).group(1)): os.path.join(MAPS, f)
                     for f in os.listdir(MAPS) if re.match(r"z\d+_map\.(jpg|png)$", f, re.I)} if os.path.isdir(MAPS) else {}
        left = ttk.Frame(self)
        left.pack(side="left", fill="y", padx=6, pady=6)
        ttk.Label(left, text="Zones").pack(anchor="w")
        self.zlist = tk.Listbox(left, width=26, height=30)
        self.zlist.pack(fill="y", expand=True)
        self.zone_ids = sorted(self.maps, key=lambda z: app.game.zones.get(z, str(z)))
        for z in self.zone_ids: self.zlist.insert("end", f"{app.game.zones.get(z, z)}  ({z})")
        self.zlist.bind("<<ListboxSelect>>", lambda *_: self.show(self.zone_ids[self.zlist.curselection()[0]]) if self.zlist.curselection() else None)
        ttk.Label(left, text="grey: monster spots\nblue: NPCs\nred: your saved spawns\n\nclick: pick a spot\nwheel: zoom\nright-drag: move",
                  foreground="gray").pack(anchor="w", pady=6)
        right = ttk.Frame(self)
        right.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        self.canvas = tk.Canvas(right, bg="#1d2b33", highlightthickness=0)
        self.canvas.pack(fill="both", expand=True)
        bar = ttk.Frame(right)
        bar.pack(fill="x", pady=4)
        self.info = ttk.Label(bar, text="Choose a zone, then click on the map.")
        self.info.pack(side="left")
        ttk.Button(bar, text="Use this spot", command=self.use).pack(side="right")
        self.hover = ttk.Label(right, text="", foreground="gray")
        self.hover.pack(fill="x")
        self.canvas.bind("<Button-1>", self.click)
        self.canvas.bind("<MouseWheel>", self.wheel)
        self.canvas.bind("<ButtonPress-3>", lambda e: self.canvas.scan_mark(e.x, e.y))
        self.canvas.bind("<B3-Motion>", lambda e: self.canvas.scan_dragto(e.x, e.y, gain=1))
        self.canvas.bind("<Motion>", self.motion)
        self.zoom, self.zone, self.picked = 1.0, None, None
        self.points = []  # (x, y, z, label, kind)
        self.npcs = lenient_load(WORLD, {"npcs": []}).get("npcs", [])
        self.population = lenient_load(POPULATION, {"zones": {}}).get("zones", {})
        if zone in self.maps:
            self.zlist.selection_set(self.zone_ids.index(zone))
            self.after(80, lambda: self.show(zone))

    def show(self, zone):
        self.zone = zone
        self.base = Image.open(self.maps[zone]).convert("RGB")
        (self.minx, self.minz), (self.maxx, self.maxz) = self.app.game.bounds.get(zone, ([-512, -512], [512, 512]))
        self.points = []
        for c in self.population.get(str(zone), {}).get("camps", []):
            names = ", ".join(dict.fromkeys(self.app.monster_name(t) for t in c["templates"]))
            self.points.append((c["x"], c["y"], c["z"], names, "camp"))
        for n in self.npcs:
            if n.get("zone") == zone: self.points.append((n["x"], n["y"], n["z"], n["name"], "npc"))
        try:
            with sqlite3.connect(f"file:{DB}?mode=ro", uri=True) as db:
                for x, y, z, tid in db.execute("select x, y, z, template_id from spawns where zone = ?", (zone,)):
                    self.points.append((x, y, z, self.app.monster_name(tid) + " (saved spawn)", "saved"))
        except Exception:
            pass
        self.picked = None
        self.zoom = max(400, self.canvas.winfo_height() or 800) / self.base.height
        self.draw()

    def to_canvas(self, x, z):
        w, h = self.base.width * self.zoom, self.base.height * self.zoom
        return (x - self.minx) / (self.maxx - self.minx) * w, (self.maxz - z) / (self.maxz - self.minz) * h

    def to_world(self, cx, cy):
        w, h = self.base.width * self.zoom, self.base.height * self.zoom
        return self.minx + cx / w * (self.maxx - self.minx), self.maxz - cy / h * (self.maxz - self.minz)

    def draw(self):
        if self.zone is None: return
        self.canvas.delete("all")
        size = (max(1, int(self.base.width * self.zoom)), max(1, int(self.base.height * self.zoom)))
        self.photo = ImageTk.PhotoImage(self.base.resize(size))
        self.canvas.create_image(0, 0, image=self.photo, anchor="nw")
        colours = {"camp": "#9aa0a6", "npc": "#3d8bff", "saved": "#ff3b30"}
        for x, _, z, _, kind in self.points:
            u, v = self.to_canvas(x, z)
            r = 4 if kind == "saved" else 2
            self.canvas.create_oval(u - r, v - r, u + r, v + r, fill=colours[kind], outline="")
        if self.picked:
            u, v = self.to_canvas(self.picked[0], self.picked[2])
            self.canvas.create_line(u - 10, v, u + 10, v, fill="yellow", width=2)
            self.canvas.create_line(u, v - 10, u, v + 10, fill="yellow", width=2)
        self.canvas.config(scrollregion=(0, 0) + size)

    def nearest(self, x, z):
        return min(self.points, key=lambda p: (p[0] - x) ** 2 + (p[2] - z) ** 2, default=None)

    def click(self, e):
        if self.zone is None: return
        x, z = self.to_world(self.canvas.canvasx(e.x), self.canvas.canvasy(e.y))
        near = self.nearest(x, z)
        y = near[1] if near else 0.0
        self.picked = (round(x, 2), round(y, 2), round(z, 2))
        dist = ((near[0] - x) ** 2 + (near[2] - z) ** 2) ** 0.5 if near else 0
        self.info.config(text=f"x {x:.1f}, z {z:.1f}, height {y:.1f} (from {near[3] if near else '-'}, {dist:.0f} m away)")
        self.draw()

    def motion(self, e):
        if self.zone is None or not self.points: return
        x, z = self.to_world(self.canvas.canvasx(e.x), self.canvas.canvasy(e.y))
        near = self.nearest(x, z)
        if near and ((near[0] - x) ** 2 + (near[2] - z) ** 2) ** 0.5 < 6 / max(self.zoom, 0.1):
            self.hover.config(text=f"{near[3]}  (x {near[0]:.1f}, z {near[2]:.1f})")
        else:
            self.hover.config(text=f"x {x:.1f}, z {z:.1f}")

    def wheel(self, e):
        if self.zone is None: return
        self.zoom = min(6.0, max(0.3, self.zoom * (1.15 if e.delta > 0 else 1 / 1.15)))
        self.draw()

    def use(self):
        if not self.picked:
            messagebox.showinfo("Map", "Click a spot on the map first.", parent=self)
            return
        self.on_pick(self.zone, *self.picked)
        self.destroy()


# ---------------------------------------------------------------- spawns
class SpawnsTab(ttk.Frame):
    COLS = ("id", "zone", "monster", "level", "x", "y", "z", "dir", "respawn")

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        left = ttk.Frame(self)
        left.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        self.tree = ttk.Treeview(left, columns=self.COLS, show="headings", height=24)
        for c, w in zip(self.COLS, (50, 140, 220, 50, 70, 70, 70, 50, 70)):
            self.tree.heading(c, text=c.title())
            self.tree.column(c, width=w, anchor="w")
        self.tree.pack(fill="both", expand=True)
        self.tree.bind("<<TreeviewSelect>>", self.load_selected)
        bar = ttk.Frame(left)
        bar.pack(fill="x", pady=4)
        ttk.Button(bar, text="Refresh", command=self.refresh).pack(side="left")
        ttk.Button(bar, text="Delete selected", command=self.delete).pack(side="left", padx=6)

        right = ttk.LabelFrame(self, text="Spawn")
        right.pack(side="right", fill="y", padx=6, pady=6)
        self.vars = {k: tk.StringVar() for k in ("id", "zone", "template", "level", "x", "y", "z", "direction", "respawn", "roam")}
        self.vars["direction"].set("0"); self.vars["respawn"].set("1800"); self.vars["roam"].set("0")
        r = 0
        ttk.Label(right, text="Monster").grid(row=r, column=0, sticky="w", padx=4)
        self.monster_label = ttk.Label(right, text="(none)", width=30)
        self.monster_label.grid(row=r, column=1, sticky="w")
        ttk.Button(right, text="Choose…", command=self.choose_monster).grid(row=r, column=2, padx=4)
        r += 1
        for key, label in (("level", "Level"), ("zone", "Zone id"), ("x", "X"), ("y", "Y (height)"), ("z", "Z"),
                           ("direction", "Facing (0-360)"), ("respawn", "Respawn (s)"), ("roam", "Roam radius (m)")):
            form_row(right, r, label, self.vars[key]); r += 1
        ttk.Label(right, text="Zone name").grid(row=r, column=0, sticky="w", padx=4)
        self.zone_label = ttk.Label(right, text="")
        self.zone_label.grid(row=r, column=1, sticky="w"); r += 1
        self.vars["zone"].trace_add("write", lambda *_: self.zone_label.config(text=self.app.game.zones.get(self.int_or(self.vars["zone"].get(), -1), "")))
        ttk.Separator(right).grid(row=r, column=0, columnspan=3, sticky="we", pady=6); r += 1
        ttk.Label(right, text="Copy position from character").grid(row=r, column=0, columnspan=3, sticky="w", padx=4); r += 1
        self.char = ttk.Combobox(right, state="readonly", width=30)
        self.char.grid(row=r, column=0, columnspan=2, sticky="we", padx=4)
        ttk.Button(right, text="Use", command=self.use_character).grid(row=r, column=2, padx=4); r += 1
        ttk.Label(right, text="(where the character last logged out)", foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4); r += 1
        ttk.Button(right, text="Pick on the map…", command=self.pick_on_map).grid(row=r, column=0, columnspan=3, sticky="we", padx=4, pady=4); r += 1
        ttk.Separator(right).grid(row=r, column=0, columnspan=3, sticky="we", pady=6); r += 1
        ttk.Button(right, text="Save as new spawn", command=lambda: self.save(new=True)).grid(row=r, column=0, columnspan=3, sticky="we", padx=4, pady=2); r += 1
        ttk.Button(right, text="Update selected spawn", command=lambda: self.save(new=False)).grid(row=r, column=0, columnspan=3, sticky="we", padx=4, pady=2); r += 1
        ttk.Label(right, text="Restart the server to see changes.", foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4)
        self.refresh()

    @staticmethod
    def int_or(v, d):
        try: return int(float(v))
        except Exception: return d

    def db(self, write=False):
        if not os.path.exists(DB): raise RuntimeError(f"no database at {DB}")
        return sqlite3.connect(DB) if write else sqlite3.connect(f"file:{DB}?mode=ro", uri=True)

    def refresh(self):
        self.tree.delete(*self.tree.get_children())
        try:
            with self.db() as db:
                for row in db.execute("select id, zone, template_id, level, x, y, z, direction, respawn_seconds from spawns where opinion < 50 order by zone, id"):
                    sid, zone, tid, lvl, x, y, z, d, rs = row
                    self.tree.insert("", "end", iid=str(sid), values=(sid, self.app.game.zones.get(zone, zone), f"{self.app.monster_name(tid)} ({tid})", lvl,
                                                                   f"{x:.2f}", f"{y:.2f}", f"{z:.2f}", f"{d:.0f}", rs))
                chars = db.execute("select name, zone, x, y, z, direction from characters order by name").fetchall()
            self.chars = {c[0]: c for c in chars}
            self.char["values"] = [c[0] for c in chars]
        except Exception as e:
            messagebox.showerror("Spawns", str(e))

    def choose_monster(self):
        got = pick(self, "Choose a monster", self.app.all_monsters())
        if got:
            self.vars["template"].set(str(got[0]))
            self.monster_label.config(text=f"{got[1]} ({got[0]})")

    def pick_on_map(self):
        def got(zone, x, y, z):
            for k, val in (("zone", zone), ("x", x), ("y", y), ("z", z)): self.vars[k].set(str(val))
        MapPicker(self, self.app, got, self.int_or(self.vars["zone"].get(), None))

    def use_character(self):
        c = self.chars.get(self.char.get())
        if not c: return
        _, zone, x, y, z, d = c
        for k, v in (("zone", zone), ("x", f"{x:.2f}"), ("y", f"{y:.2f}"), ("z", f"{z:.2f}"), ("direction", f"{d:.0f}")): self.vars[k].set(v)

    def load_selected(self, *_):
        sel = self.tree.selection()
        if not sel: return
        with self.db() as db:
            r = db.execute("select id, zone, template_id, level, x, y, z, direction, respawn_seconds, roam_radius from spawns where id = ?", (int(sel[0]),)).fetchone()
        if not r: return
        for k, v in zip(("id", "zone", "template", "level", "x", "y", "z", "direction", "respawn", "roam"), r): self.vars[k].set(str(v))
        self.monster_label.config(text=f"{self.app.monster_name(r[2])} ({r[2]})")

    def save(self, new):
        try:
            v = {k: self.vars[k].get().strip() for k in self.vars}
            if not v["template"]: raise ValueError("choose a monster first")
            vals = (int(v["zone"]), int(v["template"]), float(v["x"]), float(v["y"]), float(v["z"]), float(v["direction"] or 0),
                    int(v["level"]), int(v["respawn"] or 1800), float(v["roam"] or 0))
        except Exception as e:
            messagebox.showerror("Spawn", f"Check the fields: {e}")
            return
        if server_running() and not messagebox.askyesno("Spawn", "The server is running. It reads spawns when it starts, so this shows after a restart. Save anyway?"):
            return
        backup(DB)
        with self.db(write=True) as db:
            if new:
                db.execute("insert into spawns(zone, template_id, x, y, z, direction, opinion, level, conversation_id, respawn_seconds, roam_radius, created_by) "
                           "values (?, ?, ?, ?, ?, ?, 0, ?, -1, ?, ?, 'CH Server Tool')", vals)
            else:
                if not v["id"]: messagebox.showerror("Spawn", "Select a spawn in the list first."); return
                db.execute("update spawns set zone=?, template_id=?, x=?, y=?, z=?, direction=?, level=?, respawn_seconds=?, roam_radius=? where id=?", vals + (int(v["id"]),))
        self.refresh()
        messagebox.showinfo("Spawn", "Saved. Restart the server to see it.")

    def delete(self):
        sel = self.tree.selection()
        if not sel: return
        if not messagebox.askyesno("Delete spawn", f"Delete spawn {sel[0]} ({self.tree.item(sel[0])['values'][2]})?"): return
        backup(DB)
        with self.db(write=True) as db: db.execute("delete from spawns where id = ?", (int(sel[0]),))
        self.refresh()


# ---------------------------------------------------------------- monsters
class MonstersTab(ttk.Frame):
    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        left = ttk.Frame(self)
        left.pack(side="left", fill="y", padx=6, pady=6)
        self.list = tk.Listbox(left, width=34, height=16)
        self.list.pack(fill="y", expand=True)
        self.list.bind("<<ListboxSelect>>", self.load_selected)
        ttk.Button(left, text="New monster (copy one)…", command=self.new).pack(fill="x", pady=2)
        ttk.Button(left, text="Delete selected", command=self.delete).pack(fill="x")

        mid = ttk.LabelFrame(self, text="Monster")
        mid.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        self.v = {k: tk.StringVar() for k in ("id", "base", "name", "level", "health", "attack", "defence", "xp", "gold", "scale", "resists", "evasion", "colour", "petLook", "hitDamage", "hitDamageType")}
        self.divine = tk.BooleanVar()
        r = 0
        for key, label in (("id", "Id (new: 90300+)"), ("name", "Name"), ("level", "Level"), ("health", "Health"), ("attack", "Attack (blank = copied)"),
                           ("defence", "Defence (blank = copied)"), ("hitDamage", "Auto-attack damage (blank = copied)"),
                           ("hitDamageType", "Auto-attack type (blank = copied)"), ("xp", "XP per kill (blank = copied)"), ("gold", "Gold (blank = copied)"),
                           ("scale", "Size (1 = same, 3 = 3x)"), ("resists", "Resists (all: 400, Poison: 300)"), ("evasion", "Evasion (all: 500)")):
            form_row(mid, r, label, self.v[key], width=40); r += 1
        colour_row(mid, r, self.v["colour"]); r += 1
        ttk.Label(mid, text="Looks like pet (blank = copied)").grid(row=r, column=0, sticky="w", padx=4, pady=2)
        pbox = ttk.Frame(mid)
        pbox.grid(row=r, column=1, sticky="w", padx=4, pady=2)
        ttk.Entry(pbox, textvariable=self.v["petLook"], width=30).pack(side="left")
        ttk.Button(pbox, text="Choose…", command=self.choose_pet).pack(side="left", padx=2)
        ttk.Button(pbox, text="Clear", command=lambda: self.v["petLook"].set("")).pack(side="left"); r += 1
        ttk.Label(mid, text="Copied from").grid(row=r, column=0, sticky="w", padx=4)
        self.base_label = ttk.Label(mid, text="")
        self.base_label.grid(row=r, column=1, sticky="w"); r += 1
        ttk.Checkbutton(mid, text="Takes Divine Strike", variable=self.divine).grid(row=r, column=1, sticky="w"); r += 1
        ttk.Label(mid, text="Resist types: " + ", ".join(RESIST_TYPES) + "\nEvasions: " + ", ".join(EVASIONS), foreground="gray").grid(row=r, column=0, columnspan=2, sticky="w", padx=4); r += 1

        ab = ttk.LabelFrame(mid, text="Boss abilities")
        ab.grid(row=r, column=0, columnspan=2, sticky="nsew", padx=4, pady=6)
        self.ab_list = tk.Listbox(ab, height=6)
        self.ab_list.pack(fill="x", padx=4, pady=4)
        self.ab_list.bind("<<ListboxSelect>>", self.load_ability)
        self.ab_form = ttk.Frame(ab)
        self.ab_form.pack(fill="x", padx=4)
        bar = ttk.Frame(ab)
        bar.pack(fill="x", padx=4, pady=4)
        self.kind = ttk.Combobox(bar, values=list(KINDS), state="readonly", width=12)
        self.kind.set("weaponBuff")
        self.kind.pack(side="left")
        ttk.Button(bar, text="Add ability", command=self.add_ability).pack(side="left", padx=4)
        ttk.Button(bar, text="Apply to selected", command=self.apply_ability).pack(side="left", padx=4)
        ttk.Button(bar, text="Remove selected", command=self.remove_ability).pack(side="left", padx=4)
        r += 1
        dr = ttk.LabelFrame(mid, text="Drops (each item rolls its own chance; a no-drop chance makes it at most one item)")
        dr.grid(row=r, column=0, columnspan=2, sticky="nsew", padx=4, pady=6)
        self.drop_list = tk.Listbox(dr, height=5)
        self.drop_list.pack(fill="x", padx=4, pady=4)
        dbar = ttk.Frame(dr)
        dbar.pack(fill="x", padx=4, pady=2)
        self.drop_chance = tk.StringVar(value="10")
        self.drop_qty = tk.StringVar(value="1")
        self.no_drop = tk.StringVar()
        ttk.Label(dbar, text="Chance %").pack(side="left")
        ttk.Entry(dbar, textvariable=self.drop_chance, width=6).pack(side="left", padx=2)
        ttk.Label(dbar, text="Qty").pack(side="left")
        ttk.Entry(dbar, textvariable=self.drop_qty, width=4).pack(side="left", padx=2)
        ttk.Button(dbar, text="Add item…", command=self.add_drop).pack(side="left", padx=4)
        ttk.Button(dbar, text="Remove selected", command=self.remove_drop).pack(side="left")
        nbar = ttk.Frame(dr)
        nbar.pack(fill="x", padx=4, pady=2)
        ttk.Label(nbar, text="No-drop chance % (blank = each item rolls on its own)").pack(side="left")
        ttk.Entry(nbar, textvariable=self.no_drop, width=6).pack(side="left", padx=4)
        self.drops = []
        r += 1
        ttk.Button(mid, text="Save monster", command=self.save).grid(row=r, column=0, columnspan=2, sticky="we", padx=4, pady=6); r += 1
        ttk.Label(mid, text="After saving: Install (game closed), then restart the server. Place it on the Spawns tab.", foreground="gray").grid(row=r, column=0, columnspan=2, sticky="w", padx=4)
        self.abilities = []
        self.ab_vars = {}
        self.refresh()

    def data(self):
        return lenient_load(MONSTERS, {"_comment": "", "monsters": []})

    def refresh(self):
        self.list.delete(0, "end")
        self.items = self.data().get("monsters", [])
        for m in self.items: self.list.insert("end", f"{m['id']}  {m['name']}")

    def next_id(self, used, start):
        i = start
        while i in used: i += 1
        return i

    # The chicken pickups' names, so searching them finds the pet that looks the same.
    PET_ALIASES = {"pet_chicken_black": "Black Dumpy", "pet_chicken_brown": "Marsh Brown", "pet_chicken_red": "Red Rooster",
                   "pet_chicken_white": "Common Chicken"}
    PET_BASE = 71377  # Brown Rabbit: a small monster, so the name and clicking sit at a pet's height

    def pet_entries(self):
        rows = []
        for prefab, name in sorted(self.app.game.pets.items(), key=lambda x: x[1].lower()):
            alias = next((a for p, a in self.PET_ALIASES.items() if prefab.startswith(p)), None)
            rows.append(("pet:" + prefab, f"{name}{' / ' + alias if alias else ''}  (pet look)"))
        return rows

    def new(self):
        entries = self.pet_entries() + sorted(self.app.game.mobs.items(), key=lambda x: x[1].lower())
        got = pick(self, "Copy which monster? (pets and chickens too: search chicken, black dumpy...)", entries)
        if not got: return
        used = {m["id"] for m in self.items} | set(self.app.game.mobs)
        self.clear()
        self.v["id"].set(str(self.next_id(used, 90300)))
        self.v["scale"].set("1")
        if isinstance(got[0], str) and got[0].startswith("pet:"):  # a pet's look on a small monster
            prefab = got[0][4:]
            base = self.PET_BASE
            self.v["petLook"].set(prefab)
            self.v["name"].set(self.app.game.pets.get(prefab, prefab).replace("Pet ", "") + " Boss")
        else:
            base = got[0]
            self.v["name"].set(got[1] + " (copy)")
        self.v["base"].set(str(base))
        self.base_label.config(text=f"{self.app.game.mobs.get(base, '?')} ({base})")

    def choose_pet(self):
        pets = sorted(self.app.game.pets.items(), key=lambda x: x[1].lower())
        got = pick(self, "Draw it as which pet? (chickens: search chicken)", [(prefab, name) for prefab, name in pets])
        if got: self.v["petLook"].set(got[0])

    def clear(self):
        for var in self.v.values(): var.set("")
        self.divine.set(False)
        self.abilities = []
        self.show_abilities()
        self.drops = []
        if hasattr(self, "drop_list"):
            self.no_drop.set("")
            self.show_drops()
        self.list.selection_clear(0, "end")

    def item_choices(self):
        custom = [(g["id"], g["name"] + "  (custom gear)") for g in lenient_load(GEAR, {"gear": []}).get("gear", [])]
        return custom + sorted(self.app.game.items.items(), key=lambda x: x[1].lower())

    def item_name(self, iid):
        custom = {g["id"]: g["name"] for g in lenient_load(GEAR, {"gear": []}).get("gear", [])}
        return custom.get(iid) or self.app.game.items.get(iid, f"item {iid}")

    def show_drops(self):
        self.drop_list.delete(0, "end")
        for d in self.drops: self.drop_list.insert("end", f"{d['name']}  ({d['id']})  {d['chance']}%  x{d.get('quantity', 1)}")

    def add_drop(self):
        got = pick(self, "Which item drops?", self.item_choices())
        if not got: return
        try:
            chance = float(self.drop_chance.get()); qty = max(1, int(self.drop_qty.get() or 1))
            if not 0 < chance <= 100: raise ValueError
        except ValueError:
            messagebox.showerror("Drops", "Chance is a percent from 0 to 100, quantity a whole number."); return
        self.drops.append({"id": got[0], "name": self.item_name(got[0]), "chance": chance, "quantity": qty})
        self.show_drops()

    def remove_drop(self):
        sel = self.drop_list.curselection()
        if sel:
            del self.drops[sel[0]]
            self.show_drops()

    def tuning_entry(self, mid):
        t = lenient_load(TUNING, {"monsters": []})
        return next((x for x in t.get("monsters", []) if x["id"] == mid), None)

    def save_drops(self, mid, name):
        """The monster's extraDrops and noDropChance in spawn-tuning.json (its entry is added if it has none)."""
        no_drop = self.no_drop.get().strip()
        if self.tuning_entry(mid) is None:
            if not self.drops and not no_drop: return
            self.set_divine(mid, name, self.divine.get(), force_entry=True)
        data = lenient_load(TUNING, {"monsters": []})
        entry = next(x for x in data["monsters"] if x["id"] == mid)
        entry["extraDrops"] = [{"id": d["id"], "name": d["name"], "chance": d["chance"], "quantity": d.get("quantity", 1)} for d in self.drops]
        entry["noDropChance"] = float(no_drop) if no_drop else None
        save_json(TUNING, data)

    def divine_ids(self):
        t = lenient_load(TUNING, {"monsters": []})
        return {m["id"] for m in t.get("monsters", []) if m.get("divine")}

    def load_selected(self, *_):
        sel = self.list.curselection()
        if not sel: return
        m = self.items[sel[0]]
        self.clear()
        for k in ("id", "base", "name", "level", "health", "attack", "defence", "xp", "gold", "scale", "colour", "petLook", "hitDamage", "hitDamageType"):
            if k in m: self.v[k].set(str(m[k]))
        self.v["resists"].set(kv_text(m.get("resists")))
        self.v["evasion"].set(kv_text(m.get("evasion")))
        self.base_label.config(text=f"{self.app.game.mobs.get(m['base'], '?')} ({m['base']})")
        self.divine.set(m["id"] in self.divine_ids())
        self.abilities = [dict(a) for a in m.get("abilities", [])]
        self.show_abilities()
        entry = self.tuning_entry(m["id"]) or {}
        self.drops = [dict(d, name=d.get("name") or self.item_name(d["id"])) for d in entry.get("extraDrops", [])]
        self.no_drop.set("" if entry.get("noDropChance") is None else str(entry["noDropChance"]))
        self.show_drops()

    # ---- abilities
    def show_abilities(self):
        self.ab_list.delete(0, "end")
        for a in self.abilities: self.ab_list.insert("end", f"{a.get('name', '?')}  ({a.get('kind')}, looks like {self.app.game.skills.get(a.get('look', 0), '?')})")
        for w in self.ab_form.winfo_children(): w.destroy()
        self.ab_vars = {}

    def build_ability_form(self, kind, a):
        for w in self.ab_form.winfo_children(): w.destroy()
        ttk.Label(self.ab_form, text=KINDS[kind], foreground="gray").grid(row=0, column=0, columnspan=3, sticky="w")
        self.ab_vars = {"kind": tk.StringVar(value=kind), "name": tk.StringVar(value=a.get("name", "")), "look": tk.StringVar(value=str(a.get("look", "")))}
        form_row(self.ab_form, 1, "Name shown in game", self.ab_vars["name"], width=30)
        form_row(self.ab_form, 2, "Looks like skill (id)", self.ab_vars["look"], width=30)
        ttk.Button(self.ab_form, text="Choose…", command=self.choose_look).grid(row=2, column=2, padx=4)
        r = 3
        for field, label, default in KIND_FIELDS[kind]:
            val = a.get(field, default)
            if isinstance(val, list): val = ", ".join(str(x) for x in val)
            self.ab_vars[field] = tk.StringVar(value=str(val))
            form_row(self.ab_form, r, label, self.ab_vars[field], width=30); r += 1

    def choose_look(self):
        got = pick(self, "Which skill's look and animation?", sorted(((i, n) for i, n in self.app.game.skills.items() if n), key=lambda x: x[1].lower()))
        if got: self.ab_vars["look"].set(str(got[0]))

    def ability_from_form(self, old):
        kind = self.ab_vars["kind"].get()
        a = {"kind": kind, "name": self.ab_vars["name"].get().strip(), "look": int(self.ab_vars["look"].get())}
        if not a["name"]: raise ValueError("the ability needs a name")
        if a["look"] not in self.app.game.skills: raise ValueError(f"skill {a['look']} doesn't exist")
        for field, _, default in KIND_FIELDS[kind]:
            raw = self.ab_vars[field].get().strip()
            if field == "atHealth": a[field] = [float(x) for x in raw.split(",") if x.strip()]
            elif field == "interruptedBy": a[field] = [x.strip() for x in raw.split(",") if x.strip()]
            elif isinstance(default, str): a[field] = raw
            else: a[field] = float(raw) if "." in raw else int(raw)
        # its own skill and effect ids (the client mod adds copies under them): keep the old ones, else the next free
        all_skill_ids = {x.get("skillId") for m in self.items for x in m.get("abilities", [])} | {x.get("skillId") for x in self.abilities}
        all_effect_ids = {x.get("effectId") for m in self.items for x in m.get("abilities", [])} | {x.get("effectId") for x in self.abilities}
        a["skillId"] = old.get("skillId") or self.next_id(all_skill_ids | set(self.app.game.skills), 9501)
        if kind != "selfHeal": a["effectId"] = old.get("effectId") or self.next_id(all_effect_ids | self.app.game.effects, 9601)
        return a

    def add_ability(self):
        if self.ab_vars.get("kind") is None or self.ab_list.curselection() or self.ab_vars["kind"].get() != self.kind.get():
            self.ab_list.selection_clear(0, "end")
            self.build_ability_form(self.kind.get(), {})
            messagebox.showinfo("Ability", "Fill in the ability below, then press Add ability again.")
            return
        try: a = self.ability_from_form({})
        except Exception as e:
            messagebox.showerror("Ability", str(e)); return
        self.abilities.append(a)
        self.show_abilities()

    def load_ability(self, *_):
        sel = self.ab_list.curselection()
        if sel:
            a = self.abilities[sel[0]]
            self.build_ability_form(a["kind"], a)

    def apply_ability(self):
        sel = self.ab_list.curselection()
        if not sel or not self.ab_vars: return
        try: self.abilities[sel[0]] = self.ability_from_form(self.abilities[sel[0]])
        except Exception as e:
            messagebox.showerror("Ability", str(e)); return
        self.show_abilities()

    def remove_ability(self):
        sel = self.ab_list.curselection()
        if sel:
            del self.abilities[sel[0]]
            self.show_abilities()

    # ---- save / delete
    def save(self):
        try:
            v = {k: self.v[k].get().strip() for k in self.v}
            if not v["id"] or not v["base"]: raise ValueError("press New monster to copy one first, or select one")
            m = {"id": int(v["id"]), "base": int(v["base"]), "name": v["name"] or "Unnamed",
                 "level": int(v["level"] or 1), "health": int(v["health"] or 1)}
            for k in ("attack", "defence", "xp", "gold"):
                if v[k]: m[k] = int(v[k])
            if v["scale"] and float(v["scale"]) != 1: m["scale"] = float(v["scale"])
            if v["resists"]: m["resists"] = parse_kv(v["resists"], ["all"] + RESIST_TYPES, "resist")
            if v["evasion"]: m["evasion"] = parse_kv(v["evasion"], ["all"] + EVASIONS, "evasion")
            m.update(check_hit(v["hitDamage"], v["hitDamageType"]))
            if check_colour(v["colour"]): m["colour"] = check_colour(v["colour"])
            if v["petLook"]:
                if v["petLook"] not in self.app.game.pets: raise ValueError(f"{v['petLook']} isn't one of the game's pets (press Choose…)")
                m["petLook"] = v["petLook"]
            if self.abilities: m["abilities"] = self.abilities
        except Exception as e:
            messagebox.showerror("Monster", f"Check the fields: {e}"); return
        d = self.data()
        d["monsters"] = [x for x in d["monsters"] if x["id"] != m["id"]] + [m]
        save_json(MONSTERS, d)
        self.set_divine(m["id"], m["name"], self.divine.get())
        try: self.save_drops(m["id"], m["name"])
        except ValueError:
            messagebox.showerror("Drops", "The no-drop chance is a percent from 0 to 100."); return
        self.refresh()
        messagebox.showinfo("Monster", f"Saved {m['name']} ({m['id']}). Install with the game closed, then restart the server.")

    def set_divine(self, mid, name, on, force_entry=False):
        text = open(TUNING, encoding="utf-8-sig").read() if os.path.exists(TUNING) else '{\n "monsters": [\n ]\n}'
        data = json.loads(re.sub(r",(\s*[\]\}])", r"\1", text))
        entry = next((x for x in data.get("monsters", []) if x["id"] == mid), None)
        if entry is None and not on and not force_entry: return
        if entry is None:  # add one entry without reformatting the rest of the owner's file
            i = text.index('"monsters": [') + len('"monsters": [')
            text = text[:i] + f'\n  {{\n   "id": {mid},\n   "name": {json.dumps(name + " (custom)")},\n   "divine": {"true" if on else "false"}\n  }},' + text[i:]
            backup(TUNING)
            open(TUNING, "w", encoding="utf-8").write(text)
        elif bool(entry.get("divine")) != on:
            entry["divine"] = on
            save_json(TUNING, data)

    def delete(self):
        sel = self.list.curselection()
        if not sel: return
        m = self.items[sel[0]]
        if not messagebox.askyesno("Delete monster", f"Delete {m['name']} ({m['id']})? Its drops and Divine Strike setting go too."): return
        d = self.data()
        d["monsters"] = [x for x in d["monsters"] if x["id"] != m["id"]]
        save_json(MONSTERS, d)
        t = lenient_load(TUNING, {"monsters": []})
        if any(x["id"] == m["id"] for x in t.get("monsters", [])):
            t["monsters"] = [x for x in t["monsters"] if x["id"] != m["id"]]
            save_json(TUNING, t)
        spawns = self.app.spawns
        try:
            with spawns.db() as db: count = db.execute("select count(*) from spawns where template_id = ?", (m["id"],)).fetchone()[0]
        except Exception:
            count = 0
        if count and messagebox.askyesno("Delete monster", f"{m['name']} is placed at {count} spawn(s). Delete those spawns too?"):
            backup(DB)
            with spawns.db(write=True) as db: db.execute("delete from spawns where template_id = ?", (m["id"],))
            spawns.refresh()
        self.clear()
        self.refresh()
        messagebox.showinfo("Delete monster", f"Deleted {m['name']}. Install (game closed) and restart the server.")


# ---------------------------------------------------------------- the game's own monsters
class GameMonstersTab(ttk.Frame):
    """Overrides for official monsters (monster-overrides.json) and their drops (spawn-tuning.json)."""
    FILTERS = ["Armour bosses", "Legacy 5/6 stars", "Raid bosses", "Changed", "All monsters"]
    FIELDS = (("level", "Level"), ("health", "Health"), ("attack", "Attack"), ("defence", "Defence"), ("xp", "XP per kill"),
              ("gold", "Gold"), ("hitDamage", "Auto-attack damage (replaces the game's)"), ("hitDamageType", "Auto-attack type"),
              ("damageScale", "Damage x (1 = same)"), ("scale", "Size (1 = same, 2 = 2x)"), ("resists", "Resists (all: 400, Poison: 300)"), ("evasion", "Evasion (all: 500)"))

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        try: self.stats = json.load(open(MOB_STATS, encoding="utf-8-sig"))["mobs"]
        except Exception: self.stats = {}
        try: self.loot = json.load(open(LOOT, encoding="utf-8-sig"))
        except Exception: self.loot = {"mobs": {}, "sets": {}, "tables": {}}
        left = ttk.Frame(self)
        left.pack(side="left", fill="y", padx=6, pady=6)
        self.filter = ttk.Combobox(left, values=self.FILTERS, state="readonly", width=32)
        self.filter.set(self.FILTERS[0])
        self.filter.pack(fill="x")
        self.filter.bind("<<ComboboxSelected>>", lambda *_: self.refresh())
        self.search = tk.StringVar()
        ttk.Entry(left, textvariable=self.search).pack(fill="x", pady=4)
        self.search.trace_add("write", lambda *_: self.refresh())
        self.list = tk.Listbox(left, width=40, height=16)
        self.list.pack(fill="y", expand=True)
        self.list.bind("<<ListboxSelect>>", self.load_selected)

        mid = ttk.LabelFrame(self, text="Monster (blank = as the game has it)")
        mid.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        self.heading = ttk.Label(mid, text="Choose a monster on the left", font=("Segoe UI", 10, "bold"))
        self.heading.grid(row=0, column=0, columnspan=3, sticky="w", padx=4, pady=2)
        self.v = {k: tk.StringVar() for k, _ in self.FIELDS}
        self.game_labels = {}
        ttk.Label(mid, text="Game's value", foreground="gray").grid(row=1, column=2, sticky="w")
        r = 2
        for key, label in self.FIELDS:
            form_row(mid, r, label, self.v[key], width=34)
            self.game_labels[key] = ttk.Label(mid, text="", foreground="gray", wraplength=330, justify="left")
            self.game_labels[key].grid(row=r, column=2, sticky="w", padx=4); r += 1
        self.colour = tk.StringVar()
        colour_row(mid, r, self.colour)
        ttk.Label(mid, text="needs Install (game closed)", foreground="gray").grid(row=r, column=2, sticky="w", padx=4); r += 1
        self.divine = tk.BooleanVar()
        ttk.Checkbutton(mid, text="Takes Divine Strike", variable=self.divine).grid(row=r, column=1, sticky="w"); r += 1
        ttk.Label(mid, text="Resist types: " + ", ".join(RESIST_TYPES) + "\nEvasions: " + ", ".join(EVASIONS), foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4); r += 1

        lt = ttk.LabelFrame(mid, text="Game's drops (chance per kill)  -  select one and press Remove / restore")
        lt.grid(row=r, column=0, columnspan=3, sticky="nsew", padx=4, pady=4); r += 1
        self.loot_list = tk.Listbox(lt, height=7)
        self.loot_list.pack(fill="x", padx=4, pady=4)
        ttk.Button(lt, text="Remove / restore selected", command=self.toggle_loot).pack(anchor="w", padx=4, pady=2)

        dr = ttk.LabelFrame(mid, text="Extra drops (each rolls its own chance; a no-drop chance makes it at most one item)")
        dr.grid(row=r, column=0, columnspan=3, sticky="nsew", padx=4, pady=4); r += 1
        self.drop_list = tk.Listbox(dr, height=4)
        self.drop_list.pack(fill="x", padx=4, pady=4)
        dbar = ttk.Frame(dr)
        dbar.pack(fill="x", padx=4, pady=2)
        self.drop_chance = tk.StringVar(value="10")
        self.drop_qty = tk.StringVar(value="1")
        self.no_drop = tk.StringVar()
        ttk.Label(dbar, text="Chance %").pack(side="left")
        ttk.Entry(dbar, textvariable=self.drop_chance, width=6).pack(side="left", padx=2)
        ttk.Label(dbar, text="Qty").pack(side="left")
        ttk.Entry(dbar, textvariable=self.drop_qty, width=4).pack(side="left", padx=2)
        ttk.Button(dbar, text="Add item…", command=self.add_drop).pack(side="left", padx=4)
        ttk.Button(dbar, text="Remove selected", command=self.remove_drop).pack(side="left")
        ttk.Label(dbar, text="   No-drop chance %").pack(side="left")
        ttk.Entry(dbar, textvariable=self.no_drop, width=6).pack(side="left", padx=4)

        bar = ttk.Frame(mid)
        bar.grid(row=r, column=0, columnspan=3, sticky="we", padx=4, pady=6); r += 1
        ttk.Button(bar, text="Save", command=self.save).pack(side="left", fill="x", expand=True)
        ttk.Button(bar, text="Reset stats to the game's", command=self.reset).pack(side="left", padx=6)
        ttk.Label(mid, text="Changes are server-side only: restart the server after saving (no Install needed).", foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4)
        self.mid = None
        self.drops, self.removed, self.loot_rows = [], set(), []
        self.refresh()

    # ---- data
    def overrides(self):
        return lenient_load(OVERRIDES, {"_comment": "", "monsters": []})

    def tuning(self):
        return lenient_load(TUNING, {"monsters": []})

    def refresh(self):
        tuning = {m["id"]: m for m in self.tuning().get("monsters", [])}
        changed = {m["id"] for m in self.overrides().get("monsters", [])} | {i for i, m in tuning.items() if m.get("removeDrops") or m.get("extraDrops")}
        kind = self.filter.get()
        mobs = self.app.game.mobs
        if kind == "Armour bosses": ids = [i for i, m in tuning.items() if m.get("set")]
        elif kind == "Legacy 5/6 stars": ids = [i for i, m in tuning.items() if not m.get("set") and m.get("stars") in (5, 6)]
        elif kind == "Raid bosses": ids = RAID_BOSSES
        elif kind == "Changed": ids = sorted(changed)
        else: ids = list(mobs)
        needle = self.search.get().lower().strip()
        rows = []
        for i in ids:
            if i not in mobs: continue
            t = tuning.get(i, {})
            tag = t.get("set") or (f"{t['stars']} star" if t.get("stars") else "")
            text = f"{mobs[i]}  ({i})" + (f"  [{tag}]" if tag and kind != "Raid bosses" else "") + ("  *" if i in changed else "")
            if needle and needle not in text.lower(): continue
            rows.append((mobs[i].lower(), i, text))
        rows.sort()
        self.rows = [i for _, i, _ in rows]
        self.list.delete(0, "end")
        for _, _, text in rows[:3000]: self.list.insert("end", text)

    def official_drops(self, mid):
        """Each item's chance per kill from the game's loot sets (several rolls of one set count as separate tries)."""
        miss = {}
        for set_id, times in self.loot["mobs"].get(str(mid), []):
            tables = self.loot["sets"].get(str(set_id), [])
            total_t = sum(max(0, w) for _, w in tables) or 1
            per_roll = {}
            for table, w in tables:
                items = self.loot["tables"].get(str(table), [])
                total_i = sum(max(0, x[2]) for x in items) or 1
                for item, qty, wi in items:
                    if item > 0: per_roll[item] = per_roll.get(item, 0) + (max(0, w) / total_t) * (max(0, wi) / total_i)
            for item, p in per_roll.items(): miss[item] = miss.get(item, 1.0) * (1 - p) ** times
        return sorted(((item, 100 * (1 - m)) for item, m in miss.items()), key=lambda x: -x[1])

    def game_text(self, st, key):
        if not st: return "(not in the game data)"
        if key == "resists":
            return ", ".join(f"{RESIST_TYPES[t] if t < len(RESIST_TYPES) else t} {flat}" for t, flat, share in st.get("resists", []) if share) or "none"
        if key == "evasion":
            return ", ".join(f"{EVASIONS[int(t)]} {v}" for t, v in st.get("evade", {}).items() if int(t) < len(EVASIONS)) or "none"
        if key == "gold":
            g = st.get("gold") or [0, 0]
            return f"{g[0]:,}" if g[0] == g[1] else f"{g[0]:,} - {g[1]:,}"
        if key == "scale":
            return "1"
        if key == "hitDamage":
            return " + ".join(f"{a:,} {DAMAGE_TYPES[t] if t < len(DAMAGE_TYPES) else t}" for t, a in st.get("damage", [])) or "-"
        if key == "hitDamageType":
            return ", ".join(DAMAGE_TYPES)
        if key == "damageScale":
            return ", ".join(f"{DAMAGE_TYPES[t] if t < len(DAMAGE_TYPES) else t} {a}" for t, a in st.get("damage", [])) or "-"
        v = st.get(key)
        return f"{v:,}" if isinstance(v, int) else "-"

    # ---- selection
    def load_selected(self, *_):
        sel = self.list.curselection()
        if not sel: return
        self.mid = self.rows[sel[0]]
        name = self.app.game.mobs.get(self.mid, "?")
        st = self.stats.get(str(self.mid))
        self.heading.config(text=f"{name}  ({self.mid})")
        o = next((m for m in self.overrides().get("monsters", []) if m["id"] == self.mid), {})
        for key, _ in self.FIELDS:
            val = o.get(key, "")
            self.v[key].set(kv_text(val) if isinstance(val, dict) else str(val))
            self.game_labels[key].config(text=self.game_text(st, key))
        self.colour.set(o.get("colour", ""))
        t = next((m for m in self.tuning().get("monsters", []) if m["id"] == self.mid), {})
        self.divine.set(bool(t.get("divine")))
        self.removed = {d["id"] if isinstance(d, dict) else d for d in t.get("removeDrops") or []}
        self.drops = [dict(d, name=d.get("name") or self.app.monsters.item_name(d["id"])) for d in t.get("extraDrops") or []]
        self.no_drop.set("" if t.get("noDropChance") is None else str(t["noDropChance"]))
        self.show_loot()
        self.show_drops()

    def show_loot(self):
        self.loot_list.delete(0, "end")
        self.loot_rows = self.official_drops(self.mid) if self.mid else []
        known = {i for i, _ in self.loot_rows}
        self.loot_rows += [(i, None) for i in sorted(self.removed - known)]
        for item, chance in self.loot_rows:
            text = f"{self.app.monsters.item_name(item)}  ({item})  " + (f"{chance:.2f}%" if chance is not None else "")
            self.loot_list.insert("end", ("[REMOVED]  " if item in self.removed else "") + text)
        if not self.loot_rows: self.loot_list.insert("end", "(no drops in the game's loot tables)")

    def toggle_loot(self):
        sel = self.loot_list.curselection()
        if not sel or sel[0] >= len(self.loot_rows): return
        item = self.loot_rows[sel[0]][0]
        self.removed ^= {item}
        self.show_loot()
        self.loot_list.selection_set(sel[0])

    def show_drops(self):
        self.drop_list.delete(0, "end")
        for d in self.drops: self.drop_list.insert("end", f"{d['name']}  ({d['id']})  {d['chance']}%  x{d.get('quantity', 1)}")

    def add_drop(self):
        if not self.mid: return
        got = pick(self, "Which item drops?", self.app.monsters.item_choices())
        if not got: return
        try:
            chance = float(self.drop_chance.get()); qty = max(1, int(self.drop_qty.get() or 1))
            if not 0 < chance <= 100: raise ValueError
        except ValueError:
            messagebox.showerror("Drops", "Chance is a percent from 0 to 100, quantity a whole number."); return
        self.drops.append({"id": got[0], "name": self.app.monsters.item_name(got[0]), "chance": chance, "quantity": qty})
        self.show_drops()

    def remove_drop(self):
        sel = self.drop_list.curselection()
        if sel:
            del self.drops[sel[0]]
            self.show_drops()

    def reset(self):
        for var in self.v.values(): var.set("")
        self.colour.set("")

    # ---- save
    def save(self):
        if not self.mid:
            messagebox.showinfo("Game monsters", "Choose a monster on the left first."); return
        name = self.app.game.mobs.get(self.mid, "?")
        try:
            v = {k: self.v[k].get().strip().replace(",", "") if k not in ("resists", "evasion") else self.v[k].get().strip() for k in self.v}
            o = {"id": self.mid, "name": name}
            for k in ("level", "health", "attack", "defence", "xp", "gold"):
                if v[k]: o[k] = int(float(v[k]))
            if v["damageScale"] and float(v["damageScale"]) != 1: o["damageScale"] = float(v["damageScale"])
            o.update(check_hit(v["hitDamage"], v["hitDamageType"]))
            if v["scale"]:
                if not 0.01 <= float(v["scale"]) <= 50: raise ValueError("size is from 0.01 to 50")
                if float(v["scale"]) != 1: o["scale"] = float(v["scale"])
            if v["resists"]: o["resists"] = parse_kv(v["resists"], ["all"] + RESIST_TYPES, "resist")
            if v["evasion"]: o["evasion"] = parse_kv(v["evasion"], ["all"] + EVASIONS, "evasion")
            if check_colour(self.colour.get()): o["colour"] = check_colour(self.colour.get())
            no_drop = self.no_drop.get().strip()
            no_drop = float(no_drop) if no_drop else None
            if no_drop is not None and not 0 <= no_drop <= 100: raise ValueError("the no-drop chance is a percent from 0 to 100")
        except Exception as e:
            messagebox.showerror("Game monsters", f"Check the fields: {e}"); return
        d = self.overrides()
        d.setdefault("monsters", [])
        d["monsters"] = [x for x in d["monsters"] if x["id"] != self.mid] + ([o] if len(o) > 2 else [])
        save_json(OVERRIDES, d)
        t = self.tuning()
        entry = next((x for x in t.setdefault("monsters", []) if x["id"] == self.mid), None)
        if entry is None and (self.divine.get() or self.removed or self.drops or no_drop is not None):
            entry = {"id": self.mid, "name": name}
            t["monsters"].append(entry)
        if entry is not None:
            entry["divine"] = self.divine.get()
            names = {i: self.app.monsters.item_name(i) for i in self.removed}
            if self.removed: entry["removeDrops"] = [{"id": i, "name": names[i]} for i in sorted(self.removed)]
            else: entry.pop("removeDrops", None)
            entry["extraDrops"] = [{"id": x["id"], "name": x["name"], "chance": x["chance"], "quantity": x.get("quantity", 1)} for x in self.drops]
            entry["noDropChance"] = no_drop
            save_json(TUNING, t)
        self.refresh()
        messagebox.showinfo("Game monsters", f"Saved {name} ({self.mid}). Restart the server to use it" + (" (and Install with the game closed for the colour)." if o.get("colour") else "."))


# ---------------------------------------------------------------- crits
class CritsTab(ttk.Frame):
    """crit-chances.json (the base chances) and ability-gain.json (how fast Critical Strike / Critical Skills train)."""
    HIT = (("critStrike", "Critical Strike: auto-attack crit base %", 8), ("critSkill", "Critical Skills: skill crit base %", 12),
           ("critDot", "Damage over time: tick crit base %", 12), ("critHeal", "Heals and heals over time: crit base %", 12))
    GAIN = (("criticalStrikeChance", "Critical Strike: % chance to level up per landed auto-attack"),
            ("criticalSkillsChance", "Critical Skills: % chance to level up per skill hit or heal"),
            ("dotCriticalChance", "Critical Skills: % chance to level up per damage/heal-over-time tick"))

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        f = ttk.LabelFrame(self, text="Chance to crit")
        f.pack(fill="x", padx=8, pady=8)
        self.v = {}
        r = 0
        for key, label, _ in self.HIT:
            self.v[key] = tk.StringVar()
            form_row(f, r, label, self.v[key], width=10); r += 1
        ttk.Label(f, text="Chance = base x (ability + 40) / (ability + 40 + 10 x (target level + 3)), plus the passive tree's crit bonus.\n"
                          "The live game's bases: 8% auto-attacks, 12% skills.", foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4); r += 1
        g = ttk.LabelFrame(self, text="Chance to level up")
        g.pack(fill="x", padx=8, pady=8)
        r = 0
        for key, label in self.GAIN:
            self.v[key] = tk.StringVar()
            form_row(g, r, label, self.v[key], width=10); r += 1
        ttk.Label(g, text="Elixirs of Wisdom multiply these. Abilities stop at ten per character level.", foreground="gray").grid(row=r, column=0, columnspan=3, sticky="w", padx=4)
        p = ttk.LabelFrame(self, text="What it comes to")
        p.pack(fill="x", padx=8, pady=8)
        self.ability = tk.StringVar(value="1500")
        self.target = tk.StringVar(value="200")
        form_row(p, 0, "Crit ability level", self.ability, width=10)
        form_row(p, 1, "Target level", self.target, width=10)
        self.preview = ttk.Label(p, text="", font=("Segoe UI", 10))
        self.preview.grid(row=2, column=0, columnspan=3, sticky="w", padx=4, pady=4)
        for var in self.v.values(): var.trace_add("write", lambda *_: self.update_preview())
        self.ability.trace_add("write", lambda *_: self.update_preview())
        self.target.trace_add("write", lambda *_: self.update_preview())
        ttk.Button(self, text="Save crits", command=self.save).pack(fill="x", padx=8, pady=6)
        self.build_landing()
        ttk.Label(self, text="Restart the server after saving (no Install needed).", foreground="gray").pack(anchor="w", padx=8)
        self.load()

    # ---- how often a skill lands on monsters (skill-overrides.json)
    def build_landing(self):
        f = ttk.LabelFrame(self, text="Skill landing on monsters: lands = 1 - E x m / (E + 50 x your level); E = the monster's evasion to the skill's type, m = 0.95 Warrior/Rogue, 0.7 others")
        f.pack(fill="both", expand=True, padx=8, pady=8)
        try: self.mob_stats = json.load(open(MOB_STATS, encoding="utf-8-sig"))["mobs"]
        except Exception: self.mob_stats = {}
        b = json.load(open(BALANCE, encoding="utf-8-sig"))["SkillTemplates"]
        self.avoid = {int(k): v.get("AvoidanceType", -1) for k, v in b.items() if isinstance(v, dict)}
        row = ttk.Frame(f)
        row.pack(fill="x", padx=4, pady=2)
        self.skill_id = 192
        self.skill_label = ttk.Label(row, text="", width=40)
        self.skill_label.pack(side="left")
        ttk.Button(row, text="Choose skill…", command=self.choose_skill).pack(side="left", padx=4)
        self.lv = {"evasionPercent": tk.StringVar(), "minLandChance": tk.StringVar(), "level": tk.StringVar(value="200")}
        row2 = ttk.Frame(f)
        row2.pack(fill="x", padx=4, pady=2)
        for key, label, width in (("evasionPercent", "Monster evasion counts %", 6), ("minLandChance", "  Lowest land chance %", 6), ("level", "  Your level", 5)):
            ttk.Label(row2, text=label).pack(side="left")
            ttk.Entry(row2, textvariable=self.lv[key], width=width).pack(side="left", padx=2)
        self.cls = ttk.Combobox(row2, values=CLASSES, state="readonly", width=9)
        self.cls.set("Rogue")
        self.cls.pack(side="left", padx=6)
        self.cls.bind("<<ComboboxSelected>>", lambda *_: self.update_landing())
        ttk.Button(row2, text="Save skill", command=self.save_landing).pack(side="left", padx=6)
        ttk.Label(f, text="100 / 0 = as the game has it. 50 = bosses' evasion counts half; a lowest chance of 60 = it always has at least 60%.", foreground="gray").pack(anchor="w", padx=4)
        self.land_list = tk.Listbox(f, height=9, font=("Consolas", 9))
        self.land_list.pack(fill="both", expand=True, padx=4, pady=4)
        for var in self.lv.values(): var.trace_add("write", lambda *_: self.update_landing())
        self.load_landing()

    def choose_skill(self):
        got = pick(self, "Which skill?", sorted(((i, n) for i, n in self.app.game.skills.items() if n and self.avoid.get(i, -1) >= 0), key=lambda x: x[1].lower()))
        if got:
            self.skill_id = got[0]
            self.load_landing()

    def load_landing(self):
        o = lenient_load(SKILL_OVERRIDES, {}).get(str(self.skill_id), {})
        t = self.avoid.get(self.skill_id, -1)
        self.skill_label.config(text=f"{self.app.game.skills.get(self.skill_id, '?')} ({self.skill_id}), evaded by {EVASIONS[t] if 0 <= t < len(EVASIONS) else 'nothing'}")
        self.lv["evasionPercent"].set(f"{o.get('evasionPercent', 100):g}")
        self.lv["minLandChance"].set(f"{o.get('minLandChance', 0):g}")

    def update_landing(self):
        self.land_list.delete(0, "end")
        t = self.avoid.get(self.skill_id, -1)
        if t < 0:
            self.land_list.insert("end", "This skill has no evasion type: it always lands."); return
        try:
            pct, floor, level = (float(self.lv[k].get()) for k in ("evasionPercent", "minLandChance", "level"))
        except ValueError:
            return
        m = 0.95 if self.cls.get() in ("Warrior", "Rogue") else 0.7
        self.land_list.insert("end", f"{'Boss':32}{'evasion':>9}{'game':>8}{'now':>8}")
        for mid in RAID_BOSSES + [90301]:
            st = self.mob_stats.get(str(mid))
            e = (st or {}).get("evade", {}).get(str(t), 0)
            ov = next((x for x in lenient_load(OVERRIDES, {"monsters": []}).get("monsters", []) if x["id"] == mid), {}).get("evasion", {})
            e = ov.get(EVASIONS[t], ov.get("all", e))  # your Game monsters change, if any
            def lands(e):
                return 1 if e <= 0 else 1 - e * m / (e + 50 * level)
            name = self.app.monster_name(mid)
            if not st and mid == 90301: continue
            self.land_list.insert("end", f"{name[:31]:32}{e:>9,}{lands(e) * 100:>7.1f}%{max(lands(e * pct / 100), floor / 100) * 100:>7.1f}%")

    def save_landing(self):
        try:
            pct, floor = float(self.lv["evasionPercent"].get()), float(self.lv["minLandChance"].get())
            if pct < 0 or not 0 <= floor <= 100: raise ValueError
        except ValueError:
            messagebox.showerror("Skill landing", "Evasion counts % is 0 or more; lowest land chance is 0 to 100."); return
        d = lenient_load(SKILL_OVERRIDES, {})
        e = d.setdefault(str(self.skill_id), {"name": self.app.game.skills.get(self.skill_id, "")})
        e["evasionPercent"], e["minLandChance"] = pct, floor
        save_json(SKILL_OVERRIDES, d, indent=2)
        messagebox.showinfo("Skill landing", f"Saved {self.app.game.skills.get(self.skill_id, '')}. Restart the server to use it.")

    @staticmethod
    def pct(x):
        return f"{x * 100:g}"

    def load(self):
        c = lenient_load(CRITS, {})
        for key, _, default in self.HIT: self.v[key].set(self.pct(c.get(key, default / 100)))
        a = lenient_load(ABILITY_GAIN, {})
        crit = a.get("criticalChance", 0.2)
        for key, _ in self.GAIN: self.v[key].set(self.pct(a.get(key, crit if key != "dotCriticalChance" else 0.05)))

    def update_preview(self):
        try:
            level, target = float(self.ability.get()), float(self.target.get())
            f = (level + 40) / (level + 40 + 10 * (target + 3))
            parts = [f"{label.split(':')[0]} {float(self.v[key].get()) * f:.2f}%" for key, label, _ in self.HIT]
            self.preview.config(text="Crit chance:  " + "   ".join(parts))
        except (ValueError, ZeroDivisionError):
            self.preview.config(text="")

    def save(self):
        try:
            vals = {k: float(self.v[k].get()) / 100 for k in self.v}
            if any(not 0 <= x <= 1 for x in vals.values()): raise ValueError("every value is a percent from 0 to 100")
        except ValueError as e:
            messagebox.showerror("Crits", f"Check the fields: {e}"); return
        c = lenient_load(CRITS, {})
        for key, _, _ in self.HIT: c[key] = round(vals[key], 6)
        save_json(CRITS, c)
        a = lenient_load(ABILITY_GAIN, {})
        for key, _ in self.GAIN: a[key] = round(vals[key], 6)
        save_json(ABILITY_GAIN, a, indent=2)
        messagebox.showinfo("Crits", "Saved. Restart the server to use them.")


# ---------------------------------------------------------------- gear
class GearTab(ttk.Frame):
    FIELDS = [("id", "Id (new: 90100+)"), ("name", "Name"), ("type", "Type: ring, necklace, bracelet, charm (blank for armour/weapons)"),
              ("look", "Look: an item id or exact name (armour/weapons)"), ("level", "Level"), ("description", "Description"),
              ("armour", "Armour"), ("damage", "Damage (Slash: 300, Cold: 100)"), ("speed", "Weapon speed ms (blank = look's)"),
              ("stats", "Stats (Strength: 40, Dexterity: 40)"), ("bonuses", "Bonuses (Health: 500, Attack: 100)"),
              ("resists", "Resists (Cold: 25, Heat: 25)"), ("abilities", "Abilities (Treasure Hunter: 800)"),
              ("avoidance", "Avoidance (Physical: 25)"), ("skills", "Skill ranks (Fire Bolt: 2)"),
              ("skillDamage", "Skill damage (Double Shot: 50)"), ("regen", "Regen (health: 25, energy: 25)"),
              ("proc", "Proc: skill, level, chance% (Fire Bolt, 10, 5)"), ("equipSkill", "Equip skill (name or id)"), ("stars", "Stars")]
    KV = {"damage": DAMAGE_TYPES, "stats": STATS, "bonuses": BONUSES, "resists": RESIST_TYPES, "avoidance": EVASIONS,
          "regen": ["health", "energy"], "abilities": None, "skills": None, "skillDamage": None}

    def __init__(self, parent, app):
        super().__init__(parent)
        self.app = app
        left = ttk.Frame(self)
        left.pack(side="left", fill="y", padx=6, pady=6)
        self.list = tk.Listbox(left, width=40, height=16)
        self.list.pack(fill="y", expand=True)
        self.list.bind("<<ListboxSelect>>", self.load_selected)
        ttk.Button(left, text="New gear", command=self.new).pack(fill="x", pady=2)
        ttk.Button(left, text="Copy selected as new", command=self.copy).pack(fill="x")
        ttk.Button(left, text="Start from a game item…", command=self.from_game_item).pack(fill="x", pady=2)
        ttk.Button(left, text="Delete selected", command=self.delete).pack(fill="x", pady=2)
        form = ttk.LabelFrame(self, text="Gear")
        form.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        self.v = {k: tk.StringVar() for k, _ in self.FIELDS}
        self.classes = {c: tk.BooleanVar() for c in CLASSES}
        self.tradeable = tk.BooleanVar(value=True)
        ttk.Label(form, text="Template (fills in the look)").grid(row=0, column=0, sticky="w", padx=4, pady=2)
        self.template = ttk.Combobox(form, values=[t for t, _ in TEMPLATES], state="readonly", width=48)
        self.template.grid(row=0, column=1, sticky="we", padx=4, pady=2)
        self.template.bind("<<ComboboxSelected>>", self.use_template)
        for r, (k, label) in enumerate(self.FIELDS, start=1):
            form_row(form, r, label, self.v[k], width=50)
        r = len(self.FIELDS) + 1
        ttk.Button(form, text="Choose look…", command=self.choose_look).grid(row=4, column=2, padx=4)
        cf = ttk.Frame(form)
        cf.grid(row=r, column=1, sticky="w"); r += 1
        ttk.Label(form, text="Classes (none = all)").grid(row=r - 1, column=0, sticky="w", padx=4)
        for c in CLASSES: ttk.Checkbutton(cf, text=c, variable=self.classes[c]).pack(side="left")
        ttk.Checkbutton(form, text="Tradeable", variable=self.tradeable).grid(row=r, column=1, sticky="w"); r += 1
        ttk.Button(form, text="Save gear", command=self.save).grid(row=r, column=0, columnspan=2, sticky="we", padx=4, pady=6); r += 1
        ttk.Label(form, text="After saving: Install (game closed), restart the server, then .give <id> in game.", foreground="gray").grid(row=r, column=0, columnspan=2, sticky="w", padx=4)
        self.refresh()

    def data(self):
        return lenient_load(GEAR, {"_comment": "", "gear": []})

    def refresh(self):
        self.list.delete(0, "end")
        self.items = self.data().get("gear", [])
        for g in self.items: self.list.insert("end", f"{g['id']}  {g['name']}")

    def clear(self):
        for var in self.v.values(): var.set("")
        for c in self.classes.values(): c.set(False)
        self.tradeable.set(True)

    def next_id(self):
        used = {g["id"] for g in self.items} | set(self.app.game.items)
        i = 90100
        while i in used: i += 1
        return i

    def new(self):
        self.clear()
        self.list.selection_clear(0, "end")
        self.v["id"].set(str(self.next_id()))
        self.v["type"].set("ring")

    def copy(self):
        sel = self.list.curselection()
        if not sel: return
        self.load_selected()
        self.v["id"].set(str(self.next_id()))
        self.v["name"].set(self.v["name"].get() + " (copy)")
        self.list.selection_clear(0, "end")

    def use_template(self, *_):
        target = dict(TEMPLATES).get(self.template.get())
        if isinstance(target, tuple):  # armour and "any" slots: choose an item of that slot to copy the look of
            self.v["type"].set("")
            self.choose_look(target[1])
        elif isinstance(target, str):  # jewellery: the type's default look
            self.v["type"].set(target)
            self.v["look"].set("")
        elif target:
            self.v["type"].set("")
            self.v["look"].set(str(target))

    def choose_look(self, slot=None):
        slots = self.app.game.item_slot
        got = pick(self, f"Copy the look of which {slot.lower() + ' ' if slot else ''}item?",
                   sorted(((i, f"{n}  [{slots.get(i)}]") for i, n in self.app.game.items.items()
                           if (slots.get(i) == slot if slot else slots.get(i) not in ("Unequipable", "None"))), key=lambda x: x[1].lower()))
        if got: self.v["look"].set(str(got[0]))

    # names the server reads (CustomGear.cs) for the game data's bonus and damage names
    GAME_NAMES = {"HealthBonus": "Health", "EnergyBonus": "Energy", "AttackBonus": "Attack", "DefenceBonus": "Defence",
                  "ConcentrationBonus": "Concentration", "HeatResist": "Heat", "ColdResist": "Cold", "MagicResist": "Magic",
                  "PoisonResist": "Poison", "DivineResist": "Divine", "ChaosResist": "Chaos", "PiercingResist": "Pierce",
                  "SlashingResist": "Slash", "CrushingResist": "Crush", "PiercingDamage": "Pierce", "SlashingDamage": "Slash",
                  "CrushingDamage": "Crush", "HeatDamage": "Heat", "ColdDamage": "Cold", "MagicDamage": "Magic",
                  "PoisonDamage": "Poison", "DivineDamage": "Divine", "ChaosDamage": "Chaos", "TrueDamage": "True"}

    def from_game_item(self):
        """A new gear entry with a game item's look and numbers, ready to change (armour, weapons, anything wearable)."""
        slots = self.app.game.item_slot
        got = pick(self, "Start from which item? (its look and numbers are filled in)",
                   sorted(((i, f"{n}  [{slots.get(i)}]") for i, n in self.app.game.items.items()
                           if slots.get(i) not in ("Unequipable", "None", None) and not str(slots.get(i)).startswith("Fash")), key=lambda x: x[1].lower()))
        if not got: return
        it = self.app.game.item_raw[got[0]]
        self.new()
        self.v["type"].set("")
        self.v["look"].set(str(got[0]))
        self.v["name"].set(it.get("Name", "") + " (custom)")
        self.v["description"].set(it.get("Description", ""))
        level = next((r["Amount"] for r in it.get("RequirementTypes") or [] if r.get("RequirementType") == "LevelRequirement"), None)
        if level: self.v["level"].set(str(level))
        if it.get("Armour"): self.v["armour"].set(str(it["Armour"]))
        if slots.get(got[0]) == "Weapon" and it.get("Speed"): self.v["speed"].set(str(it["Speed"]))
        name = lambda k: self.GAME_NAMES.get(k)
        dmg = {name(d["DamageType"]): d["Amount"] for d in it.get("DamageTypes") or [] if name(d["DamageType"])}
        bonuses = {name(b["BonusType"]): b["Amount"] for b in it.get("BonusTypes") or [] if b["BonusType"].endswith("Bonus") and name(b["BonusType"])}
        resists = {name(b["BonusType"]): b["Amount"] for b in it.get("BonusTypes") or [] if b["BonusType"].endswith("Resist") and name(b["BonusType"])}
        stats = {b["PrimaryStatType"]: b["Amount"] for b in it.get("PrimaryStatBonuses") or [] if b.get("PrimaryStatType") in STATS}
        abilities = {self.app.game.abilities.get(b["AbilityType"], str(b["AbilityType"])): b["Amount"] for b in it.get("AbilityBonuses") or []}
        avoid = {EVASIONS[b["AbilityType"]]: b["Amount"] for b in it.get("AvoidanceBonuses") or [] if 0 <= b.get("AbilityType", -1) < len(EVASIONS)}
        for k, d in (("damage", dmg), ("bonuses", bonuses), ("resists", resists), ("stats", stats), ("abilities", abilities), ("avoidance", avoid)):
            self.v[k].set(kv_text(d))
        for c in it.get("ClassTypes") or []:
            if c in self.classes: self.classes[c].set(True)
        if it.get("StarLevel", -1) > 0: self.v["stars"].set(str(it["StarLevel"]))
        messagebox.showinfo("Gear", f"Filled in from {it.get('Name')}. Change the name and numbers, then Save gear.")

    def load_selected(self, *_):
        sel = self.list.curselection()
        if not sel: return
        g = self.items[sel[0]]
        self.clear()
        for k, _ in self.FIELDS:
            val = g.get(k)
            if val is None: continue
            if k in self.KV: self.v[k].set(kv_text(val))
            elif k == "proc": self.v[k].set(f"{val.get('skill')}, {val.get('level', 1)}, {val.get('chance', 0)}")
            else: self.v[k].set(str(val))
        for c in g.get("classes", []):
            if c in self.classes: self.classes[c].set(True)
        self.tradeable.set(g.get("tradeable", True) is not False)

    def save(self):
        try:
            v = {k: self.v[k].get().strip() for k in self.v}
            if not v["id"] or not v["name"]: raise ValueError("id and name are needed")
            g = {"id": int(v["id"]), "name": v["name"]}
            if v["type"]: g["type"] = v["type"].lower()
            if v["look"]: g["look"] = int(v["look"]) if v["look"].isdigit() else v["look"]
            if "type" not in g and "look" not in g: raise ValueError("give a type (ring, necklace, bracelet, charm) or a look")
            for k in ("level", "armour", "speed", "stars"):
                if v[k]: g[k] = int(v[k])
            if v["description"]: g["description"] = v["description"]
            for k, allowed in self.KV.items():
                if v[k]: g[k] = parse_kv(v[k], allowed, k)
            if v["proc"]:
                parts = [p.strip() for p in v["proc"].split(",")]
                if len(parts) != 3: raise ValueError("proc is: skill name, level, chance%")
                g["proc"] = {"skill": parts[0], "level": int(parts[1]), "chance": float(parts[2])}
            if v["equipSkill"]: g["equipSkill"] = int(v["equipSkill"]) if v["equipSkill"].isdigit() else v["equipSkill"]
            classes = [c for c, var in self.classes.items() if var.get()]
            if classes: g["classes"] = classes
            if not self.tradeable.get(): g["tradeable"] = False
        except Exception as e:
            messagebox.showerror("Gear", f"Check the fields: {e}"); return
        d = self.data()
        d["gear"] = [x for x in d["gear"] if x["id"] != g["id"]] + [g]
        save_json(GEAR, d, indent=2)
        self.refresh()
        messagebox.showinfo("Gear", f"Saved {g['name']} ({g['id']}). Install with the game closed, restart the server, then .give {g['id']}.")

    def delete(self):
        sel = self.list.curselection()
        if not sel: return
        g = self.items[sel[0]]
        if not messagebox.askyesno("Delete gear", f"Delete {g['name']} ({g['id']})? Characters who have one keep an unknown item."): return
        d = self.data()
        d["gear"] = [x for x in d["gear"] if x["id"] != g["id"]]
        save_json(GEAR, d, indent=2)
        self.clear()
        self.refresh()


# ---------------------------------------------------------------- install
class InstallTab(ttk.Frame):
    def __init__(self, parent, app):
        super().__init__(parent)
        ttk.Label(self, text="Puts your gear, monsters and quests into the game copy. Close the game first; restart the server afterwards.").pack(anchor="w", padx=8, pady=8)
        ttk.Button(self, text="Install into the game", command=self.run).pack(anchor="w", padx=8)
        self.out = tk.Text(self, height=30)
        self.out.pack(fill="both", expand=True, padx=8, pady=8)

    def run(self):
        self.out.delete("1.0", "end")
        r = subprocess.run([sys.executable, os.path.join(HERE, "install-custom-gear.py"), BALANCE], capture_output=True, text=True)
        self.out.insert("end", r.stdout + r.stderr)
        if r.returncode == 0: self.out.insert("end", "\nDone. Restart the server.\n")


if __name__ == "__main__":
    App().mainloop()
