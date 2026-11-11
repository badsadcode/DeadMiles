using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// In-game overlay, drawn directly: needs (only prominent when they matter), the interaction
/// prompt, a compass strip, the clock, the instrument cluster while driving, short messages,
/// plus the full-screen layers (fade, loading, death, rain on the windscreen).
/// </summary>
public partial class Hud : Control
{
    private readonly List<(string text, float life)> _toasts = new();
    private string _hint = "", _subtitle = "", _loadingText = "";
    private float _hintLife, _subtitleLife, _flash, _shake, _fadeTarget, _fadeValue;
    private ColorRect _fade = null!, _windshield = null!;
    private ShaderMaterial _windshieldMat = null!;
    private PanelContainer _death = null!;
    private Label _deathLabel = null!;
    private Font _font = null!;
    private GameWorld _g = null!;

    public void Setup(GameWorld g)
    {
        _g = g;
        _font = ThemeDB.FallbackFont;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Theme = Ui.Theme;

        _windshieldMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/windshield.gdshader") };
        _windshield = new ColorRect { Material = _windshieldMat, MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true, Visible = false };
        _windshield.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_windshield);

        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore };
        _fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_fade);

        _death = new PanelContainer { Visible = false };
        _death.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _death.GrowHorizontal = GrowDirection.Both;
        _death.GrowVertical = GrowDirection.Both;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        box.AddChild(Ui.Label("THE ROAD ENDS HERE", 34, Ui.Danger));
        box.AddChild(_deathLabel = Ui.Label("", 18, Ui.Dim));
        if (SaveService.Exists() || true)
            box.AddChild(Ui.Button("Load last save", () => Main.I.ContinueGame(), 320));
        box.AddChild(Ui.Button("Main menu", () => Main.I.ShowMenu(), 320));
        _death.AddChild(box);
        AddChild(_death);
    }

    // ---- messages ------------------------------------------------------------------------

    public void Toast(string text)
    {
        _toasts.Add((text, 5.5f));
        if (_toasts.Count > 5) _toasts.RemoveAt(0);
    }

    public void Hint(string text) { _hint = text; _hintLife = 5f; }
    public void Subtitle(string text) { _subtitle = text; _subtitleLife = 4.5f; }
    public void DamageFlash(float amount) => _flash = MathF.Min(1f, _flash + 0.25f + amount * 0.03f);
    public void Shake(float amount) => _shake = MathF.Max(_shake, amount);
    public void Fade(bool toBlack) => _fadeTarget = toBlack ? 1f : 0f;
    public void SetBlack() { _fadeTarget = 1f; _fadeValue = 1f; }
    public void SetLoading(string text) => _loadingText = text;
    public bool FadeDone => MathF.Abs(_fadeValue - _fadeTarget) < 0.02f;

    public void ShowDeath(string cause)
    {
        _deathLabel.Text = cause;
        _death.Visible = true;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            _toasts[i] = (_toasts[i].text, _toasts[i].life - dt);
            if (_toasts[i].life <= 0) _toasts.RemoveAt(i);
        }
        _hintLife -= dt;
        _subtitleLife -= dt;
        _flash = MathF.Max(0, _flash - dt * 0.9f);
        _shake = MathF.Max(0, _shake - dt * 2f);
        _fadeValue = MathX.Approach(_fadeValue, _fadeTarget, dt * 1.1f);
        _fade.Color = new Color(0, 0, 0, _fadeValue);

        var v = _g.Vehicle;
        bool cockpit = _g.Player.InVehicle && v.FirstPerson && _g.Weather.Rain > 0.04f;
        _windshield.Visible = cockpit;
        if (cockpit)
        {
            float frac = v.WiperPhase - MathF.Floor(v.WiperPhase);
            float clear = v.EngineOn ? MathX.Smooth(0.08f, 0.95f, frac) : 1f;
            _windshieldMat.SetShaderParameter("intensity", _g.Weather.Rain);
            _windshieldMat.SetShaderParameter("clear_amount", clear);
        }
        QueueRedraw();
    }

    // ---- drawing -------------------------------------------------------------------------

    private void Text(Vector2 pos, string s, int size, Color c, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1)
    {
        var shadow = new Color(0, 0, 0, c.A * 0.7f);
        DrawString(_font, pos + new Vector2(1, 1), s, align, width, size, shadow);
        DrawString(_font, pos, s, align, width, size, c);
    }

    private void Bar(Vector2 pos, float w, float value, Color c, string label, float alpha)
    {
        DrawRect(new Rect2(pos, new Vector2(w, 7)), new Color(0, 0, 0, 0.45f * alpha));
        DrawRect(new Rect2(pos, new Vector2(w * Mathf.Clamp(value, 0, 1), 7)), new Color(c.R, c.G, c.B, alpha));
        Text(pos + new Vector2(w + 8, 8), label, 12, new Color(Ui.Paper.R, Ui.Paper.G, Ui.Paper.B, alpha * 0.9f));
    }

    public override void _Draw()
    {
        var size = Size;
        if (_loadingText != "")
        {
            DrawRect(new Rect2(Vector2.Zero, size), Colors.Black);
            Text(new Vector2(0, size.Y * 0.5f - 20), "DEAD MILES", 44, Ui.Paper, HorizontalAlignment.Center, size.X);
            Text(new Vector2(0, size.Y * 0.5f + 24), _loadingText, 18, Ui.Dim, HorizontalAlignment.Center, size.X);
            return;
        }
        var p = _g.Player;
        var st = p.Stats;
        bool ui = _g.UiOpen;
        var shake = _shake > 0.01f ? new Vector2(GD.Randf() - 0.5f, GD.Randf() - 0.5f) * _shake * 14f : Vector2.Zero;
        DrawSetTransform(shake);

        // damage and low-health vignette
        float hurt = MathF.Max(_flash, st.Health < 30 ? (30 - st.Health) / 30f * 0.5f : 0f);
        if (hurt > 0.01f)
        {
            var red = new Color(0.55f, 0.02f, 0.02f, hurt * 0.55f);
            float b = 70f + hurt * 60f;
            DrawRect(new Rect2(0, 0, size.X, b), red);
            DrawRect(new Rect2(0, size.Y - b, size.X, b), red);
            DrawRect(new Rect2(0, b, b, size.Y - 2 * b), red);
            DrawRect(new Rect2(size.X - b, b, b, size.Y - 2 * b), red);
        }

        // needs: faint until something needs attention
        float x = 28, y = size.Y - 92;
        float A(float v) => v < 35 ? 1f : v < 60 ? 0.75f : 0.35f;
        Bar(new Vector2(x, y), 150, st.Health / 100f, st.Health < 35 ? Ui.Danger : new Color(0.72f, 0.3f, 0.26f), "HEALTH", A(st.Health));
        Bar(new Vector2(x, y + 17), 150, st.Hunger / 100f, new Color(0.80f, 0.62f, 0.28f), st.Hunger < 20 ? "FOOD - starving" : "FOOD", A(st.Hunger));
        Bar(new Vector2(x, y + 34), 150, st.Thirst / 100f, new Color(0.35f, 0.62f, 0.80f), st.Thirst < 20 ? "WATER - parched" : "WATER", A(st.Thirst));
        if (st.Stamina < 99f) Bar(new Vector2(x, y + 51), 150, st.Stamina / 100f, new Color(0.75f, 0.78f, 0.7f), "STAMINA", 0.8f);

        // compass strip
        var cam = GetViewport().GetCamera3D();
        if (cam != null)
        {
            var f = -cam.GlobalTransform.Basis.Z;
            float heading = Mathf.RadToDeg(MathF.Atan2(f.X, -f.Z)); // 0 = north (-Z), 90 = east (+X)
            float cx = size.X * 0.5f, cy = 30;
            const float pxPerDeg = 3.2f;
            DrawRect(new Rect2(cx - 190, cy - 16, 380, 26), new Color(0, 0, 0, 0.22f));
            for (int deg = 0; deg < 360; deg += 15)
            {
                float d = Mathf.Wrap(deg - heading, -180f, 180f);
                if (MathF.Abs(d) > 58f) continue;
                float px = cx + d * pxPerDeg;
                float fade = 1f - MathF.Abs(d) / 58f;
                string? name = deg switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", 45 => "NE", 135 => "SE", 225 => "SW", 315 => "NW", _ => null };
                if (name != null) Text(new Vector2(px - 20, cy + 4), name, name.Length == 1 ? 17 : 12, new Color(Ui.Paper.R, Ui.Paper.G, Ui.Paper.B, fade), HorizontalAlignment.Center, 40);
                else DrawLine(new Vector2(px, cy - 6), new Vector2(px, cy + 2), new Color(1, 1, 1, 0.45f * fade), 1.5f);
            }
            DrawLine(new Vector2(cx, cy + 10), new Vector2(cx, cy + 15), Ui.Accent, 2f);
        }

        // clock and weather
        string weather = _g.Weather.State switch
        {
            WeatherState.Clear => _g.Time.IsNight ? "clear night" : "clear",
            WeatherState.Overcast => "overcast",
            WeatherState.Drizzle => "drizzle",
            WeatherState.HeavyRain => "heavy rain",
            WeatherState.Fog => "fog",
            _ => "thunderstorm",
        };
        Text(new Vector2(size.X - 260, 34), $"Day {_g.Time.Day}   {_g.Time.ClockText}", 18, Ui.Paper, HorizontalAlignment.Right, 232);
        Text(new Vector2(size.X - 260, 54), weather, 13, Ui.Dim, HorizontalAlignment.Right, 232);

        // crosshair and prompt
        var c = size * 0.5f;
        if (!ui && !p.InVehicle && !st.Dead)
        {
            DrawCircle(c, 2.2f, new Color(1, 1, 1, p.Prompt != null ? 0.95f : 0.5f));
            if (p.Aiming) DrawArc(c, 9f, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.5f), 1.2f);
        }
        if (!ui && p.Prompt != null)
        {
            string s = $"[{InputSetup.KeyName("interact")}]  {p.Prompt}";
            float w = _font.GetStringSize(s, HorizontalAlignment.Left, -1, 18).X + 28;
            DrawStyleBox(Ui.Box(new Color(0, 0, 0, 0.5f), 4, 0), new Rect2(c.X - w * 0.5f, c.Y + 52, w, 32));
            Text(new Vector2(c.X - w * 0.5f, c.Y + 74), s, 18, Ui.Paper, HorizontalAlignment.Center, w);
        }

        // hint, subtitle
        if (_hintLife > 0)
            Text(new Vector2(0, 92), _hint, 20, new Color(Ui.Accent.R, Ui.Accent.G, Ui.Accent.B, MathF.Min(1, _hintLife)), HorizontalAlignment.Center, size.X);
        if (_subtitleLife > 0)
            Text(new Vector2(0, size.Y - 150), $"\"{_subtitle}\"", 22, new Color(1, 1, 0.92f, MathF.Min(1, _subtitleLife)), HorizontalAlignment.Center, size.X);

        // toasts
        float ty = size.Y - 120;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            float a = MathF.Min(1, _toasts[i].life);
            Text(new Vector2(28, ty), _toasts[i].text, 16, new Color(Ui.Paper.R, Ui.Paper.G, Ui.Paper.B, a));
            ty -= 22;
        }

        // instrument cluster
        if (p.InVehicle)
        {
            var v = _g.Vehicle;
            float bx = size.X - 250, by = size.Y - 128;
            DrawStyleBox(Ui.Box(new Color(0, 0, 0, 0.42f), 5, 0), new Rect2(bx - 16, by - 14, 240, 118));
            Text(new Vector2(bx, by + 36), $"{MathF.Abs(v.Speed) * 3.6f:0}", 44, Ui.Paper);
            Text(new Vector2(bx + 84, by + 36), "km/h", 14, Ui.Dim);
            string state = v.EngineOn ? (v.LightsOn ? "LIGHTS ON" : "") : $"ENGINE OFF  [{InputSetup.KeyName("engine_toggle")}]";
            Text(new Vector2(bx + 84, by + 16), state, 12, v.EngineOn ? Ui.Dim : Ui.Accent);
            float fuel = v.Fuel / VehicleController.TankSize;
            Bar(new Vector2(bx, by + 52), 120, fuel, v.Fuel < 5 ? Ui.Danger : Ui.Accent, $"FUEL {v.Fuel:0.0} L", 1f);
            Bar(new Vector2(bx, by + 68), 120, v.EngineHealth / 100f, v.EngineHealth < 30 ? Ui.Danger : new Color(0.7f, 0.72f, 0.7f), $"ENGINE {v.EngineHealth:0}%", 0.9f);
            Bar(new Vector2(bx, by + 84), 120, v.TireHealth / 100f, v.TireHealth < 30 ? Ui.Danger : new Color(0.7f, 0.72f, 0.7f), $"TIRES {v.TireHealth:0}%", 0.9f);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
