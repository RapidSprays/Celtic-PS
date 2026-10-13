#!/usr/bin/env python3
"""Cooking recipes' ingredients (APPROXIMATED, see docs/gathering-notes.md).

    scripts/gen-cooking.py [BUILD]

The client's CraftingTemplates give each recipe's name, level, results, optional booster and cost id, but the
ingredient lists (the token vendor 221 cost lists) are server-only. They're drawn here from the recipe's name and kind:
the words name the main ingredients ("Rabbit Pie" → Rabbit Meat, "Herring Pie" → Herring), and the kind adds a base
(pies: wheat and butter; stews and soups: onion; breads and cakes: wheat and milk; roasts: onion). Higher recipes
need more of the main ingredient. Crops (wheat, oats, barley, lentils, peas, vegetables, mushrooms) grow on the
ground (gen-item-spawns.py), fish come from fishing, and the rest ("bought") is sold by food merchants.
Writes ~/CelticHeroesAnalysis/extracted/<build>/cooking.json (outside git: derived from client data).
"""
import json, os, re, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import chdata  # noqa: E402

build = sys.argv[1] if len(sys.argv) > 1 else "24928510"
bal = chdata.balance(build)
items = {int(k): v for k, v in bal["ItemTemplates"].items() if k != "$id"}
out_path = f"{chdata.HOME}/extracted/{build}/cooking.json"

WHEAT, OATS, BARLEY, LENTILS, ONION, BUTTER, PEAS, CHEESE, CREAM, MILK, EGGS = 60542, 60543, 60541, 60544, 60545, 60546, 60547, 60548, 60549, 60550, 60551
GROUND = [60541, 60542, 60543, 60544, 60545, 60547, 60552, 60556, 60557, 60558, 60560]
WORDS = [
    (r"rabbit", 60561), (r"boar", 17398), (r"beef|yule roast", 60562), (r"venison", 60563), (r"pork|pudding|sausage", 60564),
    (r"lamb|mutton", 60566), (r"raspberr", 60567), (r"blueberr", 60568), (r"warbeast", 60569), (r"wyvern", 60570),
    (r"truffle", 60571), (r"caviar|roe", 60572), (r"tenderloin|steak", 60573), (r"goose|liver|pate", 60574),
    (r"cherr", 60575), (r"lemon", 60576), (r"quail", 60577), (r"garlic", 60578), (r"meringue|pavlova", 60579),
    (r"cheese|rarebit", CHEESE), (r"cream|ice cream", CREAM), (r"milk|custard|porridge", MILK),
    (r"egg|omelette|souffle|quiche|pancake", EGGS), (r"sweet|sugar|cake|cookie|chocolate|toffee", 60553),
    (r"raisin|bara brith|sweet roll", 60554), (r"honey", 60555), (r"fruit", 60559), (r"carrot", 60557), (r"leek", 60556),
    (r"onion", ONION), (r"ginger", 60552), (r"spinach", 60560), (r"mushroom|bolete", 60558), (r"pea\b|peas", PEAS),
    (r"lentil", LENTILS), (r"oat|porridge|granola|bannock", OATS), (r"barley|pottage|gruel", BARLEY),
    (r"bread|bun|roll|flat|loaf", WHEAT), (r"cullen skink", 59020),
]
BASE = {0: [WHEAT, BUTTER], 1: [ONION], 2: [ONION], 3: [WHEAT, MILK]}  # RecipeMeshType: 0 pie, 1 meat, 2 stew, 3 bread
FISH = sorted(((t["Name"].lower(), i) for i, t in items.items() if t.get("Subtype") == "FishingItem" and t.get("Mesh3dsID") in (257, 258, 273, 274, 275)),
              key=lambda f: -len(f[0]))

recipes = {}
for key, r in bal["CraftingTemplates"].items():
    if key == "$id" or re.search(r"DUMMY|TEST|Stolen", r["RecipeName"]): continue
    name = r["RecipeName"].lower()
    main = []
    for fish_name, fid in FISH:
        if fish_name in name and fid not in main:
            main.append(fid)
            name = name.replace(fish_name, "")
    main += [i for rx, i in WORDS if re.search(rx, name) and i not in main]
    base = [i for i in BASE.get(r["RecipeMeshType"], [ONION]) if i not in main]
    need = 1 + r["RecipeLevel"] // 60
    ingredients = [(i, need) for i in main[:2]] + [(i, 1) for i in base]
    if len(ingredients) < 2: ingredients.append((ONION if ONION not in dict(ingredients) else WHEAT, 1))
    recipes[str(r["CostId"])] = {"recipe": r["RecipeId"], "name": r["RecipeName"], "ingredients": ingredients[:4]}

used = {i for r in recipes.values() for i, _ in r["ingredients"]}
fish_ids = {i for _, i in FISH}
bought = sorted(i for i in used if i not in GROUND and i not in fish_ids)
json.dump({"build": build, "recipes": recipes, "ground": GROUND, "bought": bought}, open(out_path, "w"), indent=1)
print(f"{len(recipes)} recipes; {len(bought)} bought ingredients, {len(GROUND)} ground crops, "
      f"{len(used & fish_ids)} fish; wrote {out_path}")
for c in list(recipes)[:8]: print(" ", recipes[c]["name"], [(items[i]["Name"], q) for i, q in recipes[c]["ingredients"]])
