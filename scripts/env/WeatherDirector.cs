using System;
using Godot;

namespace DeadMiles;

public enum WeatherState { Clear, Overcast, Drizzle, HeavyRain, Fog, Storm }

/// <summary>
/// Weather state machine. Each state is just a set of targets (cloud, rain, fog, wind) that the
/// live values ease towards, so every transition is gradual. The live values drive the sky,
/// fog, rain particles, ground wetness, foliage sway, audio and tyre grip.
/// </summary>
public partial class WeatherDirector : Node3D
{
    public WeatherState State { get; private set; } = WeatherState.Clear;
    public float Timer = 300f;

    // live, smoothed values (0..1)
    public float Cloud = 0.15f, Rain, Fog, Wind = 0.2f, Wetness, Flash;

    private GpuParticles3D _rain = null!, _leaves = null!, _fireflies = null!;
    private StandardMaterial3D _rainMat = null!;
    private ParticleProcessMaterial _rainProcess = null!;
    private readonly RandomNumberGenerator _rng = new();
    private double _lightningTimer = 8;
    private Vector3 _lastCam;

    /// <summary>0..1 daylight, set by the day/night cycle so rain streaks dim at night.</summary>
    public float Daylight = 1f;

    private static (float cloud, float rain, float fog, float wind) Targets(WeatherState s) => s switch
    {
        WeatherState.Clear => (0.15f, 0f, 0f, 0.18f),
        WeatherState.Overcast => (0.85f, 0f, 0.12f, 0.35f),
        WeatherState.Drizzle => (0.92f, 0.3f, 0.28f, 0.35f),
        WeatherState.HeavyRain => (1f, 0.85f, 0.4f, 0.6f),
        WeatherState.Fog => (0.6f, 0f, 1f, 0.08f),
        _ => (1f, 1f, 0.45f, 1f),
    };

    public override void _Ready()
    {
        _rng.Randomize();
        _rainProcess = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(15f, 0.5f, 15f),
            Direction = new Vector3(0, -1, 0),
            Spread = 2f,
            InitialVelocityMin = 21f,
            InitialVelocityMax = 27f,
            Gravity = new Vector3(0, -3f, 0),
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
        };
        _rainMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.7f, 0.76f, 0.85f, 0.3f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // streaks right in front of the lens would fill the screen
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = 0.8f,
            DistanceFadeMaxDistance = 2.6f,
        };
        _rain = new GpuParticles3D
        {
            Amount = 5500,
            Lifetime = 0.75f,
            ProcessMaterial = _rainProcess,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.014f, 0.6f), Material = _rainMat },
            TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity,
            LocalCoords = false,
            Emitting = false,
            CollisionBaseSize = 0.05f,
            VisibilityAabb = new Aabb(new Vector3(-24, -30, -24), new Vector3(48, 40, 48)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(_rain);
        BuildAmbientParticles();
    }

    public void Restore(int state, float timer, float wetness)
    {
        State = (WeatherState)Math.Clamp(state, 0, 5);
        Timer = timer;
        Wetness = wetness;
        var t = Targets(State);
        Cloud = t.cloud; Rain = t.rain; Fog = t.fog; Wind = t.wind;
    }

    public void Force(WeatherState s, bool instant = false)
    {
        State = s;
        Timer = _rng.RandfRange(200f, 400f);
        if (instant)
        {
            var t = Targets(s);
            Cloud = t.cloud; Rain = t.rain; Fog = t.fog; Wind = t.wind;
            Wetness = t.rain > 0.1f ? 1f : 0f;
        }
        Log.Info("WeatherDirector", $"Weather set to {s}");
    }

    /// <summary>Fast-forwards the weather (used when the player sleeps).</summary>
    public void Skip(float seconds)
    {
        Timer -= seconds;
        int guard = 0;
        while (Timer <= 0 && guard++ < 50)
        {
            float carry = Timer;
            Advance();
            Timer += carry;
        }
        var t = Targets(State);
        Cloud = t.cloud; Rain = t.rain; Fog = t.fog; Wind = t.wind;
        Wetness = t.rain > 0.1f ? 1f : MathF.Max(0f, Wetness - seconds / 260f);
    }

    private void Advance()
    {
        float r = _rng.Randf();
        var next = State switch
        {
            WeatherState.Clear => r < 0.55f ? WeatherState.Overcast : r < 0.78f ? WeatherState.Fog : WeatherState.Drizzle,
            WeatherState.Overcast => r < 0.4f ? WeatherState.Clear : r < 0.7f ? WeatherState.Drizzle : r < 0.85f ? WeatherState.HeavyRain : WeatherState.Fog,
            WeatherState.Drizzle => r < 0.4f ? WeatherState.Overcast : r < 0.75f ? WeatherState.HeavyRain : r < 0.9f ? WeatherState.Clear : WeatherState.Fog,
            WeatherState.HeavyRain => r < 0.4f ? WeatherState.Drizzle : r < 0.7f ? WeatherState.Storm : WeatherState.Overcast,
            WeatherState.Fog => r < 0.5f ? WeatherState.Clear : r < 0.9f ? WeatherState.Overcast : WeatherState.Drizzle,
            _ => r < 0.5f ? WeatherState.HeavyRain : r < 0.8f ? WeatherState.Drizzle : WeatherState.Overcast,
        };
        State = next;
        Timer = next switch
        {
            WeatherState.Clear => _rng.RandfRange(300f, 620f),
            WeatherState.Storm => _rng.RandfRange(110f, 220f),
            _ => _rng.RandfRange(160f, 380f),
        };
        Log.Info("WeatherDirector", $"Weather changing to {next} for {Timer:0}s");
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        Timer -= dt;
        if (Timer <= 0) Advance();

        var t = Targets(State);
        float k = MathX.Damp(0.04f, dt);
        Cloud = Mathf.Lerp(Cloud, t.cloud, k);
        Rain = Mathf.Lerp(Rain, t.rain, k);
        Fog = Mathf.Lerp(Fog, t.fog, k);
        // gusts
        float gust = 0.85f + 0.15f * MathF.Sin((float)Time.GetTicksMsec() * 0.0004f) + 0.1f * MathF.Sin((float)Time.GetTicksMsec() * 0.0011f);
        Wind = Mathf.Lerp(Wind, t.wind * gust, MathX.Damp(0.3f, dt));

        // Ground gets wet within a minute of rain and takes several minutes to dry.
        if (Rain > 0.05f) Wetness = MathF.Min(1f, Wetness + dt * Rain / 45f);
        else Wetness = MathF.Max(0f, Wetness - dt / 260f);

        RenderingServer.GlobalShaderParameterSet("wetness", Wetness);
        RenderingServer.GlobalShaderParameterSet("wind_strength", Wind);

        // lightning
        Flash = MathF.Max(0f, Flash - dt * 5f);
        if (State == WeatherState.Storm && Rain > 0.6f)
        {
            _lightningTimer -= delta;
            if (_lightningTimer <= 0)
            {
                _lightningTimer = _rng.RandfRange(5f, 17f);
                Flash = 1f;
                // a second flicker shortly after
                GetTree().CreateTimer(_rng.RandfRange(0.08f, 0.2f)).Timeout += () => Flash = MathF.Max(Flash, 0.7f);
                GameWorld.I?.Audio.Thunder(_rng.RandfRange(0.4f, 3.5f));
            }
        }

        UpdateRain(dt);
    }

    private void UpdateRain(float dt)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var cp = cam.GlobalPosition;
        var vel = dt > 0 ? (cp - _lastCam) / dt : Vector3.Zero;
        if (vel.Length() > 80f) vel = Vector3.Zero; // teleport
        _lastCam = cp;
        // Lead the emitter so there is rain ahead of a moving car, not behind it.
        _rain.GlobalPosition = cp + new Vector3(0, 9f, 0) + new Vector3(vel.X, 0, vel.Z) * 0.35f;
        bool on = Rain > 0.03f;
        if (_rain.Emitting != on) _rain.Emitting = on;
        _rain.AmountRatio = Mathf.Clamp(Rain * 1.1f, 0.05f, 1f);
        _rainProcess.Direction = new Vector3(Wind * 0.22f, -1f, Wind * 0.1f).Normalized();
        float lum = 0.1f + 0.9f * Daylight;
        _rainMat.AlbedoColor = new Color(0.7f * lum, 0.76f * lum, 0.85f * lum, 0.16f + 0.2f * Rain);
        UpdateAmbientParticles(cp, vel);
    }

    /// <summary>Leaves coming off the trees in the wind, and fireflies over the grass at night.</summary>
    private void BuildAmbientParticles()
    {
        var leafColors = new Gradient();
        leafColors.SetColor(0, new Color(0.75f, 0.28f, 0.08f));
        leafColors.SetColor(1, new Color(0.85f, 0.62f, 0.12f));
        leafColors.AddPoint(0.5f, new Color(0.62f, 0.16f, 0.08f));
        var leafProcess = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(16f, 3.5f, 16f),
            Direction = new Vector3(1f, -0.35f, 0.4f),
            Spread = 35f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 2.2f,
            Gravity = new Vector3(0, -0.9f, 0),
            AngularVelocityMin = -220f,
            AngularVelocityMax = 220f,
            AngleMin = 0f,
            AngleMax = 360f,
            ScaleMin = 0.6f,
            ScaleMax = 1.4f,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 1.4f,
            TurbulenceNoiseScale = 3.5f,
            ColorInitialRamp = new GradientTexture1D { Gradient = leafColors },
            CollisionMode = ParticleProcessMaterial.CollisionModeEnum.HideOnContact,
        };
        var leafMat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.9f,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha, DistanceFadeMinDistance = 0.5f, DistanceFadeMaxDistance = 1.6f,
        };
        _leaves = new GpuParticles3D
        {
            Amount = 90, Lifetime = 8f, Preprocess = 4f, ProcessMaterial = leafProcess,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.12f, 0.08f), Material = leafMat },
            LocalCoords = false, Emitting = false, CollisionBaseSize = 0.05f, TopLevel = true,
            VisibilityAabb = new Aabb(new Vector3(-30, -20, -30), new Vector3(60, 40, 60)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_leaves);

        var glow = new Gradient();
        glow.SetColor(0, new Color(1, 1, 1, 0));
        glow.SetColor(1, new Color(1, 1, 1, 0));
        glow.AddPoint(0.3f, new Color(1, 1, 1, 1));
        glow.AddPoint(0.55f, new Color(1, 1, 1, 0.15f));
        glow.AddPoint(0.75f, new Color(1, 1, 1, 1));
        var flyProcess = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(18f, 1.2f, 18f),
            Direction = new Vector3(1, 0.2f, 0),
            Spread = 180f,
            InitialVelocityMin = 0.1f,
            InitialVelocityMax = 0.5f,
            Gravity = Vector3.Zero,
            TurbulenceEnabled = true,
            TurbulenceNoiseStrength = 0.6f,
            TurbulenceNoiseScale = 2.5f,
            ColorRamp = new GradientTexture1D { Gradient = glow },
        };
        var flyMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(0.85f, 1f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, BillboardKeepScale = true,
        };
        _fireflies = new GpuParticles3D
        {
            Amount = 45, Lifetime = 6f, Preprocess = 3f, ProcessMaterial = flyProcess,
            DrawPass1 = new SphereMesh { Radius = 0.022f, Height = 0.044f, RadialSegments = 6, Rings = 3, Material = flyMat },
            LocalCoords = false, Emitting = false, TopLevel = true,
            VisibilityAabb = new Aabb(new Vector3(-30, -10, -30), new Vector3(60, 20, 60)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_fireflies);
    }

    private void UpdateAmbientParticles(Vector3 cam, Vector3 vel)
    {
        var world = GameWorld.I?.World;
        if (world == null) return;
        world.Biome(cam.X, cam.Z, out float forest, out float field);
        float trees = MathF.Max(forest, 0.25f * (1f - field));
        bool leaves = Rain < 0.5f && trees > 0.1f;
        if (_leaves.Emitting != leaves) _leaves.Emitting = leaves;
        _leaves.AmountRatio = Mathf.Clamp(trees * (0.25f + Wind * 1.3f), 0.08f, 1f);
        _leaves.GlobalPosition = cam + new Vector3(0, 4.5f, 0) + new Vector3(vel.X, 0, vel.Z) * 0.6f;

        bool flies = Daylight < 0.2f && Rain < 0.08f && Fog < 0.6f && field < 0.5f;
        if (_fireflies.Emitting != flies) _fireflies.Emitting = flies;
        float ground = world.Height(cam.X, cam.Z);
        _fireflies.GlobalPosition = new Vector3(cam.X, ground + 1.3f, cam.Z);
    }

    /// <summary>Tyre grip multiplier for the current road condition.</summary>
    public float GripFactor => 1f - 0.3f * Wetness;
}
