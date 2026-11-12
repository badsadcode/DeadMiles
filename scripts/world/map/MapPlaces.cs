using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Keeps the things to stop for in the real-map world loaded around the player: fuel pumps and
/// supply crates at real shops, garages and clinics, and abandoned cars along the roads.
/// Runs on the main thread a couple of times a second; everything is rebuilt from the map and
/// the save's deltas, so unloading is just freeing nodes.
/// </summary>
public sealed class MapPlaces
{
    private const float SpawnDist = 240f, DespawnDist = 300f;

    private readonly MapWorld _map;
    private readonly PoiBuilder _builder;
    private readonly Node3D _root;
    private readonly Dictionary<string, Node3D> _live = new();
    private readonly Dictionary<long, List<(string id, Vector3 pos, float yaw)>> _wrecks = new();
    private readonly List<string> _remove = new();

    public MapPlaces(MapWorld map, PoiBuilder builder, Node3D root)
    {
        _map = map; _builder = builder; _root = root;
    }

    public void Update(Vector3 fp)
    {
        int tx0 = (int)Math.Floor((fp.X - SpawnDist) / MapTile.Size), tx1 = (int)Math.Floor((fp.X + SpawnDist) / MapTile.Size);
        int tz0 = (int)Math.Floor((fp.Z - SpawnDist) / MapTile.Size), tz1 = (int)Math.Floor((fp.Z + SpawnDist) / MapTile.Size);
        int built = 0;
        for (int tz = tz0; tz <= tz1; tz++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = _map.Data.Tile(tx, tz);
                if (tile.Empty) continue;
                foreach (var p in tile.Pois)
                {
                    if (built >= 4) return;   // spread the work over a few ticks
                    if (_live.ContainsKey(p.Id)) continue;
                    float x = (float)(tile.OriginX + p.X), z = (float)(tile.OriginZ + p.Z);
                    if (Dist2(x, z, fp) > SpawnDist * SpawnDist) continue;
                    Place(p, x, z);
                    built++;
                }
                foreach (var (id, pos, yaw) in WrecksFor(tile))
                {
                    if (built >= 4) return;
                    if (_live.ContainsKey(id) || Dist2(pos.X, pos.Z, fp) > SpawnDist * SpawnDist) continue;
                    Add(id, () => _builder.BuildMapWreck(id, pos, yaw));
                    built++;
                }
            }
        }
        _remove.Clear();
        foreach (var (id, node) in _live)
            if (Dist2(node.Position.X, node.Position.Z, fp) > DespawnDist * DespawnDist) _remove.Add(id);
        foreach (var id in _remove) { _live[id].QueueFree(); _live.Remove(id); }
    }

    private static float Dist2(float x, float z, Vector3 fp) => (x - fp.X) * (x - fp.X) + (z - fp.Z) * (z - fp.Z);

    private void Add(string id, Func<Node3D> build)
    {
        Node3D node;
        try { node = build(); }
        catch (Exception e)
        {
            Log.Error("MapPlaces", $"Building {id} failed: {e.Message}");
            node = new Node3D();
        }
        _root.AddChild(node);
        _live[id] = node;
    }

    /// <summary>
    /// Map points usually sit in the middle of their building. Walk out towards the nearest road
    /// until the spot is clear, so the crate or pump ends up by the door-side wall, facing the street.
    /// </summary>
    private void Place(MapPoi p, float x, float z)
    {
        // Fuel stations: the pumps belong under the canopy, lined up with it.
        if (p.Kind == MapPoiKind.Fuel && _map.NearestCanopy(x, z, 70f, out float cx, out float cz, out float cyaw))
        {
            var under = new Vector3(cx, _map.Height(cx, cz), cz);
            Add(p.Id, () => _builder.BuildMapPoi(p, under, cyaw));
            return;
        }
        Vector2 dir = new(1, 0);
        float roadDist = 60f;
        if (_map.NearestRoad(x, z, 90f, 0f, out var road, out _))
        {
            var to = new Vector2(road.X - x, road.Z - z);
            roadDist = to.Length();
            if (roadDist > 0.5f) dir = to / roadDist;
        }
        float px = x, pz = z;
        for (float t = 0; t < MathF.Min(roadDist, 45f); t += 0.75f)
        {
            px = x + dir.X * t; pz = z + dir.Y * t;
            if (_map.IsWild(px, pz, 1.1f)) break;
        }
        if (!_map.IsWild(px, pz, 0.4f) && _map.RoadEdgeDistance(px, pz, 12f) < 0.6f)
        {
            // ended up on the carriageway: step back onto the verge
            px -= dir.X * 2.5f; pz -= dir.Y * 2.5f;
        }
        var pos = new Vector3(px, _map.Height(px, pz), pz);
        float yaw = MathF.Atan2(dir.X, dir.Y);   // local +Z looks at the road
        Add(p.Id, () => _builder.BuildMapPoi(p, pos, yaw));
    }

    /// <summary>Abandoned cars for a tile, rolled once from the tile coordinates.</summary>
    private List<(string id, Vector3 pos, float yaw)> WrecksFor(MapTile tile)
    {
        long key = ((long)tile.Tx << 32) ^ (uint)tile.Tz;
        if (_wrecks.TryGetValue(key, out var list)) return list;
        list = new List<(string, Vector3, float)>();
        var rng = new Rng(Hash.Of(tile.Tx, tile.Tz, 4471));
        for (int i = 0; i < tile.Roads.Length; i++)
        {
            var r = tile.Roads[i];
            if (!r.Drivable || r.IsBridge || r.Cls > RoadClass.Residential && r.Cls != RoadClass.Link) continue;
            int n = r.X.Length;
            float length = r.Along[n - 1] - r.Along[0];
            // roughly one wreck per 2.2 km of road, fewer on tiny streets
            if (!rng.Chance(length / (r.Cls >= RoadClass.Residential ? 5200f : 2200f))) continue;
            int k = rng.Int(n - 1);
            float dx = r.X[k + 1] - r.X[k], dz = r.Z[k + 1] - r.Z[k];
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 0.5f) continue;
            dx /= len; dz /= len;
            bool onRoad = rng.Chance(0.3f);
            float side = (onRoad ? r.HalfWidth * 0.45f : r.HalfWidth + 1.9f) * (rng.Chance(0.5f) ? 1f : -1f);
            float x = (float)(tile.OriginX + (r.X[k] + r.X[k + 1]) * 0.5f - dz * side);
            float z = (float)(tile.OriginZ + (r.Z[k] + r.Z[k + 1]) * 0.5f + dx * side);
            if (!onRoad && _map.Point(x, z, 3f).InBuilding(0, 0, 2.5f)) continue;
            float yaw = MathF.Atan2(-dx, -dz) + rng.Range(-0.35f, 0.35f) + (rng.Chance(0.5f) ? Mathf.Pi : 0f);
            list.Add(($"w{tile.Tx}_{tile.Tz}_{i}", new Vector3(x, _map.Height(x, z), z), yaw));
        }
        _wrecks[key] = list;
        if (_wrecks.Count > 64) _wrecks.Clear();
        return list;
    }

    public void Clear()
    {
        foreach (var n in _live.Values) n.QueueFree();
        _live.Clear();
    }
}
