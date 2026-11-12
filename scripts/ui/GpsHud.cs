using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// The small guidance strip under the compass while a destination is set on the map: an arrow
/// towards the road ahead, the distance left and the next turn.
/// </summary>
public partial class GpsHud : Control
{
    private GameWorld _g = null!;
    private Font _font = null!;

    public void Setup(GameWorld g)
    {
        _g = g;
        _font = ThemeDB.FallbackFont;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        bool show = _g.IsReady && (_g.Route != null || _g.Routing) && _g.MapView is { Visible: false };
        if (show != Visible) Visible = show;
        if (show) QueueRedraw();
    }

    private static string Distance(float m) => m >= 10000f ? $"{m / 1000f:0} km" : m >= 1000f ? $"{m / 1000f:0.0} km" : $"{MathF.Round(m / 10f) * 10f:0} m";

    public override void _Draw()
    {
        float cx = Size.X * 0.5f, y = 66f;
        string text;
        var route = _g.Route;
        if (route == null) text = "Calculating route...";
        else
        {
            text = Distance(_g.NavRemaining) + " to go";
            if (_g.NavTurn != 0) text += $"   -   turn {(_g.NavTurn > 0 ? "right" : "left")} in {Distance(_g.NavTurnDistance)}";
        }
        var ts = _font.GetStringSize(text, HorizontalAlignment.Left, -1, 16);
        float w = ts.X + 64f;
        DrawRect(new Rect2(cx - w * 0.5f, y - 15, w, 30), new Color(0, 0, 0, 0.42f));
        DrawString(_font, new Vector2(cx - w * 0.5f + 46, y + 6), text, HorizontalAlignment.Left, -1, 16, Ui.Paper);
        if (route == null) return;

        // arrow: where the route goes, relative to where you are facing
        var fwd3 = _g.Player.InVehicle ? -_g.Vehicle.GlobalTransform.Basis.Z : -_g.Player.GlobalTransform.Basis.Z;
        var fwd = new Vector2(fwd3.X, fwd3.Z);
        var pos3 = _g.Player.InVehicle ? _g.Vehicle.GlobalPosition : _g.Player.GlobalPosition;
        var to = _g.NavAhead - new Vector2(pos3.X, pos3.Z);
        if (fwd.LengthSquared() < 1e-4f || to.LengthSquared() < 1e-2f) return;
        float rel = fwd.AngleTo(to);   // + = to the right (clockwise seen from above with Z pointing south)
        var c = new Vector2(cx - w * 0.5f + 22, y);
        var up = new Vector2(MathF.Sin(rel), -MathF.Cos(rel));
        var side = new Vector2(-up.Y, up.X);
        DrawColoredPolygon(new[] { c + up * 11, c - up * 8 + side * 7, c - up * 3, c - up * 8 - side * 7 }, Ui.Accent);
    }
}
