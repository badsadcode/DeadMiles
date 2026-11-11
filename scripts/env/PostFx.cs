using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// Screen-space finishing pass (see post.gdshader). Sits between the 3D view and the HUD and
/// follows the game state: heavier vignette at night, colour drained by overcast skies and by
/// low health, a white flash for lightning.
/// </summary>
public partial class PostFx : CanvasLayer
{
    private ColorRect _rect = null!;
    private ShaderMaterial _mat = null!;
    private float _saturation = 1f, _vignette = 0.35f;

    public bool Enabled { get => _rect.Visible; set => _rect.Visible = value; }

    public override void _Ready()
    {
        Layer = 0;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/post.gdshader") };
        _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore };
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _Process(double delta)
    {
        var g = GameWorld.I;
        if (g == null || !_rect.Visible) return;
        float dt = (float)delta;
        float day = g.Time.Daylight;
        float overcast = MathX.Smooth(0.45f, 1f, g.Weather.Cloud);
        float health = g.Player.Stats.Health;
        float hurt = health < 35f ? (35f - health) / 35f : 0f;

        float sat = Mathf.Lerp(1.0f, 0.85f, overcast) * Mathf.Lerp(0.9f, 1f, day) * (1f - hurt * 0.55f);
        float vig = 0.28f + (1f - day) * 0.08f + overcast * 0.06f + hurt * 0.3f;
        _saturation = Mathf.Lerp(_saturation, sat, MathX.Damp(2f, dt));
        _vignette = Mathf.Lerp(_vignette, vig, MathX.Damp(2f, dt));

        // warm in clear daylight, cool at night and in rain
        var warm = new Color(1.03f, 1.0f, 0.96f);
        var cool = new Color(0.94f, 0.98f, 1.05f);
        var tint = cool.Lerp(warm, day * (1f - overcast * 0.8f));

        _mat.SetShaderParameter("saturation", _saturation);
        _mat.SetShaderParameter("vignette", _vignette);
        _mat.SetShaderParameter("tint", tint);
        _mat.SetShaderParameter("grain", 0.022f + (1f - day) * 0.02f);
        _mat.SetShaderParameter("flash", g.Weather.Flash * 0.12f);
    }
}
