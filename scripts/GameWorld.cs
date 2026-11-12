using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// One running game: creates every system for a seed, places (or restores) the player and the
/// car, and owns the things that cut across systems: containers, sleeping, saving and death.
/// </summary>
public partial class GameWorld : Node3D
{
    public static GameWorld I = null!;

    public delegate void AutopilotFn(VehicleController v, ref float throttle, ref float brake, ref float steer);
    /// <summary>Test hook: lets the debug harness drive the car.</summary>
    public AutopilotFn? Autopilot;

    public SaveData State = new();
    public bool IsNewGame;

    public WorldGenerator World = null!;
    public ChunkStreamer Streamer = null!;
    public DayNightCycle Time = null!;
    public WeatherDirector Weather = null!;
    public PlayerController Player = null!;
    public VehicleController Vehicle = null!;
    public EncounterDirector Encounters = null!;
    public Hud Hud = null!;
    public InventoryUi InventoryUi = null!;
    public PauseMenu Pause = null!;
    /// <summary>The road atlas; only exists in the real-map world.</summary>
    public MapScreen? MapView;
    public PostFx PostFx = null!;
    public AudioDirector Audio => Main.I.Audio;

    public bool IsReady { get; private set; }
    public bool UiOpen => !IsReady || _sleeping || InventoryUi.Visible || Pause.Visible || Player.Stats.Dead || MapView is { Visible: true };

    private readonly Dictionary<string, Inventory> _containers = new();
    private bool _sleeping, _sheltered;
    private double _autosave = 180, _shelterTimer, _loadTime;
    private readonly Queue<(double at, string text)> _intro = new();

    public override void _Ready()
    {
        I = this;
        ProcessMode = ProcessModeEnum.Pausable;
        Log.Info("GameWorld", IsNewGame ? $"New game, seed {State.Seed}" : $"Continuing seed {State.Seed}, day {State.Day}");

        MapWorld? map = State.Map.Length > 0 ? MapWorld.TryOpen(State.Map) : null;
        if (State.Map.Length > 0 && map == null)
            Log.Error("GameWorld", $"This save needs the map '{State.Map}', which is missing. Falling back to the generated road.");
        World = new WorldGenerator(State.Seed, map);
        var veg = new VegetationLibrary();
        Streamer = new ChunkStreamer { Name = "World" };
        AddChild(Streamer);
        Streamer.Setup(World, veg, new PoiBuilder(World));

        Weather = new WeatherDirector { Name = "Weather" };
        AddChild(Weather);
        Time = new DayNightCycle { Name = "DayNight", Weather = Weather };
        AddChild(Time);

        Player = new PlayerController { Name = "Player" };
        AddChild(Player);
        Vehicle = new VehicleController { Name = "Car", Freeze = true, FreezeMode = RigidBody3D.FreezeModeEnum.Static };
        AddChild(Vehicle);

        PostFx = new PostFx { Name = "PostFx" };
        AddChild(PostFx);
        var layer = new CanvasLayer { Name = "UI", Layer = 2 };
        AddChild(layer);
        Hud = new Hud { Name = "Hud" };
        layer.AddChild(Hud);
        Hud.Setup(this);
        InventoryUi = new InventoryUi { Name = "Inventory" };
        layer.AddChild(InventoryUi);
        InventoryUi.Setup(this);
        if (World.Map != null)
        {
            MapView = new MapScreen { Name = "Map" };
            layer.AddChild(MapView);
            MapView.Setup(this, World.Map);
        }
        Pause = new PauseMenu { Name = "Pause" };
        layer.AddChild(Pause);
        Pause.Setup(this);

        Encounters = new EncounterDirector { Name = "Encounters" };
        AddChild(Encounters);
        Encounters.Setup(this);

        if (IsNewGame) PlaceNewGame(); else Restore();

        Streamer.Focus = Player;
        ApplySettings(Main.I.Settings);
        Player.SetPhysicsProcess(false);
        Hud.SetLoading("Finding the road...");
        Audio.InGame = true;
    }

    public override void _ExitTree()
    {
        if (I == this) I = null!;
        Audio.InGame = false;
        GetTree().Paused = false;
    }

    /// <summary>Real-map start: the car is parked on the nearest quiet road, the player stands beside it.</summary>
    private void PlaceNewGameOnMap(MapWorld map)
    {
        float sx = State.MapStart[0], sz = State.MapStart[1];
        Vector3 road = new(sx, map.Height(sx, sz), sz);
        float yaw = 0f;
        bool found = false;
        foreach (var (radius, cls) in new[] { (150f, RoadClass.Residential), (500f, RoadClass.Residential), (1400f, RoadClass.Track) })
        {
            if (!map.NearestRoad(sx, sz, radius, 1.4f, out var p, out float y, cls)) continue;
            road = p; yaw = y; found = true;
            break;
        }
        if (!found) Log.Warn("GameWorld", $"No road within reach of the start point ({sx:0}, {sz:0}); starting in the open");
        Vehicle.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), road + Vector3.Up * 0.45f);
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var pp = road + right * 2.4f;
        for (int i = 0; i < 6 && map.InBuilding(pp.X, pp.Z, 0.5f); i++) pp -= right * 0.8f;
        pp.Y = map.Height(pp.X, pp.Z) + 0.15f;
        Player.GlobalPosition = pp;
        Player.Yaw = yaw + Mathf.Pi * 0.5f;   // facing the car

        Player.Inventory.Add("water", 1);
        Player.Inventory.Add("chocolate", 1);
        foreach (var item in new[] { "crowbar", "fuel_can", "beans", "bandage", "empty_bottle", "water", "beans" })
            Vehicle.Trunk.Add(item, 1);
        Vehicle.Fuel = State.Fuel;
        Vehicle.EngineHealth = State.EngineHealth;
        Vehicle.TireHealth = State.TireHealth;
        Time.TimeOfDay = State.TimeOfDay;
        Time.Day = State.Day;
        Weather.Restore((int)WeatherState.Clear, 260f, 0f);

        var place = map.NearestPlace(pp.X, pp.Z, 6000f);
        _intro.Enqueue((2.5, place != null ? $"{place.Name}. Your supplies are in the trunk." : "Your supplies are in the trunk."));
        _intro.Enqueue((10, $"[{InputSetup.KeyName("interact")}] to get in the car. The trunk is at the back."));
        _intro.Enqueue((26, $"Fuel is low. [{InputSetup.KeyName("map")}] opens the map; fuel stations are marked in orange."));
    }

    private void PlaceNewGame()
    {
        if (World.Map != null) { PlaceNewGameOnMap(World.Map); return; }
        var poi = World.Pois.ForSegment(0)[0];
        var carPos = poi.ToWorld(new Vector3(8.5f, 0, -7.5f));
        carPos.Y = World.Height(carPos.X, carPos.Z) + 0.45f;
        Vehicle.GlobalTransform = new Transform3D(new Basis(Vector3.Up, World.Road.YawAt(carPos.Z)), carPos);
        var pp = poi.ToWorld(new Vector3(0.4f, 0, 2.6f));
        pp.Y = World.Height(pp.X, pp.Z) + 0.15f;
        Player.GlobalPosition = pp;
        Player.Yaw = poi.Yaw;

        Player.Inventory.Add("water", 1);
        Player.Inventory.Add("chocolate", 1);
        Vehicle.Trunk.Add("crowbar", 1);
        Vehicle.Trunk.Add("fuel_can", 1);
        Vehicle.Trunk.Add("beans", 1);
        Vehicle.Trunk.Add("bandage", 1);
        Vehicle.Trunk.Add("empty_bottle", 1);
        Vehicle.Fuel = State.Fuel;
        Vehicle.EngineHealth = State.EngineHealth;
        Vehicle.TireHealth = State.TireHealth;

        Time.TimeOfDay = State.TimeOfDay;
        Time.Day = State.Day;
        Weather.Restore((int)WeatherState.Clear, 260f, 0f);

        _intro.Enqueue((2.5, "Your supplies are in the crate inside the shelter."));
        _intro.Enqueue((10, $"The car is parked by the road. [{InputSetup.KeyName("interact")}] to get in. The trunk is at the back."));
        _intro.Enqueue((26, "Fuel is low. Watch the roadside for signs."));
    }

    private void Restore()
    {
        Player.LoadState(State);
        if (State.HasVehicle) Vehicle.LoadState(State);
        Time.TimeOfDay = State.TimeOfDay;
        Time.Day = State.Day;
        Weather.Restore(State.WeatherState, State.WeatherTimer, State.Wetness);
    }

    public void ApplySettings(Settings s)
    {
        if (Streamer == null) return;
        Streamer.ViewRadius = s.ViewDistance;
        Streamer.Density = s.VegetationDensity;
        if (Streamer.GrassEnabled && !s.Grass) Streamer.ClearGrass();
        Streamer.GrassEnabled = s.Grass;
        Time.Env.SsaoEnabled = s.Ssao;
        Time.Env.VolumetricFogEnabled = s.VolumetricFog;
        PostFx.Enabled = s.PostEffects;
        Time.Sun.ShadowEnabled = s.ShadowQuality > 0;
        Time.Moon.ShadowEnabled = s.ShadowQuality > 1;
        Time.Sun.DirectionalShadowMaxDistance = s.ShadowQuality switch { 1 => 90f, 3 => 190f, _ => 140f };
    }

    // ---- frame ---------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!IsReady)
        {
            _loadTime += delta;
            // Wait for ground collision under the player before letting anything move.
            if (_loadTime > 0.4 && Streamer.IsSolidAt(Player.GlobalPosition))
            {
                IsReady = true;
                Player.SetPhysicsProcess(true);
                Hud.SetLoading("");
                Hud.SetBlack();
                Hud.Fade(false);
                Input.MouseMode = Input.MouseModeEnum.Captured;
                if (!IsNewGame && State.InVehicle && State.HasVehicle) Player.EnterVehicle(Vehicle);
                Log.Info("GameWorld", $"World ready in {_loadTime:0.0}s ({Streamer.LoadedChunks} chunks)");
                _loadTime = 0;
            }
            return;
        }

        State.PlayTime += delta;
        _loadTime += delta;
        while (_intro.Count > 0 && _loadTime > _intro.Peek().at) Hud.Hint(_intro.Dequeue().text);

        _shelterTimer -= delta;
        if (_shelterTimer <= 0)
        {
            _shelterTimer = 0.25;
            if (Player.InVehicle) _sheltered = true;
            else
            {
                var from = Player.GlobalPosition + Vector3.Up * 1.7f;
                var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Up * 10f, 1);
                _sheltered = GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
            }
        }
        var audio = Audio;
        audio.WindLevel = Weather.Wind;
        audio.RainLevel = Weather.Rain;
        audio.Daylight = Time.Daylight;
        audio.Sheltered = _sheltered;
        audio.ListenerPos = Player.GlobalPosition;

        if (World.Map != null && (_placeTimer -= delta) <= 0)
        {
            _placeTimer = 2.0;
            AnnouncePlace(World.Map);
        }

        _autosave -= delta;
        if (_autosave <= 0)
        {
            _autosave = 180;
            SaveGame(false);
        }
    }

    private double _placeTimer = 6;
    private string _lastPlace = "";

    /// <summary>A quiet line on screen when the player reaches a named town or village.</summary>
    private void AnnouncePlace(MapWorld map)
    {
        var pos = Player.GlobalPosition;
        MapPlace? here = null;
        float best = float.MaxValue;
        foreach (var p in map.Data.Places)
        {
            float reach = p.Rank == 0 ? 6500f : p.Rank == 1 ? 2200f : 700f;
            float d = new Vector2(p.X - pos.X, p.Z - pos.Z).Length();
            if (d > reach) continue;
            float score = d / reach + p.Rank * 0.01f;
            if (score < best) { best = score; here = p; }
        }
        string name = here?.Name ?? "";
        if (name == _lastPlace) return;
        _lastPlace = name;
        if (name.Length > 0 && _loadTime > 8) Toast(name);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsReady || Player.Stats.Dead || _sleeping) return;
        if (e.IsActionPressed("map") && MapView != null && !InventoryUi.Visible && !Pause.Visible)
        {
            MapView.Toggle();
            GetViewport().SetInputAsHandled();
        }
        else if (MapView is { Visible: true }) return;   // the map handles its own keys
        else if (e.IsActionPressed("pause"))
        {
            if (InventoryUi.Visible) InventoryUi.Close(); else Pause.Open();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed("inventory"))
        {
            if (InventoryUi.Visible) InventoryUi.Close();
            else InventoryUi.Open(Player.InVehicle ? Vehicle.Trunk : null);
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed("interact") && InventoryUi.Visible)
        {
            InventoryUi.Close();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed("quicksave")) SaveGame(true);
    }

    // ---- services for other systems ------------------------------------------------------

    public void Toast(string text) => Hud.Toast(text);

    /// <summary>The car, if it is within reach of the given point (default: the player).</summary>
    public VehicleController? NearbyVehicle(float distance, Vector3? from = null)
    {
        var p = from ?? Player.GlobalPosition;
        return Vehicle.GlobalPosition.DistanceTo(p) <= distance ? Vehicle : null;
    }

    public string ContainerPrompt(string id, string title)
        => State.Containers.TryGetValue(id, out var items) && items.Count == 0 ? $"{title} (empty)" : $"Search: {title}";

    /// <summary>Containers are rolled from the seed on first open; afterwards the save holds what is left.</summary>
    public Inventory GetContainer(string id, string title, string table, uint seed)
    {
        if (_containers.TryGetValue(id, out var inv)) return inv;
        inv = new Inventory(title, 99999f);
        if (State.Containers.TryGetValue(id, out var saved)) inv.Load(saved);
        else
        {
            inv.Load(LootSpawner.Generate(table, seed));
            State.Containers[id] = inv.Snapshot();
        }
        var captured = inv;
        inv.Changed += () => State.Containers[id] = captured.Snapshot();
        _containers[id] = inv;
        return inv;
    }

    public void OpenContainer(string id, string title, string table, uint seed)
    {
        var inv = GetContainer(id, title, table, seed);
        Audio.Play("open", -4f);
        InventoryUi.Open(inv);
    }

    public void OpenTrunk()
    {
        Audio.Play("door", -4f);
        InventoryUi.Open(Vehicle.Trunk);
    }

    public async void Sleep(string message)
    {
        if (_sleeping || !IsReady) return;
        if (Encounters.HostileNear(Player.GlobalPosition, 50f)) { Toast("You cannot sleep with someone hunting you."); return; }
        _sleeping = true;
        Hud.Fade(true);
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;
        float t = Time.TimeOfDay;
        float hours = (t >= 19f || t < 5f) ? (6.5f - t + 24f) % 24f : 3f;
        Time.AdvanceHours(hours);
        Weather.Skip(hours * DayNightCycle.SecondsPerHour);
        var s = Player.Stats;
        s.Hunger = MathF.Max(8f, s.Hunger - hours * 3.2f);
        s.Thirst = MathF.Max(8f, s.Thirst - hours * 4.2f);
        s.Heal(hours * 9f);
        s.Stamina = 100;
        SaveGame(false);
        await ToSignal(GetTree().CreateTimer(0.9), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;
        Hud.Fade(false);
        _sleeping = false;
        Toast($"{message} It is {Time.ClockText} on day {Time.Day}.");
    }

    public void OnPlayerDied(string cause)
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        InventoryUi.Close();
        Input.MouseMode = Input.MouseModeEnum.Visible;
        string text = cause switch
        {
            "thirst" => "You died of thirst, a day's walk from water.",
            "hunger" => "You starved. The road does not feed anyone.",
            "attack" => "Someone wanted what you had.",
            "crash" => "The wreck will be one more thing for someone else to find.",
            _ => "You did not make it.",
        };
        Log.Info("GameWorld", $"Player died: {cause}");
        Hud.ShowDeath(text);
    }

    public bool SaveGame(bool announce)
    {
        if (!IsReady || Player.Stats.Dead) return false;
        State.Day = Time.Day;
        State.TimeOfDay = Time.TimeOfDay;
        State.WeatherState = (int)Weather.State;
        State.WeatherTimer = Weather.Timer;
        State.Wetness = Weather.Wetness;
        Player.SaveState(State);
        Vehicle.SaveState(State);
        bool ok = SaveService.Write(State);
        if (announce)
        {
            Toast(ok ? "Game saved." : "Saving failed. See the log.");
            if (ok) Audio.Play("save", -6f);
        }
        return ok;
    }
}
