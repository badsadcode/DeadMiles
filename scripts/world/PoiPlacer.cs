using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

public enum PoiKind { Starter, GasStation, Diner, Workshop, Farmhouse, Cabin, Checkpoint, Campsite, Settlement, Wreck }

/// <summary>Where and how a point of interest sits in the world. Built deterministically from the seed.</summary>
public sealed class PoiDesc
{
    public string Id = "";
    public PoiKind Kind;
    public int Segment;
    public int Side;            // +1 = +X side of the road, -1 = the other
    public float AttachZ;       // where it meets the main road
    public Vector3 Center;      // Y is the flattened pad height
    public float Radius;        // flattened pad radius (0 = no pad)
    public float Yaw;           // local -Z looks back at the road
    public bool Gravel;         // pad is bare ground rather than grass
    public bool HasTrack;       // dirt track from the road to the pad
    public Vector2 TrackA, TrackB;
    public float TrackAH;
    public uint Seed;

    public Transform3D Transform => new Transform3D(new Basis(Vector3.Up, Yaw), Center);
    public Vector3 ToWorld(Vector3 local) => Transform * local;
}

/// <summary>
/// Chooses which stretch of road gets which point of interest. The road is cut into fixed
/// segments; each segment is rolled once from the seed, validated (slope, water, access) and cached.
/// That gives guaranteed spacing, a connected access track for every remote site and no overlaps.
/// </summary>
public sealed class PoiPlacer
{
    public const float SegmentLength = 280f;

    private readonly WorldGenerator _world;
    private readonly int _seed;
    private readonly ConcurrentDictionary<int, PoiDesc[]> _segments = new();
    private readonly ConcurrentDictionary<int, PoiDesc[]> _near = new();
    private static readonly PoiDesc[] Empty = Array.Empty<PoiDesc>();

    public PoiPlacer(WorldGenerator world, int seed) { _world = world; _seed = seed; }

    public static int SegmentOf(float z) => Mathf.FloorToInt(z / SegmentLength);

    /// <summary>Every POI that can influence terrain at this Z (own segment and both neighbours).</summary>
    public PoiDesc[] Near(float z)
    {
        int k = SegmentOf(z);
        return _near.GetOrAdd(k, kk =>
        {
            var list = new List<PoiDesc>();
            for (int i = kk - 1; i <= kk + 1; i++) list.AddRange(ForSegment(i));
            return list.Count == 0 ? Empty : list.ToArray();
        });
    }

    public PoiDesc[] ForSegment(int k) => _segments.GetOrAdd(k, Generate);

    private PoiKind Roll(int k, ref Rng rng)
    {
        if (k == 0) return PoiKind.Starter;
        // One guaranteed fuel stop per block of six segments keeps long trips fair.
        int block = Mathf.FloorToInt(k / 6f);
        int slot = (int)(Hash.Of(_seed, block, 771) % 6);
        if (Mathf.PosMod(k, 6) == slot) return PoiKind.GasStation;

        float r = rng.F();
        if (r < 0.26f) return (PoiKind)(-1);
        if (r < 0.40f) return PoiKind.Wreck;
        if (r < 0.50f) return PoiKind.Farmhouse;
        if (r < 0.60f) return PoiKind.Cabin;
        if (r < 0.69f) return PoiKind.Campsite;
        if (r < 0.77f) return PoiKind.Diner;
        if (r < 0.85f) return PoiKind.Workshop;
        if (r < 0.91f) return PoiKind.Checkpoint;
        if (r < 0.96f) return PoiKind.Settlement;
        return PoiKind.GasStation;
    }

    private PoiDesc[] Generate(int k)
    {
        var rng = new Rng(Hash.Of(_seed, k, 9001));
        var kind = Roll(k, ref rng);
        if ((int)kind < 0) return Empty;

        float attachZ = k == 0 ? 0f : (k + rng.Range(0.3f, 0.7f)) * SegmentLength;
        int side = rng.Chance(0.5f) ? 1 : -1;
        var rs = _world.Road.Sample(attachZ);
        var a = new Vector2(rs.X, attachZ);
        var n = new Vector2(1f, -rs.Slope) * rs.InvLen; // unit normal pointing to +X side

        var p = new PoiDesc { Kind = kind, Segment = k, AttachZ = attachZ, Seed = Hash.Of(_seed, k, 4242), Id = $"poi{k}" };

        bool remote = kind is PoiKind.Farmhouse or PoiKind.Cabin or PoiKind.Campsite;
        if (remote)
        {
            float min = kind == PoiKind.Farmhouse ? 105f : kind == PoiKind.Cabin ? 90f : 62f;
            float max = kind == PoiKind.Farmhouse ? 180f : kind == PoiKind.Cabin ? 200f : 130f;
            p.Radius = kind == PoiKind.Farmhouse ? 31f : kind == PoiKind.Cabin ? 13f : 11f;
            p.Gravel = kind != PoiKind.Farmhouse;
            bool placed = false;
            for (int attempt = 0; attempt < 4 && !placed; attempt++)
            {
                int s = attempt % 2 == 0 ? side : -side;
                float dist = rng.Range(min, max);
                var c = a + n * (s * dist);
                float padH = _world.BaseHeight(c.X, c.Y);
                if (padH < WorldGenerator.WaterLevel + 1.3f) continue;
                if (MathF.Abs(padH - rs.H) / dist > 0.15f) continue;
                bool dry = true;
                for (int i = 1; i <= 3 && dry; i++)
                {
                    var m = a.Lerp(c, i / 4f);
                    if (_world.BaseHeight(m.X, m.Y) < WorldGenerator.WaterLevel + 0.6f) dry = false;
                }
                if (!dry) continue;
                p.Side = s;
                p.Center = new Vector3(c.X, padH, c.Y);
                p.HasTrack = true;
                p.TrackA = a + n * (s * 3.5f);
                p.TrackB = c;
                p.TrackAH = rs.H;
                placed = true;
            }
            if (!placed)
            {
                // No buildable ground on either side: fall back to something that always fits.
                kind = PoiKind.Wreck;
                p.Kind = kind;
                p.Radius = 0f;
                p.Gravel = false;
            }
        }

        if (!remote || kind == PoiKind.Wreck)
        {
            float lateral;
            switch (kind)
            {
                case PoiKind.Starter: lateral = 22f; p.Radius = 15f; side = 1; break;
                case PoiKind.GasStation: lateral = 29f; p.Radius = 21f; break;
                case PoiKind.Diner: lateral = 29f; p.Radius = 20f; break;
                case PoiKind.Workshop: lateral = 27f; p.Radius = 18f; break;
                case PoiKind.Settlement: lateral = 45f; p.Radius = 37f; break;
                case PoiKind.Checkpoint: lateral = 15f; p.Radius = 9f; break;
                default: lateral = 5.4f; p.Radius = 0f; break;
            }
            p.Side = side;
            p.Gravel = p.Radius > 0f;
            var c = a + n * (side * lateral);
            p.Center = new Vector3(c.X, rs.H, c.Y);
        }

        var toRoad = new Vector2(a.X - p.Center.X, a.Y - p.Center.Z);
        if (toRoad.LengthSquared() < 0.01f) toRoad = -n * p.Side;
        toRoad = toRoad.Normalized();
        p.Yaw = MathF.Atan2(-toRoad.X, -toRoad.Y);
        return new[] { p };
    }

    /// <summary>All POIs whose attach point lies within a Z range (for streaming and signs).</summary>
    public IEnumerable<PoiDesc> InRange(float zMin, float zMax)
    {
        for (int k = SegmentOf(zMin) - 1; k <= SegmentOf(zMax) + 1; k++)
            foreach (var p in ForSegment(k))
                if (p.Center.Z >= zMin && p.Center.Z <= zMax) yield return p;
    }
}
