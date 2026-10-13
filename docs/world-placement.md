# NPC and monster placement on the real ground

The client ships no spawn tables or NPC coordinates; the official server keeps those. No community map records them either. So placement is still **approximated**, but on each zone's real ground, using two things the client does ship.

1. **The map mask:** `<map>_mask.png`, 256×256, the map screen's mask. Light grey is walkable ground, dark grey is cliffs and edges, black is sea, walls and building footprints. Map pixels convert to world positions with the zone's map bounds, exactly as `MapScreen.SetupPlayerIcon` does:
   - u = (x − minX) / width
   - v = 1 − (z − minZ) / height

   The self-check: every zone's leystones land on walkable pixels (43 of 43).
2. **The zone's 3D world:** `<scene>_scenes_*.bundle`. It holds every named object's world position (Transform chains). In Lir's Reach, for example: `Wheat_Mill`, `tavern`, `fish_stall`, `boathouse`, `statue`, `cottage`, `stable`, `barberShopPosition`, `druid`, `ranger`, tents and campfires. The world's leystone objects sit within a few metres of the balance data's leystone coordinates.

## Pipeline
1. `scripts/extract-zone-worlds.py` runs with UnityPy in `~/CelticHeroesAnalysis/venv`, read-only. It writes `extracted/<build>/zone-worlds.json` with each zone's bounds, mask and named objects.
2. `scripts/worldplace.py` is the shared helper (stdlib only):
   - walkability, open-ground share and nearest walkable spot;
   - object search;
   - the NPC → object table `NPC_PLACES`, the village objects, and the camp-dweller rules.
3. `scripts/gen-world.py` places NPCs in this order of preference:
   - a leystone's own NPC stands at that leystone;
   - an NPC whose name or title matches a world object stands beside it, on walkable ground outside its footprint, facing it (miller → mill, captain → boathouses, vendor → stalls…);
   - otherwise the NPC takes a building or village object near its village leystone, one each, 5 m apart;
   - last resort: the old ring around the leystone, snapped to walkable ground.
4. `scripts/gen-zone-population.py`, run after gen-world, places monster camps:
   - Camps go only where the ground within the camp's reach is at least 85% walkable. They keep clear of leystones, exits, village objects and NPCs.
   - Lower levels sit nearer the zone's first leystone, and bosses further out.
   - Camp-dwelling monsters (raiders, bandits, cultists…) take the world's tents and campfires.
   - Narrow dungeons get smaller camps rather than none.
   - Lir's Reach keeps its hand-picked camp list, each camp moved onto the nearest open ground.
   - Every monster gets its own walkable home point (`points`). `ZonePopulation.Build` uses those points, and prefers generated camps over the hand-made fallback.
5. `scripts/prepare-server.sh`, used by `run-private.sh` and the background service, reruns all of this when the inputs or scripts change.

## Limits
- Positions are plausible, not the official ones: "beside the mill", "in the right tunnel".
- Palace and Valley of the Ancients have blank masks, so they keep the older placement.
- Roaming monsters can still drift a few metres off the walkable ground, because the server doesn't check the mask while they move.
- GM commands (`.npc`, `.spawn`, `.delete`) still adjust anything by hand.
