using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>User options, stored in user://settings.cfg and applied to the engine and the running game.</summary>
public sealed class Settings
{
    private const string Path = "user://settings.cfg";

    public bool Fullscreen = false;
    public bool VSync = true;
    public float RenderScale = 1f;
    public int ShadowQuality = 2;       // 0 off, 1 low, 2 medium, 3 high
    public int ViewDistance = 6;        // chunks (96 m each)
    public float VegetationDensity = 1f;
    public bool Grass = true;
    public bool AntiAliasing = true;
    public bool Ssao = true;
    public bool VolumetricFog = true;
    public bool PostEffects = true;
    public float Fov = 75f;

    public float MasterVolume = 0.8f, SfxVolume = 1f, AmbienceVolume = 1f;
    public float MouseSensitivity = 1f;
    public bool InvertY = false;

    /// <summary>Rebound keyboard keys: action name -> physical keycode.</summary>
    public readonly Dictionary<string, long> Keys = new();

    public void Load()
    {
        var c = new ConfigFile();
        if (c.Load(Path) != Error.Ok)
        {
            Log.Info("Settings", "No settings file yet, using defaults");
            return;
        }
        Fullscreen = c.GetValue("video", "fullscreen", Fullscreen).AsBool();
        VSync = c.GetValue("video", "vsync", VSync).AsBool();
        RenderScale = Mathf.Clamp(c.GetValue("video", "render_scale", RenderScale).AsSingle(), 0.5f, 1f);
        ShadowQuality = Mathf.Clamp(c.GetValue("video", "shadows", ShadowQuality).AsInt32(), 0, 3);
        ViewDistance = Mathf.Clamp(c.GetValue("video", "view_distance", ViewDistance).AsInt32(), 4, 8);
        VegetationDensity = Mathf.Clamp(c.GetValue("video", "vegetation", VegetationDensity).AsSingle(), 0.4f, 1f);
        Grass = c.GetValue("video", "grass", Grass).AsBool();
        AntiAliasing = c.GetValue("video", "aa", AntiAliasing).AsBool();
        Ssao = c.GetValue("video", "ssao", Ssao).AsBool();
        VolumetricFog = c.GetValue("video", "volumetric_fog", VolumetricFog).AsBool();
        PostEffects = c.GetValue("video", "post_effects", PostEffects).AsBool();
        Fov = Mathf.Clamp(c.GetValue("video", "fov", Fov).AsSingle(), 60f, 100f);
        MasterVolume = c.GetValue("audio", "master", MasterVolume).AsSingle();
        SfxVolume = c.GetValue("audio", "sfx", SfxVolume).AsSingle();
        AmbienceVolume = c.GetValue("audio", "ambience", AmbienceVolume).AsSingle();
        MouseSensitivity = Mathf.Clamp(c.GetValue("input", "mouse_sensitivity", MouseSensitivity).AsSingle(), 0.2f, 3f);
        InvertY = c.GetValue("input", "invert_y", InvertY).AsBool();
        if (c.HasSection("keys"))
            foreach (string action in c.GetSectionKeys("keys"))
                Keys[action] = c.GetValue("keys", action).AsInt64();
    }

    public void Save()
    {
        var c = new ConfigFile();
        c.SetValue("video", "fullscreen", Fullscreen);
        c.SetValue("video", "vsync", VSync);
        c.SetValue("video", "render_scale", RenderScale);
        c.SetValue("video", "shadows", ShadowQuality);
        c.SetValue("video", "view_distance", ViewDistance);
        c.SetValue("video", "vegetation", VegetationDensity);
        c.SetValue("video", "grass", Grass);
        c.SetValue("video", "aa", AntiAliasing);
        c.SetValue("video", "ssao", Ssao);
        c.SetValue("video", "volumetric_fog", VolumetricFog);
        c.SetValue("video", "post_effects", PostEffects);
        c.SetValue("video", "fov", Fov);
        c.SetValue("audio", "master", MasterVolume);
        c.SetValue("audio", "sfx", SfxVolume);
        c.SetValue("audio", "ambience", AmbienceVolume);
        c.SetValue("input", "mouse_sensitivity", MouseSensitivity);
        c.SetValue("input", "invert_y", InvertY);
        foreach (var (action, key) in Keys) c.SetValue("keys", action, key);
        var err = c.Save(Path);
        if (err != Error.Ok) Log.Error("Settings", $"Could not save settings: {err}");
    }

    public void Apply(Node context)
    {
        var mode = Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed;
        if (DisplayServer.WindowGetMode() != mode && !(mode == DisplayServer.WindowMode.Windowed && DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Maximized))
            DisplayServer.WindowSetMode(mode);
        DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

        var vp = context.GetViewport();
        vp.Scaling3DScale = RenderScale;
        vp.Msaa3D = AntiAliasing ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Disabled;
        vp.ScreenSpaceAA = AntiAliasing ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
        int atlas = ShadowQuality switch { 1 => 2048, 2 => 4096, 3 => 8192, _ => 1024 };
        RenderingServer.DirectionalShadowAtlasSetSize(atlas, true);

        SetBus("Master", MasterVolume);
        SetBus("Sfx", SfxVolume);
        SetBus("Ambience", AmbienceVolume);

        InputSetup.ApplyKeys(this);
        GameWorld.I?.ApplySettings(this);
    }

    private static void SetBus(string name, float linear)
    {
        int i = AudioServer.GetBusIndex(name);
        if (i < 0) return;
        AudioServer.SetBusVolumeDb(i, linear <= 0.001f ? -80f : Mathf.LinearToDb(linear));
    }
}
