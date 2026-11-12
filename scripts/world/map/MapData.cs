using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;

namespace DeadMiles;

// Reader for the tile packs written by tools/osm_import/build.py. The binary layout and the
// numeric ids below must stay in step with that script.

/// <summary>Land-cover classes of the 4 m raster.</summary>
public static class MapLand
{
    public const byte Open = 0, Forest = 1, Field = 2, Meadow = 3, Water = 4, Residential = 5, Paved = 6,
        Scrub = 7, Wetland = 8, Sand = 9, Orchard = 10, Park = 11, Cemetery = 12, Farmyard = 13;
}

public static class RoadFlag
{
    public const byte Bridge = 1, Tunnel = 2, Oneway = 4, Unpaved = 8, Lit = 16, Rail = 32, StartCap = 64, EndCap = 128;
}

/// <summary>Road classes, most important first. Rail is last and is not drivable.</summary>
public static class RoadClass
{
    public const byte Motorway = 0, Trunk = 1, Primary = 2, Secondary = 3, Tertiary = 4, Unclassified = 5,
        Residential = 6, Living = 7, Service = 8, Track = 9, Link = 10, Rail = 11;
}

public static class BuildingKind
{
    public const byte Generic = 0, House = 1, Apartments = 2, Commercial = 3, Industrial = 4, Farm = 5,
        Shed = 6, Church = 7, Civic = 8, Tower = 9;
}

public static class RoofKind
{
    public const byte Flat = 1, Gabled = 2, Hipped = 3, Pyramidal = 4, Dome = 5;
}

public enum MapPoiKind : byte { Fuel, Food, Shop, Medical, Diner, Garage, Police, Hardware, Camp }

/// <summary>A stretch of road inside one tile. Points are tile-local metres, at most 12 m apart.</summary>
public sealed class MapRoad
{
    public byte Cls, Flags;
    public float HalfWidth;
    public float[] X = null!, Z = null!;
    /// <summary>Carriageway height in world space at each point (ground, or deck height on bridges).</summary>
    public float[] Y = null!;
    public float[] Lift = null!;
    /// <summary>Distance along the whole road at each point, for lane markings.</summary>
    public float[] Along = null!;

    public bool IsBridge => (Flags & RoadFlag.Bridge) != 0 && (Flags & RoadFlag.Tunnel) == 0;
    public bool IsRail => (Flags & RoadFlag.Rail) != 0;
    public bool Unpaved => (Flags & RoadFlag.Unpaved) != 0;
    public bool Drivable => !IsRail;
}

public sealed class MapBuilding
{
    public int Index;
    public byte Type, Flags, Roof;
    public float Height, MinHeight, RoofHeight;
    /// <summary>Rings in tile-local metres. Ring 0 is the outline (positive shoelace area in X,Z); the rest are courtyards.</summary>
    public float[][] RX = null!, RZ = null!;
    /// <summary>Roof triangulation over all rings' vertices in order; only present for outlines with courtyards.</summary>
    public ushort[]? Tris;
    public float MinX, MinZ, MaxX, MaxZ, Cx, Cz;

    public bool Ghost => (Flags & 1) != 0;
    public bool IsPart => (Flags & 4) != 0;

    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        var rx = RX[0]; var rz = RZ[0];
        bool inside = false;
        for (int i = 0, j = rx.Length - 1; i < rx.Length; j = i++)
        {
            if ((rz[i] > z) != (rz[j] > z) && x < (rx[j] - rx[i]) * (z - rz[i]) / (rz[j] - rz[i]) + rx[i])
                inside = !inside;
        }
        return inside;
    }
}

public sealed class MapPoi
{
    public string Id = "";
    public MapPoiKind Kind;
    public float X, Z;      // tile-local
    public string Name = "";
}

/// <summary>One 1536 m square of the map: roads, buildings, points of interest and the land rasters.</summary>
public sealed class MapTile
{
    public const float Size = 1536f;
    public const int LandRes = 384;
    public const float LandCell = Size / LandRes;
    public const int WdRes = 32;
    public const float WdCell = Size / WdRes;
    public const int Cells = 16;                 // bucket grid: one bucket per 96 m terrain chunk
    public const float CellSize = Size / Cells;
    private const float MaxSegment = 12f;

    public readonly int Tx, Tz;
    public readonly double OriginX, OriginZ;
    public readonly bool Empty;
    public MapRoad[] Roads = Array.Empty<MapRoad>();
    public MapBuilding[] Buildings = Array.Empty<MapBuilding>();
    public MapPoi[] Pois = Array.Empty<MapPoi>();
    public byte[] Land;
    public byte[] WaterDist;
    private float _wdUnit = 4f;
    /// <summary>Per bucket: packed (road index &lt;&lt; 12 | segment index).</summary>
    private readonly List<int>?[] _roadCells = new List<int>?[Cells * Cells];
    private readonly List<int>?[] _buildingCells = new List<int>?[Cells * Cells];

    private static readonly byte[] EmptyLand = new byte[LandRes * LandRes];
    private static readonly byte[] EmptyWd = MakeFilled((WdRes + 1) * (WdRes + 1), 255);
    private static byte[] MakeFilled(int n, byte v) { var a = new byte[n]; Array.Fill(a, v); return a; }

    private readonly MapData _map;

    /// <summary>A tile with no data: open grassland. Used outside the mapped region.</summary>
    public MapTile(MapData map, int tx, int tz)
    {
        _map = map; Tx = tx; Tz = tz; OriginX = tx * (double)Size; OriginZ = tz * (double)Size;
        Land = EmptyLand; WaterDist = EmptyWd; Empty = true;
    }

    public MapTile(MapData map, int tx, int tz, byte[] raw, float wdUnit)
    {
        _map = map; Tx = tx; Tz = tz; OriginX = tx * (double)Size; OriginZ = tz * (double)Size;
        _wdUnit = wdUnit;
        using var ms = new MemoryStream(raw);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != 0x31544D44u) throw new InvalidDataException("bad tile magic"); // "DMT1"

        // Rasters sit at the end of the blob but are needed first (road heights depend on them).
        int rasterBytes = LandRes * LandRes + (WdRes + 1) * (WdRes + 1);
        Land = new byte[LandRes * LandRes];
        WaterDist = new byte[(WdRes + 1) * (WdRes + 1)];
        Buffer.BlockCopy(raw, raw.Length - rasterBytes, Land, 0, Land.Length);
        Buffer.BlockCopy(raw, raw.Length - WaterDist.Length, WaterDist, 0, WaterDist.Length);

        int nRoads = (int)r.ReadUInt32();
        Roads = new MapRoad[nRoads];
        var xs = new List<float>(); var zs = new List<float>(); var ls = new List<float>(); var al = new List<float>();
        for (int i = 0; i < nRoads; i++)
        {
            var road = new MapRoad { Cls = r.ReadByte(), Flags = r.ReadByte() };
            road.HalfWidth = r.ReadByte() * 0.05f;
            r.ReadSByte(); // layer: already folded into the lift values
            float along = r.ReadSingle();
            int n = r.ReadUInt16();
            xs.Clear(); zs.Clear(); ls.Clear(); al.Clear();
            float px = 0, pz = 0, pl = 0;
            for (int k = 0; k < n; k++)
            {
                float x = r.ReadInt16() * 0.1f, z = r.ReadInt16() * 0.1f, l = r.ReadByte() * 0.1f;
                if (k > 0)
                {
                    float len = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                    int parts = Math.Max(1, (int)MathF.Ceiling(len / MaxSegment));
                    for (int s = 1; s <= parts; s++)
                    {
                        float t = s / (float)parts;
                        xs.Add(px + (x - px) * t); zs.Add(pz + (z - pz) * t); ls.Add(pl + (l - pl) * t);
                        al.Add(along + len * t);
                    }
                    along += len;
                }
                else { xs.Add(x); zs.Add(z); ls.Add(l); al.Add(along); }
                px = x; pz = z; pl = l;
            }
            road.X = xs.ToArray(); road.Z = zs.ToArray(); road.Lift = ls.ToArray(); road.Along = al.ToArray();
            road.Y = new float[road.X.Length];
            float floor = WorldGenerator.WaterLevel + 0.6f;
            for (int k = 0; k < road.X.Length; k++)
                road.Y[k] = MathF.Max(BaseHeight(road.X[k], road.Z[k]), floor) + road.Lift[k];
            Roads[i] = road;
            int segs = Math.Min(road.X.Length - 1, 4095);
            for (int k = 0; k < segs; k++)
                Bucket(_roadCells, MathF.Min(road.X[k], road.X[k + 1]), MathF.Min(road.Z[k], road.Z[k + 1]),
                    MathF.Max(road.X[k], road.X[k + 1]), MathF.Max(road.Z[k], road.Z[k + 1]), (i << 12) | k);
        }

        int nBuildings = (int)r.ReadUInt32();
        Buildings = new MapBuilding[nBuildings];
        for (int i = 0; i < nBuildings; i++)
        {
            var b = new MapBuilding { Index = i, Type = r.ReadByte(), Flags = r.ReadByte(), Roof = r.ReadByte() };
            b.Height = r.ReadUInt16() * 0.1f;
            b.MinHeight = r.ReadUInt16() * 0.1f;
            b.RoofHeight = r.ReadUInt16() * 0.1f;
            int rings = r.ReadByte();
            b.RX = new float[rings][]; b.RZ = new float[rings][];
            b.MinX = b.MinZ = float.MaxValue; b.MaxX = b.MaxZ = float.MinValue;
            for (int q = 0; q < rings; q++)
            {
                int n = r.ReadUInt16();
                var rx = new float[n]; var rz = new float[n];
                for (int k = 0; k < n; k++)
                {
                    rx[k] = r.ReadInt16() * 0.1f; rz[k] = r.ReadInt16() * 0.1f;
                    if (q == 0)
                    {
                        b.MinX = MathF.Min(b.MinX, rx[k]); b.MaxX = MathF.Max(b.MaxX, rx[k]);
                        b.MinZ = MathF.Min(b.MinZ, rz[k]); b.MaxZ = MathF.Max(b.MaxZ, rz[k]);
                        b.Cx += rx[k]; b.Cz += rz[k];
                    }
                }
                if (q == 0 && n > 0) { b.Cx /= n; b.Cz /= n; }
                b.RX[q] = rx; b.RZ[q] = rz;
            }
            if ((b.Flags & 2) != 0)
            {
                int n = r.ReadUInt16();
                b.Tris = new ushort[n];
                for (int k = 0; k < n; k++) b.Tris[k] = r.ReadUInt16();
            }
            Buildings[i] = b;
            Bucket(_buildingCells, b.MinX, b.MinZ, b.MaxX, b.MaxZ, i);
        }

        int nPois = r.ReadUInt16();
        Pois = new MapPoi[nPois];
        for (int i = 0; i < nPois; i++)
        {
            var p = new MapPoi { Kind = (MapPoiKind)r.ReadByte(), X = r.ReadUInt16() * 0.1f, Z = r.ReadUInt16() * 0.1f };
            int len = r.ReadByte();
            p.Name = Encoding.UTF8.GetString(r.ReadBytes(len));
            p.Id = $"m{tx}_{tz}_{i}";
            Pois[i] = p;
        }
    }

    private static void Bucket(List<int>?[] cells, float x0, float z0, float x1, float z1, int value)
    {
        int cx0 = Math.Clamp((int)MathF.Floor(x0 / CellSize), 0, Cells - 1), cx1 = Math.Clamp((int)MathF.Floor(x1 / CellSize), 0, Cells - 1);
        int cz0 = Math.Clamp((int)MathF.Floor(z0 / CellSize), 0, Cells - 1), cz1 = Math.Clamp((int)MathF.Floor(z1 / CellSize), 0, Cells - 1);
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
                (cells[cz * Cells + cx] ??= new List<int>(8)).Add(value);
    }

    public List<int>? RoadCell(int cx, int cz) => _roadCells[cz * Cells + cx];
    public List<int>? BuildingCell(int cx, int cz) => _buildingCells[cz * Cells + cx];

    // ---- rasters (tile-local coordinates; lookups clamp to this tile so they never need a neighbour) ----

    public byte LandAt(float lx, float lz)
    {
        int i = Math.Clamp((int)(lx / LandCell), 0, LandRes - 1), j = Math.Clamp((int)(lz / LandCell), 0, LandRes - 1);
        return Land[j * LandRes + i];
    }

    /// <summary>Bilinear blend of a per-class value (see the look-up tables in <see cref="MapWorld"/>).</summary>
    public float LandBlend(float lx, float lz, float[] lut)
    {
        float fx = lx / LandCell - 0.5f, fz = lz / LandCell - 0.5f;
        int i0 = (int)MathF.Floor(fx), j0 = (int)MathF.Floor(fz);
        float u = fx - i0, v = fz - j0;
        int i1 = Math.Clamp(i0 + 1, 0, LandRes - 1), j1 = Math.Clamp(j0 + 1, 0, LandRes - 1);
        i0 = Math.Clamp(i0, 0, LandRes - 1); j0 = Math.Clamp(j0, 0, LandRes - 1);
        float a = lut[Land[j0 * LandRes + i0]], b = lut[Land[j0 * LandRes + i1]];
        float c = lut[Land[j1 * LandRes + i0]], d = lut[Land[j1 * LandRes + i1]];
        return (a + (b - a) * u) * (1f - v) + (c + (d - c) * u) * v;
    }

    /// <summary>Metres to the nearest open water, saturating at about one kilometre.</summary>
    public float WaterDistance(float lx, float lz)
    {
        float fx = Math.Clamp(lx / WdCell, 0f, WdRes - 0.001f), fz = Math.Clamp(lz / WdCell, 0f, WdRes - 0.001f);
        int i = (int)fx, j = (int)fz;
        float u = fx - i, v = fz - j;
        int w = WdRes + 1;
        float a = WaterDist[j * w + i], b = WaterDist[j * w + i + 1], c = WaterDist[(j + 1) * w + i], d = WaterDist[(j + 1) * w + i + 1];
        return ((a + (b - a) * u) * (1f - v) + (c + (d - c) * u) * v) * _wdUnit;
    }

    /// <summary>
    /// Natural ground before roads are cut into it. The map carries no elevation, so the land is a
    /// gentle invented relief that flattens towards real water and dips under it.
    /// </summary>
    public float BaseHeight(float lx, float lz)
    {
        float wx = (float)(OriginX + lx), wz = (float)(OriginZ + lz);
        float amp = MathX.Smooth(10f, 520f, WaterDistance(lx, lz));
        float h = WorldGenerator.WaterLevel + 0.9f;
        if (amp > 0f)
        {
            float n = _map.Macro.Fbm(wx / 1500f, wz / 1500f, 3) * 0.5f + 0.5f;
            float hills = _map.Hills.Fbm(wx / 240f, wz / 240f, 2) * 0.5f + 0.5f;
            h += amp * (n * 11f + hills * 2.4f);
        }
        // Four spread taps round off the 4 m raster steps into a soft shoreline.
        const float r = 3f;
        float water = (LandBlend(lx - r, lz - r, MapWorld.WaterLut) + LandBlend(lx + r, lz - r, MapWorld.WaterLut)
                     + LandBlend(lx - r, lz + r, MapWorld.WaterLut) + LandBlend(lx + r, lz + r, MapWorld.WaterLut)) * 0.25f;
        if (water > 0f) h = Mathf.Lerp(h, WorldGenerator.WaterLevel - 1.6f, MathX.Smooth(0.25f, 0.75f, water));
        return h;
    }
}

/// <summary>A named settlement, for the HUD and for choosing where a new game starts.</summary>
public sealed class MapPlace
{
    public string Name = "";
    public int Rank;       // 0 city, 1 town, 2 village
    public float X, Z;
    public int Population;
}

/// <summary>Opens a map (index + pack files) and hands out tiles on demand. Safe to call from worker threads.</summary>
public sealed class MapData
{
    public readonly string Name;
    public readonly Noise2 Macro, Hills;
    public readonly List<MapPlace> Places = new();
    public double OriginLat, OriginLon;
    public int TileCount => _index.Count;

    private readonly struct Entry
    {
        public readonly int Pak; public readonly uint Offset, Length, RawLength;
        public Entry(int pak, uint offset, uint length, uint raw) { Pak = pak; Offset = offset; Length = length; RawLength = raw; }
    }

    private readonly Dictionary<long, Entry> _index = new();
    private readonly Dictionary<int, Godot.FileAccess> _paks = new();
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<long, Lazy<MapTile>> _tiles = new();
    private readonly string _dir;
    private float _wdUnit = 4f;

    public static string IndexPath(string name) => $"res://map/{name}.idx";
    public static bool Exists(string name) => Godot.FileAccess.FileExists(IndexPath(name));

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public MapData(string name, int seed)
    {
        Name = name;
        _dir = "res://map/";
        Macro = new Noise2(seed);
        Hills = new Noise2(seed + 101);
        using var f = Godot.FileAccess.Open(IndexPath(name), Godot.FileAccess.ModeFlags.Read)
            ?? throw new IOException($"Cannot open {IndexPath(name)}: {Godot.FileAccess.GetOpenError()}");
        var bytes = f.GetBuffer((long)f.GetLength());
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (r.ReadUInt32() != 0x58494D44u) throw new InvalidDataException("bad map index magic"); // "DMIX"
        uint version = r.ReadUInt32();
        float tile = r.ReadSingle();
        uint landRes = r.ReadUInt32(), wdRes = r.ReadUInt32();
        _wdUnit = r.ReadSingle();
        OriginLat = r.ReadDouble(); OriginLon = r.ReadDouble();
        if (version != 1 || tile != MapTile.Size || landRes != MapTile.LandRes || wdRes != MapTile.WdRes)
            throw new InvalidDataException($"map '{name}' was built with an incompatible importer (version {version})");
        int count = (int)r.ReadUInt32();
        for (int i = 0; i < count; i++)
        {
            int tx = r.ReadInt16(), tz = r.ReadInt16(), pak = r.ReadUInt16();
            uint off = r.ReadUInt32(), len = r.ReadUInt32(), raw = r.ReadUInt32();
            _index[Key(tx, tz)] = new Entry(pak, off, len, raw);
        }
        LoadPlaces();
        Log.Info("MapData", $"Map '{name}': {count} tiles, {Places.Count} named places");
    }

    private void LoadPlaces() => Places.AddRange(ReadPlaces(Name));

    /// <summary>Named settlements of a map, biggest first. Readable without opening the whole map.</summary>
    public static List<MapPlace> ReadPlaces(string name)
    {
        var list = new List<MapPlace>();
        string path = $"res://map/{name}.places.json";
        if (!Godot.FileAccess.FileExists(path)) return list;
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (f == null) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(f.GetAsText());
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add(new MapPlace
                {
                    Name = e.GetProperty("n").GetString() ?? "", Rank = e.GetProperty("r").GetInt32(),
                    X = e.GetProperty("x").GetSingle(), Z = e.GetProperty("z").GetSingle(), Population = e.GetProperty("p").GetInt32(),
                });
        }
        catch (Exception ex) { Log.Warn("MapData", $"Could not read {path}: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// Where a new game may begin: a quiet village first (the default), then the capital and the
    /// larger towns spread across the region.
    /// </summary>
    public static List<MapPlace> StartPlaces(string name)
    {
        var all = ReadPlaces(name);
        var result = new List<MapPlace>();
        var village = all.Find(p => p.Name == DefaultStartVillage && p.Rank == 2);
        if (village != null) result.Add(village);
        foreach (string town in StartTowns)
        {
            var p = all.Find(q => q.Name == town && q.Rank <= 1);
            if (p != null) result.Add(p);
        }
        if (result.Count == 0 && all.Count > 0) result.Add(all[0]);
        return result;
    }

    public const string DefaultStartVillage = "Kampinos";
    public static readonly string[] StartTowns =
        { "Warszawa", "Radom", "Płock", "Siedlce", "Ostrołęka", "Ciechanów", "Żyrardów", "Mińsk Mazowiecki", "Grójec" };

    public bool HasTile(int tx, int tz) => _index.ContainsKey(Key(tx, tz));

    public MapTile Tile(int tx, int tz)
        => _tiles.GetOrAdd(Key(tx, tz), _ => new Lazy<MapTile>(() => Load(tx, tz))).Value;

    public MapTile TileAt(double wx, double wz)
        => Tile((int)Math.Floor(wx / MapTile.Size), (int)Math.Floor(wz / MapTile.Size));

    private MapTile Load(int tx, int tz)
    {
        if (!_index.TryGetValue(Key(tx, tz), out var e)) return new MapTile(this, tx, tz);
        try
        {
            byte[] comp;
            lock (_ioLock)
            {
                if (!_paks.TryGetValue(e.Pak, out var f))
                {
                    string path = $"{_dir}{Name}.{e.Pak:00}.pak";
                    f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
                        ?? throw new IOException($"Cannot open {path}: {Godot.FileAccess.GetOpenError()}");
                    _paks[e.Pak] = f;
                }
                f.Seek(e.Offset);
                comp = f.GetBuffer(e.Length);
            }
            var raw = new byte[e.RawLength];
            using (var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != raw.Length) throw new InvalidDataException("tile is truncated");
            }
            return new MapTile(this, tx, tz, raw, _wdUnit);
        }
        catch (Exception ex)
        {
            GD.PushError($"[MapData] Tile {tx},{tz} could not be read: {ex.Message}");
            return new MapTile(this, tx, tz);
        }
    }

    /// <summary>Drops tiles far from the focus. Workers that still hold one keep it alive until they finish.</summary>
    public void Trim(double wx, double wz, int keepRadius)
    {
        int fx = (int)Math.Floor(wx / MapTile.Size), fz = (int)Math.Floor(wz / MapTile.Size);
        foreach (var key in _tiles.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (Math.Abs(tx - fx) > keepRadius || Math.Abs(tz - fz) > keepRadius) _tiles.TryRemove(key, out _);
        }
    }
}
