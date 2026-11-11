using System;
using Godot;

namespace DeadMiles;

/// <summary>Shared look for every menu: one theme and a few factory helpers.</summary>
public static class Ui
{
    public static readonly Color Paper = new(0.90f, 0.87f, 0.78f);
    public static readonly Color Dim = new(0.62f, 0.60f, 0.54f);
    public static readonly Color Accent = new(0.88f, 0.58f, 0.22f);
    public static readonly Color Danger = new(0.78f, 0.22f, 0.16f);
    public static readonly Color Bg = new(0.055f, 0.06f, 0.065f, 0.93f);
    public static readonly Color BgLight = new(0.13f, 0.135f, 0.14f, 0.95f);

    private static Theme? _theme;

    public static StyleBoxFlat Box(Color bg, int radius = 3, int margin = 10, Color? border = null, int borderWidth = 1)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(radius);
        s.SetContentMarginAll(margin);
        if (border != null)
        {
            s.BorderColor = border.Value;
            s.SetBorderWidthAll(borderWidth);
        }
        return s;
    }

    public static Theme Theme
    {
        get
        {
            if (_theme != null) return _theme;
            var t = new Theme { DefaultFontSize = 18 };
            var normal = Box(new Color(0.16f, 0.165f, 0.17f, 0.95f), 3, 9, new Color(1, 1, 1, 0.08f));
            var hover = Box(new Color(0.26f, 0.24f, 0.2f, 0.98f), 3, 9, Accent);
            var pressed = Box(new Color(0.40f, 0.27f, 0.12f, 1f), 3, 9, Accent);
            var disabled = Box(new Color(0.11f, 0.11f, 0.115f, 0.8f), 3, 9);
            foreach (var type in new[] { "Button", "OptionButton" })
            {
                t.SetStylebox("normal", type, normal);
                t.SetStylebox("hover", type, hover);
                t.SetStylebox("pressed", type, pressed);
                t.SetStylebox("focus", type, Box(new Color(0, 0, 0, 0), 3, 9, Accent, 2));
                t.SetStylebox("disabled", type, disabled);
                t.SetColor("font_color", type, Paper);
                t.SetColor("font_hover_color", type, Colors.White);
                t.SetColor("font_pressed_color", type, Colors.White);
                t.SetColor("font_disabled_color", type, new Color(0.4f, 0.4f, 0.38f));
            }
            t.SetStylebox("panel", "PanelContainer", Box(Bg, 4, 22, new Color(1, 1, 1, 0.07f)));
            t.SetStylebox("panel", "Panel", Box(Bg, 4, 0));
            t.SetColor("font_color", "Label", Paper);
            t.SetStylebox("normal", "LineEdit", Box(new Color(0.03f, 0.03f, 0.035f, 1f), 3, 8, new Color(1, 1, 1, 0.15f)));
            t.SetStylebox("focus", "LineEdit", Box(new Color(0.03f, 0.03f, 0.035f, 1f), 3, 8, Accent));
            t.SetColor("font_color", "LineEdit", Paper);
            t.SetColor("font_color", "CheckBox", Paper);
            t.SetStylebox("panel", "TabContainer", Box(new Color(0.09f, 0.095f, 0.1f, 0.9f), 3, 16));
            t.SetStylebox("tab_selected", "TabContainer", Box(new Color(0.30f, 0.22f, 0.12f, 1f), 3, 10));
            t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0.13f, 0.135f, 0.14f, 1f), 3, 10));
            t.SetStylebox("tab_hovered", "TabContainer", Box(new Color(0.2f, 0.18f, 0.15f, 1f), 3, 10));
            t.SetColor("font_selected_color", "TabContainer", Colors.White);
            t.SetColor("font_unselected_color", "TabContainer", Dim);
            _theme = t;
            return t;
        }
    }

    public static Label Label(string text, int size = 18, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Paper);
        return l;
    }

    public static Button Button(string text, Action onPressed, int minWidth = 0)
    {
        var b = new Button { Text = text };
        if (minWidth > 0) b.CustomMinimumSize = new Vector2(minWidth, 0);
        b.Pressed += () =>
        {
            Main.I.Audio.Play("click", -6f);
            onPressed();
        };
        return b;
    }
}
