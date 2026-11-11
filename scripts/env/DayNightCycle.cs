using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// Game clock plus everything that depends on where the sun is: the two directional lights,
/// the sky shader colours, ambient light and fog. Weather is blended in here so there is one
/// place that decides what the scene's lighting looks like.
/// </summary>
public partial class DayNightCycle : Node3D
{
    public const float SecondsPerHour = 120f; // a full day is 48 real minutes

    public float TimeOfDay = 8.5f;
    public int Day = 1;
    public float TimeScale = 1f;

    /// <summary>0 at night, 1 in full day.</summary>
    public float Daylight { get; private set; } = 1f;
    public Vector3 SunDirection { get; private set; } = Vector3.Up;
    public bool IsNight => Daylight < 0.3f;

    public WeatherDirector? Weather;
    public DirectionalLight3D Sun = null!, Moon = null!;
    public Godot.Environment Env = null!;
    private ShaderMaterial _sky = null!;
    private WorldEnvironment _worldEnv = null!;

    public override void _Ready()
    {
        _sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
        Env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, RadianceSize = Sky.RadianceSizeEnum.Size32, ProcessMode = Sky.ProcessModeEnum.Quality },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapWhite = 7f,
            TonemapExposure = 1.0f,
            AdjustmentEnabled = true,
            AdjustmentContrast = 1.06f,
            SsaoRadius = 1.6f,
            SsaoIntensity = 1.7f,
            SsaoPower = 1.6f,
            // volumetric fog: light shafts through trees, headlight cones in mist
            VolumetricFogLength = 150f,
            VolumetricFogDetailSpread = 2.6f,
            VolumetricFogAnisotropy = 0.62f,
            VolumetricFogAmbientInject = 0.25f,
            VolumetricFogSkyAffect = 0f,
            VolumetricFogGIInject = 0f,
            FogEnabled = true,
            FogSunScatter = 0.15f,
            FogAerialPerspective = 0.0f,
            GlowEnabled = true,
            GlowIntensity = 0.55f,
            GlowBloom = 0.04f,
            GlowHdrThreshold = 1.0f,
            GlowStrength = 1.05f,
            SsaoEnabled = false,
        };
        _worldEnv = new WorldEnvironment { Environment = Env };
        AddChild(_worldEnv);

        Sun = new DirectionalLight3D
        {
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 150f,
            DirectionalShadowBlendSplits = true,
            ShadowBias = 0.06f,
            ShadowNormalBias = 1.6f,
            LightAngularDistance = 0.6f,
            LightVolumetricFogEnergy = 1.3f,
        };
        Moon = new DirectionalLight3D
        {
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 90f,
            LightColor = new Color(0.55f, 0.65f, 0.9f),
            ShadowBias = 0.08f,
            ShadowNormalBias = 2f,
            LightVolumetricFogEnergy = 0.8f,
        };
        AddChild(Sun);
        AddChild(Moon);
        Apply();
    }

    public string ClockText
    {
        get
        {
            int h = (int)TimeOfDay, m = (int)((TimeOfDay - h) * 60f);
            return $"{h:00}:{m:00}";
        }
    }

    public void AdvanceHours(float hours)
    {
        TimeOfDay += hours;
        while (TimeOfDay >= 24f) { TimeOfDay -= 24f; Day++; }
    }

    public override void _Process(double delta)
    {
        AdvanceHours((float)delta * TimeScale / SecondsPerHour);
        Apply();
    }

    private static Color Mix(Color a, Color b, float t) => a.Lerp(b, Mathf.Clamp(t, 0f, 1f));

    private void Apply()
    {
        float a = (TimeOfDay - 12f) / 24f * Mathf.Tau;
        var sun = new Vector3(MathF.Sin(a) * 0.94f, MathF.Cos(a) * 0.94f, 0.34f).Normalized();
        var moon = new Vector3(MathF.Sin(a + Mathf.Pi + 0.5f) * 0.9f, MathF.Cos(a + Mathf.Pi + 0.5f) * 0.9f, -0.43f).Normalized();
        SunDirection = sun;
        float e = sun.Y;
        float day = MathX.Smooth(-0.24f, 0.16f, e);      // light level: dusk lingers after sunset
        float skyDay = MathX.Smooth(-0.3f, 0.1f, e);     // the sky stays bright longer than the ground
        float twilight = MathF.Exp(-(e * e) / (0.16f * 0.16f));
        Daylight = day;

        float cloud = Weather?.Cloud ?? 0.15f;
        float fog = Weather?.Fog ?? 0f;
        float rain = Weather?.Rain ?? 0f;
        float flash = Weather?.Flash ?? 0f;
        float overcast = MathX.Smooth(0.45f, 1f, cloud);

        var zenith = Mix(new Color(0.004f, 0.007f, 0.02f), new Color(0.09f, 0.24f, 0.55f), skyDay);
        var horizon = Mix(new Color(0.012f, 0.018f, 0.035f), new Color(0.50f, 0.64f, 0.80f), skyDay);
        zenith = Mix(zenith, new Color(0.10f, 0.13f, 0.30f), twilight * 0.3f);
        horizon = Mix(horizon, new Color(0.92f, 0.56f, 0.34f), twilight * 0.42f * (1f - overcast * 0.7f));
        // overcast skies lose their colour and brightness
        float grey = 0.04f + 0.42f * skyDay;
        zenith = Mix(zenith, new Color(grey * 0.75f, grey * 0.8f, grey * 0.88f), overcast * 0.92f);
        horizon = Mix(horizon, new Color(grey * 1.05f, grey * 1.1f, grey * 1.15f), overcast * 0.85f);
        float storm = MathX.Smooth(0.5f, 1f, rain);
        zenith = zenith.Darkened(storm * 0.45f);
        horizon = horizon.Darkened(storm * 0.35f);

        var sunCol = Mix(new Color(1f, 0.42f, 0.16f), new Color(1f, 0.95f, 0.86f), MathX.Smooth(0f, 0.35f, e));
        float sunEnergy = MathX.Smooth(-0.03f, 0.14f, e) * 1.3f * (1f - 0.82f * overcast);
        float moonUp = MathX.Smooth(0.0f, 0.2f, moon.Y);
        float moonEnergy = moonUp * 0.42f * (1f - day) * (1f - 0.75f * overcast);

        Sun.LightColor = sunCol;
        Sun.LightEnergy = sunEnergy;
        Sun.Visible = sunEnergy > 0.01f;
        Aim(Sun, sun);
        Moon.LightEnergy = moonEnergy;
        Moon.Visible = !Sun.Visible && moonEnergy > 0.005f;
        Aim(Moon, moon);

        var ambient = Mix(new Color(0.095f, 0.125f, 0.2f), new Color(0.52f, 0.57f, 0.64f), day);
        ambient = Mix(ambient, new Color(0.36f, 0.32f, 0.36f), twilight * 0.45f);
        ambient = Mix(ambient, new Color(grey * 0.9f, grey * 0.93f, grey), overcast * 0.6f * day);
        Env.AmbientLightColor = ambient;
        // Nights stay readable: more fill light and a slightly opened exposure, like eyes adjusting.
        Env.AmbientLightEnergy = 1f + (1f - day) * 2.4f + flash * 4f;
        Env.TonemapExposure = 1f + (1f - day) * 0.45f;

        var fogCol = Mix(horizon, new Color(grey, grey * 1.03f, grey * 1.06f), MathF.Max(overcast * 0.7f, fog * 0.85f));
        Env.FogLightColor = fogCol;
        // At night dense distance fog would swallow everything the headlights reach, so thin it there
        // and let the volumetric fog carry the look instead.
        float nightThin = 0.4f + 0.6f * day;
        Env.FogDensity = 0.0026f + (fog * 0.03f + rain * 0.0055f) * nightThin + (1f - day) * 0.0008f;
        Env.FogSkyAffect = Mathf.Clamp(0.12f + fog * 0.9f + rain * 0.35f, 0f, 1f);
        Env.FogLightEnergy = 1f + flash * 2f;
        Env.VolumetricFogDensity = 0.0012f + 0.0028f * day + fog * (0.012f + 0.02f * day) + rain * 0.006f + twilight * 0.003f;
        Env.VolumetricFogAlbedo = fogCol.Lerp(new Color(0.75f, 0.78f, 0.82f), 0.5f);
        Env.AdjustmentSaturation = Mathf.Lerp(0.98f, 0.82f, overcast);

        // drifting cloud shadows are strongest under broken cloud in daylight
        float broken = MathX.Smooth(0.08f, 0.45f, cloud) * (1f - MathX.Smooth(0.75f, 1f, cloud));
        RenderingServer.GlobalShaderParameterSet("cloud_shadow", (0.12f + broken * 0.5f) * day);

        var cloudLit = Mix(new Color(0.03f, 0.04f, 0.065f), new Color(0.95f, 0.95f, 0.95f), day);
        cloudLit = Mix(cloudLit, new Color(1f, 0.6f, 0.38f), twilight * 0.55f * (1f - overcast));
        var cloudDark = Mix(new Color(0.012f, 0.016f, 0.028f), new Color(0.36f, 0.39f, 0.44f), day).Darkened(storm * 0.4f);

        _sky.SetShaderParameter("zenith_color", zenith);
        _sky.SetShaderParameter("horizon_color", horizon);
        _sky.SetShaderParameter("ground_color", fogCol.Darkened(0.5f));
        _sky.SetShaderParameter("sun_color", sunCol);
        _sky.SetShaderParameter("sun_dir", sun);
        _sky.SetShaderParameter("moon_dir", moon);
        _sky.SetShaderParameter("sun_disc", MathX.Smooth(-0.06f, 0.02f, e));
        _sky.SetShaderParameter("star_intensity", (1f - MathX.Smooth(-0.3f, -0.1f, e)) * (1f - overcast));
        _sky.SetShaderParameter("cloud_cover", cloud);
        _sky.SetShaderParameter("cloud_lit", cloudLit);
        _sky.SetShaderParameter("cloud_dark", cloudDark);
        _sky.SetShaderParameter("cloud_speed", 0.004f + (Weather?.Wind ?? 0.2f) * 0.012f);
        _sky.SetShaderParameter("flash", flash * 0.5f);

        RenderingServer.GlobalShaderParameterSet("sky_tint", new Vector3(horizon.R, horizon.G, horizon.B) * (0.6f + 0.4f * day));
        if (Weather != null) Weather.Daylight = day;
    }

    private static void Aim(DirectionalLight3D light, Vector3 towardsLight)
    {
        var up = MathF.Abs(towardsLight.Y) > 0.98f ? Vector3.Right : Vector3.Up;
        light.Basis = Basis.LookingAt(-towardsLight, up);
    }
}
