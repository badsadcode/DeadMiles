using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>Everything a worker thread computed for one terrain chunk. Applied on the main thread.</summary>
public sealed class ChunkBuild
{
    public Vector2I Coord;
    public int Lod;
    public Vector3[] Verts = null!;
    public Vector3[] Normals = null!;
    public Color[] Colors = null!;
    public int[] Indices = null!;
    public Vector3[]? CollisionFaces;
    public bool HasWater;
    public float[]?[] VegBuffers = new float[VegetationLibrary.Count][];
    public int[] VegCounts = new int[VegetationLibrary.Count];
    public float[]? ImpostorBuffer;
    public int ImpostorCount;
    public List<Vector4>? Trunks; // xyz local position, w radius
    /// <summary>Roads and buildings of the real-map world; null in the procedural world.</summary>
    public MapChunkGeometry? Map;
}

/// <summary>Worker-side terrain and vegetation generation. No scene tree access in here.</summary>
public static class ChunkBuilder
{
    public const float ChunkSize = 96f;
    public const int Cells = 48;           // finest grid: 2 m
    public const float TreeCell = 6.5f;

    public static ChunkBuild Build(WorldGenerator world, Transform3D[] vegLocals, Vector2I coord, int lod, float density)
    {
        int step = 1 << lod;
        int n = Cells / step;
        float s = ChunkSize / n;
        float x0 = coord.X * ChunkSize, z0 = coord.Y * ChunkSize;
        float depress = 0.3f + 0.15f * (s - 2f);

        // Real-map world: gather the roads and buildings around this chunk once, in chunk-local
        // coordinates, and let that context answer every height / biome / occupancy question.
        MapContext? ctx = null;
        if (world.Map != null)
        {
            ctx = _mapContext;
            if (ctx == null || ctx.World != world.Map) _mapContext = ctx = new MapContext(world.Map);
            ctx.Reset(coord.X * (double)ChunkSize, coord.Y * (double)ChunkSize, -s, -s, ChunkSize + s, ChunkSize + s);
        }

        // Heights with a one-cell border so normals match across chunk seams.
        int gw = n + 3;
        var hgt = new float[gw * gw];
        var dirt = new float[gw * gw];
        float minH = float.MaxValue;
        for (int j = 0; j < gw; j++)
        {
            float z = z0 + (j - 1) * s;
            var rs = ctx == null ? world.Road.Sample(z) : default;
            var pois = world.Pois.Near(z);
            for (int i = 0; i < gw; i++)
            {
                float x = x0 + (i - 1) * s;
                float d;
                float h = ctx != null ? ctx.Height((i - 1) * s, (j - 1) * s, depress, out d) : world.Compose(x, z, rs, pois, depress, out d);
                hgt[j * gw + i] = h;
                dirt[j * gw + i] = d;
                if (h < minH) minH = h;
            }
        }

        int vw = n + 1;
        int mainVerts = vw * vw;
        int skirtVerts = vw * 4;
        var verts = new Vector3[mainVerts + skirtVerts];
        var normals = new Vector3[mainVerts + skirtVerts];
        var colors = new Color[mainVerts + skirtVerts];
        for (int j = 0; j < vw; j++)
        {
            for (int i = 0; i < vw; i++)
            {
                int g = (j + 1) * gw + (i + 1);
                float h = hgt[g];
                int v = j * vw + i;
                verts[v] = new Vector3(i * s, h, j * s);
                normals[v] = new Vector3(hgt[g - 1] - hgt[g + 1], 2f * s, hgt[g - gw] - hgt[g + gw]).Normalized();
                float forest, field;
                if (ctx != null) ctx.Biome(i * s, j * s, out forest, out field);
                else world.Biome(x0 + i * s, z0 + j * s, out forest, out field);
                colors[v] = new Color(forest, field, dirt[g], 1f);
            }
        }

        var idx = new List<int>(n * n * 6 + n * 4 * 12);
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v00 = j * vw + i, v10 = v00 + 1, v01 = v00 + vw, v11 = v01 + 1;
                idx.Add(v00); idx.Add(v10); idx.Add(v01);
                idx.Add(v10); idx.Add(v11); idx.Add(v01);
            }
        }

        // Skirts hide the cracks between neighbouring chunks of different detail.
        float skirt = 1.5f + s;
        int sv = mainVerts;
        for (int e = 0; e < 4; e++)
        {
            int first = sv;
            for (int k = 0; k < vw; k++)
            {
                int v = e switch { 0 => k, 1 => (vw - 1) * vw + k, 2 => k * vw, _ => k * vw + vw - 1 };
                verts[sv] = verts[v] - new Vector3(0, skirt, 0);
                normals[sv] = normals[v];
                colors[sv] = colors[v];
                sv++;
            }
            for (int k = 0; k < n; k++)
            {
                int a = e switch { 0 => k, 1 => (vw - 1) * vw + k, 2 => k * vw, _ => k * vw + vw - 1 };
                int b = e switch { 0 => k + 1, 1 => (vw - 1) * vw + k + 1, 2 => (k + 1) * vw, _ => (k + 1) * vw + vw - 1 };
                int c = first + k, d = first + k + 1;
                idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(b); idx.Add(d); idx.Add(c);
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        }

        var build = new ChunkBuild
        {
            Coord = coord, Lod = lod, Verts = verts, Normals = normals, Colors = colors,
            Indices = idx.ToArray(), HasWater = minH < WorldGenerator.WaterLevel + 0.05f,
        };

        if (lod == 0)
        {
            var faces = new Vector3[n * n * 6];
            int f = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int v00 = j * vw + i, v10 = v00 + 1, v01 = v00 + vw, v11 = v01 + 1;
                    faces[f++] = verts[v00]; faces[f++] = verts[v10]; faces[f++] = verts[v01];
                    faces[f++] = verts[v10]; faces[f++] = verts[v11]; faces[f++] = verts[v01];
                }
            }
            build.CollisionFaces = faces;
            build.Trunks = new List<Vector4>();
        }

        PlaceVegetation(world, ctx, vegLocals, build, x0, z0, lod, density);
        if (ctx != null)
        {
            build.Map = new MapChunkGeometry();
            MapMesher.Build(ctx, ChunkSize, lod, build.Map, lod == 0);
        }
        return build;
    }

    [ThreadStatic] private static MapContext? _mapContext;

    private static void PlaceVegetation(WorldGenerator world, MapContext? ctx, Transform3D[] locals, ChunkBuild build, float x0, float z0, int lod, float density)
    {
        var lists = new List<float>?[VegetationLibrary.Count];
        List<float>? impostor = null;
        int gx0 = Mathf.FloorToInt(x0 / TreeCell), gx1 = Mathf.FloorToInt((x0 + ChunkSize) / TreeCell);
        int gz0 = Mathf.FloorToInt(z0 / TreeCell), gz1 = Mathf.FloorToInt((z0 + ChunkSize) / TreeCell);

        for (int gz = gz0; gz <= gz1; gz++)
        {
            for (int gx = gx0; gx <= gx1; gx++)
            {
                var rng = new Rng(Hash.Of(world.Seed, gx, gz, 55));
                float x = (gx + rng.Range(0.1f, 0.9f)) * TreeCell;
                float z = (gz + rng.Range(0.1f, 0.9f)) * TreeCell;
                if (x < x0 || x >= x0 + ChunkSize || z < z0 || z >= z0 + ChunkSize) continue;
                float roll = rng.F();
                float pick = rng.F();
                float sc = rng.F();
                float yaw = rng.Range(0f, Mathf.Tau);
                float tintR = rng.Range(0.82f, 1.08f), tintG = rng.Range(0.85f, 1.1f);

                float forest, field, h, verge;
                if (ctx != null)
                {
                    float lx = x - x0, lz = z - z0;
                    if (!ctx.IsWild(lx, lz, 2.2f)) continue;
                    ctx.Biome(lx, lz, out forest, out field);
                    verge = 0f;
                    float pm = forest * 0.85f + (1f - forest) * (1f - field) * 0.02f;
                    if (roll > pm * density) continue;
                    h = ctx.Height(lx, lz, 0f, out float bare);
                    if (bare > 0.5f) continue;   // paved yards, sand
                }
                else
                {
                    var rs = world.Road.Sample(z);
                    var pois = world.Pois.Near(z);
                    if (!world.IsWild(x, z, rs, pois, 2.2f)) continue;
                    world.Biome(x, z, out forest, out field);
                    float lateral = MathF.Abs(x - rs.X) * rs.InvLen;
                    verge = lateral < 30f ? 0.22f * (1f - field) : 0f;
                    float p = forest * 0.85f + (1f - forest) * (1f - field) * 0.05f + verge;
                    if (roll > p * density) continue;
                    h = world.Compose(x, z, rs, pois, 0f, out _);
                }
                if (h < WorldGenerator.WaterLevel + 0.45f) continue;

                float variety = world.Variety(x, z);
                float conifer = MathX.Smooth(-0.25f, 0.2f, variety) * (0.35f + 0.65f * forest);
                int type;
                float scale;
                if (pick < 0.035f) { type = VegetationLibrary.Dead0 + (pick < 0.018f ? 0 : 1); scale = 1.3f + sc * 0.9f; }
                else if (forest < 0.4f && pick > 0.62f) { type = VegetationLibrary.Bush0 + (pick > 0.8f ? 1 : 0); scale = 0.7f + sc * 0.7f; }
                else if (pick < 0.035f + conifer * 0.93f) { type = sc > 0.5f ? VegetationLibrary.PineA : VegetationLibrary.PineB; scale = 0.75f + sc * 0.75f; }
                else
                {
                    int species = (int)(rng.F() * 3f) % 3;
                    type = (variety > -0.1f ? VegetationLibrary.Maple0 : VegetationLibrary.Birch0) + species;
                    if (rng.Chance(0.25f)) type = (type < VegetationLibrary.Maple0 ? VegetationLibrary.Maple0 : VegetationLibrary.Birch0) + species;
                    scale = 1.55f + sc * 0.85f;
                }

                bool far = lod >= 2;
                if (far && type >= VegetationLibrary.Dead0) continue; // small stuff is not worth drawing out there
                var local = new Vector3(x - x0, h, z - z0);
                var tint = new Color(tintR, tintG, tintR * 0.95f, 1f);
                if (far && type >= VegetationLibrary.Birch0)
                {
                    impostor ??= new List<float>(256);
                    Write(impostor, new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * scale), local), tint);
                    continue;
                }
                var list = lists[type] ??= new List<float>(512);
                var basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * scale);
                Write(list, new Transform3D(basis, local) * locals[type], tint);
                if (build.Trunks != null && type < VegetationLibrary.Bush0)
                    build.Trunks.Add(new Vector4(local.X, local.Y, local.Z, (type <= VegetationLibrary.PineB ? 0.24f : 0.2f) * scale));
            }
        }

        for (int t = 0; t < lists.Length; t++)
        {
            if (lists[t] == null) continue;
            build.VegBuffers[t] = lists[t]!.ToArray();
            build.VegCounts[t] = lists[t]!.Count / 16;
        }
        if (impostor != null)
        {
            build.ImpostorBuffer = impostor.ToArray();
            build.ImpostorCount = impostor.Count / 16;
        }
    }

    /// <summary>Appends one instance in MultiMesh buffer layout (3x4 transform rows + colour).</summary>
    public static void Write(List<float> o, Transform3D t, Color c)
    {
        var b = t.Basis;
        o.Add(b.X.X); o.Add(b.Y.X); o.Add(b.Z.X); o.Add(t.Origin.X);
        o.Add(b.X.Y); o.Add(b.Y.Y); o.Add(b.Z.Y); o.Add(t.Origin.Y);
        o.Add(b.X.Z); o.Add(b.Y.Z); o.Add(b.Z.Z); o.Add(t.Origin.Z);
        o.Add(c.R); o.Add(c.G); o.Add(c.B); o.Add(c.A);
    }
}
