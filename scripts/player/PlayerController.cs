using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// First-person body: walking, sprinting, crouching, jumping, the flashlight, the interaction
/// ray, the two hand tools, and getting in and out of the car.
/// </summary>
public partial class PlayerController : CharacterBody3D, IDamageable
{
    public const float EyeHeight = 1.62f, CrouchEye = 1.0f;
    public const uint InteractMask = 1 | 4 | 8;

    public readonly PlayerStats Stats = new();
    public readonly Inventory Inventory = new("Backpack", 22f);
    public Camera3D Cam = null!;
    public SpotLight3D Flashlight = null!;
    public bool InVehicle => Vehicle != null;
    public VehicleController? Vehicle;

    /// <summary>What the interaction ray is resting on, for the HUD.</summary>
    public string? Prompt { get; private set; }
    public Vector3 LastHitPoint { get; private set; }
    public int Slot { get; private set; } // 0 hands, 1 crowbar, 2 rifle
    public bool Aiming { get; private set; }

    private Node3D _head = null!;
    private CollisionShape3D _shape = null!;
    private CapsuleShape3D _capsule = null!;
    private Node3D _viewModel = null!, _crowbar = null!, _rifle = null!;
    private OmniLight3D _muzzle = null!;
    private IInteractable? _target;
    private bool _waterTarget;
    private float _pitch, _stepDistance, _attackCooldown, _bob, _crouch, _recoil, _swing;
    private bool _crouching;
    private const float Gravity = 17f;

    public override void _Ready()
    {
        CollisionLayer = 2;
        CollisionMask = 1 | 4 | 8;
        FloorMaxAngle = Mathf.DegToRad(48f);
        FloorSnapLength = 0.35f;
        _capsule = new CapsuleShape3D { Radius = 0.34f, Height = 1.78f };
        _shape = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, 0.89f, 0) };
        AddChild(_shape);

        _head = new Node3D { Position = new Vector3(0, EyeHeight, 0) };
        AddChild(_head);
        Cam = new Camera3D { Near = 0.06f, Far = 1600f, Fov = Main.I.Settings.Fov, Current = true };
        _head.AddChild(Cam);

        Flashlight = new SpotLight3D
        {
            SpotRange = 42f, SpotAngle = 27f, SpotAngleAttenuation = 0.9f, LightEnergy = 5.5f, SpotAttenuation = 0.7f,
            LightColor = new Color(1f, 0.95f, 0.84f), ShadowEnabled = true, Visible = false, LightVolumetricFogEnergy = 3.5f,
            Position = new Vector3(0.18f, -0.14f, 0.05f),
        };
        Cam.AddChild(Flashlight);

        BuildViewModel();
        Stats.Died += cause => GameWorld.I.OnPlayerDied(cause);
        Stats.Damaged += (amount, _) =>
        {
            GameWorld.I.Hud.DamageFlash(amount);
            GameWorld.I.Audio.Play("hurt", -3f);
        };
        Inventory.Changed += ValidateSlot;
    }

    private void BuildViewModel()
    {
        _viewModel = new Node3D();
        Cam.AddChild(_viewModel);
        var steel = PoiBuilder.Mat("handsteel", 0.30f, 0.29f, 0.28f, 0.5f, 0.5f);
        var wood = PoiBuilder.DarkWood;

        _crowbar = new Node3D { Position = new Vector3(0.38f, -0.42f, -0.62f), Rotation = new Vector3(-0.5f, 0.25f, -0.2f), Visible = false };
        _crowbar.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.022f, 0.6f, 0.022f) }, MaterialOverride = steel, Position = new Vector3(0, 0.18f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _crowbar.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.022f, 0.026f, 0.1f) }, MaterialOverride = steel, Position = new Vector3(0, 0.48f, -0.05f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _viewModel.AddChild(_crowbar);

        _rifle = new Node3D { Position = new Vector3(0.2f, -0.2f, -0.42f), Visible = false };
        _rifle.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.09f, 0.5f) }, MaterialOverride = wood, Position = new Vector3(0, -0.02f, 0.1f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _rifle.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.013f, BottomRadius = 0.013f, Height = 0.62f, RadialSegments = 6, Rings = 1 }, MaterialOverride = steel, Position = new Vector3(0, 0.025f, -0.35f), Rotation = new Vector3(Mathf.Pi * 0.5f, 0, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _rifle.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.16f) }, MaterialOverride = steel, Position = new Vector3(0, 0.06f, -0.02f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _viewModel.AddChild(_rifle);

        _muzzle = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.4f), LightEnergy = 0f, OmniRange = 9f, Position = new Vector3(0.2f, -0.15f, -1.1f) };
        Cam.AddChild(_muzzle);
    }

    public float Yaw { get => Rotation.Y; set => Rotation = new Vector3(0, value, 0); }

    public void SetLook(float yaw, float pitch)
    {
        Yaw = yaw;
        _pitch = Mathf.Clamp(pitch, -1.5f, 1.5f);
        _head.Rotation = new Vector3(_pitch, 0, 0);
    }

    public void SetSlot(int slot)
    {
        if (slot == 1 && !Inventory.Has("crowbar")) { GameWorld.I.Toast("You have no crowbar."); return; }
        if (slot == 2 && !Inventory.Has("rifle")) { GameWorld.I.Toast("You have no rifle."); return; }
        Slot = slot;
        _crowbar.Visible = slot == 1 && !InVehicle;
        _rifle.Visible = slot == 2 && !InVehicle;
    }

    private void ValidateSlot()
    {
        if ((Slot == 1 && !Inventory.Has("crowbar")) || (Slot == 2 && !Inventory.Has("rifle"))) SetSlot(0);
    }

    // ---- input ---------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        var g = GameWorld.I;
        if (g.UiOpen || Stats.Dead || InVehicle) return;
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float sens = Main.I.Settings.MouseSensitivity * 0.0022f * (Aiming ? 0.55f : 1f);
            Yaw -= mm.Relative.X * sens;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * sens * (Main.I.Settings.InvertY ? -1f : 1f), -1.5f, 1.5f);
        }
        else if (e.IsActionPressed("interact"))
        {
            // Consume the key: otherwise the same press would close the container or car it just opened.
            GetViewport().SetInputAsHandled();
            TryInteract();
        }
        else if (e.IsActionPressed("flashlight"))
        {
            Flashlight.Visible = !Flashlight.Visible;
            g.Audio.Play("click");
        }
        else if (e.IsActionPressed("slot1")) SetSlot(0);
        else if (e.IsActionPressed("slot2")) SetSlot(Slot == 1 ? 0 : 1);
        else if (e.IsActionPressed("slot3")) SetSlot(Slot == 2 ? 0 : 2);
    }

    private void TryInteract()
    {
        if (_target != null) { _target.Interact(this); return; }
        if (_waterTarget)
        {
            var g = GameWorld.I;
            Stats.Drink(35);
            Stats.Hurt(2f, "lake water", true);
            int n = Inventory.Count("empty_bottle"), filled = 0;
            for (int i = 0; i < n; i++)
            {
                Inventory.Remove("empty_bottle", 1);
                if (Inventory.Add("water", 1) == 1) filled++; else { Inventory.Add("empty_bottle", 1); break; }
            }
            g.Audio.Play("drink");
            g.Toast(filled > 0 ? $"You drink and fill {filled} bottle{(filled > 1 ? "s" : "")}." : "The lake water is cold and tastes of silt.");
        }
    }

    // ---- simulation ----------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var g = GameWorld.I;
        if (Stats.Dead) return;

        if (InVehicle)
        {
            GlobalPosition = Vehicle!.SeatPosition;
            Stats.Tick(dt, false, false);
            Prompt = null;
            return;
        }

        bool ui = g.UiOpen;
        var input = ui ? Vector2.Zero : Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        if (!ui)
        {
            var look = Input.GetVector("look_left", "look_right", "look_up", "look_down");
            if (look.LengthSquared() > 0.01f)
            {
                Yaw -= look.X * dt * 2.6f;
                _pitch = Mathf.Clamp(_pitch - look.Y * dt * 2.0f, -1.5f, 1.5f);
            }
        }

        bool wantCrouch = !ui && Input.IsActionPressed("crouch");
        if (_crouching && !wantCrouch && !HeadBlocked()) _crouching = false;
        else if (wantCrouch) _crouching = true;
        _crouch = Mathf.Lerp(_crouch, _crouching ? 1f : 0f, MathX.Damp(12f, dt));
        _capsule.Height = Mathf.Lerp(1.78f, 1.15f, _crouch);
        _shape.Position = new Vector3(0, _capsule.Height * 0.5f, 0);

        bool moving = input.LengthSquared() > 0.02f;
        bool sprinting = moving && !_crouching && !ui && Input.IsActionPressed("sprint") && Stats.Stamina > 1f && input.Y < 0.2f;
        Aiming = !ui && Slot == 2 && Input.IsActionPressed("aim");
        float speed = _crouching ? 1.9f : sprinting ? 6.4f : 3.7f;
        if (Aiming) speed *= 0.55f;
        bool wading = GlobalPosition.Y < WorldGenerator.WaterLevel - 0.35f;
        if (wading) speed *= 0.55f;
        if (Stats.Hunger <= 0 || Stats.Thirst <= 0) speed *= 0.8f;

        var wish = (GlobalTransform.Basis * new Vector3(input.X, 0, input.Y));
        wish.Y = 0;
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        var vel = Velocity;
        bool grounded = IsOnFloor();
        float accel = grounded ? 11f : 2.5f;
        var horiz = new Vector3(vel.X, 0, vel.Z).Lerp(wish * speed, MathX.Damp(accel, dt));
        vel.X = horiz.X; vel.Z = horiz.Z;
        if (grounded)
        {
            vel.Y = -1f;
            if (!ui && Input.IsActionJustPressed("jump") && !_crouching && Stats.Stamina > 6f)
            {
                vel.Y = 5.4f;
                Stats.Stamina -= 6f;
            }
        }
        else vel.Y -= Gravity * dt;
        Velocity = vel;
        MoveAndSlide();

        // Safety net: never fall out of the world if collision has not streamed in yet.
        float ground = g.World.Height(GlobalPosition.X, GlobalPosition.Z);
        if (GlobalPosition.Y < ground - 3f)
        {
            GlobalPosition = new Vector3(GlobalPosition.X, ground + 0.3f, GlobalPosition.Z);
            Velocity = Vector3.Zero;
        }

        Stats.Tick(dt, sprinting, moving);

        float hs = new Vector3(Velocity.X, 0, Velocity.Z).Length();
        if (grounded && hs > 0.6f)
        {
            _stepDistance += hs * dt;
            _bob += hs * dt * 3.3f;
            if (_stepDistance > (sprinting ? 2.5f : 1.9f))
            {
                _stepDistance = 0;
                g.Audio.Play(StepSound(), wading ? -4f : _crouching ? -16f : -9f, wading ? 0.7f : 1f);
            }
        }

        // camera
        _recoil = Mathf.Lerp(_recoil, 0f, MathX.Damp(9f, dt));
        float eye = Mathf.Lerp(EyeHeight, CrouchEye, _crouch) + MathF.Sin(_bob * 2f) * 0.022f * Mathf.Clamp(hs / 4f, 0f, 1.2f);
        _head.Position = new Vector3(0, eye, 0);
        _head.Rotation = new Vector3(_pitch + _recoil, 0, 0);
        float targetFov = Main.I.Settings.Fov * (Aiming ? 0.55f : sprinting ? 1.06f : 1f);
        Cam.Fov = Mathf.Lerp(Cam.Fov, targetFov, MathX.Damp(10f, dt));

        // tools
        _attackCooldown -= dt;
        _swing = Mathf.Lerp(_swing, 0f, MathX.Damp(8f, dt));
        _crowbar.Rotation = new Vector3(-0.5f - _swing * 1.5f, 0.25f + _swing * 0.5f, -0.2f);
        var rifleRest = Aiming ? new Vector3(0f, -0.105f, -0.3f) : new Vector3(0.2f, -0.2f, -0.42f);
        _rifle.Position = _rifle.Position.Lerp(rifleRest + new Vector3(0, 0, _recoil * 0.25f), MathX.Damp(14f, dt));
        _muzzle.LightEnergy = Mathf.Lerp(_muzzle.LightEnergy, 0f, MathX.Damp(30f, dt));
        if (!ui && Input.IsActionPressed("attack") && _attackCooldown <= 0f && Input.MouseMode == Input.MouseModeEnum.Captured) Attack();

        UpdateInteraction();
    }

    private bool HeadBlocked()
    {
        var q = PhysicsRayQueryParameters3D.Create(GlobalPosition + Vector3.Up * 1.0f, GlobalPosition + Vector3.Up * 1.95f, 1);
        return GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
    }

    private string StepSound()
    {
        var q = PhysicsRayQueryParameters3D.Create(GlobalPosition + Vector3.Up * 0.3f, GlobalPosition + Vector3.Down * 0.6f, 1);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node n && n.HasMeta("surface"))
        {
            string s = n.GetMeta("surface").AsString();
            if (s == "asphalt") return "step_gravel";
            if (s == "hard") return "step_hard";
        }
        float lat = MathF.Abs(GameWorld.I.World.Road.LateralDistance(GlobalPosition.X, GlobalPosition.Z));
        return lat < 7f ? "step_gravel" : "step_grass";
    }

    private void UpdateInteraction()
    {
        _target = null;
        _waterTarget = false;
        Prompt = null;
        if (GameWorld.I.UiOpen) return;
        var from = Cam.GlobalPosition;
        var dir = -Cam.GlobalTransform.Basis.Z;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 3.3f, InteractMask);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0)
        {
            LastHitPoint = hit["position"].AsVector3();
            var it = Interactable.Find(hit["collider"].AsGodotObject() as Node);
            if (it != null)
            {
                string? p = it.GetPrompt(this);
                if (p != null) { _target = it; Prompt = p; return; }
            }
        }
        // Lake: no collider, so test the view ray against the water plane.
        if (dir.Y < -0.25f && from.Y > WorldGenerator.WaterLevel)
        {
            float t = (WorldGenerator.WaterLevel - from.Y) / dir.Y;
            if (t < 3.2f)
            {
                var p = from + dir * t;
                if (GameWorld.I.World.Height(p.X, p.Z) < WorldGenerator.WaterLevel - 0.1f)
                {
                    _waterTarget = true;
                    Prompt = Inventory.Has("empty_bottle") ? "Drink and fill your bottles" : "Drink from the lake";
                }
            }
        }
    }

    // ---- combat --------------------------------------------------------------------------

    private void Attack()
    {
        var g = GameWorld.I;
        var from = Cam.GlobalPosition;
        var dir = -Cam.GlobalTransform.Basis.Z;
        if (Slot == 1)
        {
            _attackCooldown = 0.6f;
            _swing = 1f;
            Stats.Stamina = MathF.Max(0, Stats.Stamina - 4f);
            g.Audio.Play("swing", -6f);
            var hit = Cast(from, dir, 2.5f);
            if (hit.Count > 0)
            {
                var pos = hit["position"].AsVector3();
                g.Audio.Play3D("hit", pos, -4f);
                Interactable.FindDamageable(hit["collider"].AsGodotObject() as Node)?.Damage(34f, this, pos);
            }
        }
        else if (Slot == 2)
        {
            if (!Inventory.Remove("ammo", 1))
            {
                _attackCooldown = 0.5f;
                g.Audio.Play("click");
                g.Toast("No rifle rounds.");
                return;
            }
            _attackCooldown = 1.35f;
            _recoil = 0.09f;
            _muzzle.LightEnergy = 6f;
            g.Audio.Play("gunshot", -2f);
            float spread = Aiming ? 0.004f : 0.03f;
            spread += new Vector3(Velocity.X, 0, Velocity.Z).Length() * 0.006f;
            var b = Cam.GlobalTransform.Basis;
            dir = (dir + b.X * (float)GD.RandRange(-spread, spread) + b.Y * (float)GD.RandRange(-spread, spread)).Normalized();
            var hit = Cast(from, dir, 320f);
            if (hit.Count > 0)
            {
                var pos = hit["position"].AsVector3();
                g.Audio.Play3D("hit", pos, -8f);
                Interactable.FindDamageable(hit["collider"].AsGodotObject() as Node)?.Damage(100f, this, pos);
            }
            g.Encounters.OnLoudNoise(GlobalPosition, 170f);
        }
    }

    private Godot.Collections.Dictionary Cast(Vector3 from, Vector3 dir, float range)
    {
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * range, 1 | 4 | 8);
        q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
        return GetWorld3D().DirectSpaceState.IntersectRay(q);
    }

    public void Damage(float amount, Node3D? source, Vector3 hitPoint)
    {
        if (InVehicle) amount *= 0.4f; // the car body takes most of it
        Stats.Hurt(amount, "attack");
    }

    // ---- vehicle -------------------------------------------------------------------------

    public void EnterVehicle(VehicleController v)
    {
        Vehicle = v;
        Visible = false;
        CollisionLayer = 0;
        CollisionMask = 0;
        Velocity = Vector3.Zero;
        Flashlight.Visible = false;
        _crowbar.Visible = false;
        _rifle.Visible = false;
        Prompt = null;
        v.OnEnter(this);
        GameWorld.I.Audio.Play("door");
    }

    public void ExitVehicle()
    {
        if (Vehicle == null) return;
        var v = Vehicle;
        var basis = v.GlobalTransform.Basis.Orthonormalized();
        var candidates = new[] { new Vector3(-1.75f, 0.2f, -0.1f), new Vector3(1.75f, 0.2f, -0.1f), new Vector3(0, 0.2f, 3.4f), new Vector3(0, 1.9f, 0) };
        var space = GetWorld3D().DirectSpaceState;
        var pos = v.GlobalPosition + Vector3.Up * 2f;
        foreach (var c in candidates)
        {
            var p = v.GlobalPosition + basis * c;
            float ground = GameWorld.I.World.Height(p.X, p.Z);
            if (p.Y < ground + 0.1f) p.Y = ground + 0.1f;
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new CapsuleShape3D { Radius = 0.33f, Height = 1.6f },
                Transform = new Transform3D(Basis.Identity, p + Vector3.Up * 0.95f),
                CollisionMask = 1 | 4,
            };
            if (space.IntersectShape(q, 1).Count == 0) { pos = p; break; }
        }
        Vehicle = null;
        v.OnExit();
        GlobalPosition = pos;
        var fwd = -basis.Z;
        Yaw = MathF.Atan2(-fwd.X, -fwd.Z);
        _pitch = 0;
        Visible = true;
        CollisionLayer = 2;
        CollisionMask = 1 | 4 | 8;
        Cam.Current = true;
        SetSlot(Slot);
        GameWorld.I.Audio.Play("door");
    }

    public void LoadState(SaveData s)
    {
        GlobalPosition = new Vector3(s.PlayerPos[0], s.PlayerPos[1], s.PlayerPos[2]);
        Yaw = s.PlayerYaw;
        Stats.Health = s.Health; Stats.Hunger = s.Hunger; Stats.Thirst = s.Thirst; Stats.Stamina = s.Stamina;
        Inventory.Load(s.PlayerItems);
    }

    public void SaveState(SaveData s)
    {
        var p = GlobalPosition;
        s.PlayerPos = new[] { p.X, p.Y, p.Z };
        s.PlayerYaw = Yaw;
        s.Health = Stats.Health; s.Hunger = Stats.Hunger; s.Thirst = Stats.Thirst; s.Stamina = Stats.Stamina;
        s.PlayerItems = Inventory.Snapshot();
        s.InVehicle = InVehicle;
    }
}
