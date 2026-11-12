using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>One road segment gathered for a query, in the query's local frame.</summary>
public struct MapSeg
{
    public float Ax, Az, Bx, Bz;    // local metres
    public float Ya, Yb;            // carriageway height at each end (world)
    public float Hw;                // half width
    public MapRoad Road;
    public int Index;               // segment index within the road
    public float OffX, OffZ;        // tile origin relative to the query origin

    /// <summary>Distance from a point to the segment; <paramref name="u"/> is the 0..1 position of the closest point.</summary>
    public float Distance(float x, float z, out float u)
    {
        float bx = Bx - Ax, bz = Bz - Az;
        float len2 = bx * bx + bz * bz;
        u = len2 > 1e-6f ? Math.Clamp(((x - Ax) * bx + (z - Az) * bz) / len2, 0f, 1f) : 0f;
        float ex = x - (Ax + bx * u), ez = z - (Az + bz * u);
        return MathF.Sqrt(ex * ex + ez * ez);
    }
}

public struct MapBuildingRef
{
    public MapBuilding B;
    public float OffX, OffZ;
}

/// <summary>
/// Everything man-made around one spot, gathered once so that many height / occupancy queries
/// (a terrain chunk, a grass cell) do not each have to walk the tile buckets. Coordinates are
/// relative to the origin given to <see cref="Reset"/>, which keeps them small and exact even
/// 150 km from the world origin. Not thread-safe: one context per worker.
/// </summary>
public sealed class MapContext
{
    private const float Reach = 26f;        // how far a road can influence the ground, plus slack

    public readonly MapWorld World;
    public double Ox, Oz;
    public readonly List<MapSeg> Segs = new();
    public readonly List<MapBuildingRef> Buildings = new();
    /// <summary>Mapped trees, lamps, poles and pylons: X/Z local metres, Y holds the <see cref="MapPointKind"/>.</summary>
    public readonly List<Vector3> Points = new();
    private readonly HashSet<long> _seen = new();
    private MapTile? _tile;

    public MapContext(MapWorld world) { World = world; }

    /// <summary>Gathers roads and buildings touching the local rectangle (plus the road influence margin).</summary>
    public void Reset(double ox, double oz, float minX, float minZ, float maxX, float maxZ)
    {
        Ox = ox; Oz = oz;
        Segs.Clear(); Buildings.Clear(); Points.Clear(); _seen.Clear(); _tile = null;
        double wx0 = ox + minX - Reach, wx1 = ox + maxX + Reach, wz0 = oz + minZ - Reach, wz1 = oz + maxZ + Reach;
        int tx0 = (int)Math.Floor(wx0 / MapTile.Size), tx1 = (int)Math.Floor(wx1 / MapTile.Size);
        int tz0 = (int)Math.Floor(wz0 / MapTile.Size), tz1 = (int)Math.Floor(wz1 / MapTile.Size);
        for (int tz = tz0; tz <= tz1; tz++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = World.Data.Tile(tx, tz);
                if (tile.Empty) continue;
                float offX = (float)(tile.OriginX - ox), offZ = (float)(tile.OriginZ - oz);
                int cx0 = Math.Clamp((int)Math.Floor((wx0 - tile.OriginX) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cx1 = Math.Clamp((int)Math.Floor((wx1 - tile.OriginX) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cz0 = Math.Clamp((int)Math.Floor((wz0 - tile.OriginZ) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cz1 = Math.Clamp((int)Math.Floor((wz1 - tile.OriginZ) / MapTile.CellSize), 0, MapTile.Cells - 1);
                long tileKey = ((long)(tx & 0xffff) << 48) | ((long)(tz & 0xffff) << 32);
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        var rc = tile.RoadCell(cx, cz);
                        if (rc != null)
                        {
                            foreach (int packed in rc)
                            {
                                if (!_seen.Add(tileKey | (uint)packed)) continue;
                                var road = tile.Roads[packed >> 12];
                                int k = packed & 4095;
                                Segs.Add(new MapSeg
                                {
                                    Ax = offX + road.X[k], Az = offZ + road.Z[k], Bx = offX + road.X[k + 1], Bz = offZ + road.Z[k + 1],
                                    Ya = road.Y[k], Yb = road.Y[k + 1], Hw = road.HalfWidth, Road = road, Index = k, OffX = offX, OffZ = offZ,
                                });
                            }
                        }
                        var pc = tile.PointCell(cx, cz);
                        if (pc != null)
                            foreach (int pi in pc)
                                Points.Add(new Vector3(offX + tile.PointX[pi], tile.PointKind[pi], offZ + tile.PointZ[pi]));
                        var bc = tile.BuildingCell(cx, cz);
                        if (bc != null)
                        {
                            foreach (int bi in bc)
                            {
                                if (!_seen.Add(tileKey | 0x80000000u | (uint)bi)) continue;
                                Buildings.Add(new MapBuildingRef { B = tile.Buildings[bi], OffX = offX, OffZ = offZ });
                            }
                        }
                    }
                }
            }
        }
    }

    private MapTile TileFor(float lx, float lz, out float tlx, out float tlz)
    {
        double wx = Ox + lx, wz = Oz + lz;
        var t = _tile;
        if (t == null || wx < t.OriginX || wx >= t.OriginX + MapTile.Size || wz < t.OriginZ || wz >= t.OriginZ + MapTile.Size)
            _tile = t = World.Data.TileAt(wx, wz);
        tlx = (float)(wx - t.OriginX); tlz = (float)(wz - t.OriginZ);
        return t;
    }

    /// <summary>The nearest road that shapes the ground (bridges do not). Returns its edge distance, or a large value.</summary>
    public float NearestGroundRoad(float lx, float lz, out float roadY, out MapRoad? road)
    {
        float best = 1e9f; roadY = 0; road = null;
        var segs = Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Road.IsBridge || s.Road.IsTram) continue;   // trams are laid into whatever they cross
            float pad = s.Hw + 10f;
            if (lx < MathF.Min(s.Ax, s.Bx) - pad || lx > MathF.Max(s.Ax, s.Bx) + pad ||
                lz < MathF.Min(s.Az, s.Bz) - pad || lz > MathF.Max(s.Az, s.Bz) + pad) continue;
            float edge = s.Distance(lx, lz, out float u) - s.Hw;
            if (edge < best) { best = edge; roadY = s.Ya + (s.Yb - s.Ya) * u; road = s.Road; }
        }
        return best;
    }

    /// <summary>
    /// Final ground height at a local point. Roads cut a level bed (or raise an embankment where
    /// they climb to a bridge); <paramref name="depress"/> sinks the ground under the carriageway
    /// so the road mesh never fights the terrain mesh; <paramref name="dirt"/> is the bare-earth mask.
    /// </summary>
    public float Height(float lx, float lz, float depress, out float dirt)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        float h = tile.BaseHeight(tlx, tlz);
        dirt = tile.Empty ? 0f : tile.LandBlend(tlx, tlz, MapWorld.DirtLut);
        float edge = NearestGroundRoad(lx, lz, out float roadY, out var road);
        if (edge < 9f)
        {
            h = Mathf.Lerp(roadY, h, MathX.Smooth(1.5f, 9f, edge));
            dirt = MathF.Max(dirt, road!.Unpaved || road.IsRail ? 1f - MathX.Smooth(-0.2f, 1.1f, edge) : 1f - MathX.Smooth(0.3f, 1.7f, edge));
            if (depress > 0f) h -= depress * (1f - MathX.Smooth(0.15f, 1.25f, edge));
        }
        return h;
    }

    public void Biome(float lx, float lz, out float forest, out float field)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        if (tile.Empty) { forest = 0.1f; field = 0f; return; }
        forest = tile.LandBlend(tlx, tlz, MapWorld.ForestLut);
        field = tile.LandBlend(tlx, tlz, MapWorld.FieldLut);
    }

    public byte Land(float lx, float lz)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        return tile.LandAt(tlx, tlz);
    }

    /// <summary>True when no road, railway or building is within <paramref name="margin"/> metres.</summary>
    public bool IsWild(float lx, float lz, float margin)
    {
        var segs = Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            float pad = s.Hw + 1.6f + margin;
            if (lx < MathF.Min(s.Ax, s.Bx) - pad || lx > MathF.Max(s.Ax, s.Bx) + pad ||
                lz < MathF.Min(s.Az, s.Bz) - pad || lz > MathF.Max(s.Az, s.Bz) + pad) continue;
            if (s.Distance(lx, lz, out _) < pad) return false;
        }
        return !InBuilding(lx, lz, margin);
    }

    public bool InBuilding(float lx, float lz, float margin)
    {
        var list = Buildings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var b = r.B;
            if (b.IsCanopy) continue;   // open underneath
            float x = lx - r.OffX, z = lz - r.OffZ;
            if (x < b.MinX - margin || x > b.MaxX + margin || z < b.MinZ - margin || z > b.MaxZ + margin) continue;
            if (b.Contains(x, z)) return true;
            if (margin <= 0f) continue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0, j = rx.Length - 1; k < rx.Length; j = k++)
            {
                float bx = rx[k] - rx[j], bz = rz[k] - rz[j];
                float len2 = bx * bx + bz * bz;
                float u = len2 > 1e-6f ? Math.Clamp(((x - rx[j]) * bx + (z - rz[j]) * bz) / len2, 0f, 1f) : 0f;
                float ex = x - (rx[j] + bx * u), ez = z - (rz[j] + bz * u);
                if (ex * ex + ez * ez < margin * margin) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// The real-map world: answers the same questions as the procedural <see cref="WorldGenerator"/>
/// (height, biome, "is this spot free") from OpenStreetMap tiles instead of noise. Thread-safe.
/// </summary>
public sealed class MapWorld
{
    /// <summary>Seed of the invented relief; fixed so the land is the same for everyone.</summary>
    public const int TerrainSeed = 20261;
    public const string DefaultMap = "mazowsze";

    public readonly MapData Data;

    /// <summary>Opens a map by name, or returns null (with the reason logged) when its files are missing or unreadable.</summary>
    public static MapWorld? TryOpen(string name)
    {
        if (!MapData.Exists(name))
        {
            Log.Error("MapWorld", $"Map '{name}' is not installed (expected {MapData.IndexPath(name)})");
            return null;
        }
        try { return new MapWorld(new MapData(name, TerrainSeed)); }
        catch (Exception e)
        {
            Log.Error("MapWorld", $"Map '{name}' could not be opened: {e.Message}");
            return null;
        }
    }

    public bool InBuilding(float x, float z, float margin) => Point(x, z, margin + 1f).InBuilding(0, 0, margin);

    /// <summary>Centre of the nearest roof-on-posts (fuel station canopy) within reach, if any.</summary>
    public bool NearestCanopy(float x, float z, float reach, out float cx, out float cz, out float yaw)
    {
        var c = Point(x, z, reach);
        float best = reach * reach;
        cx = cz = yaw = 0;
        bool found = false;
        foreach (var r in c.Buildings)
        {
            var b = r.B;
            if (!b.IsCanopy || b.Ghost || (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ) < 30f) continue;
            float bx = r.OffX + b.Cx, bz = r.OffZ + b.Cz;
            float d = bx * bx + bz * bz;
            if (d >= best) continue;
            best = d; found = true;
            cx = x + bx; cz = z + bz;
            // line the pumps up with the canopy's longest edge
            var rx = b.RX[0]; var rz = b.RZ[0];
            float longest = 0;
            for (int i = 0, j = rx.Length - 1; i < rx.Length; j = i++)
            {
                float dx = rx[i] - rx[j], dz = rz[i] - rz[j];
                float len = dx * dx + dz * dz;
                if (len > longest) { longest = len; yaw = MathF.Atan2(-dz, dx); }
            }
        }
        return found;
    }

    public byte LandAt(float x, float z) => Point(x, z).Land(0, 0);

    // Per land class: how wooded, how cultivated and how bare the ground is.
    public static readonly float[] ForestLut = Lut((MapLand.Forest, 1f), (MapLand.Residential, 0.13f), (MapLand.Park, 0.32f),
        (MapLand.Orchard, 0.36f), (MapLand.Scrub, 0.24f), (MapLand.Cemetery, 0.3f), (MapLand.Wetland, 0.1f), (MapLand.Open, 0.05f));
    public static readonly float[] FieldLut = Lut((MapLand.Field, 1f));
    public static readonly float[] DirtLut = Lut((MapLand.Paved, 0.85f), (MapLand.Farmyard, 0.8f), (MapLand.Sand, 1f));
    public static readonly float[] WaterLut = Lut((MapLand.Water, 1f));

    private static float[] Lut(params (byte cls, float v)[] entries)
    {
        var lut = new float[256];
        foreach (var (cls, v) in entries) lut[cls] = v;
        return lut;
    }

    [ThreadStatic] private static MapContext? _point;

    public MapWorld(MapData data) { Data = data; }

    /// <summary>A small context around one world point, reused per thread (valid until the next call on this thread).</summary>
    public MapContext Point(float x, float z, float radius = 0f)
    {
        var c = _point;
        if (c == null || c.World != this) _point = c = new MapContext(this);
        c.Reset(x, z, -radius, -radius, radius, radius);
        return c;
    }

    public float Height(float x, float z) => Point(x, z).Height(0, 0, 0, out _);

    public float Height(float x, float z, float depress, out float dirt) => Point(x, z).Height(0, 0, depress, out dirt);

    public void Biome(float x, float z, out float forest, out float field) => Point(x, z).Biome(0, 0, out forest, out field);

    public bool IsWild(float x, float z, float margin) => Point(x, z, margin).IsWild(0, 0, margin);

    /// <summary>Signed-less distance to the edge of the nearest drivable road (negative when on it).</summary>
    public float RoadEdgeDistance(float x, float z, float search = 30f)
    {
        var c = Point(x, z, search);
        float best = 1e9f;
        foreach (var s in c.Segs)
        {
            if (!s.Road.Drivable) continue;
            float e = s.Distance(0, 0, out _) - s.Hw;
            if (e < best) best = e;
        }
        return best;
    }

    /// <summary>
    /// Finds a drivable road near a point. <paramref name="pos"/> is on the carriageway, offset
    /// <paramref name="lateral"/> metres to the right of the direction given by <paramref name="yaw"/>.
    /// </summary>
    public bool NearestRoad(float x, float z, float search, float lateral, out Vector3 pos, out float yaw, int maxClass = RoadClass.Link)
    {
        var c = Point(x, z, search);
        float best = 1e9f; MapSeg bs = default; float bu = 0; bool found = false;
        foreach (var s in c.Segs)
        {
            if (!s.Road.Drivable || s.Road.IsBridge || s.Road.Cls > maxClass) continue;
            float d = s.Distance(0, 0, out float u);
            if (d < best) { best = d; bs = s; bu = u; found = true; }
        }
        pos = default; yaw = 0;
        if (!found) return false;
        float dx = bs.Bx - bs.Ax, dz = bs.Bz - bs.Az;
        float len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dz * dz));
        dx /= len; dz /= len;
        float side = MathF.Min(lateral, MathF.Max(0f, bs.Hw - 1.0f));
        // right of travel (dx,dz) is (-dz, dx) in Godot's X/Z plane
        float px = bs.Ax + (bs.Bx - bs.Ax) * bu - dz * side, pz = bs.Az + (bs.Bz - bs.Az) * bu + dx * side;
        pos = new Vector3(x + px, bs.Ya + (bs.Yb - bs.Ya) * bu, z + pz);
        yaw = MathF.Atan2(-dx, -dz);
        return true;
    }

    /// <summary>A random spot on the verge of a drivable road between two distances from a point.</summary>
    public bool RandomRoadside(float x, float z, float minDist, float maxDist, ref Rng rng, out Vector3 pos)
    {
        var c = Point(x, z, maxDist);
        pos = default;
        int count = 0;
        MapSeg pick = default;
        foreach (var s in c.Segs)
        {
            if (!s.Road.Drivable || s.Road.IsBridge || s.Road.Cls == RoadClass.Motorway) continue;
            float d = s.Distance(0, 0, out _);
            if (d < minDist || d > maxDist) continue;
            count++;
            if (rng.Int(count) == 0) pick = s;   // reservoir sampling
        }
        if (count == 0) return false;
        float dx = pick.Bx - pick.Ax, dz = pick.Bz - pick.Az;
        float len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dz * dz));
        float side = (pick.Hw + 0.9f) * (rng.Chance(0.5f) ? 1f : -1f);
        float px = (pick.Ax + pick.Bx) * 0.5f - dz / len * side, pz = (pick.Az + pick.Bz) * 0.5f + dx / len * side;
        pos = new Vector3(x + px, 0, z + pz);
        pos.Y = Height(pos.X, pos.Z);
        return true;
    }

    /// <summary>The nearest named settlement of at least the given rank, or null.</summary>
    public MapPlace? NearestPlace(float x, float z, float maxDist, int maxRank = 2)
    {
        MapPlace? best = null;
        float bestD = maxDist * maxDist;
        foreach (var p in Data.Places)
        {
            if (p.Rank > maxRank) continue;
            float d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
