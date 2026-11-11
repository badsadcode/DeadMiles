using Godot;

namespace DeadMiles;

/// <summary>Pause overlay: resume, manual save, settings, back to the menu or quit.</summary>
public partial class PauseMenu : Control
{
    private PanelContainer _menu = null!;
    private SettingsPanel _settings = null!;
    private GameWorld _g = null!;

    public void Setup(GameWorld g)
    {
        _g = g;
        Theme = Ui.Theme;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        _menu = new PanelContainer();
        Center(_menu);
        AddChild(_menu);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _menu.AddChild(box);
        box.AddChild(Ui.Label("PAUSED", 30, Ui.Accent));
        box.AddChild(Ui.Label("The road will wait.", 15, Ui.Dim));
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        box.AddChild(Ui.Button("Resume", Close, 300));
        box.AddChild(Ui.Button("Save game", () => { _g.SaveGame(true); Close(); }, 300));
        box.AddChild(Ui.Button("Settings", () => { _menu.Visible = false; _settings.Visible = true; }, 300));
        box.AddChild(Ui.Button("Save and return to menu", () => { _g.SaveGame(false); GetTree().Paused = false; Main.I.ShowMenu(); }, 300));
        box.AddChild(Ui.Button("Save and quit", () => { _g.SaveGame(false); GetTree().Quit(); }, 300));

        _settings = new SettingsPanel { Visible = false };
        Center(_settings);
        _settings.Closed = () => { _settings.Visible = false; _menu.Visible = true; };
        AddChild(_settings);
    }

    private static void Center(Control c)
    {
        c.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        c.GrowHorizontal = GrowDirection.Both;
        c.GrowVertical = GrowDirection.Both;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible || !e.IsActionPressed("pause")) return;
        if (_settings.Visible) { _settings.Visible = false; _menu.Visible = true; }
        else Close();
        GetViewport().SetInputAsHandled();
    }

    public void Open()
    {
        Visible = true;
        _menu.Visible = true;
        _settings.Visible = false;
        GetTree().Paused = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Close()
    {
        Visible = false;
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }
}
