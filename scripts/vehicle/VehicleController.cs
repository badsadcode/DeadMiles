using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// The player's car. Physics is Godot's raycast VehicleBody3D with speed-sensitive steering,
/// a simple power curve and surface-dependent grip. On top of that sits the persistent
/// condition (fuel, engine, tires), the trunk, lights, cameras and a recovery option so a
/// physics mishap can never cost the player their only way forward.
/// </summary>
public partial class VehicleController : VehicleBody3D, IInteractable, IDamageable
{
    public const float TankSize = 45f;
    private const float ModelScale = 1.12f;
    private const float TopSpeed = 36f;          // m/s on asphalt with a healthy engine
    // VehicleBody3D drives towards +Z for positive engine force; the car's nose is -Z.
    private const float DriveSign = -1f;
    private const float SteerSign = 1f;

    public float Fuel = 9f, EngineHealth = 78f, TireHealth = 85f, Odometer;
    public bool EngineOn { get; private set; }
    public bool LightsOn { get; private set; }
    public readonly Inventory Trunk = new("Trunk", 90f);
    public PlayerController? Driver { get; private set; }
    public float Speed { get; private set; }           // signed, m/s along the nose
    public bool OnRoad { get; private set; }
    public bool FirstPerson { get; private set; } = true;
    public float WiperPhase { get; private set; }
    public Vector3 SeatPosition => GlobalTransform * new Vector3(-0.42f, 0.9f, 0f);

    private VehicleWheel3D[] _wheels = new VehicleWheel3D[4]; // FL, FR, RL, RR
    private Node3D _model = null!, _interior = null!, _rearAxle = null!, _steeringWheel = null!;
    private Node3D[] _wipers = new Node3D[2];
    private Camera3D _cockpitCam = null!, _chaseCam = null!;
    private SpotLight3D[] _headlights = new SpotLight3D[2];
    private StandardMaterial3D _headMat = null!, _tailMat = null!, _glassMat = null!;
    private OmniLight3D _domeLight = null!;
    private AudioStreamPlayer3D _engineAudio = null!, _tireAudio = null!, _gravelAudio = null!;
    private float _steer, _lookYaw, _lookPitch, _startTimer, _lastRecover = -100f, _stuckTime;
    private Vector3 _prevVelocity;
    private float _impactGrace = 1f;
    private bool _starting;
    private double _clock;

    public override void _Ready()
    {
        Mass = 1250f;
        CollisionLayer = 4;
        CollisionMask = 1 | 2 | 8;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0, 0.42f, 0.05f);
        // Replace (not add to) the project-wide default damping: drag is modelled explicitly below.
        LinearDampMode = DampMode.Replace;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0.35f;
        LinearDamp = 0f;
        ContinuousCd = true;
        CanSleep = false;

        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.95f, 0.62f, 4.5f) }, Position = new Vector3(0, 0.8f, 0) });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.7f, 0.55f, 2.6f) }, Position = new Vector3(0, 1.17f, 0.35f) });
        AddChild(new GpuParticlesCollisionBox3D { Size = new Vector3(2.0f, 1.5f, 4.4f), Position = new Vector3(0, 0.9f, 0) });

        BuildModel();
        BuildWheels();
        BuildInterior();
        BuildLightsAndCameras();
        BuildAudio();
    }

    // ---- construction --------------------------------------------------------------------

    private void BuildModel()
    {
        _model = new Node3D { Rotation = new Vector3(0, Mathf.Pi, 0), Scale = Vector3.One * ModelScale };
        AddChild(_model);
        _headMat = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.8f, 0.72f), EmissionEnabled = true, Emission = new Color(1f, 0.93f, 0.75f), EmissionEnergyMultiplier = 0f, Roughness = 0.3f };
        _tailMat = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.04f, 0.03f), EmissionEnabled = true, Emission = new Color(1f, 0.08f, 0.04f), EmissionEnergyMultiplier = 0f, Roughness = 0.4f };
        // The model is a single-sided shell with opaque glass. Render the shell double-sided so it
        // forms the cabin when seen from the driver's seat, and make the glass truly transparent:
        // dark from outside, almost clear from inside (see SetView).
        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.33f, 0.31f), Roughness = 0.55f, Metallic = 0.25f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _glassMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.03f, 0.045f, 0.05f, 0.9f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.1f, Metallic = 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        const string path = "res://assets/vehicles/SUV.glb";
        var scene = ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : null;
        if (scene == null)
        {
            Log.Warn("Vehicle", $"Missing {path}; using a placeholder body");
            _model.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.9f, 4.0f) }, Position = new Vector3(0, 0.75f, 0), MaterialOverride = paint });
            return;
        }
        var car = scene.Instantiate<Node3D>();
        _model.AddChild(car);
        foreach (var n in car.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            if (mi.Mesh == null) continue;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                string name = mi.Mesh.SurfaceGetMaterial(s)?.ResourceName ?? "";
                var src = mi.Mesh.SurfaceGetMaterial(s);
                if (name == "White") mi.SetSurfaceOverrideMaterial(s, paint);
                else if (name.Contains("Window")) mi.SetSurfaceOverrideMaterial(s, _glassMat);
                else if (name == "Headlights") mi.SetSurfaceOverrideMaterial(s, _headMat);
                else if (name == "TailLights") mi.SetSurfaceOverrideMaterial(s, _tailMat);
                else if (src is BaseMaterial3D b)
                {
                    var two = (BaseMaterial3D)b.Duplicate();
                    two.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                    mi.SetSurfaceOverrideMaterial(s, two);
                }
            }
        }
    }

    private void BuildWheels()
    {
        // Wheel layout taken from the model (scaled): nose is -Z, left is -X.
        float fx = 0.77f * ModelScale, fz = -1.32f * ModelScale, rz = 1.25f * ModelScale;
        float radius = 0.325f * ModelScale;
        const float rest = 0.22f;
        float y = radius + rest - 0.09f;
        var spots = new[] { new Vector3(-fx, y, fz), new Vector3(fx, y, fz), new Vector3(-fx, y, rz), new Vector3(fx, y, rz) };
        Node? car = _model.GetChildCount() > 0 ? _model.GetChild(0) : null;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            var w = new VehicleWheel3D
            {
                Position = spots[i],
                UseAsSteering = front,
                UseAsTraction = true,
                WheelRadius = radius,
                WheelRestLength = rest,
                SuspensionTravel = 0.2f,
                SuspensionStiffness = 30f,
                SuspensionMaxForce = 18000f,
                DampingCompression = 2.6f,
                DampingRelaxation = 3.6f,
                WheelFrictionSlip = front ? 3.2f : 3.0f,
                WheelRollInfluence = 0.07f,
            };
            AddChild(w);
            _wheels[i] = w;
            if (front && car != null)
            {
                string key = i == 0 ? "FrontLeft" : "FrontRight";
                foreach (var n in car.GetChildren())
                {
                    if (n is MeshInstance3D mi && mi.Name.ToString().Contains(key))
                    {
                        mi.GetParent().RemoveChild(mi);
                        w.AddChild(mi);
                        mi.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi).Scaled(Vector3.One * ModelScale), Vector3.Zero);
                    }
                }
            }
        }
        // Both rear wheels are one mesh in the model: carry it on a helper that follows the axle.
        _rearAxle = new Node3D();
        AddChild(_rearAxle);
        if (car != null)
        {
            foreach (var n in car.GetChildren())
            {
                if (n is MeshInstance3D mi && mi.Name.ToString().Contains("Back"))
                {
                    mi.GetParent().RemoveChild(mi);
                    _rearAxle.AddChild(mi);
                    mi.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi).Scaled(Vector3.One * ModelScale), Vector3.Zero);
                }
            }
        }
    }

    private void BuildInterior()
    {
        // Only what the body shell does not provide: dashboard, wheel, seats, floor.
        _interior = new Node3D();
        AddChild(_interior);
        var dark = PoiBuilder.Mat("cabin", 0.07f, 0.07f, 0.075f, 0.9f);
        var trim = PoiBuilder.Mat("cabintrim", 0.17f, 0.15f, 0.13f, 0.85f);
        void Box(Vector3 c, Vector3 size, Material m, Vector3 rot = default)
            => _interior.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, Position = c, Rotation = rot, MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        Box(new Vector3(0, 0.80f, -0.98f), new Vector3(1.8f, 0.14f, 0.5f), dark);               // dashboard top
        Box(new Vector3(0, 0.60f, -0.82f), new Vector3(1.8f, 0.40f, 0.3f), trim);               // lower dash
        Box(new Vector3(-0.42f, 0.89f, -0.84f), new Vector3(0.36f, 0.05f, 0.14f), dark);        // instrument hood
        Box(new Vector3(-0.42f, 0.78f, 0.42f), new Vector3(0.5f, 0.74f, 0.13f), trim);          // seat backs
        Box(new Vector3(0.42f, 0.78f, 0.42f), new Vector3(0.5f, 0.74f, 0.13f), trim);
        Box(new Vector3(0, 0.40f, 0.2f), new Vector3(1.8f, 0.06f, 3.4f), dark);                 // floor

        _steeringWheel = new Node3D { Position = new Vector3(-0.42f, 0.84f, -0.6f), Rotation = new Vector3(-1.15f, 0, 0) };
        _steeringWheel.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.135f, OuterRadius = 0.16f, Rings = 20, RingSegments = 8 }, MaterialOverride = dark, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _steeringWheel.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.27f, 0.02f, 0.04f) }, MaterialOverride = dark, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _interior.AddChild(_steeringWheel);

        // a faint dome light so the cabin never reads as a black hole
        _domeLight = new OmniLight3D { Position = new Vector3(0, 1.25f, 0.1f), OmniRange = 2.4f, LightEnergy = 0.35f, LightColor = new Color(0.9f, 0.85f, 0.75f), ShadowEnabled = false };
        _interior.AddChild(_domeLight);

        for (int i = 0; i < 2; i++)
        {
            var pivot = new Node3D { Position = new Vector3(i == 0 ? -0.5f : 0.3f, 0.97f, -1.12f), Rotation = new Vector3(0.95f, 0, 0) };
            var arm = new Node3D();
            arm.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.016f, 0.42f, 0.012f) }, Position = new Vector3(0, 0.21f, 0), MaterialOverride = dark, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            pivot.AddChild(arm);
            AddChild(pivot);
            _wipers[i] = arm;
        }
        _interior.Visible = false;
    }

    private void BuildLightsAndCameras()
    {
        for (int i = 0; i < 2; i++)
        {
            var l = new SpotLight3D
            {
                Position = new Vector3(i == 0 ? -0.68f : 0.68f, 0.78f, -2.38f),
                Rotation = new Vector3(-0.06f, i == 0 ? 0.03f : -0.03f, 0),
                SpotRange = 70f, SpotAngle = 33f, SpotAngleAttenuation = 0.8f, SpotAttenuation = 0.55f,
                LightEnergy = 9f, LightColor = new Color(1f, 0.92f, 0.76f), LightVolumetricFogEnergy = 5f,
                ShadowEnabled = i == 0, Visible = false,
            };
            AddChild(l);
            _headlights[i] = l;
        }
        _cockpitCam = new Camera3D { Position = new Vector3(-0.42f, 1.31f, -0.12f), Near = 0.05f, Far = 1600f, Fov = Main.I.Settings.Fov };
        AddChild(_cockpitCam);
        _chaseCam = new Camera3D { TopLevel = true, Near = 0.1f, Far = 1600f, Fov = Main.I.Settings.Fov };
        AddChild(_chaseCam);
    }

    private void BuildAudio()
    {
        var audio = Main.I.Audio;
        AudioStreamPlayer3D Loop(string name) => new()
        {
            Stream = audio.Get(name), Bus = "Sfx", VolumeDb = -80f, UnitSize = 7f, MaxDistance = 140f,
            Position = new Vector3(0, 0.8f, -1.2f), Autoplay = true,
        };
        AddChild(_engineAudio = Loop("engine"));
        AddChild(_tireAudio = Loop("tires_asphalt"));
        AddChild(_gravelAudio = Loop("tires_gravel"));
    }

    // ---- interaction ---------------------------------------------------------------------

    public string? GetPrompt(PlayerController player)
    {
        if (player.InVehicle) return null;
        var local = GlobalTransform.AffineInverse() * player.LastHitPoint;
        if (local.Z > 1.5f) return "Open the trunk";
        if (local.Z < -1.5f) return $"Hood: engine {EngineHealth:0}%, tires {TireHealth:0}%, fuel {Fuel:0} L";
        return "Get in";
    }

    public void Interact(PlayerController player)
    {
        var local = GlobalTransform.AffineInverse() * player.LastHitPoint;
        if (local.Z > 1.5f) GameWorld.I.OpenTrunk();
        else if (local.Z < -1.5f) GameWorld.I.Toast(EngineHealth < 40 ? "The engine needs parts. Use engine parts from your backpack." : "Use fuel cans, engine parts or tire kits from your backpack while standing here.");
        else player.EnterVehicle(this);
    }

    public void OnEnter(PlayerController p)
    {
        Driver = p;
        _lookYaw = 0; _lookPitch = 0;
        SetView(FirstPerson);
        if (!EngineOn)
            GameWorld.I.Hud.Hint($"[{InputSetup.KeyName("engine_toggle")}] engine   [{InputSetup.KeyName("flashlight")}] headlights   [{InputSetup.KeyName("camera_toggle")}] camera   [{InputSetup.KeyName("inventory")}] trunk");
        _engineAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled;
        _tireAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled;
        _gravelAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled;
    }

    public void OnExit()
    {
        Driver = null;
        _interior.Visible = false;
        _glassMat.AlbedoColor = new Color(0.03f, 0.045f, 0.05f, 0.9f);
        _cockpitCam.Current = false;
        _chaseCam.Current = false;
        EngineForce = 0;
        Brake = 40f;
        _engineAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
        _tireAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
        _gravelAudio.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
    }

    public void SetViewForTest(bool firstPerson) { if (Driver != null) SetView(firstPerson); }

    private void SetView(bool firstPerson)
    {
        FirstPerson = firstPerson;
        _interior.Visible = firstPerson;
        _glassMat.AlbedoColor = new Color(0.03f, 0.045f, 0.05f, firstPerson ? 0.06f : 0.9f);
        if (firstPerson) _cockpitCam.Current = true;
        else
        {
            _chaseCam.GlobalTransform = ChaseTarget();
            _chaseCam.Current = true;
        }
    }

    public void Damage(float amount, Node3D? source, Vector3 hitPoint)
    {
        EngineHealth = MathF.Max(0, EngineHealth - amount * 0.03f);
        Driver?.Damage(amount * 0.35f, source, hitPoint);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Driver == null || GameWorld.I.UiOpen) return;
        var g = GameWorld.I;
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float sens = Main.I.Settings.MouseSensitivity * 0.0022f;
            _lookYaw = Mathf.Clamp(_lookYaw - mm.Relative.X * sens, -2.4f, 2.4f);
            _lookPitch = Mathf.Clamp(_lookPitch - mm.Relative.Y * sens * (Main.I.Settings.InvertY ? -1f : 1f), -0.7f, 0.6f);
        }
        else if (e.IsActionPressed("interact"))
        {
            GetViewport().SetInputAsHandled();
            if (MathF.Abs(Speed) > 3f) g.Toast("Stop the car before getting out.");
            else Driver.ExitVehicle();
        }
        else if (e.IsActionPressed("engine_toggle")) ToggleEngine();
        else if (e.IsActionPressed("flashlight")) SetLights(!LightsOn);
        else if (e.IsActionPressed("horn"))
        {
            g.Audio.Play("horn", -4f);
            g.Encounters.OnLoudNoise(GlobalPosition, 90f);
        }
        else if (e.IsActionPressed("camera_toggle")) SetView(!FirstPerson);
        else if (e.IsActionPressed("recover")) Recover();
    }

    public void SetLights(bool on)
    {
        LightsOn = on;
        foreach (var l in _headlights) l.Visible = on;
        _headMat.EmissionEnergyMultiplier = on ? 1.6f : 0f;
        Main.I.Audio.Play("click");
    }

    public void ToggleEngine()
    {
        var g = GameWorld.I;
        if (_starting) return;
        if (EngineOn)
        {
            EngineOn = false;
            g.Audio.Play("click");
            return;
        }
        if (Fuel <= 0.01f) { g.Audio.Play("engine_fail"); g.Toast("The tank is dry. Find fuel."); return; }
        if (EngineHealth <= 1f) { g.Audio.Play("engine_fail"); g.Toast("The engine is dead. It needs parts."); return; }
        if (EngineHealth < 25f && GD.Randf() < 0.45f) { g.Audio.Play("engine_fail"); g.Toast("It coughs and dies. Try again."); return; }
        g.Audio.Play("engine_start", -2f);
        _starting = true;
        _startTimer = 1.0f;
    }

    /// <summary>Starts the engine immediately (loading a save, automated tests).</summary>
    public void ForceEngine(bool on) { EngineOn = on && Fuel > 0 && EngineHealth > 1; _starting = false; }

    /// <summary>Unsticks the car. First press rights it in place; a second press soon after tows it to the road.</summary>
    public void Recover()
    {
        var g = GameWorld.I;
        float now = (float)_clock;
        var p = GlobalPosition;
        bool toRoad = now - _lastRecover < 12f || p.Y < WorldGenerator.WaterLevel + 0.1f;
        _lastRecover = now;
        float yaw;
        if (toRoad)
        {
            if (g.World.Map != null)
            {
                var ahead = -GlobalTransform.Basis.Z;
                yaw = MathF.Atan2(-ahead.X, -ahead.Z);
                if (g.World.Map.NearestRoad(p.X, p.Z, 400f, 1.6f, out var onRoad, out float roadYaw)) { p = onRoad; yaw = roadYaw; }
                else p.Y = g.World.Height(p.X, p.Z);
            }
            else
            {
                p = g.World.Road.PointAt(p.Z, 1.9f);
                yaw = g.World.Road.YawAt(p.Z);
            }
            g.Toast("You drag the car back onto the road.");
        }
        else
        {
            var fwd = -GlobalTransform.Basis.Z;
            yaw = MathF.Atan2(-fwd.X, -fwd.Z);
            p.Y = g.World.Height(p.X, p.Z);
            g.Toast("You rock the car back onto its wheels. (Press again to pull it to the road.)");
        }
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), p + Vector3.Up * 0.6f);
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        _prevVelocity = Vector3.Zero;
        _impactGrace = 1f;
        _stuckTime = 0;
    }

    // ---- simulation ----------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        _clock += delta;
        var g = GameWorld.I;

        // Never simulate over ground that has no collision loaded.
        bool solid = g.Streamer.IsSolidAt(GlobalPosition);
        if (Freeze == solid)
        {
            Freeze = !solid;
            _impactGrace = 0.6f; // waking up is not a crash
        }
        if (!solid) return;
        _impactGrace -= dt;

        var basis = GlobalTransform.Basis;
        Speed = LinearVelocity.Dot(-basis.Z);
        float absSpeed = MathF.Abs(Speed);

        if (_starting)
        {
            _startTimer -= dt;
            if (_startTimer <= 0) { _starting = false; EngineOn = true; }
        }

        bool driving = Driver != null && !g.UiOpen && !Driver.Stats.Dead;
        float throttle = driving ? Input.GetActionStrength("accelerate") : 0f;
        float brakeIn = driving ? Input.GetActionStrength("brake") : 0f;
        float steerIn = driving ? Input.GetActionStrength("steer_left") - Input.GetActionStrength("steer_right") : 0f;
        bool handbrake = driving ? Input.IsActionPressed("handbrake") : Driver == null;
        if (g.Autopilot != null && Driver != null) g.Autopilot(this, ref throttle, ref brakeIn, ref steerIn);

        // surface under the driven wheels
        int contacts = 0, asphalt = 0;
        foreach (var w in _wheels)
        {
            if (!w.IsInContact()) continue;
            contacts++;
            if (w.GetContactBody() is Node n && n.HasMeta("surface") && n.GetMeta("surface").AsString() != "ground") asphalt++;
        }
        OnRoad = contacts > 0 && asphalt * 2 >= contacts;

        // steering: quick at parking speed, calm at speed
        float maxSteer = Mathf.Lerp(0.56f, 0.085f, MathX.Smooth(0f, 30f, absSpeed));
        float target = steerIn * maxSteer;
        float rate = MathF.Abs(target) < MathF.Abs(_steer) ? 4.5f : 2.6f;
        _steer = MathX.Approach(_steer, target, rate * dt * (0.25f + maxSteer));
        Steering = _steer * SteerSign;

        // engine and brakes
        float power = EngineOn ? 0.4f + 0.6f * EngineHealth / 100f : 0f;
        float top = TopSpeed * (0.55f + 0.45f * EngineHealth / 100f) * (OnRoad ? 1f : 0.62f);
        float force = 0f, brake = 0f;
        if (throttle > 0.02f)
        {
            if (Speed < -1.0f) brake = 34f * throttle;
            else force = 5600f * throttle * power * Mathf.Clamp(1f - Speed / top, 0f, 1f) * (Speed < 6f ? 1.25f : 1f);
        }
        if (brakeIn > 0.02f)
        {
            if (Speed > 1.2f) { brake = 38f * brakeIn; force = 0f; }
            else force = -2600f * brakeIn * power * Mathf.Clamp(1f + Speed / 9f, 0f, 1f);
        }
        if (throttle < 0.02f && brakeIn < 0.02f)
        {
            brake = 1.6f + (EngineOn ? 0f : 1.5f); // engine braking / rolling drag
            if (absSpeed < 1.2f) brake = 45f;       // hold on hills: a stopped car stays where you left it
        }
        if (handbrake) { force = 0; brake = MathF.Max(brake, Driver == null ? 60f : 4f); }

        float grip = g.Weather.GripFactor * (0.65f + 0.35f * TireHealth / 100f) * (OnRoad ? 1f : 0.8f);
        for (int i = 0; i < 4; i++)
        {
            var w = _wheels[i];
            bool rear = i >= 2;
            w.EngineForce = force * 0.25f * DriveSign;
            w.Brake = handbrake && rear ? 70f : brake * (rear ? 0.75f : 1f);
            w.WheelFrictionSlip = (rear ? 3.0f : 3.2f) * grip * (handbrake && rear ? 0.45f : 1f);
        }

        // air and off-road drag
        var v = LinearVelocity;
        ApplyCentralForce(-v * v.Length() * 0.42f);
        if (!OnRoad && contacts > 0) ApplyCentralForce(-v * 55f);

        // condition
        float dist = absSpeed * dt;
        Odometer += dist;
        TireHealth = MathF.Max(0, TireHealth - dist * (OnRoad ? 0.0009f : 0.0035f));
        if (EngineOn)
        {
            Fuel -= dt * (0.0025f + MathF.Abs(force) / 5600f * 0.026f);
            if (Fuel <= 0)
            {
                Fuel = 0;
                EngineOn = false;
                g.Toast("The engine sputters out. The tank is empty.");
            }
            if (EngineHealth <= 1f) { EngineOn = false; g.Toast("The engine seizes with a bang."); }
        }

        // hard impacts hurt the engine; scrapes and kerbs do not
        float dv = (v - _prevVelocity).Length();
        _prevVelocity = v;
        if (dv > 6.5f && _impactGrace <= 0f)
        {
            float dmg = MathF.Min(30f, (dv - 6.5f) * 3.2f);
            EngineHealth = MathF.Max(3f, EngineHealth - dmg);
            g.Audio.Play3D("hit", GlobalPosition, 2f, 0.6f);
            Driver?.Stats.Hurt(MathF.Min(25f, (dv - 6.5f) * 2f), "crash");
            g.Hud.Shake(0.5f);
        }

        // offer recovery when the car is on its side or roof for a moment
        if (Driver != null && (basis.Y.Y < 0.35f || (EngineOn && throttle > 0.5f && absSpeed < 0.4f && contacts < 2)))
        {
            _stuckTime += dt;
            if (_stuckTime > 2.5f) { g.Hud.Hint("Press [R] to recover the car"); _stuckTime = -6f; }
        }
        else if (_stuckTime > 0) _stuckTime = 0;

        if (GlobalPosition.Y < g.World.Height(GlobalPosition.X, GlobalPosition.Z) - 4f) Recover();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var g = GameWorld.I;

        // rear axle visual follows the two rear wheels
        var rl = _wheels[2].Transform;
        var rr = _wheels[3].Transform;
        _rearAxle.Transform = new Transform3D(rl.Basis, (rl.Origin + rr.Origin) * 0.5f);

        _steeringWheel.Rotation = new Vector3(-1.15f, 0, 0);
        _steeringWheel.RotateObjectLocal(Vector3.Up, _steer * 5.5f);

        // wipers run while it rains and the engine is on
        bool wipe = EngineOn && g.Weather.Rain > 0.08f;
        if (wipe) WiperPhase += dt * (g.Weather.Rain > 0.6f ? 1.5f : 0.85f);
        else WiperPhase = Mathf.Lerp(WiperPhase, MathF.Round(WiperPhase), MathX.Damp(4f, dt));
        float sweep = (0.5f - 0.5f * MathF.Cos(WiperPhase * Mathf.Tau)) * 1.75f;
        _wipers[0].Rotation = new Vector3(0, 0, 1.2f - sweep);
        _wipers[1].Rotation = new Vector3(0, 0, 1.2f - sweep);

        float brakeGlow = Driver != null && Input.IsActionPressed("brake") && Speed > 0.5f ? 5f : LightsOn ? 1.2f : 0f;
        _tailMat.EmissionEnergyMultiplier = brakeGlow;

        // engine note: fake gears so the pitch rises and drops as you accelerate
        float abs = MathF.Abs(Speed);
        float[] gears = { 0f, 7f, 14f, 22f, 30f, 42f };
        int gear = 0;
        while (gear < gears.Length - 2 && abs > gears[gear + 1]) gear++;
        float frac = Mathf.Clamp((abs - gears[gear]) / (gears[gear + 1] - gears[gear]), 0f, 1f);
        float load = Driver != null ? Input.GetActionStrength("accelerate") : 0f;
        float pitch = EngineOn ? 0.55f + frac * 0.6f + gear * 0.06f + load * 0.1f : 0.4f;
        _engineAudio.PitchScale = Mathf.Lerp(_engineAudio.PitchScale, pitch, MathX.Damp(6f, dt));
        float inside = Driver != null && FirstPerson ? -9f : -5f;
        float engineDb = EngineOn || _starting ? inside + load * 3f : -80f;
        _engineAudio.VolumeDb = Mathf.Lerp(_engineAudio.VolumeDb, engineDb, MathX.Damp(8f, dt));
        float roll = Mathf.Clamp(abs / 25f, 0f, 1f);
        _tireAudio.VolumeDb = Mathf.Lerp(_tireAudio.VolumeDb, roll > 0.03f && OnRoad ? Mathf.LinearToDb(roll * 0.45f) : -80f, MathX.Damp(6f, dt));
        _gravelAudio.VolumeDb = Mathf.Lerp(_gravelAudio.VolumeDb, roll > 0.03f && !OnRoad ? Mathf.LinearToDb(MathF.Min(1f, roll * 1.6f) * 0.5f) : -80f, MathX.Damp(6f, dt));
        _tireAudio.PitchScale = 0.7f + roll * 0.7f;
        _gravelAudio.PitchScale = 0.75f + roll * 0.5f;

        if (Driver == null) return;
        if (!g.UiOpen)
        {
            var look = Input.GetVector("look_left", "look_right", "look_up", "look_down");
            _lookYaw = Mathf.Clamp(_lookYaw - look.X * dt * 2.4f, -2.4f, 2.4f);
            _lookPitch = Mathf.Clamp(_lookPitch - look.Y * dt * 1.8f, -0.7f, 0.6f);
        }
        if (FirstPerson)
        {
            _cockpitCam.Rotation = new Vector3(_lookPitch - 0.07f, _lookYaw, 0);
            _cockpitCam.Fov = Main.I.Settings.Fov;
        }
        else
        {
            var want = ChaseTarget();
            var cur = _chaseCam.GlobalTransform;
            var pos = cur.Origin.Lerp(want.Origin, MathX.Damp(5f, dt));
            float ground = g.World.Height(pos.X, pos.Z) + 0.6f;
            if (pos.Y < ground) pos.Y = ground;
            _chaseCam.GlobalPosition = pos;
            _chaseCam.LookAt(GlobalPosition + Vector3.Up * 1.3f - GlobalTransform.Basis.Z * 2f, Vector3.Up);
        }
    }

    private Transform3D ChaseTarget()
    {
        var b = GlobalTransform.Basis;
        var fwd = -b.Z; fwd.Y = 0;
        if (fwd.LengthSquared() < 0.01f) fwd = Vector3.Forward;
        fwd = fwd.Normalized().Rotated(Vector3.Up, _lookYaw);
        var pos = GlobalPosition - fwd * 7.2f + Vector3.Up * (2.9f - _lookPitch * 3f);
        return new Transform3D(Basis.Identity, pos);
    }

    // ---- persistence ---------------------------------------------------------------------

    public void SaveState(SaveData s)
    {
        s.HasVehicle = true;
        var p = GlobalPosition;
        var q = GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();
        s.VehiclePos = new[] { p.X, p.Y, p.Z };
        s.VehicleRot = new[] { q.X, q.Y, q.Z, q.W };
        s.Fuel = Fuel; s.EngineHealth = EngineHealth; s.TireHealth = TireHealth;
        s.Headlights = LightsOn; s.Odometer = Odometer;
        s.Trunk = Trunk.Snapshot();
    }

    public void LoadState(SaveData s)
    {
        var q = new Quaternion(s.VehicleRot[0], s.VehicleRot[1], s.VehicleRot[2], s.VehicleRot[3]).Normalized();
        GlobalTransform = new Transform3D(new Basis(q), new Vector3(s.VehiclePos[0], s.VehiclePos[1], s.VehiclePos[2]));
        Fuel = s.Fuel; EngineHealth = s.EngineHealth; TireHealth = s.TireHealth; Odometer = s.Odometer;
        Trunk.Load(s.Trunk);
        if (s.Headlights) SetLights(true);
        LinearVelocity = Vector3.Zero;
        AngularVelocity = Vector3.Zero;
        _prevVelocity = Vector3.Zero;
        _impactGrace = 1.5f;
    }
}
