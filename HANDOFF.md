# Dead Miles — handoff

Written 12 Nov 2026 at the end of a cloud session, for whoever picks this up on Tom's PC
(`H:\BIW\DeadMiles`, Godot 4.7.2 .NET, RTX 3080, Ryzen 9, 32 GB). Branch:
`claude/youthful-noether-4wxif0`. `main` holds only Tom's original upload.

People: **Tom / BadSad** owns the project and does not use git from the command line (he uploads
and downloads through the GitHub website). **Noor** does art. The assistant goes by **Sage**.

## What this is

Dead Miles is a first-person road-survival sandbox (see `DEAD_MILES_GODOT_BRIEF.md`). The original
game generates an endless road from a seed. This session added a second world: **all of
województwo mazowieckie at real scale, built from OpenStreetMap**. Tom treats Dead Miles as an
experiment in what is possible; the ideas it gave him (bus / taxi / logistics sims on smaller
maps, a tile editor) will be new projects.

New Game offers "Mazowsze – the real map" (with a choice of start town) or the "Endless road".
The procedural world is unchanged and still passes its tests.

## First things to do on Tom's PC

1. **Get Tom's real `project.godot` into the repo.** It never reached GitHub (and neither did
   `assets/` or `.godot/`). Everything here was built and tested with a minimal stand-in that is
   deliberately *not* committed. Its whole content:
   - main scene `res://scenes/Main.tscn`, assembly name `DeadMiles`, 1280x720
   - `[shader_globals]`: `cloud_shadow` (float), `wetness` (float), `wind_strength` (float),
     `sky_tint` (vec3) — the shaders need these four
   - nothing else, so **physics engine = engine default (GodotPhysics)**. If Tom's project uses
     Jolt, the car was tuned for Jolt and the test builds behaved differently from his editor.
     Check this first when looking at the two unreproduced bugs below.
2. Replace the whole `map/` folder from the branch (tile format is version 2; old packs are
   rejected with a clear error).
3. Open in Godot, let it build the C# solution, run. With Tom's assets present the placeholder
   trees, car, wrecks and animals are replaced automatically (same `res://assets/...` paths).
4. Things only a real GPU can answer — nothing here has been seen above 2–3 fps:
   - frame rate in central Warsaw and hitching when driving fast through a city
   - whether Forward+ / Vulkan works (see "Vulkan crash" below)
   - whether getting out of the car and car recovery behave

## Layout

| Path | What |
| --- | --- |
| `tools/osm_import/` | Python importer: `extract.py` (pbf → arrays), `build.py` (arrays → tile packs), `common.py` (tag tables), `README.md` (how to rebuild / use another region) |
| `map/` | Generated packs: `mazowsze.idx`, `mazowsze.00–03.pak`, `mazowsze.places.json` (176 MB). Has a `.gdignore`, so Godot does not import it |
| `scripts/world/map/MapData.cs` | Tile reader, tile cache, junction detection, ids shared with the importer |
| `scripts/world/map/MapWorld.cs` | `MapContext` (roads/buildings/points gathered around a spot) and `MapWorld` (height, biome, "is this free", nearest road, places) |
| `scripts/world/map/MapMesher.cs` | Road ribbons, bridges, tram rails, buildings, roofs, canopies, street furniture (worker threads) |
| `scripts/world/map/MapSkyline.cs` | Buildings over 42 m drawn per tile out to ~3 km |
| `scripts/world/map/MapPlaces.cs` | Fuel pumps, shop crates, house/farm stashes, road wrecks, occupied places |
| `scripts/world/map/MapRouter.cs` | A* routing for the GPS |
| `scripts/ui/MapScreen.cs`, `GpsHud.cs` | Map screen (M) and the guidance strip |
| `shaders/map_road.gdshader`, `building.gdshader` | Road surfaces/markings and facades |

How it plugs in: `WorldGenerator` has an optional `Map` (`MapWorld`). When set, `Height`,
`Compose`, `Biome` and `IsWild` defer to it and `PoiPlacer` returns nothing, so the rest of the
game keeps calling the same API. `ChunkBuilder` builds one `MapContext` per chunk;
`ChunkStreamer` adds the map meshes to each chunk node and runs `MapPlaces` / `MapSkyline`.
`SaveData.Map` names the map; saves of the procedural world are unaffected.

## How the map works (the decisions worth knowing)

- **Coordinates:** transverse Mercator centred on the Palace of Culture (world 0,0). X = east,
  Z = south (north is −Z, matching the HUD compass). 1 unit = 1 m.
- **Tiles:** 1536 m (16 chunks of 96 m), zlib blobs in packs under 48 MiB so they fit in git.
  Per tile: roads (cut at tile borders), buildings, POIs, points (trees/lamps/poles/pylons), a
  4 m land-cover raster and a 48 m distance-to-water raster.
- **No elevation in OSM.** The ground is invented: gentle noise (up to ~13 m) that flattens
  within ~520 m of real water and dips under it. `MapTile.BaseHeight`.
- **Roads:** flat cross-section at the centre-line height; terrain is pulled to road level
  within 9 m. Bridges get a "lift" computed in the importer (6 m when crossing a road or rail,
  1.4 m over water only) with 5 % ramps spread along the connected roads. Each road class has a
  small height bias plus per-road jitter so overlaps never z-fight.
- **Road collision** includes the sloping shoulders and is one-sided. This matters: without it
  people on foot wedge under the road edge (`mapwalk` test covers it).
- **Lanes** are estimated from carriageway width (`MapRoad.Lanes`); the real `lanes` tag is read
  by `extract.py` and used for the width, but is not stored in the tile. Storing it means a
  format bump and a full rebuild.
- **Junctions** = points where 3+ road arms meet, excluding service roads and tracks; markings
  fade before them and a stop line is drawn (`MapTile.FindJunctions`, UV2 in the road mesh).
- **Buildings:** footprint extrusion; `building:part` pieces replace their outline (this is what
  makes the Palace of Culture recognisable); roofs flat / gabled / hipped / pyramidal / dome;
  windows and a front door (longest wall) are drawn in the shader. No interiors.
- **Exported builds** look for `map/` beside the executable (`MapData.MapDir`), because the
  packs are excluded from the `.pck`.

## Tests

`scripts/core/DebugHarness.cs` drives the real game. Example:

```bash
godot -- --autotest=mapstart,mapwalk --map=mazowsze --place=Kampinos --shots=C:/temp/shots
```

Map scenarios: `mapstart`, `mapair`, `mapdrive`, `mapviews` (`--views=x:z:height:yawDeg:pitchDeg;...`),
`mapscreen` (map, routing, fast travel; `--dest=x,z`), `mapexit`, `maprecover`, `mapwalk`.
Start with `--place=<name>` or `--start=x,z`. Original scenarios (`start`, `drive`, `save`, ...)
still work without `--map`.

Handy coordinates: Kampinos (−37200, −4400), Radom (10259, 92292), central Warsaw junction
(330, 150), Orlen near Kampinos (−36691, −4023).

Known test weakness: the `mapdrive` autopilot just steers at the nearest road and ends up in a
wall after ~150 m. It proves the surface is drivable, not that a route can be followed.

## Verified, and how

- Loads and plays from Kampinos, central Warsaw and Radom (screenshots, software renderer).
- Save / continue round-trips on the map; procedural `start` and `drive` still pass.
- `mapwalk`: 10 of 15 road crossings stuck before the shoulder fix, 0 after.
- `maprecover`: flipped car is righted then towed; upright stuck car is towed in one press.
- Routing: Kampinos → Radom, 139 km, about 3 s.
- A Linux export found the map beside the executable and started a game. The Windows export was
  run by Tom on an office PC (Intel UHD 770) with the OpenGL launcher.

## Open problems

- **Vulkan crash on Intel UHD 770** (Tom's office Dell): Forward+ closes the game just after the
  world is created, no error in `godot.log`. Not reproduced here on software Vulkan.
  `--rendering-driver opengl3` works. Cause unknown; may not affect the 3080.
- **Getting out of the car "inside it" and being launched** — reported by Tom, never reproduced.
  A defensive fix is in (more exit spots, no on-roof fallback, 1.2 s collision exception). See
  the physics-engine note above.
- **Car recovery "does nothing"** — reported, not reproduced in its original form; behaviour was
  changed so an upright car is towed on the first press.
- **Floating buildings** — Tom saw a few. Whole outlines now ignore `min_height`; not confirmed
  against one of his examples.
- **Not looked at:** roundabouts, motorway slip roads, walking on bridges / tram tracks / rails,
  a house stash or occupied place seen by eye, performance of any kind on real hardware.
- **Road tunnels** are drawn as surface roads (rail and tram tunnels are hidden).
- **Junction corners** are overlapping ribbons with rounded ends, not real kerb curves.
- **Float precision:** fine at Radom (92 km); the far corners (~150 km) are untested.
- **Repo weight:** the 29 `mazowieckie-261110.osm.zip.0xx` parts (290 MB) from Tom's upload are
  no longer needed and can be deleted. Each map rebuild adds ~176 MB of history.

## Ideas discussed with Tom, not started

- **Real elevation.** Needs a DEM file; the cloud session could not download one (the network
  allow-list never took effect for added hosts). Easy locally.
- **Whole Poland.** Needs a moving origin and a long-distance routing layer. Tom's
  **Thornwake** (`github.com/badsadcode/Thornwake_Rewrite`) already has both of the hard parts:
  double-precision `WorldPoint` with rebasing (`Game/Core/WorldCoordinates.cs`), a nested-LOD
  streamer with 10–40 km view distance and main-thread commit budgets
  (`Game/Rewrite/WorldStreamer.cs`). Only the README and those two files were skimmed; how tightly
  the streamer is tied to Thornwake's terrain generator has not been checked. Likely best route:
  OSM importer + tile format from here, world machinery from Thornwake.
- **The real goal (Tom, 12 Nov):** not just Poland but a drivable Baltic loop — Poland, Lithuania,
  Latvia, Estonia, Finland, Sweden and Denmark — so he can "drive" to family in Sweden and friends
  in Denmark and Estonia, who all play Project Zomboid with him. He likes working big and does not
  mind breaking things. Consequences to plan for: one map projection will not do across ~2000 km
  (project per tile or per region around the moving origin); sea crossings need ferries (real
  `route=ferry` lines exist in OSM) plus the Øresund bridge and tunnel; tile data in the tens of GB,
  so packs per country or region downloaded on demand rather than kept in git; multiplayer is the
  obvious follow-on and Thornwake already has networking code (`Game/Rewrite/Net`).
- **Real elevation and water (discussed 12 Nov).** Height data: Copernicus DEM GLO-30 (free,
  global, ~30 m, but a surface model: forests and city blocks show as raised ground); Poland's
  GUGiK NMT (free, 1 m / 5 m, bare ground) is better for Poland. No manual alignment: sample the
  height at each tile grid point via the tile's lat/lon and store a height grid in the tile.
  Work needed: smooth heights along roads, reconcile with the invented bridge ramps, and replace
  the single global water level. Thornwake's water looks like the answer to the last one: river
  sheets that follow the terrain (`WaterRiver.gdshader`, `TerrainSampler.Water.cs`), waterfalls
  (`Waterfalls.cs`), an FFT ocean with a height function (`Ocean/OceanFft.cs`), local water
  simulation (`LocalWaterView.cs`), hydrology in `Game/WorldGen/Hydrology.cs`. Only file names
  and header comments were read. Expectation: use its drawing/simulation side and feed it real
  OSM river lines and lake outlines; carve rivers a few metres into the DEM along their mapped
  line so water always sits in a channel.
- **Decision (Tom, 12 Nov): start a new repository that mixes both worlds** — Thornwake's world
  machinery and water with this project's OSM importer, tile format, roads, buildings, routing
  and map screen. Dead Miles stays as the experiment it was. Nothing has been created yet.
- **Tile editor** as a Godot editor tool, storing overrides per tile (replace building N with a
  model, add props, mark bus stops / depots) so the generated map can be re-imported without
  losing edits.
- **Bus / taxi / logistics sims** on a smaller region: OSM already has stops, routes, depots,
  warehouses and speed limits; they would be new importer layers.

## Licence

Map data © OpenStreetMap contributors, ODbL 1.0. The credit on the main menu must stay visible,
and the generated packs are a derived database under the same licence.
