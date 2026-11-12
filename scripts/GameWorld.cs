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
            var gps = new GpsHud { Name = "Gps", Visible = false };
            layer.AddChild(gps);
            gps.Setup(this);
        }
        Pause = new PauseMenu { Name = "Pause" };
        layer.AddChild(Pause);
        Pause.Setup(this);

        Encounters = new EncounterDirector { Name = "Encounters" };
        AddChild(Encounters);
        Encounters.Setup(this);

        if (IsNewGame) PlaceNewGame(); else Restore();
        if (World.Map != null && State.Destination is { Length: 2 } dest) Destination = new Vector2(dest[0], dest[1]);

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
                if (Destination != null) StartRouting();
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

        if (World.Map != null) UpdateNavigation(delta);
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

    // ---- navigation (real-map world) ----------------------------------------------------------

    /// <summary>The current GPS route, or null. <see cref="Routing"/> is true while one is being worked out.</summary>
    public MapRoute? Route { get; private set; }
    public bool Routing { get; private set; }
    public Vector2? Destination { get; private set; }
    public float NavRemaining { get; private set; }
    /// <summary>Next turn: -1 left, +1 right, 0 none in sight; and how far ahead it is.</summary>
    public int NavTurn { get; private set; }
    public float NavTurnDistance { get; private set; }
    /// <summary>A point on the route a little way ahead, for the guidance arrow.</summary>
    public Vector2 NavAhead { get; private set; }
    private int _routeVersion, _routeIndex;
    private double _navTimer;
    private bool _teleporting;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int version, MapRoute? route)> _routeDone = new();

    private Vector2 FocusXZ
    {
        get
        {
            var p = Player.InVehicle ? Vehicle.GlobalPosition : Player.GlobalPosition;
            return new Vector2(p.X, p.Z);
        }
    }

    /// <summary>Sets (or with null, clears) the GPS destination and starts working out a route.</summary>
    public void SetDestination(Vector2? destination)
    {
        Destination = destination;
        State.Destination = destination == null ? null : new[] { destination.Value.X, destination.Value.Y };
        Route = null;
        NavTurn = 0;
        _routeVersion++;
        Routing = false;
        if (destination != null) StartRouting();
    }

    private void StartRouting()
    {
        if (World.Map == null || Destination == null) return;
        int version = ++_routeVersion;
        Routing = true;
        var from = FocusXZ; var to = Destination.Value;
        var data = World.Map.Data;
        System.Threading.Tasks.Task.Run(() =>
        {
            MapRoute? route = null;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                route = new MapRouter(data).Find(from, to, () => _routeVersion != version);
                Log.Info("GameWorld", $"Routing took {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalSeconds:0.0} s ({(route == null ? "no route" : $"{route.Length / 1000f:0.0} km")})");
            }
            catch (Exception e) { GD.PushError($"[GameWorld] Routing failed: {e}"); }
            _routeDone.Enqueue((version, route));
        });
    }

    private void UpdateNavigation(double delta)
    {
        while (_routeDone.TryDequeue(out var done))
        {
            if (done.version != _routeVersion) continue;
            Routing = false;
            Route = done.route;
            _routeIndex = 0;
            if (Route == null) Toast("No road route found to that spot.");
            else Toast($"Route set: {Route.Length / 1000f:0.0} km.");
        }
        if (_teleporting && Streamer.IsSolidAt(Player.GlobalPosition))
        {
            _teleporting = false;
            Player.SetPhysicsProcess(true);
        }
        _navTimer -= delta;
        if (Route == null || _navTimer > 0) return;
        _navTimer = 0.25;
        var route = Route;
        var pos = FocusXZ;
        int idx = route.Nearest(pos, _routeIndex, out float off);
        _routeIndex = idx;
        NavRemaining = route.Length - route.Along[idx];
        if (NavRemaining < 35f && off < 45f)
        {
            Toast("You have arrived.");
            SetDestination(null);
            return;
        }
        if (off > 85f && !Routing) { StartRouting(); return; }   // wandered off: plan again from here

        // a point about 45 m ahead for the arrow
        int ahead = idx;
        while (ahead < route.Points.Length - 1 && route.Along[ahead] - route.Along[idx] < 45f) ahead++;
        NavAhead = route.Points[ahead];

        // the next real turn: where the heading swings by more than 40 degrees within a few points
        NavTurn = 0;
        for (int j = Math.Max(idx + 1, 2); j < route.Points.Length - 2; j++)
        {
            float ahead_m = route.Along[j] - route.Along[idx];
            if (ahead_m > 3000f) break;
            var a = route.Points[j] - route.Points[j - 2];
            var b = route.Points[j + 2] - route.Points[j];
            if (a.LengthSquared() < 1f || b.LengthSquared() < 1f) continue;
            float turn = a.AngleTo(b);
            if (MathF.Abs(turn) < Mathf.DegToRad(40f)) continue;
            NavTurn = turn > 0 ? 1 : -1;
            NavTurnDistance = MathF.Max(0f, ahead_m);
            break;
        }
    }

    /// <summary>
    /// Test-phase fast travel: puts the car (and the player with or beside it) on the nearest road
    /// to a map position. The world streams in around the new spot before anything moves.
    /// </summary>
    public void TeleportTo(float x, float z)
    {
        var map = World.Map;
        if (map == null || !IsReady) return;
        float yaw = 0f;
        if (!map.NearestRoad(x, z, 600f, 1.4f, out var pos, out yaw)) pos = new Vector3(x, map.Height(x, z), z);
        Vehicle.Warp(new Transform3D(new Basis(Vector3.Up, yaw), pos + Vector3.Up * 0.45f));
        if (!Player.InVehicle)
        {
            var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
            var pp = pos + right * 2.4f;
            pp.Y = map.Height(pp.X, pp.Z) + 0.2f;
            Player.GlobalPosition = pp;
            Player.SetPhysicsProcess(false);
            _teleporting = true;
        }
        if (Destination != null) StartRouting();
        var place = map.NearestPlace(pos.X, pos.Z, 5000f);
        Toast(place != null ? $"Moved to the road near {place.Name}." : "Moved to the nearest road.");
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
        else if (e.IsActionPressed("recover") && !Player.InVehicle && NearbyVehicle(20f) != null)
        {
            // also works from outside, standing by the car (after climbing out of a wreck, say)
            Vehicle.Recover();
            GetViewport().SetInputAsHandled();
        }
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
