using System;
using Godot;

namespace DeadMiles;

/// <summary>Graphics, audio and control options. Every change applies and saves immediately.</summary>
public partial class SettingsPanel : PanelContainer
{
    public Action? Closed;
    private Settings _s = null!;
    private string? _rebinding;
    private Button? _rebindButton;

    public override void _Ready()
    {
        _s = Main.I.Settings;
        Theme = Ui.Theme;
        ProcessMode = ProcessModeEnum.Always;
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        AddChild(root);
        root.AddChild(Ui.Label("SETTINGS", 26, Ui.Accent));
        var tabs = new TabContainer { CustomMinimumSize = new Vector2(640, 440) };
        root.AddChild(tabs);

        var video = Page(tabs, "Graphics");
        Check(video, "Fullscreen", _s.Fullscreen, v => _s.Fullscreen = v);
        Check(video, "Vertical sync", _s.VSync, v => _s.VSync = v);
        Option(video, "Shadows", new[] { "Off", "Low", "Medium", "High" }, _s.ShadowQuality, v => _s.ShadowQuality = v);
        Slider(video, "View distance", 4, 8, 1, _s.ViewDistance, v => _s.ViewDistance = (int)v, v => $"{v * 96:0} m");
        Slider(video, "Vegetation density", 0.4f, 1f, 0.1f, _s.VegetationDensity, v => _s.VegetationDensity = v, v => $"{v * 100:0}%");
        Check(video, "Grass and flowers", _s.Grass, v => _s.Grass = v);
        Check(video, "Anti-aliasing", _s.AntiAliasing, v => _s.AntiAliasing = v);
        Check(video, "Ambient occlusion", _s.Ssao, v => _s.Ssao = v);
        Check(video, "Volumetric fog and light shafts", _s.VolumetricFog, v => _s.VolumetricFog = v);
        Check(video, "Film look (vignette, grain, grading)", _s.PostEffects, v => _s.PostEffects = v);
        Slider(video, "Render scale", 0.5f, 1f, 0.05f, _s.RenderScale, v => _s.RenderScale = v, v => $"{v * 100:0}%");
        Slider(video, "Field of view", 60, 100, 1, _s.Fov, v => _s.Fov = v, v => $"{v:0}");

        var audio = Page(tabs, "Audio");
        Slider(audio, "Master volume", 0, 1, 0.05f, _s.MasterVolume, v => _s.MasterVolume = v, v => $"{v * 100:0}%");
        Slider(audio, "Effects", 0, 1, 0.05f, _s.SfxVolume, v => _s.SfxVolume = v, v => $"{v * 100:0}%");
        Slider(audio, "Ambience", 0, 1, 0.05f, _s.AmbienceVolume, v => _s.AmbienceVolume = v, v => $"{v * 100:0}%");

        var controls = Page(tabs, "Controls");
        Slider(controls, "Mouse sensitivity", 0.2f, 3f, 0.05f, _s.MouseSensitivity, v => _s.MouseSensitivity = v, v => $"{v:0.00}");
        Check(controls, "Invert vertical look", _s.InvertY, v => _s.InvertY = v);
        controls.AddChild(Ui.Label("Click a key to rebind it. Gamepads are supported with a fixed layout.", 13, Ui.Dim));
        foreach (var a in InputSetup.Actions)
        {
            if (!a.Rebindable) continue;
            var info = a;
            Button? b = null;
            b = Ui.Button(InputSetup.KeyName(a.Name), () =>
            {
                _rebinding = info.Name;
                _rebindButton = b;
                b!.Text = "press a key...";
            }, 170);
            Row(controls, a.Label, b);
        }

        var bottom = new HBoxContainer();
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        bottom.AddChild(Ui.Button("Back", () => Closed?.Invoke(), 140));
        root.AddChild(bottom);
    }

    public override void _Input(InputEvent e)
    {
        if (_rebinding == null || !IsVisibleInTree()) return;
        if (e is InputEventKey { Pressed: true } k)
        {
            GetViewport().SetInputAsHandled();
            if (k.PhysicalKeycode != Key.Escape)
            {
                _s.Keys[_rebinding] = (long)k.PhysicalKeycode;
                Commit();
            }
            if (_rebindButton != null) _rebindButton.Text = InputSetup.KeyName(_rebinding);
            _rebinding = null;
        }
    }

    private void Commit()
    {
        _s.Apply(this);
        _s.Save();
    }

    private static VBoxContainer Page(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(box);
        return box;
    }

    private static void Row(Container parent, string label, Control control, Label? value = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var l = Ui.Label(label, 16);
        l.CustomMinimumSize = new Vector2(230, 0);
        row.AddChild(l);
        row.AddChild(control);
        if (value != null) row.AddChild(value);
        parent.AddChild(row);
    }

    private void Check(Container parent, string label, bool initial, Action<bool> set)
    {
        var c = new CheckBox { ButtonPressed = initial };
        c.Toggled += v => { set(v); Commit(); };
        Row(parent, label, c);
    }

    private void Option(Container parent, string label, string[] items, int initial, Action<int> set)
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
        foreach (var i in items) o.AddItem(i);
        o.Selected = initial;
        o.ItemSelected += v => { set((int)v); Commit(); };
        Row(parent, label, o);
    }

    private void Slider(Container parent, string label, float min, float max, float step, float initial, Action<float> set, Func<float, string> format)
    {
        var value = Ui.Label(format(initial), 15, Ui.Dim);
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, CustomMinimumSize = new Vector2(240, 24) };
        s.ValueChanged += v => { set((float)v); value.Text = format((float)v); };
        // apply on release so dragging a slider does not rebuild the world every pixel
        s.DragEnded += _ => Commit();
        Row(parent, label, s, value);
    }
}
