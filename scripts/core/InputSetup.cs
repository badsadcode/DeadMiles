using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Defines every input action in code (keyboard, mouse and gamepad) so the project needs no
/// hand-edited input map, and lets the settings screen rebind the keyboard side.
/// </summary>
public static class InputSetup
{
    public sealed record ActionInfo(string Name, string Label, Key DefaultKey, bool Rebindable = true);

    public static readonly List<ActionInfo> Actions = new()
    {
        new("move_forward", "Walk forward", Key.W),
        new("move_back", "Walk back", Key.S),
        new("move_left", "Strafe left", Key.A),
        new("move_right", "Strafe right", Key.D),
        new("jump", "Jump", Key.Space),
        new("sprint", "Sprint", Key.Shift),
        new("crouch", "Crouch", Key.Ctrl),
        new("interact", "Interact / enter / exit", Key.E),
        new("inventory", "Inventory", Key.Tab),
        new("flashlight", "Flashlight / headlights", Key.F),
        new("slot1", "Empty hands", Key.Key1),
        new("slot2", "Crowbar", Key.Key2),
        new("slot3", "Rifle", Key.Key3),
        new("accelerate", "Drive: accelerate", Key.W),
        new("brake", "Drive: brake / reverse", Key.S),
        new("steer_left", "Drive: steer left", Key.A),
        new("steer_right", "Drive: steer right", Key.D),
        new("handbrake", "Drive: handbrake", Key.Space),
        new("engine_toggle", "Drive: engine on / off", Key.X),
        new("horn", "Drive: horn", Key.H),
        new("camera_toggle", "Drive: change camera", Key.C),
        new("recover", "Drive: recover car", Key.R),
        new("quicksave", "Quick save", Key.F5),
        new("pause", "Pause", Key.Escape, false),
    };

    public static void Install()
    {
        foreach (var a in Actions)
        {
            if (!InputMap.HasAction(a.Name)) InputMap.AddAction(a.Name, 0.3f);
            SetKey(a.Name, a.DefaultKey);
        }
        foreach (var n in new[] { "attack", "aim", "look_left", "look_right", "look_up", "look_down" })
            if (!InputMap.HasAction(n)) InputMap.AddAction(n, 0.25f);

        Mouse("attack", MouseButton.Left);
        Mouse("aim", MouseButton.Right);

        // gamepad
        Axis("move_left", JoyAxis.LeftX, -1); Axis("move_right", JoyAxis.LeftX, 1);
        Axis("move_forward", JoyAxis.LeftY, -1); Axis("move_back", JoyAxis.LeftY, 1);
        Axis("look_left", JoyAxis.RightX, -1); Axis("look_right", JoyAxis.RightX, 1);
        Axis("look_up", JoyAxis.RightY, -1); Axis("look_down", JoyAxis.RightY, 1);
        Axis("steer_left", JoyAxis.LeftX, -1); Axis("steer_right", JoyAxis.LeftX, 1);
        Axis("accelerate", JoyAxis.TriggerRight, 1); Axis("brake", JoyAxis.TriggerLeft, 1);
        Axis("attack", JoyAxis.TriggerRight, 1); Axis("aim", JoyAxis.TriggerLeft, 1);
        Pad("jump", JoyButton.A); Pad("handbrake", JoyButton.A);
        Pad("crouch", JoyButton.B);
        Pad("interact", JoyButton.X);
        Pad("inventory", JoyButton.Y);
        Pad("sprint", JoyButton.LeftStick);
        Pad("camera_toggle", JoyButton.RightStick);
        Pad("flashlight", JoyButton.DpadUp);
        Pad("engine_toggle", JoyButton.DpadDown);
        Pad("horn", JoyButton.DpadLeft);
        Pad("recover", JoyButton.Back);
        Pad("pause", JoyButton.Start);
        Pad("slot2", JoyButton.LeftShoulder);
        Pad("slot3", JoyButton.RightShoulder);
    }

    private static void Mouse(string action, MouseButton b) => InputMap.ActionAddEvent(action, new InputEventMouseButton { ButtonIndex = b });
    private static void Pad(string action, JoyButton b) => InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = b });
    private static void Axis(string action, JoyAxis axis, float dir) => InputMap.ActionAddEvent(action, new InputEventJoypadMotion { Axis = axis, AxisValue = dir });

    /// <summary>Replaces the keyboard binding of an action, leaving mouse and gamepad events alone.</summary>
    public static void SetKey(string action, Key key)
    {
        foreach (var e in InputMap.ActionGetEvents(action))
            if (e is InputEventKey) InputMap.ActionEraseEvent(action, e);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }

    public static Key GetKey(string action)
    {
        foreach (var e in InputMap.ActionGetEvents(action))
            if (e is InputEventKey k) return k.PhysicalKeycode;
        return Key.None;
    }

    public static string KeyName(string action) => OS.GetKeycodeString(GetKey(action));

    public static void ApplyKeys(Settings s)
    {
        foreach (var a in Actions)
            SetKey(a.Name, s.Keys.TryGetValue(a.Name, out long k) ? (Key)k : a.DefaultKey);
    }
}
