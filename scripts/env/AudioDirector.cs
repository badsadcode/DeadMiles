using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Owns every sound: synthesises the library at startup, mixes the ambience layers from the
/// weather and time of day, and plays pooled one-shots (2D for the player, 3D for the world).
/// </summary>
public partial class AudioDirector : Node
{
    private readonly Dictionary<string, AudioStream[]> _sounds = new();
    private readonly List<AudioStreamPlayer> _pool2D = new();
    private readonly List<AudioStreamPlayer3D> _pool3D = new();
    private AudioStreamPlayer _wind = null!, _rainLight = null!, _rainHeavy = null!, _roof = null!, _birds = null!, _crickets = null!;
    private readonly RandomNumberGenerator _rng = new();
    private double _crowTimer = 20, _thunderDelay = -1;
    private int _thunderVariant;

    /// <summary>Set by the game each frame; all zero while in the menu.</summary>
    public float WindLevel, RainLevel, Daylight;
    public bool Sheltered, InGame;
    public Vector3 ListenerPos;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        EnsureBus("Sfx");
        EnsureBus("Ambience");
        var t0 = Time.GetTicksMsec();
        Register("wind", SoundSynth.Wind());
        Register("rain_light", SoundSynth.Rain(false));
        Register("rain_heavy", SoundSynth.Rain(true));
        Register("roof", SoundSynth.RoofRain());
        Register("birds", SoundSynth.Birdsong());
        Register("crickets", SoundSynth.Crickets());
        Register("fire", SoundSynth.Fire());
        Register("engine", SoundSynth.Engine());
        Register("tires_asphalt", SoundSynth.Tires(false));
        Register("tires_gravel", SoundSynth.Tires(true));
        Register("engine_start", SoundSynth.EngineStart(false));
        Register("engine_fail", SoundSynth.EngineStart(true));
        Register("horn", SoundSynth.Horn());
        Register("step_grass", SoundSynth.Footstep(0, 0), SoundSynth.Footstep(0, 1), SoundSynth.Footstep(0, 2), SoundSynth.Footstep(0, 3));
        Register("step_gravel", SoundSynth.Footstep(1, 0), SoundSynth.Footstep(1, 1), SoundSynth.Footstep(1, 2), SoundSynth.Footstep(1, 3));
        Register("step_hard", SoundSynth.Footstep(2, 0), SoundSynth.Footstep(2, 1), SoundSynth.Footstep(2, 2));
        Register("crow", SoundSynth.Crow(0), SoundSynth.Crow(1), SoundSynth.Crow(2));
        Register("thunder", SoundSynth.Thunder(0), SoundSynth.Thunder(1), SoundSynth.Thunder(2));
        Register("gunshot", SoundSynth.Gunshot());
        Register("swing", SoundSynth.Burst(70, 0.28f, 500, 2600, 0.09f, 0.07f, 0.4f));
        Register("hit", SoundSynth.Burst(71, 0.2f, 0, 1400, 0.002f, 0.05f, 0.8f, 95f, 1.2f, 180f));
        Register("hurt", SoundSynth.Burst(72, 0.3f, 0, 600, 0.004f, 0.09f, 0.8f, 70f, 1.3f, 60f));
        Register("door", SoundSynth.Burst(73, 0.3f, 0, 900, 0.002f, 0.06f, 0.75f, 85f, 1.0f, 90f));
        Register("open", SoundSynth.Burst(74, 0.35f, 300, 2400, 0.04f, 0.1f, 0.4f, 140f, 0.5f, 100f));
        Register("click", SoundSynth.Blip(1300, 900, 0.035f, 0.35f));
        Register("pickup", SoundSynth.Blip(520, 880, 0.09f, 0.3f));
        Register("eat", SoundSynth.Sequence(75, 4, 0.2f, 600, 3800, 0.03f, 0.5f));
        Register("drink", SoundSynth.Sequence(76, 4, 0.24f, 0, 500, 0.06f, 0.55f, 210f, 1.0f, -260f));
        Register("repair", SoundSynth.Sequence(77, 6, 0.17f, 1500, 6000, 0.018f, 0.5f, 2700f, 0.6f, 0f));
        Register("cloth", SoundSynth.Sequence(78, 3, 0.16f, 900, 4200, 0.06f, 0.3f));
        Register("pour", SoundSynth.Pour());
        Register("fire_light", SoundSynth.Burst(79, 0.7f, 200, 2500, 0.15f, 0.2f, 0.45f));
        Register("save", SoundSynth.Blip(660, 990, 0.16f, 0.22f));
        Log.Info("AudioDirector", $"Synthesised {_sounds.Count} sounds in {Time.GetTicksMsec() - t0} ms");

        _wind = Bed("wind");
        _rainLight = Bed("rain_light");
        _rainHeavy = Bed("rain_heavy");
        _roof = Bed("roof");
        _birds = Bed("birds");
        _crickets = Bed("crickets");
        _rng.Randomize();
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0) return;
        AudioServer.AddBus();
        int i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, "Master");
    }

    private void Register(string name, params AudioStream[] variants) => _sounds[name] = variants;

    private AudioStreamPlayer Bed(string name)
    {
        var p = new AudioStreamPlayer { Stream = _sounds[name][0], Bus = "Ambience", VolumeDb = -80 };
        AddChild(p);
        p.Play();
        return p;
    }

    public AudioStream? Get(string name)
    {
        if (_sounds.TryGetValue(name, out var v)) return v[_rng.RandiRange(0, v.Length - 1)];
        Log.Warn("AudioDirector", $"Unknown sound '{name}'");
        return null;
    }

    public void Play(string name, float volumeDb = 0f, float pitch = 1f)
    {
        var s = Get(name);
        if (s == null) return;
        AudioStreamPlayer? p = null;
        foreach (var c in _pool2D) if (!c.Playing) { p = c; break; }
        if (p == null)
        {
            if (_pool2D.Count >= 16) return;
            p = new AudioStreamPlayer { Bus = "Sfx" };
            AddChild(p);
            _pool2D.Add(p);
        }
        p.Stream = s;
        p.VolumeDb = volumeDb;
        p.PitchScale = pitch * _rng.RandfRange(0.96f, 1.04f);
        p.Play();
    }

    public void Play3D(string name, Vector3 pos, float volumeDb = 0f, float pitch = 1f, float unitSize = 12f, float maxDistance = 220f)
    {
        var s = Get(name);
        if (s == null) return;
        AudioStreamPlayer3D? p = null;
        foreach (var c in _pool3D) if (!c.Playing) { p = c; break; }
        if (p == null)
        {
            if (_pool3D.Count >= 16) return;
            p = new AudioStreamPlayer3D { Bus = "Sfx", AttenuationFilterCutoffHz = 9000 };
            AddChild(p);
            _pool3D.Add(p);
        }
        p.Stream = s;
        p.GlobalPosition = pos;
        p.VolumeDb = volumeDb;
        p.UnitSize = unitSize;
        p.MaxDistance = maxDistance;
        p.PitchScale = pitch * _rng.RandfRange(0.95f, 1.05f);
        p.Play();
    }

    /// <summary>Schedules a thunderclap; the delay stands in for distance.</summary>
    public void Thunder(float delay)
    {
        _thunderDelay = delay;
        _thunderVariant = _rng.RandiRange(0, 2);
    }

    private static float Db(float linear) => linear <= 0.0005f ? -80f : Mathf.LinearToDb(linear);

    private static void Fade(AudioStreamPlayer p, float target, float dt)
        => p.VolumeDb = Mathf.Lerp(p.VolumeDb, Db(target), MathX.Damp(2.5f, dt));

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        float open = InGame ? (Sheltered ? 0.28f : 1f) : 0f;
        float g = InGame ? 1f : 0f;
        Fade(_wind, (0.07f + WindLevel * 0.5f) * open, dt);
        Fade(_rainLight, MathF.Min(RainLevel * 2.2f, 1f) * 0.5f * open, dt);
        Fade(_rainHeavy, MathX.Smooth(0.35f, 1f, RainLevel) * 0.7f * open, dt);
        Fade(_roof, Sheltered ? RainLevel * 0.75f * g : 0f, dt);
        float calm = (1f - MathX.Smooth(0.05f, 0.4f, RainLevel)) * (1f - WindLevel * 0.6f);
        Fade(_birds, MathX.Smooth(0.3f, 0.8f, Daylight) * calm * 0.22f * open, dt);
        Fade(_crickets, (1f - MathX.Smooth(0.15f, 0.5f, Daylight)) * calm * 0.2f * open, dt);
        _wind.PitchScale = 0.85f + WindLevel * 0.45f;

        if (!InGame || GetTree().Paused) return;

        if (_thunderDelay >= 0)
        {
            _thunderDelay -= delta;
            if (_thunderDelay < 0)
            {
                var s = _sounds["thunder"][_thunderVariant];
                AudioStreamPlayer? p = null;
                foreach (var c in _pool2D) if (!c.Playing) { p = c; break; }
                if (p == null && _pool2D.Count < 16) { p = new AudioStreamPlayer { Bus = "Sfx" }; AddChild(p); _pool2D.Add(p); }
                if (p != null) { p.Stream = s; p.VolumeDb = Sheltered ? -7f : -1f; p.PitchScale = _rng.RandfRange(0.8f, 1.1f); p.Play(); }
            }
        }

        // A crow somewhere off in the trees now and then: silence broken, not filled.
        _crowTimer -= delta;
        if (_crowTimer <= 0)
        {
            _crowTimer = _rng.RandfRange(22f, 75f);
            if (Daylight > 0.3f && RainLevel < 0.3f)
            {
                float a = _rng.RandfRange(0, Mathf.Tau), d = _rng.RandfRange(45f, 110f);
                Play3D("crow", ListenerPos + new Vector3(MathF.Cos(a) * d, _rng.RandfRange(8f, 22f), MathF.Sin(a) * d), -4f, _rng.RandfRange(0.9f, 1.1f), 30f, 300f);
            }
        }
    }
}
