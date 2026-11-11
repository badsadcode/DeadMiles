using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// Backpack on the left, and on the right whatever is open: the trunk or a searched container.
/// Deliberately simple lists limited by weight; double-click (or the buttons) to move or use.
/// </summary>
public partial class InventoryUi : Control
{
    private Inventory _player = null!;
    private Inventory? _other;
    private VBoxContainer _leftList = null!, _rightList = null!;
    private Label _leftTitle = null!, _rightTitle = null!, _desc = null!;
    private Control _rightColumn = null!;
    private Button _useBtn = null!, _moveBtn = null!, _moveAllBtn = null!, _discardBtn = null!, _takeAllBtn = null!;
    private string? _selected;
    private bool _selectedLeft = true;
    private GameWorld _g = null!;

    public void Setup(GameWorld g)
    {
        _g = g;
        _player = g.Player.Inventory;
        Theme = Ui.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        panel.AddChild(root);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 26);
        root.AddChild(columns);
        columns.AddChild(Column(out _leftTitle, out _leftList));
        columns.AddChild(_rightColumn = Column(out _rightTitle, out _rightList));

        _desc = Ui.Label("", 15, Ui.Dim);
        _desc.CustomMinimumSize = new Vector2(0, 44);
        _desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_desc);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        root.AddChild(buttons);
        buttons.AddChild(_useBtn = Ui.Button("Use", UseSelected, 90));
        buttons.AddChild(_moveBtn = Ui.Button("Move", () => MoveSelected(1), 90));
        buttons.AddChild(_moveAllBtn = Ui.Button("Move stack", () => MoveSelected(9999), 120));
        buttons.AddChild(_takeAllBtn = Ui.Button("Take all", TakeAll, 100));
        buttons.AddChild(_discardBtn = Ui.Button("Discard", DiscardSelected, 100));
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        buttons.AddChild(spacer);
        buttons.AddChild(Ui.Button("Close", Close, 100));

        _player.Changed += () => { if (Visible) Refresh(); };
    }

    private static Control Column(out Label title, out VBoxContainer list)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        col.AddThemeConstantOverride("separation", 8);
        title = Ui.Label("", 20, Ui.Accent);
        col.AddChild(title);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(380, 380), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        col.AddChild(scroll);
        list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);
        return col;
    }

    public void Open(Inventory? other)
    {
        if (_other != null) _other.Changed -= Refresh;
        _other = other;
        if (_other != null) _other.Changed += Refresh;
        _selected = null;
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Refresh();
    }

    public void Close()
    {
        if (!Visible) return;
        if (_other != null) _other.Changed -= Refresh;
        _other = null;
        Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void Refresh()
    {
        if (!Visible) return;
        _rightColumn.Visible = _other != null;
        _leftTitle.Text = $"Backpack   {_player.Weight:0.0} / {_player.Capacity:0} kg";
        Fill(_leftList, _player, true);
        if (_other != null)
        {
            _rightTitle.Text = _other.Capacity < 1000 ? $"{_other.Title}   {_other.Weight:0.0} / {_other.Capacity:0} kg" : _other.Title;
            Fill(_rightList, _other, false);
        }
        var inv = _selectedLeft ? _player : _other;
        if (_selected != null && (inv == null || !inv.Has(_selected))) _selected = null;
        var def = _selected != null ? ItemDb.Get(_selected) : null;
        _desc.Text = def != null ? $"{def.Name} - {def.Desc}" : (_other != null ? "Double-click an item to move it across." : "Double-click an item to use it.");
        _useBtn.Disabled = def == null || !def.Usable || !_selectedLeft;
        _moveBtn.Disabled = def == null || _other == null;
        _moveAllBtn.Disabled = def == null || _other == null;
        _takeAllBtn.Disabled = _other == null || _other.Items.Count == 0;
        _discardBtn.Disabled = def == null;
    }

    private void Fill(VBoxContainer list, Inventory inv, bool left)
    {
        foreach (var c in list.GetChildren()) c.QueueFree();
        if (inv.Items.Count == 0)
        {
            list.AddChild(Ui.Label(left ? "Nothing but lint." : "Empty.", 15, Ui.Dim));
            return;
        }
        foreach (var st in inv.Items)
        {
            var def = ItemDb.Get(st.Id);
            string id = st.Id;
            var b = new Button
            {
                Text = $"{def.Name}{(st.Count > 1 ? "  x" + st.Count : "")}",
                Alignment = HorizontalAlignment.Left,
                ToggleMode = true,
                ButtonPressed = _selected == id && _selectedLeft == left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var weight = Ui.Label($"{def.Weight * st.Count:0.0} kg", 13, Ui.Dim);
            weight.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterRight);
            weight.Position -= new Vector2(64, 0);
            weight.MouseFilter = MouseFilterEnum.Ignore;
            b.AddChild(weight);
            b.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
                {
                    _selected = id;
                    _selectedLeft = left;
                    if (mb.DoubleClick)
                    {
                        if (_other != null) MoveSelected(1);
                        else UseSelected();
                    }
                    else Callable.From(Refresh).CallDeferred();
                    AcceptEvent();
                }
            };
            list.AddChild(b);
        }
    }

    private void UseSelected()
    {
        if (_selected == null || !_selectedLeft) return;
        string id = _selected;
        if (!ItemDb.Get(id).Usable) return;
        if (ItemDb.Use(id, _g)) _player.Remove(id, 1);
        Refresh();
    }

    private void MoveSelected(int count)
    {
        if (_selected == null || _other == null) return;
        var from = _selectedLeft ? _player : _other;
        var to = _selectedLeft ? _other : _player;
        int moved = from.MoveTo(to, _selected, count);
        if (moved == 0) _g.Toast(_selectedLeft ? "There is no room for that." : "Too heavy. Your backpack is full.");
        else Main.I.Audio.Play("pickup", -8f);
        Refresh();
    }

    private void TakeAll()
    {
        if (_other == null) return;
        bool left = false;
        foreach (var st in _other.Snapshot())
        {
            int moved = _other.MoveTo(_player, st.Id, st.Count);
            if (moved < st.Count) left = true;
        }
        if (left) _g.Toast("You cannot carry all of it.");
        Main.I.Audio.Play("pickup", -8f);
        Refresh();
    }

    private void DiscardSelected()
    {
        if (_selected == null) return;
        var inv = _selectedLeft ? _player : _other;
        inv?.Remove(_selected, 1);
        Refresh();
    }
}
