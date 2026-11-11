using System;
using Godot;

namespace DeadMiles;

public enum AnimalKind { Deer, Stag, Fox }

/// <summary>
/// Wildlife. Animals glide over the terrain height function instead of using physics, so
/// they are cheap and never fall through unloaded ground. Deer and stags can be hunted and
/// harvested; foxes are just life at the edge of the headlights.
/// </summary>
public partial class Animal : AnimatableBody3D, IDamageable, IInteractable
{
    private enum Mode { Graze, Wander, Alert, Flee, Dead }

    public AnimalKind Kind;
    public bool IsDead => _mode == Mode.Dead;
    public bool Harvested { get; private set; }

    private Mode _mode = Mode.Graze;
    private float _timer = 3f, _health, _yaw, _speed, _deadTime;
    private Vector3 _goal, _threat;
    private AnimationPlayer? _anim;
    private string _current = "";
    private Node3D _model = null!;
    private readonly RandomNumberGenerator _rng = new();

    private float WalkSpeed => Kind == AnimalKind.Fox ? 1.3f : 1.1f;
    private float RunSpeed => Kind == AnimalKind.Fox ? 7.5f : 10.5f;
    private float Wariness => Kind == AnimalKind.Fox ? 22f : 30f;

    public override void _Ready()
    {
        _rng.Randomize();
        CollisionLayer = 8;
        CollisionMask = 0;
        SyncToPhysics = false;
        float height = Kind switch { AnimalKind.Stag => 1.75f, AnimalKind.Deer => 1.45f, _ => 0.55f };
        _health = Kind == AnimalKind.Fox ? 30f : Kind == AnimalKind.Stag ? 85f : 60f;
        float len = Kind == AnimalKind.Fox ? 0.9f : 1.5f;
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.5f, height * 0.75f, len) },
            Position = new Vector3(0, height * 0.5f, 0),
        });

        _model = new Node3D { Rotation = new Vector3(0, Mathf.Pi, 0) };
        AddChild(_model);
        string path = $"res://assets/animals/{Kind}.gltf";
        var scene = ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : null;
        if (scene == null)
        {
            Log.Warn("Animal", $"Missing {path}; using a placeholder");
            _model.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, height * 0.5f, len) }, Position = new Vector3(0, height * 0.6f, 0), MaterialOverride = PoiBuilder.Wood });
        }
        else
        {
            var inst = scene.Instantiate<Node3D>();
            _model.AddChild(inst);
            // Normalise whatever size the asset was authored at to a believable shoulder height.
            Aabb? box = null;
            foreach (var n in inst.FindChildren("*", "MeshInstance3D", true, false))
            {
                var mi = (MeshInstance3D)n;
                var xf = Transform3D.Identity;
                Node cur = mi;
                while (cur != inst && cur is Node3D c3) { xf = c3.Transform * xf; cur = cur.GetParent(); }
                var a = xf * mi.GetAabb();
                box = box == null ? a : box.Value.Merge(a);
            }
            if (box != null && box.Value.Size.Y > 0.01f)
            {
                float s = height / box.Value.Size.Y;
                inst.Scale = Vector3.One * s;
                inst.Position = new Vector3(0, -box.Value.Position.Y * s, 0);
            }
            _anim = inst.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
            if (_anim != null)
            {
                foreach (var name in new[] { "Walk", "Gallop", "Idle", "Eating", "Idle_2" })
                    if (_anim.HasAnimation(name)) _anim.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;
            }
        }
        _yaw = _rng.RandfRange(0, Mathf.Tau);
        Rotation = new Vector3(0, _yaw, 0);
        PlayAnim("Eating");
    }

    private void PlayAnim(string name, float speed = 1f)
    {
        if (_anim == null) return;
        if (_current == name) { _anim.SpeedScale = speed; return; }
        if (!_anim.HasAnimation(name)) name = "Idle";
        if (!_anim.HasAnimation(name)) return;
        _current = name;
        _anim.Play(name, 0.25);
        _anim.SpeedScale = speed;
    }

    public void Scare(Vector3 from)
    {
        if (_mode == Mode.Dead) return;
        _threat = from;
        _mode = Mode.Flee;
        _timer = _rng.RandfRange(6f, 10f);
    }

    public void Damage(float amount, Node3D? source, Vector3 hitPoint)
    {
        if (_mode == Mode.Dead) return;
        _health -= amount;
        if (_health <= 0)
        {
            _mode = Mode.Dead;
            _speed = 0;
            PlayAnim("Death");
            GameWorld.I.Encounters.OnLoudNoise(GlobalPosition, 60f, this);
        }
        else Scare(source?.GlobalPosition ?? hitPoint);
    }

    public string? GetPrompt(PlayerController player)
    {
        if (_mode != Mode.Dead || Harvested || Kind == AnimalKind.Fox) return null;
        return "Harvest venison";
    }

    public void Interact(PlayerController player)
    {
        if (_mode != Mode.Dead || Harvested) return;
        int want = Kind == AnimalKind.Stag ? 4 : 3;
        int got = player.Inventory.Add("raw_meat", want);
        if (got == 0) { GameWorld.I.Toast("You cannot carry any more."); return; }
        Harvested = true;
        GameWorld.I.Audio.Play("cloth");
        GameWorld.I.Toast($"You take {got} cut{(got > 1 ? "s" : "")} of venison. Cook it on a campfire.");
        _deadTime = 1000f; // let it be cleaned up
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var g = GameWorld.I;
        var world = g.World;
        var pos = GlobalPosition;

        if (_mode == Mode.Dead)
        {
            _deadTime += dt;
            return;
        }

        // what is the player doing?
        var pp = g.Player.GlobalPosition;
        float dist = pos.DistanceTo(pp);
        bool inCar = g.Player.InVehicle;
        float noticed = inCar ? (MathF.Abs(g.Vehicle.Speed) > 2f ? 42f : 18f) : Wariness * (Input.IsActionPressed("crouch") ? 0.5f : 1f);
        if (_mode != Mode.Flee && dist < noticed) Scare(pp);
        else if (_mode is Mode.Graze or Mode.Wander && dist < noticed * 2.2f && _rng.Randf() < dt * 0.6f)
        {
            _mode = Mode.Alert;
            _timer = _rng.RandfRange(2f, 5f);
        }

        // being run over
        if (inCar && dist < 2.6f && MathF.Abs(g.Vehicle.Speed) > 7f)
        {
            g.Vehicle.EngineHealth = MathF.Max(3f, g.Vehicle.EngineHealth - (Kind == AnimalKind.Fox ? 1f : 8f));
            g.Audio.Play3D("hit", pos, 0f, 0.7f);
            Damage(500f, g.Vehicle, pos);
            return;
        }

        _timer -= dt;
        float targetSpeed = 0f;
        switch (_mode)
        {
            case Mode.Graze:
                PlayAnim("Eating");
                if (_timer <= 0)
                {
                    _mode = Mode.Wander;
                    _timer = _rng.RandfRange(5f, 12f);
                    float a = _rng.RandfRange(0, Mathf.Tau), d = _rng.RandfRange(8f, 26f);
                    _goal = pos + new Vector3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d);
                }
                break;
            case Mode.Wander:
                targetSpeed = WalkSpeed;
                PlayAnim("Walk", 1.1f);
                if (_timer <= 0 || new Vector2(_goal.X - pos.X, _goal.Z - pos.Z).Length() < 1.5f)
                {
                    _mode = Mode.Graze;
                    _timer = _rng.RandfRange(4f, 14f);
                }
                break;
            case Mode.Alert:
                PlayAnim("Idle");
                _goal = pp;
                Face(pp - pos, dt, 3f);
                if (_timer <= 0) { _mode = Mode.Graze; _timer = _rng.RandfRange(3f, 8f); }
                break;
            case Mode.Flee:
                targetSpeed = RunSpeed;
                PlayAnim("Gallop", 1.25f);
                var away = pos - _threat; away.Y = 0;
                if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
                _goal = pos + away.Normalized() * 30f;
                if (_timer <= 0 && pos.DistanceTo(pp) > 75f) { _mode = Mode.Graze; _timer = _rng.RandfRange(4f, 9f); }
                break;
        }

        _speed = Mathf.Lerp(_speed, targetSpeed, MathX.Damp(3f, dt));
        if (_speed > 0.05f)
        {
            var dir = _goal - pos; dir.Y = 0;
            if (dir.LengthSquared() > 0.01f) Face(dir, dt, _mode == Mode.Flee ? 5f : 2f);
            var fwd = new Vector3(-MathF.Sin(_yaw), 0, -MathF.Cos(_yaw));
            var next = pos + fwd * _speed * dt;
            float h = world.Height(next.X, next.Z);
            // keep out of lakes and buildings; turn aside rather than stop
            var probe = pos + fwd * 4f;
            var rs = world.Road.Sample(probe.Z);
            bool blocked = world.Height(probe.X, probe.Z) < WorldGenerator.WaterLevel + 0.2f;
            foreach (var p in world.Pois.Near(probe.Z))
            {
                if (p.Radius <= 0) continue;
                float dx = probe.X - p.Center.X, dz = probe.Z - p.Center.Z;
                if (dx * dx + dz * dz < (p.Radius + 2f) * (p.Radius + 2f)) blocked = true;
            }
            if (blocked) { _yaw += dt * 2.5f; _goal = pos + new Vector3(-MathF.Sin(_yaw), 0, -MathF.Cos(_yaw)) * 20f; if (_mode == Mode.Flee) _threat = probe; }
            else pos = new Vector3(next.X, h, next.Z);
        }
        else pos.Y = world.Height(pos.X, pos.Z);

        GlobalPosition = pos;
        Rotation = new Vector3(0, _yaw, 0);
    }

    private void Face(Vector3 dir, float dt, float rate)
    {
        float want = MathF.Atan2(-dir.X, -dir.Z);
        _yaw = Mathf.LerpAngle(_yaw, want, MathX.Damp(rate, dt));
    }

    /// <summary>Carcasses linger for a while, then clear once the player has moved on.</summary>
    public bool ShouldDespawn(Vector3 playerPos)
    {
        float d = GlobalPosition.DistanceTo(playerPos);
        if (_mode == Mode.Dead) return (_deadTime > 240f && d > 60f) || d > 320f;
        return d > 260f;
    }
}
