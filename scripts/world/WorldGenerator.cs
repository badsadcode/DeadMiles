using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// Pure, thread-safe description of the world for a given seed: terrain height, biomes,
/// the road and the points of interest. Nothing here touches the scene tree, so chunk
/// workers can call it freely. Everything is a function of (seed, position) which is what
/// lets the save file store only deltas.
/// </summary>
public sealed class WorldGenerator
{
    public const float WaterLevel = -9f;

    public readonly int Seed;
    public readonly RoadNetwork Road;
    public readonly PoiPlacer Pois;
    /// <summary>Set when the world is a real map rather than generated terrain; every query below defers to it.</summary>
    public readonly MapWorld? Map;

    private readonly Noise2 _macro, _hills, _biome, _variety;

    public WorldGenerator(int seed, MapWorld? map = null)
    {
        Seed = seed;
        Map = map;
        _macro = new Noise2(seed);
        _hills = new Noise2(seed + 101);
        _biome = new Noise2(seed + 202);
        _variety = new Noise2(seed + 303);
        Road = new RoadNetwork(this, seed);
        Pois = new PoiPlacer(this, seed);
        Log.Info("WorldGenerator", map == null ? $"World ready for seed {seed}" : $"World ready for map '{map.Data.Name}'");
    }

    /// <summary>Large scale landform only. The road follows a smoothed version of this.</summary>
    public float MacroHeight(float x, float z) => _macro.Fbm(x / 1000f, z / 1000f, 3) * 46f;

    /// <summary>Natural terrain before roads and building pads are cut into it.</summary>
    public float BaseHeight(float x, float z)
    {
        float h = MacroHeight(x, z) + _hills.Fbm(x / 170f, z / 170f, 3) * 9f;
        // Lakes: keep the bed shallow so they can be waded and never swallow the car.
        float shore = WaterLevel + 0.5f;
        if (h < shore)
        {
            float t = shore - h;
            h = shore - 1.8f * (1f - MathF.Exp(-t / 1.8f));
        }
        return h;
    }

    /// <summary>forest: 0..1 tree cover, field: 0..1 cultivated land.</summary>
    public void Biome(float x, float z, out float forest, out float field)
    {
        if (Map != null) { Map.Biome(x, z, out forest, out field); return; }
        float b = _biome.Fbm(x / 430f, z / 430f, 2);
        forest = MathX.Smooth(-0.04f, 0.2f, b);
        field = MathX.Smooth(0.26f, 0.38f, -b);
    }

    /// <summary>-1..1 slow variation used to pick tree species and tint.</summary>
    public float Variety(float x, float z) => _variety.Fbm(x / 260f, z / 260f, 2);

    public float Height(float x, float z)
    {
        if (Map != null) return Map.Height(x, z);
        var rs = Road.Sample(z);
        return Compose(x, z, rs, Pois.Near(z), 0f, out _);
    }

    /// <summary>
    /// Final ground height. Order matters: tracks, then building pads, then the road core, so
    /// the road always wins where they overlap and pads stay flat under buildings.
    /// </summary>
    /// <param name="depress">How far to sink the ground under the asphalt ribbon (visual meshes only).</param>
    /// <param name="dirt">0..1 bare-earth/gravel mask for the terrain shader.</param>
    public float Compose(float x, float z, in RoadSample rs, PoiDesc[] pois, float depress, out float dirt)
    {
        if (Map != null) return Map.Height(x, z, depress, out dirt);
        float h = BaseHeight(x, z);
        float d = MathF.Abs(x - rs.X) * rs.InvLen;
        dirt = 0f;

        if (d < 60f)
            h = Mathf.Lerp(rs.H, h, MathX.Smooth(7f, 58f, d));

        for (int i = 0; i < pois.Length; i++)
        {
            var p = pois[i];
            float px = x - p.Center.X, pz = z - p.Center.Z;
            if (p.HasTrack)
            {
                // distance to the segment TrackA -> TrackB
                float ax = x - p.TrackA.X, az = z - p.TrackA.Y;
                float bx = p.TrackB.X - p.TrackA.X, bz = p.TrackB.Y - p.TrackA.Y;
                float u = Mathf.Clamp((ax * bx + az * bz) / (bx * bx + bz * bz), 0f, 1f);
                float ex = ax - bx * u, ez = az - bz * u;
                float dd = MathF.Sqrt(ex * ex + ez * ez);
                if (dd < 13f)
                {
                    float us = u * u * (3f - 2f * u);
                    float th = Mathf.Lerp(p.TrackAH, p.Center.Y, us);
                    h = Mathf.Lerp(th, h, MathX.Smooth(2.6f, 13f, dd));
                    dirt = MathF.Max(dirt, 1f - MathX.Smooth(1.5f, 3.3f, dd));
                }
            }
            if (p.Radius > 0f)
            {
                float dist = MathF.Sqrt(px * px + pz * pz);
                if (dist < p.Radius + 24f)
                {
                    h = Mathf.Lerp(p.Center.Y, h, MathX.Smooth(p.Radius, p.Radius + 24f, dist));
                    if (p.Gravel)
                        dirt = MathF.Max(dirt, 1f - MathX.Smooth(p.Radius - 4f, p.Radius + 1f, dist));
                }
            }
        }

        if (d < 10f)
        {
            h = Mathf.Lerp(rs.H, h, MathX.Smooth(5.2f, 9.5f, d));
            dirt = MathF.Max(dirt, 1f - MathX.Smooth(4.5f, 7.5f, d));
            if (depress > 0f)
                h -= depress * (1f - MathX.Smooth(3.9f, 5.0f, d));
        }
        return h;
    }

    public Vector3 Normal(float x, float z)
    {
        const float e = 1.0f;
        float hl = Height(x - e, z), hr = Height(x + e, z);
        float hd = Height(x, z - e), hu = Height(x, z + e);
        return new Vector3(hl - hr, 2f * e, hd - hu).Normalized();
    }

    public Vector3 Ground(float x, float z, float lift = 0f) => new Vector3(x, Height(x, z) + lift, z);

    /// <summary>True when nothing man-made (road, track, pad) is within <paramref name="margin"/> metres.</summary>
    public bool IsWild(float x, float z, in RoadSample rs, PoiDesc[] pois, float margin)
    {
        if (Map != null) return Map.IsWild(x, z, margin);
        if (MathF.Abs(x - rs.X) * rs.InvLen < RoadNetwork.ShoulderHalfWidth + margin) return false;
        for (int i = 0; i < pois.Length; i++)
        {
            var p = pois[i];
            float px = x - p.Center.X, pz = z - p.Center.Z;
            float r = p.Radius + margin + 1f;
            if (px * px + pz * pz < r * r) return false;
            if (p.HasTrack)
            {
                float ax = x - p.TrackA.X, az = z - p.TrackA.Y;
                float bx = p.TrackB.X - p.TrackA.X, bz = p.TrackB.Y - p.TrackA.Y;
                float u = Mathf.Clamp((ax * bx + az * bz) / (bx * bx + bz * bz), 0f, 1f);
                float ex = ax - bx * u, ez = az - bz * u;
                float tm = 3.2f + margin;
                if (ex * ex + ez * ez < tm * tm) return false;
            }
        }
        return true;
    }
}
