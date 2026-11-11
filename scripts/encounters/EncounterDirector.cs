using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Decides when the world is not empty. Wildlife appears ahead of the player in sensible
/// habitat; people are attached to places (seeded, so the same camp is occupied every time
/// until it is cleared) plus the occasional walker on the road. Everything simulates only
/// near the player and is removed at distance.
/// </summary>
public partial class EncounterDirector : Node3D
{
    private readonly List<Animal> _animals = new();
    private readonly List<Human> _humans = new();
    private readonly RandomNumberGenerator _rng = new();
    private double _wildlifeTimer = 12, _walkerTimer = 240, _sweepTimer = 2;
    private GameWorld _g = null!;

    public int AnimalCount => _animals.Count;
    public int HumanCount => _humans.Count;

    public void Setup(GameWorld g)
    {
        _g = g;
        _rng.Randomize();
        g.Streamer.PoiSpawned += OnPoiSpawned;
        g.Streamer.PoiRemoved += OnPoiRemoved;
    }

    public override void _Process(double delta)
    {
        var pp = _g.Player.GlobalPosition;
        _wildlifeTimer -= delta;
        if (_wildlifeTimer <= 0)
        {
            _wildlifeTimer = _rng.RandfRange(18f, 40f);
            int alive = 0;
            foreach (var a in _animals) if (!a.IsDead) alive++;
            if (alive < 4 && _rng.Randf() < 0.75f) TrySpawnWildlife(pp);
        }

        _walkerTimer -= delta;
        if (_walkerTimer <= 0)
        {
            _walkerTimer = _rng.RandfRange(300f, 620f);
            if (_humans.Count < 3) SpawnWalker(pp);
        }

        _sweepTimer -= delta;
        if (_sweepTimer <= 0)
        {
            _sweepTimer = 2;
            for (int i = _animals.Count - 1; i >= 0; i--)
            {
                if (!_animals[i].ShouldDespawn(pp)) continue;
                _animals[i].QueueFree();
                _animals.RemoveAt(i);
            }
            for (int i = _humans.Count - 1; i >= 0; i--)
            {
                var h = _humans[i];
                float d = h.GlobalPosition.DistanceTo(pp);
                bool gone = h.PoiId == "" ? d > 320f : false;
                if (h.Fleeing && d > 130f) gone = true;
                if (!gone) continue;
                h.QueueFree();
                _humans.RemoveAt(i);
            }
        }
    }

    // ---- wildlife ------------------------------------------------------------------------

    private void TrySpawnWildlife(Vector3 pp)
    {
        var world = _g.World;
        // bias the search to where the player is heading
        var heading = _g.Player.InVehicle ? -_g.Vehicle.GlobalTransform.Basis.Z : -_g.Player.GlobalTransform.Basis.Z;
        heading.Y = 0;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float a = MathF.Atan2(heading.Z, heading.X) + _rng.RandfRange(-1.3f, 1.3f);
            float d = _rng.RandfRange(85f, 150f);
            float x = pp.X + MathF.Cos(a) * d, z = pp.Z + MathF.Sin(a) * d;
            var rs = world.Road.Sample(z);
            if (!world.IsWild(x, z, rs, world.Pois.Near(z), 14f)) continue;
            float h = world.Height(x, z);
            if (h < WorldGenerator.WaterLevel + 0.6f) continue;
            world.Biome(x, z, out float forest, out float field);
            bool fox = _rng.Randf() < 0.22f;
            if (!fox && forest > 0.85f && _rng.Randf() < 0.5f) continue; // deer like the edges
            int count = fox ? 1 : _rng.RandiRange(1, 3);
            for (int i = 0; i < count; i++)
            {
                var kind = fox ? AnimalKind.Fox : (i == 0 && _rng.Randf() < 0.3f ? AnimalKind.Stag : AnimalKind.Deer);
                float ox = x + _rng.RandfRange(-6f, 6f), oz = z + _rng.RandfRange(-6f, 6f);
                SpawnAnimal(kind, new Vector3(ox, world.Height(ox, oz), oz));
            }
            return;
        }
    }

    public Animal SpawnAnimal(AnimalKind kind, Vector3 pos)
    {
        var a = new Animal { Kind = kind };
        AddChild(a);
        a.GlobalPosition = pos;
        _animals.Add(a);
        return a;
    }

    // ---- people --------------------------------------------------------------------------

    private void OnPoiSpawned(PoiDesc p, Node3D node)
    {
        if (Math.Abs(p.Segment) <= 1) return; // the first stretch is always safe
        if (_g.State.Flags.Contains("cleared:" + p.Id)) return;
        var rng = new Rng(Hash.Mix(p.Seed ^ 0x0cc0beefU));
        float roll = rng.F();
        int count = 0;
        Disposition attitude = Disposition.Wary;
        bool armed = false;
        switch (p.Kind)
        {
            case PoiKind.Checkpoint:
                if (roll < 0.5f) { count = 2; attitude = Disposition.Hostile; armed = true; }
                break;
            case PoiKind.Campsite:
                if (roll < 0.2f) { count = 1; attitude = Disposition.Friendly; }
                else if (roll < 0.38f) { count = 1; attitude = Disposition.Wary; armed = rng.Chance(0.5f); }
                else if (roll < 0.46f) { count = 2; attitude = Disposition.Hostile; armed = true; }
                break;
            case PoiKind.Farmhouse:
                if (roll < 0.16f) { count = 1; attitude = Disposition.Wary; armed = true; }
                break;
            case PoiKind.Settlement:
                if (roll < 0.25f) { count = rng.IntRange(1, 2); attitude = Disposition.Friendly; }
                else if (roll < 0.35f) { count = 2; attitude = Disposition.Hostile; armed = true; }
                break;
            case PoiKind.GasStation:
            case PoiKind.Diner:
            case PoiKind.Workshop:
                if (roll < 0.1f) { count = 1; attitude = Disposition.Wary; }
                else if (roll < 0.17f) { count = 1; attitude = Disposition.Friendly; }
                else if (roll < 0.22f) { count = 2; attitude = Disposition.Hostile; armed = rng.Chance(0.6f); }
                break;
        }
        for (int i = 0; i < count; i++)
        {
            float r = MathF.Min(p.Radius * 0.45f, 7f);
            var local = new Vector3((i * 2 - (count - 1)) * 2.2f + rng.Range(-1f, 1f), 0, -r + rng.Range(-1.5f, 1.5f));
            var pos = p.ToWorld(local);
            pos.Y = _g.World.Height(pos.X, pos.Z) + 0.1f;
            SpawnHuman(pos, attitude, armed && (i == 0 || rng.Chance(0.5f)), p.Id);
        }
        if (count > 0) Log.Info("Encounters", $"{p.Kind} {p.Id} is occupied: {count} x {attitude}");
    }

    private void OnPoiRemoved(PoiDesc p)
    {
        for (int i = _humans.Count - 1; i >= 0; i--)
        {
            if (_humans[i].PoiId != p.Id) continue;
            _humans[i].QueueFree();
            _humans.RemoveAt(i);
        }
    }

    public Human SpawnHuman(Vector3 pos, Disposition attitude, bool armed, string poiId)
    {
        var h = new Human { Attitude = attitude, Armed = armed, Home = pos, PoiId = poiId };
        AddChild(h);
        h.GlobalPosition = pos;
        _humans.Add(h);
        return h;
    }

    /// <summary>A lone figure on the shoulder far ahead. Most of them just want to keep walking.</summary>
    private void SpawnWalker(Vector3 pp)
    {
        var world = _g.World;
        float dir = _g.Player.InVehicle ? MathF.Sign(-_g.Vehicle.GlobalTransform.Basis.Z.Z) : (_rng.Randf() < 0.5f ? 1f : -1f);
        if (dir == 0) dir = 1;
        float z = pp.Z + dir * _rng.RandfRange(170f, 230f);
        if (Math.Abs(PoiPlacer.SegmentOf(z)) <= 1) return;
        var pos = world.Road.PointAt(z, _rng.Randf() < 0.5f ? 4.6f : -4.6f);
        pos.Y = world.Height(pos.X, pos.Z) + 0.1f;
        float r = _rng.Randf();
        var attitude = r < 0.5f ? Disposition.Friendly : r < 0.85f ? Disposition.Wary : Disposition.Hostile;
        SpawnHuman(pos, attitude, attitude == Disposition.Hostile || _rng.Randf() < 0.3f, "");
        Log.Info("Encounters", $"A walker ({attitude}) is on the road ahead");
    }

    public void OnHumanDied(Human h)
    {
        if (h.PoiId == "") return;
        foreach (var o in _humans) if (o.PoiId == h.PoiId && !o.IsDead) return;
        if (!_g.State.Flags.Contains("cleared:" + h.PoiId)) _g.State.Flags.Add("cleared:" + h.PoiId);
    }

    public bool HostileNear(Vector3 pos, float radius)
    {
        foreach (var h in _humans)
            if (!h.IsDead && h.Attitude == Disposition.Hostile && h.GlobalPosition.DistanceTo(pos) < radius) return true;
        return false;
    }

    /// <summary>Gunshots and horns carry: animals bolt, people come to look.</summary>
    public void OnLoudNoise(Vector3 pos, float radius, Node? except = null)
    {
        foreach (var a in _animals)
            if (a != except && a.GlobalPosition.DistanceTo(pos) < radius) a.Scare(pos);
        foreach (var h in _humans)
            if (h.GlobalPosition.DistanceTo(pos) < radius * 0.6f) h.Alert(pos);
    }
}
