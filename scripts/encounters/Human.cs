using System;
using Godot;

namespace DeadMiles;

public enum Disposition { Friendly, Wary, Hostile }

/// <summary>
/// Another survivor. A small set of readable behaviours: idle/patrol, notice, warn, approach,
/// attack, flee, disengage. Friendly people talk, wary people want distance, bandits want your
/// pack. The figure is a clean primitive placeholder with a procedural walk cycle.
/// </summary>
public partial class Human : CharacterBody3D, IDamageable, IInteractable
{
    private enum Mode { Idle, Patrol, Notice, Approach, Attack, Flee, Return, Dead }

    public Disposition Attitude = Disposition.Wary;
    public bool Armed;
    public Vector3 Home;
    public string PoiId = "";
    public bool IsDead => _mode == Mode.Dead;

    private Mode _mode = Mode.Idle;
    private float _health = 100f, _timer = 2f, _yaw, _walk, _attackTimer, _warned, _closeTime, _speechTimer;
    private Vector3 _goal;
    private bool _provoked, _greeted;
    private Node3D _body = null!, _armL = null!, _armR = null!, _legL = null!, _legR = null!;
    private Label3D _speech = null!;
    private OmniLight3D _muzzle = null!;
    private string _lootId = "";
    private readonly RandomNumberGenerator _rng = new();

    private static readonly string[] FriendlyLines =
    {
        "Evening. Road's quiet further on.", "You're the first face I've seen in days.", "Fuel's getting scarce. Check the farms.",
        "Mind the checkpoints. Not everyone there is army.", "Take care out there.",
    };
    private static readonly string[] WaryLines = { "That's close enough.", "Keep walking, friend.", "I don't want trouble. Stay back.", "Not another step." };
    private static readonly string[] HostileLines = { "Drop the pack and walk away!", "That's a nice car. Leave the keys.", "Wrong road, stranger.", "Don't move!" };
    private static readonly string[] FleeLines = { "Enough! I'm going!", "Not worth it!", "Don't shoot!" };

    public override void _Ready()
    {
        _rng.Randomize();
        CollisionLayer = 8;
        CollisionMask = 1 | 2 | 4;
        FloorSnapLength = 0.3f;
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.76f }, Position = new Vector3(0, 0.88f, 0) });
        BuildFigure();
        _yaw = _rng.RandfRange(0, Mathf.Tau);
        _lootId = $"body:{PoiId}:{GetInstanceId()}";
    }

    private void BuildFigure()
    {
        _body = new Node3D();
        AddChild(_body);
        var jackets = new[] { new Color(0.22f, 0.25f, 0.16f), new Color(0.30f, 0.20f, 0.14f), new Color(0.16f, 0.20f, 0.28f), new Color(0.32f, 0.30f, 0.26f), new Color(0.38f, 0.14f, 0.10f) };
        var jacket = new StandardMaterial3D { AlbedoColor = jackets[_rng.RandiRange(0, jackets.Length - 1)], Roughness = 0.95f };
        var trousers = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.10f, 0.12f).Lerp(new Color(0.2f, 0.17f, 0.12f), _rng.Randf()), Roughness = 0.95f };
        var skin = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.40f, 0.30f).Lerp(new Color(0.28f, 0.18f, 0.12f), _rng.Randf() * 0.7f), Roughness = 0.8f };
        var dark = PoiBuilder.Black;

        MeshInstance3D Box(Node3D parent, Vector3 c, Vector3 size, Material m)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = c, MaterialOverride = m };
            parent.AddChild(mi);
            return mi;
        }
        Box(_body, new Vector3(0, 1.17f, 0), new Vector3(0.44f, 0.62f, 0.25f), jacket);
        _body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.125f, Height = 0.26f, RadialSegments = 12, Rings = 6 }, Position = new Vector3(0, 1.63f, 0), MaterialOverride = skin });
        if (_rng.Randf() < 0.6f) Box(_body, new Vector3(0, 1.73f, 0.01f), new Vector3(0.27f, 0.1f, 0.28f), _rng.Randf() < 0.5f ? jacket : dark); // hat / hood
        if (_rng.Randf() < 0.5f) Box(_body, new Vector3(0, 1.2f, 0.19f), new Vector3(0.34f, 0.44f, 0.16f), trousers); // backpack

        Node3D Limb(Vector3 pivot, Vector3 size, Material m, Material? end)
        {
            var n = new Node3D { Position = pivot };
            _body.AddChild(n);
            Box(n, new Vector3(0, -size.Y * 0.5f, 0), size, m);
            if (end != null) Box(n, new Vector3(0, -size.Y - 0.04f, 0), new Vector3(size.X * 0.95f, 0.09f, size.Z * 0.95f), end);
            return n;
        }
        _armL = Limb(new Vector3(-0.29f, 1.44f, 0), new Vector3(0.12f, 0.58f, 0.13f), jacket, skin);
        _armR = Limb(new Vector3(0.29f, 1.44f, 0), new Vector3(0.12f, 0.58f, 0.13f), jacket, skin);
        _legL = Limb(new Vector3(-0.115f, 0.86f, 0), new Vector3(0.17f, 0.8f, 0.19f), trousers, dark);
        _legR = Limb(new Vector3(0.115f, 0.86f, 0), new Vector3(0.17f, 0.8f, 0.19f), trousers, dark);
        if (Armed)
        {
            var steel = PoiBuilder.Mat("toolsteel", 0.12f, 0.12f, 0.13f, 0.45f, 0.7f);
            Box(_armR, new Vector3(0, -0.64f, -0.1f), new Vector3(0.04f, 0.1f, 0.2f), steel);
        }
        _muzzle = new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.35f), LightEnergy = 0, OmniRange = 8f, Position = new Vector3(0.3f, 1.35f, -0.7f) };
        _body.AddChild(_muzzle);
        _speech = new Label3D
        {
            Position = new Vector3(0, 2.05f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 44, PixelSize = 0.0042f,
            Modulate = new Color(0.95f, 0.93f, 0.85f), OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.85f), NoDepthTest = true, Visible = false,
        };
        AddChild(_speech);
    }

    private void Say(string[] lines)
    {
        string line = lines[_rng.RandiRange(0, lines.Length - 1)];
        _speech.Text = line;
        _speech.Visible = true;
        _speechTimer = 4f;
        GameWorld.I.Hud.Subtitle(line);
    }

    public void Alert(Vector3 from)
    {
        if (_mode == Mode.Dead || _mode == Mode.Flee) return;
        if (Attitude == Disposition.Hostile && _mode is Mode.Idle or Mode.Patrol or Mode.Return) { _mode = Mode.Approach; _goal = from; }
        else if (_mode is Mode.Idle or Mode.Patrol) { _mode = Mode.Notice; _timer = 3f; }
    }

    public void Damage(float amount, Node3D? source, Vector3 hitPoint)
    {
        if (_mode == Mode.Dead) return;
        _health -= amount;
        _provoked = true;
        if (_health <= 0)
        {
            _mode = Mode.Dead;
            _speech.Visible = false;
            CollisionMask = 1;
            var t = CreateTween();
            t.TweenProperty(_body, "rotation", new Vector3(-Mathf.Pi * 0.5f, 0, 0), 0.5f).SetEase(Tween.EaseType.In);
            t.Parallel().TweenProperty(_body, "position", new Vector3(0, 0.16f, 0), 0.5f);
            GameWorld.I.Encounters.OnHumanDied(this);
            return;
        }
        if (_health < 38f || (!Armed && Attitude != Disposition.Hostile && _rng.Randf() < 0.6f))
        {
            _mode = Mode.Flee;
            _timer = 20f;
            Say(FleeLines);
        }
        else
        {
            if (Attitude != Disposition.Hostile) Attitude = Disposition.Hostile;
            _mode = Mode.Attack;
        }
    }

    public string? GetPrompt(PlayerController player) => _mode == Mode.Dead ? GameWorld.I.ContainerPrompt(_lootId, "Body") : null;

    public void Interact(PlayerController player)
    {
        if (_mode == Mode.Dead) GameWorld.I.OpenContainer(_lootId, "Body", Armed ? "body" : "camp", Hash.OfString(_lootId));
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var g = GameWorld.I;
        if (_speechTimer > 0) { _speechTimer -= dt; if (_speechTimer <= 0) _speech.Visible = false; }
        _muzzle.LightEnergy = Mathf.Lerp(_muzzle.LightEnergy, 0, MathX.Damp(25f, dt));

        var pos = GlobalPosition;
        var vel = Velocity;
        if (!g.Streamer.IsSolidAt(pos))
        {
            // No collision out here: stand on the height function and wait.
            GlobalPosition = new Vector3(pos.X, g.World.Height(pos.X, pos.Z), pos.Z);
            return;
        }
        vel.Y = IsOnFloor() ? -0.5f : vel.Y - 17f * dt;

        if (_mode == Mode.Dead)
        {
            Velocity = new Vector3(0, vel.Y, 0);
            MoveAndSlide();
            return;
        }

        var player = g.Player;
        var pp = player.GlobalPosition;
        var to = pp - pos;
        float dist = to.Length();
        bool inCar = player.InVehicle;
        float sight = g.Time.IsNight ? ((inCar && g.Vehicle.LightsOn) || player.Flashlight.Visible ? 60f : 24f) : 60f;
        if (g.Weather.Fog > 0.5f) sight *= 0.5f;
        bool sees = dist < sight && !player.Stats.Dead;
        float speed = 0f;
        _timer -= dt;
        _attackTimer -= dt;

        switch (_mode)
        {
            case Mode.Idle:
                if (_timer <= 0)
                {
                    _mode = Mode.Patrol;
                    _timer = _rng.RandfRange(5f, 11f);
                    float a = _rng.RandfRange(0, Mathf.Tau), d = _rng.RandfRange(2f, 7f);
                    _goal = Home + new Vector3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d);
                }
                if (sees && dist < 45f) { _mode = Mode.Notice; _timer = 2f; }
                break;

            case Mode.Patrol:
                speed = 1.35f;
                if (_timer <= 0 || Flat(_goal - pos).Length() < 0.8f) { _mode = Mode.Idle; _timer = _rng.RandfRange(3f, 9f); }
                if (sees && dist < 45f) { _mode = Mode.Notice; _timer = 2f; }
                break;

            case Mode.Notice:
                Face(to, dt, 5f);
                if (!sees && _timer <= 0) { _mode = Mode.Idle; _timer = 3f; break; }
                if (Attitude == Disposition.Hostile)
                {
                    if (_timer <= 0.8f) { _mode = Mode.Approach; Say(HostileLines); }
                }
                else if (Attitude == Disposition.Friendly)
                {
                    if (dist < 12f && !_greeted) { _greeted = true; Say(FriendlyLines); _timer = 8f; }
                    if (dist > 50f || _timer <= -6f) { _mode = Mode.Idle; _timer = 4f; }
                }
                else
                {
                    if (dist < 17f && _warned <= 0) { Say(WaryLines); _warned = 9f; }
                    _warned -= dt;
                    _closeTime = dist < 6.5f && !inCar ? _closeTime + dt : MathF.Max(0, _closeTime - dt);
                    if (_closeTime > 2.5f)
                    {
                        if (Armed || _rng.Randf() < 0.5f) { Attitude = Disposition.Hostile; _mode = Mode.Attack; Say(HostileLines); }
                        else { _mode = Mode.Flee; _timer = 15f; Say(FleeLines); }
                    }
                    if (dist > 40f) { _mode = Mode.Idle; _timer = 4f; }
                }
                break;

            case Mode.Approach:
                _goal = pp;
                speed = 4.3f;
                if (!sees || dist > 85f) { _mode = Mode.Return; break; }
                if (dist < (Armed ? 16f : 2.0f)) _mode = Mode.Attack;
                break;

            case Mode.Attack:
                Face(to, dt, 6f);
                if (!sees || dist > 85f) { _mode = Mode.Return; break; }
                if (Armed)
                {
                    if (dist > 24f) { _goal = pp; speed = 3.6f; }
                    else if (dist < 7f) { _goal = pos - to; speed = 2.2f; }
                    if (_attackTimer <= 0 && dist < 45f)
                    {
                        _attackTimer = _rng.RandfRange(1.3f, 2.3f);
                        Shoot(player, dist, inCar);
                    }
                }
                else
                {
                    if (dist > 1.7f) { _goal = pp; speed = 4.6f; }
                    else if (_attackTimer <= 0)
                    {
                        _attackTimer = 1.15f;
                        _armR.Rotation = new Vector3(-2.2f, 0, 0);
                        g.Audio.Play3D("swing", pos + Vector3.Up * 1.3f, -4f);
                        player.Damage(inCar ? 0f : 11f, this, pp);
                        if (inCar) g.Vehicle.Damage(20f, this, pp);
                    }
                }
                break;

            case Mode.Flee:
                _goal = pos - Flat(to).Normalized() * 30f;
                speed = 5.2f;
                break;

            case Mode.Return:
                _goal = Home;
                speed = 1.8f;
                if (Flat(Home - pos).Length() < 2f) { _mode = Mode.Idle; _timer = 4f; }
                if (sees && dist < 30f && Attitude == Disposition.Hostile) _mode = Mode.Approach;
                break;
        }

        if (speed > 0.01f)
        {
            var dir = Flat(_goal - pos);
            if (dir.LengthSquared() > 0.01f)
            {
                dir = dir.Normalized();
                if (_mode != Mode.Attack) Face(dir, dt, 6f);
                vel.X = Mathf.Lerp(vel.X, dir.X * speed, MathX.Damp(8f, dt));
                vel.Z = Mathf.Lerp(vel.Z, dir.Z * speed, MathX.Damp(8f, dt));
            }
        }
        else
        {
            vel.X = Mathf.Lerp(vel.X, 0, MathX.Damp(10f, dt));
            vel.Z = Mathf.Lerp(vel.Z, 0, MathX.Damp(10f, dt));
        }
        Velocity = vel;
        MoveAndSlide();
        Rotation = new Vector3(0, _yaw, 0);

        // procedural walk: swing limbs with ground speed
        float hs = Flat(Velocity).Length();
        _walk += hs * dt * 3.4f;
        float swing = MathF.Sin(_walk) * Mathf.Clamp(hs / 3f, 0f, 1f) * 0.75f;
        _legL.Rotation = new Vector3(swing, 0, 0);
        _legR.Rotation = new Vector3(-swing, 0, 0);
        _armL.Rotation = new Vector3(-swing * 0.8f, 0, 0);
        float aim = _mode == Mode.Attack && Armed ? -1.45f : swing * 0.8f;
        _armR.Rotation = new Vector3(Mathf.Lerp(_armR.Rotation.X, aim, MathX.Damp(10f, dt)), 0, 0);
        _body.Position = new Vector3(0, _mode == Mode.Dead ? 0.16f : MathF.Abs(MathF.Sin(_walk)) * 0.03f * Mathf.Clamp(hs / 3f, 0f, 1f), 0);
    }

    private void Shoot(PlayerController player, float dist, bool inCar)
    {
        var g = GameWorld.I;
        _muzzle.LightEnergy = 5f;
        g.Audio.Play3D("gunshot", GlobalPosition + Vector3.Up * 1.4f, -3f, 1.15f, 25f, 400f);
        float chance = Mathf.Clamp(0.6f - dist * 0.011f, 0.1f, 0.6f);
        if (inCar && MathF.Abs(g.Vehicle.Speed) > 6f) chance *= 0.45f;
        if (g.Time.IsNight) chance *= 0.75f;
        if (_rng.Randf() > chance) return;
        // a wall or a tree between us stops the bullet
        var from = GlobalPosition + Vector3.Up * 1.4f;
        var target = player.GlobalPosition + Vector3.Up * (inCar ? 0.2f : 1.2f);
        var q = PhysicsRayQueryParameters3D.Create(from, target, 1);
        if (GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0) return;
        if (inCar) g.Vehicle.Damage(38f, this, target);
        else player.Damage(_rng.RandfRange(11f, 18f), this, target);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    private void Face(Vector3 dir, float dt, float rate)
    {
        if (dir.X * dir.X + dir.Z * dir.Z < 0.0001f) return;
        _yaw = Mathf.LerpAngle(_yaw, MathF.Atan2(-dir.X, -dir.Z), MathX.Damp(rate, dt));
    }

    public bool Fleeing => _mode == Mode.Flee;
}
