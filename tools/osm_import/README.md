# OpenStreetMap importer

Turns an OpenStreetMap extract into the tile packs the game streams in its real-map world
(`New Game -> Mazowsze`). The packs in `map/` were built with these scripts from the Geofabrik
extract of województwo mazowieckie.

## Rebuilding the map

Needs Python 3.10+ and about 6 GB of free memory.

```bash
pip install osmium numpy pillow pyproj scipy shapely mapbox-earcut
```

```bash
python3 tools/osm_import/extract.py mazowieckie-latest.osm.pbf work/
```

```bash
python3 tools/osm_import/build.py work/ map/ --name mazowsze
```

Stage 1 (`extract.py`, about 5 minutes) reads the `.pbf` once and writes flat arrays to `work/`.
Stage 2 (`build.py`, about 8 minutes) cuts everything into 1536 m tiles and writes
`map/<name>.idx`, `map/<name>.NN.pak` and `map/<name>.places.json`.

## Using another region

Download any `.osm.pbf` extract from <https://download.geofabrik.de>, set `ORIGIN_LAT` /
`ORIGIN_LON` in `common.py` to a point near the middle of the region (that point becomes world
0,0), and pass a different `--name`. Then point `MapWorld.DefaultMap` and the start-town list in
`MapData.cs` at the new map. Keep the region within roughly 150 km of the origin: positions are
32-bit floats and get coarse beyond that.

## What is in a tile

| Layer | Source | Used for |
| --- | --- | --- |
| Roads, railways, trams | `highway=*`, `railway=rail/tram` | ribbons with markings, bridges with ramps, collision |
| Buildings | `building=*`, `building:part=*` | extruded footprints, roofs, heights, collision |
| Land cover (4 m raster) | `landuse`, `natural`, `leisure`, water | ground colour, where trees and grass grow, lakes and rivers |
| Water distance (48 m raster) | derived | flattens the invented relief towards real shores |
| Trees, lamps, poles, pylons | `natural=tree`, `natural=tree_row`, `highway=street_lamp`, `power=pole/tower` | real street trees and street furniture |
| Points of interest | `amenity=fuel`, `shop=*`, ... | fuel pumps and supply crates |
| Places | `place=city/town/village` | start towns, name shown when you arrive |

OpenStreetMap has no elevation, so the ground is a gentle invented relief.

The numeric ids and the binary layout are shared with `scripts/world/map/MapData.cs`; change both
together and bump the version in the index header.

## Licence of the data

Map data © OpenStreetMap contributors, available under the Open Database License (ODbL) 1.0:
<https://www.openstreetmap.org/copyright>. The credit must stay visible to players (it is shown
on the main menu), and the generated packs are a derived database under the same licence.
