using System;
using Godot;

namespace DeadMiles;

/// <summary>Title screen: Continue, New Game (optional seed), Settings, Quit.</summary>
public partial class MainMenu : Control
{
    private VBoxContainer _buttons = null!;
    private PanelContainer _newGame = null!;
    private SettingsPanel _settings = null!;
    private LineEdit _seed = null!;
    private OptionButton? _world, _startAt;
    private Control? _seedRow, _startRow;
    private readonly System.Collections.Generic.List<MapPlace> _starts = new();

    public override void _Ready()
    {
        Theme = Ui.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Input.MouseMode = Input.MouseModeEnum.Visible;

        var bg = new ColorRect { Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/menu_bg.gdshader") } };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var left = new VBoxContainer();
        left.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        left.OffsetLeft = 90; left.OffsetRight = 560; left.OffsetTop = 110; left.OffsetBottom = -60;
        left.AddThemeConstantOverride("separation", 6);
        AddChild(left);
        left.AddChild(Ui.Label("DEAD MILES", 84, Ui.Paper));
        left.AddChild(Ui.Label("one road trip at a time", 20, Ui.Accent));
        left.AddChild(new Control { CustomMinimumSize = new Vector2(0, 48) });

        _buttons = new VBoxContainer();
        _buttons.AddThemeConstantOverride("separation", 10);
        left.AddChild(_buttons);
        var cont = Ui.Button("Continue", () => Main.I.ContinueGame(), 320);
        cont.Disabled = !SaveService.Exists();
        cont.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _buttons.AddChild(cont);
        _buttons.AddChild(Shrink(Ui.Button("New Game", ShowNewGame, 320)));
        _buttons.AddChild(Shrink(Ui.Button("Settings", () => { _settings.Visible = true; }, 320)));
        _buttons.AddChild(Shrink(Ui.Button("Quit", () => GetTree().Quit(), 320)));

        var foot = Ui.Label("WASD move / drive    E interact    Tab inventory    F light    X engine    Esc pause", 13, new Color(1, 1, 1, 0.45f));
        foot.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        foot.Position = new Vector2(90, -46);
        foot.GrowVertical = GrowDirection.Begin;
        AddChild(foot);

        if (MapData.Exists(MapWorld.DefaultMap))
        {
            // Required by the map data's licence (ODbL): credit where the player can see it.
            var credit = Ui.Label("Map data \u00a9 OpenStreetMap contributors", 13, new Color(1, 1, 1, 0.45f));
            credit.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
            credit.Position = new Vector2(-360, -46);
            credit.GrowVertical = GrowDirection.Begin;
            AddChild(credit);
        }

        _newGame = new PanelContainer { Visible = false };
        Center(_newGame);
        AddChild(_newGame);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        _newGame.AddChild(box);
        box.AddChild(Ui.Label("NEW GAME", 26, Ui.Accent));
        bool hasMap = MapData.Exists(MapWorld.DefaultMap);
        if (hasMap)
        {
            box.AddChild(Ui.Label("World", 15, Ui.Dim));
            _world = new OptionButton { CustomMinimumSize = new Vector2(360, 0) };
            _world.AddItem("Mazowsze - the real map");
            _world.AddItem("Endless road - generated from a seed");
            _world.ItemSelected += _ => SyncWorldChoice();
            box.AddChild(_world);

            var startBox = new VBoxContainer();
            startBox.AddThemeConstantOverride("separation", 6);
            startBox.AddChild(Ui.Label("Start near", 15, Ui.Dim));
            _startAt = new OptionButton { CustomMinimumSize = new Vector2(360, 0) };
            foreach (var p in MapData.StartPlaces(MapWorld.DefaultMap))
            {
                _starts.Add(p);
                _startAt.AddItem(p.Rank == 2 ? $"{p.Name} (countryside)" : p.Name);
            }
            startBox.AddChild(_startAt);
            box.AddChild(startBox);
            _startRow = startBox;
        }
        var seedBox = new VBoxContainer();
        seedBox.AddThemeConstantOverride("separation", 6);
        seedBox.AddChild(Ui.Label("World seed (leave empty for a random road)", 15, Ui.Dim));
        _seed = new LineEdit { PlaceholderText = "random", CustomMinimumSize = new Vector2(360, 0), MaxLength = 24 };
        _seed.TextSubmitted += _ => Start();
        seedBox.AddChild(_seed);
        box.AddChild(seedBox);
        _seedRow = seedBox;
        SyncWorldChoice();
        if (SaveService.Exists()) box.AddChild(Ui.Label("Starting a new game replaces your current save.", 14, Ui.Danger));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(Ui.Button("Start driving", Start, 190));
        row.AddChild(Ui.Button("Back", () => _newGame.Visible = false, 120));
        box.AddChild(row);

        _settings = new SettingsPanel { Visible = false };
        Center(_settings);
        _settings.Closed = () => _settings.Visible = false;
        AddChild(_settings);
    }

    private static Control Shrink(Control c) { c.SizeFlagsHorizontal = SizeFlags.ShrinkBegin; return c; }

    private static void Center(Control c)
    {
        c.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        c.GrowHorizontal = GrowDirection.Both;
        c.GrowVertical = GrowDirection.Both;
    }

    /// <summary>Opens the New Game panel (also used by the automated menu test).</summary>
    public void ShowNewGame()
    {
        _newGame.Visible = true;
        if (!MapChosen) _seed.GrabFocus();
    }

    private bool MapChosen => _world != null && _world.Selected == 0 && _starts.Count > 0;

    private void SyncWorldChoice()
    {
        if (_seedRow != null) _seedRow.Visible = !MapChosen;
        if (_startRow != null) _startRow.Visible = MapChosen;
    }

    private void Start()
    {
        if (MapChosen)
        {
            var p = _starts[Math.Clamp(_startAt!.Selected, 0, _starts.Count - 1)];
            Main.I.StartNewMapGame(MapWorld.DefaultMap, p.X, p.Z);
            return;
        }
        string text = _seed.Text.Trim();
        int seed;
        if (text.Length == 0) seed = (int)(GD.Randi() % 1000000);
        else if (!int.TryParse(text, out seed)) seed = (int)(Hash.OfString(text) % 1000000); // words work as seeds too
        Main.I.StartNewGame(seed);
    }
}
