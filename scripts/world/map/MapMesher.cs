using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>Growable vertex/index buffers for one surface, filled on a worker thread.</summary>
public sealed class MeshData
{
    public readonly List<Vector3> V = new();
    public readonly List<Vector3> N = new();
    public readonly List<Vector2> UV = new();
    public readonly List<Vector2> UV2 = new();
    public readonly List<Color> C = new();
    public readonly List<int> I = new();

    public bool Empty => I.Count == 0;

    public int Add(Vector3 v, Vector3 n, Vector2 uv, Vector2 uv2, Color c)
    {
        V.Add(v); N.Add(n); UV.Add(uv); UV2.Add(uv2); C.Add(c);
        return V.Count - 1;
    }

    /// <summary>Adds a triangle wound so that its front faces along <paramref name="facing"/> (Godot fronts are clockwise).</summary>
    public void Tri(int a, int b, int c, Vector3 facing, List<Vector3>? collision = null)
    {
        if ((V[b] - V[a]).Cross(V[c] - V[a]).Dot(facing) > 0f) (b, c) = (c, b);
        I.Add(a); I.Add(b); I.Add(c);
        if (collision != null) { collision.Add(V[a]); collision.Add(V[b]); collision.Add(V[c]); }
    }

    public void Quad(int a, int b, int c, int d, Vector3 facing, List<Vector3>? collision = null)
    {
        Tri(a, b, c, facing, collision);
        Tri(a, c, d, facing, collision);
    }

    public ArrayMesh ToMesh(Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = UV.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = UV2.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = I.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }
}

/// <summary>What the map adds to one terrain chunk: road ribbons and buildings, plus their collision.</summary>
public sealed class MapChunkGeometry
{
    public readonly MeshData Roads = new();
    public readonly MeshData Buildings = new();
    public List<Vector3>? PavedFaces, LooseFaces, BuildingFaces;
}

/// <summary>Worker-side generation of road and building meshes from map data. No scene tree access.</summary>
public static class MapMesher
{
    public const float ShoulderWidth = 1.5f, ShoulderDrop = 0.45f;
    public const float TallBuilding = 42f;   // taller than this is drawn by the skyline layer instead

    // Surface codes in vertex colour R, read by shaders/map_road.gdshader.
    private const float SurfAsphalt = 0f, SurfConcrete = 0.25f, SurfLoose = 0.5f, SurfRail = 1f;

    /// <summary>Small height bias per class so overlapping ribbons at junctions never z-fight.</summary>
    private static float ClassBias(byte cls) => cls switch
    {
        RoadClass.Motorway => 0.10f, RoadClass.Trunk => 0.095f, RoadClass.Primary => 0.09f, RoadClass.Secondary => 0.085f,
        RoadClass.Tertiary => 0.08f, RoadClass.Link => 0.075f, RoadClass.Unclassified => 0.07f, RoadClass.Residential => 0.065f,
        RoadClass.Living => 0.06f, RoadClass.Service => 0.055f, RoadClass.Track => 0.05f, _ => 0.04f,
    };

    private static float Marking(MapRoad r)
    {
        if (r.Unpaved || r.IsRail) return 0f;
        return r.Cls switch
        {
            RoadClass.Motorway or RoadClass.Trunk => (r.Flags & RoadFlag.Oneway) != 0 ? 1f : 0.66f,
            RoadClass.Primary or RoadClass.Secondary => (r.Flags & RoadFlag.Oneway) != 0 && r.HalfWidth > 3.2f ? 1f : 0.66f,
            RoadClass.Tertiary => 0.33f,
            _ => 0f,
        };
    }

    public static void Build(MapContext ctx, float size, int lod, MapChunkGeometry geo, bool collide)
    {
        if (collide)
        {
            geo.PavedFaces = new List<Vector3>();
            geo.LooseFaces = new List<Vector3>();
            geo.BuildingFaces = new List<Vector3>();
        }
        var segs = ctx.Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            float mx = (s.Ax + s.Bx) * 0.5f, mz = (s.Az + s.Bz) * 0.5f;
            if (mx < 0 || mx >= size || mz < 0 || mz >= size) continue;
            AddRoadSegment(geo, s, lod);
        }
        var list = ctx.Buildings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var b = r.B;
            if (b.Ghost || b.Height > TallBuilding) continue;
            float cx = r.OffX + b.Cx, cz = r.OffZ + b.Cz;
            if (cx < 0 || cx >= size || cz < 0 || cz >= size) continue;
            float gMin = float.MaxValue, gMax = float.MinValue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0; k < rx.Length; k++)
            {
                float h = ctx.Height(r.OffX + rx[k], r.OffZ + rz[k], 0f, out _);
                gMin = MathF.Min(gMin, h); gMax = MathF.Max(gMax, h);
            }
            AddBuilding(geo.Buildings, geo.BuildingFaces, b, r.OffX, r.OffZ, gMin, gMax,
                Hash.Of((int)(ctx.Ox / 96.0), (int)(ctx.Oz / 96.0), b.Index, 91));
        }
    }

    // ---- roads ---------------------------------------------------------------------------

    private static Vector2 Dir(MapRoad r, int k)
    {
        var d = new Vector2(r.X[k + 1] - r.X[k], r.Z[k + 1] - r.Z[k]);
        float len = d.Length();
        return len > 1e-5f ? d / len : new Vector2(0, 1);
    }

    /// <summary>Sideways (right of travel) offset direction at a road point, lengthened on bends so the edges stay parallel.</summary>
    private static Vector2 Miter(MapRoad r, int point)
    {
        int last = r.X.Length - 2;
        if (point <= 0) { var d = Dir(r, 0); return new Vector2(-d.Y, d.X); }
        if (point > last) { var d = Dir(r, last); return new Vector2(-d.Y, d.X); }
        var a = Dir(r, point - 1); var b = Dir(r, point);
        var sa = new Vector2(-a.Y, a.X); var sb = new Vector2(-b.Y, b.X);
        var m = sa + sb;
        float len = m.Length();
        if (len < 1e-4f) return sb;
        m /= len;
        return m / MathF.Max(0.45f, m.Dot(sb));
    }

    private static void AddRoadSegment(MapChunkGeometry geo, in MapSeg s, int lod)
    {
        var road = s.Road;
        int k = s.Index;
        var mesh = geo.Roads;
        bool bridge = road.IsBridge;
        bool loose = road.Unpaved || road.IsRail;
        float surf = road.IsRail ? SurfRail : road.Unpaved ? SurfLoose : SurfAsphalt;
        var col = new Color(surf, Marking(road), road.HalfWidth / 16f, 1f);
        float bias = ClassBias(road.Cls) + (lod > 0 ? 0.06f * lod : 0f);
        float hw = road.HalfWidth;
        float outer = bridge ? hw + 0.35f : hw + ShoulderWidth;
        float drop = bridge ? 0f : ShoulderDrop;
        var collision = loose ? geo.LooseFaces : geo.PavedFaces;

        var p0 = new Vector2(s.Ax, s.Az); var p1 = new Vector2(s.Bx, s.Bz);
        var m0 = Miter(road, k); var m1 = Miter(road, k + 1);
        float y0 = s.Ya + bias, y1 = s.Yb + bias;
        var d = Dir(road, k);
        float grade = (y1 - y0) / MathF.Max(0.01f, (p1 - p0).Length());
        var up = new Vector3(-grade * d.X, 1f, -grade * d.Y).Normalized();

        Span<float> off = stackalloc float[] { -outer, -hw, hw, outer };
        Span<float> dy = stackalloc float[] { -drop, 0f, 0f, -drop };
        int first = mesh.V.Count;
        for (int e = 0; e < 2; e++)
        {
            var p = e == 0 ? p0 : p1; var m = e == 0 ? m0 : m1;
            float y = e == 0 ? y0 : y1, along = road.Along[k + e];
            for (int i = 0; i < 4; i++)
            {
                var n = i == 0 ? (up - new Vector3(m.X, 0, m.Y) * 0.28f).Normalized()
                      : i == 3 ? (up + new Vector3(m.X, 0, m.Y) * 0.28f).Normalized() : up;
                mesh.Add(new Vector3(p.X + m.X * off[i], y + dy[i], p.Y + m.Y * off[i]), bridge ? up : n,
                    new Vector2(off[i], along), Vector2.Zero, col);
            }
        }
        for (int i = 0; i < 3; i++)
            mesh.Quad(first + i, first + i + 1, first + 4 + i + 1, first + 4 + i, Vector3.Up, i == 1 || bridge ? collision : null);

        if (!road.IsRail)
        {
            if (k == 0 && (road.Flags & RoadFlag.StartCap) != 0) AddCap(mesh, p0, -d, y0, hw, road.Along[0], col, collision);
            if (k == road.X.Length - 2 && (road.Flags & RoadFlag.EndCap) != 0) AddCap(mesh, p1, d, y1, hw, road.Along[k + 1], col, collision);
        }
        if (bridge) AddBridgeSides(mesh, geo.PavedFaces, p0, p1, m0, m1, y0, y1, hw);
    }

    /// <summary>Rounds off the end of a road so junctions and dead ends have no notches.</summary>
    private static void AddCap(MeshData mesh, Vector2 p, Vector2 outward, float y, float hw, float along, Color col, List<Vector3>? collision)
    {
        var side = new Vector2(-outward.Y, outward.X);
        int centre = mesh.Add(new Vector3(p.X, y, p.Y), Vector3.Up, new Vector2(0, along), Vector2.Zero, col);
        const int steps = 6;
        int prev = -1;
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Pi * i / steps;
            float c = MathF.Cos(a), sn = MathF.Sin(a);
            var q = p + side * (c * hw) + outward * (sn * hw);
            int v = mesh.Add(new Vector3(q.X, y, q.Y), Vector3.Up, new Vector2(c * hw * 0.98f, along + sn * hw), Vector2.Zero, col);
            if (prev >= 0) mesh.Tri(centre, prev, v, Vector3.Up, collision);
            prev = v;
        }
    }

    private static void AddBridgeSides(MeshData mesh, List<Vector3>? collision, Vector2 p0, Vector2 p1, Vector2 m0, Vector2 m1, float y0, float y1, float hw)
    {
        var col = new Color(SurfConcrete, 0f, hw / 16f, 1f);
        const float thick = 0.9f, rail = 1.05f;
        float edge = hw + 0.35f;
        for (int side = -1; side <= 1; side += 2)
        {
            var a = p0 + m0 * (edge * side); var b = p1 + m1 * (edge * side);
            var outN = new Vector3(m0.X * side, 0, m0.Y * side).Normalized();
            // fascia below the deck edge
            int v = mesh.V.Count;
            mesh.Add(new Vector3(a.X, y0, a.Y), outN, new Vector2(0, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y1, b.Y), outN, new Vector2(1, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y1 - thick, b.Y), outN, new Vector2(1, 1), Vector2.Zero, col);
            mesh.Add(new Vector3(a.X, y0 - thick, a.Y), outN, new Vector2(0, 1), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, outN);
            // parapet, visible and solid from both sides
            var ra = p0 + m0 * ((edge - 0.08f) * side); var rb = p1 + m1 * ((edge - 0.08f) * side);
            foreach (var n in new[] { outN, -outN })
            {
                v = mesh.V.Count;
                mesh.Add(new Vector3(ra.X, y0, ra.Y), n, new Vector2(0, 0), Vector2.Zero, col);
                mesh.Add(new Vector3(rb.X, y1, rb.Y), n, new Vector2(1, 0), Vector2.Zero, col);
                mesh.Add(new Vector3(rb.X, y1 + rail, rb.Y), n, new Vector2(1, 1), Vector2.Zero, col);
                mesh.Add(new Vector3(ra.X, y0 + rail, ra.Y), n, new Vector2(0, 1), Vector2.Zero, col);
                mesh.Quad(v, v + 1, v + 2, v + 3, n, collision);
            }
        }
        // underside
        {
            var a = p0 - m0 * edge; var b = p0 + m0 * edge; var c = p1 + m1 * edge; var d = p1 - m1 * edge;
            int v = mesh.V.Count;
            mesh.Add(new Vector3(a.X, y0 - thick, a.Y), Vector3.Down, new Vector2(0, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y0 - thick, b.Y), Vector3.Down, new Vector2(1, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(c.X, y1 - thick, c.Y), Vector3.Down, new Vector2(1, 1), Vector2.Zero, col);
            mesh.Add(new Vector3(d.X, y1 - thick, d.Y), Vector3.Down, new Vector2(0, 1), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, Vector3.Down);
        }
    }

    // ---- buildings -----------------------------------------------------------------------

    // Vertex colour alpha selects the surface in shaders/building.gdshader.
    private const float KindWindows = 1f, KindPlain = 0.66f, KindTiles = 0.33f, KindFlatRoof = 0f;

    private static readonly Color[] HouseWalls =
    {
        new(0.62f, 0.58f, 0.50f), new(0.66f, 0.60f, 0.44f), new(0.55f, 0.53f, 0.50f), new(0.60f, 0.50f, 0.40f),
        new(0.50f, 0.52f, 0.48f), new(0.68f, 0.66f, 0.60f), new(0.47f, 0.33f, 0.26f),
    };
    private static readonly Color[] BlockWalls =
    {
        new(0.46f, 0.45f, 0.43f), new(0.52f, 0.50f, 0.46f), new(0.40f, 0.41f, 0.42f), new(0.55f, 0.52f, 0.44f), new(0.44f, 0.47f, 0.46f),
    };
    private static readonly Color[] PitchedRoofs =
    {
        new(0.30f, 0.13f, 0.09f), new(0.24f, 0.11f, 0.08f), new(0.12f, 0.12f, 0.13f), new(0.20f, 0.17f, 0.15f), new(0.16f, 0.20f, 0.17f),
    };

    private static void Palette(byte type, uint seed, out Color wall, out Color roof, out float wallKind)
    {
        var rng = new Rng(seed);
        float shade = rng.Range(0.86f, 1.08f);
        switch (type)
        {
            case BuildingKind.House:
                wall = rng.Pick(HouseWalls); roof = rng.Pick(PitchedRoofs); wallKind = KindWindows; break;
            case BuildingKind.Apartments:
                wall = rng.Pick(BlockWalls); roof = new Color(0.10f, 0.10f, 0.11f); wallKind = KindWindows; break;
            case BuildingKind.Commercial:
                wall = rng.Chance(0.5f) ? new Color(0.58f, 0.57f, 0.54f) : new Color(0.42f, 0.44f, 0.47f);
                roof = new Color(0.14f, 0.14f, 0.15f); wallKind = KindWindows; break;
            case BuildingKind.Civic:
                wall = rng.Chance(0.5f) ? new Color(0.62f, 0.57f, 0.47f) : new Color(0.50f, 0.36f, 0.29f);
                roof = new Color(0.17f, 0.13f, 0.12f); wallKind = KindWindows; break;
            case BuildingKind.Industrial:
                wall = rng.Chance(0.5f) ? new Color(0.40f, 0.43f, 0.45f) : new Color(0.50f, 0.49f, 0.46f);
                roof = new Color(0.22f, 0.23f, 0.24f); wallKind = KindPlain; break;
            case BuildingKind.Farm:
                wall = rng.Chance(0.5f) ? new Color(0.30f, 0.21f, 0.13f) : new Color(0.44f, 0.30f, 0.24f);
                roof = new Color(0.21f, 0.19f, 0.18f); wallKind = KindPlain; break;
            case BuildingKind.Shed:
                wall = rng.Chance(0.5f) ? new Color(0.36f, 0.35f, 0.33f) : new Color(0.30f, 0.24f, 0.18f);
                roof = new Color(0.17f, 0.16f, 0.16f); wallKind = KindPlain; break;
            case BuildingKind.Church:
                wall = rng.Chance(0.5f) ? new Color(0.50f, 0.27f, 0.20f) : new Color(0.66f, 0.63f, 0.56f);
                roof = new Color(0.16f, 0.22f, 0.20f); wallKind = KindPlain; break;
            case BuildingKind.Tower:
                wall = new Color(0.45f, 0.44f, 0.42f); roof = new Color(0.2f, 0.2f, 0.2f); wallKind = KindPlain; break;
            default:
                wall = rng.Pick(BlockWalls); roof = rng.Chance(0.4f) ? rng.Pick(PitchedRoofs) : new Color(0.13f, 0.13f, 0.14f);
                wallKind = KindWindows; break;
        }
        wall = new Color(wall.R * shade, wall.G * shade, wall.B * shade);
    }

    /// <summary>
    /// Extrudes one footprint. <paramref name="gMin"/>/<paramref name="gMax"/> are the lowest and highest
    /// ground under the outline: walls start below the low point and the floor sits at the high one.
    /// </summary>
    public static void AddBuilding(MeshData mesh, List<Vector3>? collision, MapBuilding b, float offX, float offZ, float gMin, float gMax, uint seed)
    {
        Palette(b.Type, seed, out var wall, out var roofCol, out float wallKind);
        if (b.Height < 3.4f || b.IsPart && b.Height - b.MinHeight < 3f) wallKind = KindPlain;
        float floor = gMax;
        float bottom = b.MinHeight > 0.05f ? floor + b.MinHeight : gMin - 0.6f;
        float top = floor + b.Height;
        bool pitched = b.Roof >= RoofKind.Gabled && b.RoofHeight > 0.2f;
        float eave = pitched ? top - b.RoofHeight : top;
        if (eave < bottom) eave = bottom;
        var wallC = new Color(wall.R, wall.G, wall.B, wallKind);

        // walls
        if (eave - bottom > 0.05f)
        {
            float wallH = eave - floor;
            for (int q = 0; q < b.RX.Length; q++)
            {
                var rx = b.RX[q]; var rz = b.RZ[q];
                float run = 0f;
                for (int i = 0; i < rx.Length; i++)
                {
                    int j = (i + 1) % rx.Length;
                    float ax = offX + rx[i], az = offZ + rz[i], bx = offX + rx[j], bz = offZ + rz[j];
                    float dx = bx - ax, dz = bz - az;
                    float len = MathF.Sqrt(dx * dx + dz * dz);
                    if (len < 0.02f) continue;
                    var n = new Vector3(dz / len, 0, -dx / len);
                    var meta = new Vector2(len, wallH);
                    int v = mesh.V.Count;
                    mesh.Add(new Vector3(ax, bottom, az), n, new Vector2(0, bottom - floor), meta, wallC);
                    mesh.Add(new Vector3(bx, bottom, bz), n, new Vector2(len, bottom - floor), meta, wallC);
                    mesh.Add(new Vector3(bx, eave, bz), n, new Vector2(len, eave - floor), meta, wallC);
                    mesh.Add(new Vector3(ax, eave, az), n, new Vector2(0, eave - floor), meta, wallC);
                    mesh.Quad(v, v + 1, v + 2, v + 3, n, collision);
                    run += len;
                }
            }
        }

        var ox = b.RX[0]; var oz = b.RZ[0];
        if (pitched && (b.Roof == RoofKind.Pyramidal || b.Roof == RoofKind.Dome))
        {
            var apex = new Vector3(offX + b.Cx, top, offZ + b.Cz);
            var tiles = new Color(roofCol.R, roofCol.G, roofCol.B, KindTiles);
            bool dome = b.Roof == RoofKind.Dome;
            for (int i = 0; i < ox.Length; i++)
            {
                int j = (i + 1) % ox.Length;
                var a = new Vector3(offX + ox[i], eave, offZ + oz[i]);
                var c = new Vector3(offX + ox[j], eave, offZ + oz[j]);
                if (dome)
                {
                    // one intermediate ring gives a rounded profile
                    var a2 = a.Lerp(apex, 0.42f) with { Y = eave + b.RoofHeight * 0.72f };
                    var c2 = c.Lerp(apex, 0.42f) with { Y = eave + b.RoofHeight * 0.72f };
                    RoofQuad(mesh, collision, a, c, c2, a2, tiles);
                    RoofTri(mesh, collision, a2, c2, apex, tiles);
                }
                else RoofTri(mesh, collision, a, c, apex, tiles);
            }
            return;
        }
        if (pitched && b.RX.Length == 1)
        {
            AddPitchedRoof(mesh, collision, b, offX, offZ, eave, top, roofCol, wallC);
            return;
        }

        // flat roof
        var flat = new Color(roofCol.R * 0.8f, roofCol.G * 0.8f, roofCol.B * 0.8f, KindFlatRoof);
        int baseV = mesh.V.Count;
        for (int q = 0; q < b.RX.Length; q++)
            for (int i = 0; i < b.RX[q].Length; i++)
                mesh.Add(new Vector3(offX + b.RX[q][i], top, offZ + b.RZ[q][i]), Vector3.Up, new Vector2(b.RX[q][i], b.RZ[q][i]), Vector2.Zero, flat);
        if (b.Tris != null)
        {
            for (int i = 0; i + 2 < b.Tris.Length; i += 3)
                mesh.Tri(baseV + b.Tris[i], baseV + b.Tris[i + 1], baseV + b.Tris[i + 2], Vector3.Up, collision);
        }
        else
        {
            var tris = _earScratch ??= new List<int>();
            tris.Clear();
            Triangulate(ox, oz, tris);
            for (int i = 0; i + 2 < tris.Count; i += 3)
                mesh.Tri(baseV + tris[i], baseV + tris[i + 1], baseV + tris[i + 2], Vector3.Up, collision);
        }
    }

    [ThreadStatic] private static List<int>? _earScratch;
    [ThreadStatic] private static List<int>? _earRemaining;

    private static void RoofTri(MeshData mesh, List<Vector3>? collision, Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        var n = (b - a).Cross(c - a);
        if (n.LengthSquared() < 1e-8f) return;
        n = n.Normalized();
        if (n.Y < 0) n = -n;
        int v = mesh.V.Count;
        mesh.Add(a, n, new Vector2(a.X, a.Y), Vector2.Zero, col);
        mesh.Add(b, n, new Vector2(b.X, b.Y), Vector2.Zero, col);
        mesh.Add(c, n, new Vector2(c.X, c.Y), Vector2.Zero, col);
        mesh.Tri(v, v + 1, v + 2, n, collision);
    }

    private static void RoofQuad(MeshData mesh, List<Vector3>? collision, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        RoofTri(mesh, collision, a, b, c, col);
        RoofTri(mesh, collision, a, c, d, col);
    }

    /// <summary>Gabled or hipped roof over the footprint's tightest bounding rectangle.</summary>
    private static void AddPitchedRoof(MeshData mesh, List<Vector3>? collision, MapBuilding b, float offX, float offZ, float eave, float top, Color roofCol, Color wallC)
    {
        var rx = b.RX[0]; var rz = b.RZ[0];
        int n = rx.Length;
        // tightest rectangle aligned with one of the edges
        float bestArea = float.MaxValue;
        Vector2 axis = new(1, 0); float minU = 0, maxU = 0, minV = 0, maxV = 0;
        int tries = n <= 24 ? n : 1;
        int longest = 0; float longLen = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float l = (rx[j] - rx[i]) * (rx[j] - rx[i]) + (rz[j] - rz[i]) * (rz[j] - rz[i]);
            if (l > longLen) { longLen = l; longest = i; }
        }
        for (int t = 0; t < tries; t++)
        {
            int i = tries == 1 ? longest : t, j = (i + 1) % n;
            var d = new Vector2(rx[j] - rx[i], rz[j] - rz[i]);
            if (d.LengthSquared() < 1e-4f) continue;
            d = d.Normalized();
            var p = new Vector2(-d.Y, d.X);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int k = 0; k < n; k++)
            {
                float u = rx[k] * d.X + rz[k] * d.Y, v = rx[k] * p.X + rz[k] * p.Y;
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); v0 = MathF.Min(v0, v); v1 = MathF.Max(v1, v);
            }
            float area = (u1 - u0) * (v1 - v0);
            if (area < bestArea) { bestArea = area; axis = d; minU = u0; maxU = u1; minV = v0; maxV = v1; }
        }
        // make U the long axis
        var perp = new Vector2(-axis.Y, axis.X);
        if (maxV - minV > maxU - minU)
        {
            (axis, perp) = (perp, -axis);
            (minU, maxU, minV, maxV) = (minV, maxV, -maxU, -minU);
        }
        float halfL = (maxU - minU) * 0.5f, halfW = (maxV - minV) * 0.5f;
        var centre = axis * ((minU + maxU) * 0.5f) + perp * ((minV + maxV) * 0.5f);
        const float overhang = 0.3f;
        Vector3 P(float u, float v, float y) => new(offX + centre.X + axis.X * u + perp.X * v, y, offZ + centre.Y + axis.Y * u + perp.Y * v);
        var tiles = new Color(roofCol.R, roofCol.G, roofCol.B, KindTiles);
        float L = halfL + overhang, W = halfW + overhang;
        float ridge = b.Roof == RoofKind.Hipped ? MathF.Max(0f, halfL - halfW) : L;
        var r0 = P(-ridge, 0, top); var r1 = P(ridge, 0, top);
        var c00 = P(-L, -W, eave); var c01 = P(-L, W, eave); var c10 = P(L, -W, eave); var c11 = P(L, W, eave);
        RoofQuad(mesh, collision, c00, c10, r1, r0, tiles);
        RoofQuad(mesh, collision, c11, c01, r0, r1, tiles);
        if (b.Roof == RoofKind.Hipped)
        {
            RoofTri(mesh, collision, c01, c00, r0, tiles);
            RoofTri(mesh, collision, c10, c11, r1, tiles);
        }
        else
        {
            // gable ends, in wall colour, set back to the wall line
            var plain = new Color(wallC.R, wallC.G, wallC.B, KindPlain);
            foreach (float sgn in new[] { -1f, 1f })
            {
                var a = P(sgn * halfL, -halfW, eave); var c = P(sgn * halfL, halfW, eave); var t = P(sgn * halfL, 0, top - overhang * (top - eave) / MathF.Max(0.1f, W));
                var nrm = new Vector3(axis.X * sgn, 0, axis.Y * sgn);
                int v = mesh.V.Count;
                mesh.Add(a, nrm, new Vector2(0, 0), Vector2.Zero, plain);
                mesh.Add(c, nrm, new Vector2(1, 0), Vector2.Zero, plain);
                mesh.Add(t, nrm, new Vector2(0.5f, 1), Vector2.Zero, plain);
                mesh.Tri(v, v + 1, v + 2, nrm, collision);
            }
        }
    }

    /// <summary>Ear clipping for a simple polygon with positive shoelace area. Appends vertex indices.</summary>
    public static void Triangulate(float[] x, float[] z, List<int> tris)
    {
        int n = x.Length;
        if (n < 3) return;
        var rem = _earRemaining ??= new List<int>();
        rem.Clear();
        for (int i = 0; i < n; i++) rem.Add(i);
        int guard = n * n + 16;
        int at = 0;
        while (rem.Count > 3 && guard-- > 0)
        {
            int m = rem.Count;
            int ia = rem[(at + m - 1) % m], ib = rem[at % m], ic = rem[(at + 1) % m];
            float cross = (x[ib] - x[ia]) * (z[ic] - z[ia]) - (z[ib] - z[ia]) * (x[ic] - x[ia]);
            bool ear = cross > 1e-7f;
            if (ear)
            {
                for (int k = 0; k < m; k++)
                {
                    int ip = rem[k];
                    if (ip == ia || ip == ib || ip == ic) continue;
                    if (InTriangle(x[ip], z[ip], x[ia], z[ia], x[ib], z[ib], x[ic], z[ic])) { ear = false; break; }
                }
            }
            if (ear)
            {
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                rem.RemoveAt(at % m);
                if (at >= rem.Count) at = 0;
            }
            else at = (at + 1) % m;
        }
        if (rem.Count == 3) { tris.Add(rem[0]); tris.Add(rem[1]); tris.Add(rem[2]); }
        else
        {
            // degenerate outline: fall back to a fan so the roof is at least closed
            for (int i = 1; i + 1 < rem.Count; i++) { tris.Add(rem[0]); tris.Add(rem[i]); tris.Add(rem[i + 1]); }
        }
    }

    private static bool InTriangle(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
    {
        float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
