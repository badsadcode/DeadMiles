using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace DeadMiles;

/// <summary>
/// Automated smoke tests, started with:  godot -- --autotest=start,drive,night --shots=DIR [--seed=N]
/// Each scenario drives the real game (new game, autopilot, weather, saving...) and writes
/// screenshots and numbers to the log, so a build can be checked without hands on the keyboard.
/// It uses its own save slot and never touches the player's save.
/// </summary>
public partial class DebugHarness : Node
{
    private string[] _tests = Array.Empty<string>();
    private string _shotDir = "user://shots";
    private int _seed = 1234;
    private int _shotIndex;
    private string _map = "";
    private string _place = "";
    private string _views = "";
    private float _startX, _startZ;

    public static DebugHarness? TryCreate()
    {
        string? tests = null, shots = null;
        int seed = 1234;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--autotest=")) tests = arg["--autotest=".Length..];
            else if (arg.StartsWith("--shots=")) shots = arg["--shots=".Length..];
            else if (arg.StartsWith("--seed=")) int.TryParse(arg["--seed=".Length..], out seed);
        }
        if (tests == null) return null;
        var h = new DebugHarness { Name = "DebugHarness", _tests = tests.Split(',', StringSplitOptions.RemoveEmptyEntries), _seed = seed };
        // real-map runs:  --map=mazowsze  and either --place=Radom or --start=x,z (world metres)
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--map=")) h._map = arg["--map=".Length..];
            else if (arg.StartsWith("--views=")) h._views = arg["--views=".Length..];
            else if (arg.StartsWith("--place=")) h._place = arg["--place=".Length..];
            else if (arg.StartsWith("--start="))
            {
                var parts = arg["--start=".Length..].Split(',');
                if (parts.Length == 2)
                {
                    float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h._startX);
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out h._startZ);
                }
            }
        }
        if (shots != null) h._shotDir = shots;
        return h;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SaveService.Path = "user://save_test.json";
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(_shotDir));
        _ = Run();
    }

    private static void Report(string msg) => GD.Print("[Test] " + msg);

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task WaitReady(double timeout = 60)
    {
        double t = 0;
        await Wait(0.2);
        while ((GameWorld.I == null || !GameWorld.I.IsReady) && t < timeout) { await Wait(0.1); t += 0.1; }
        if (GameWorld.I == null || !GameWorld.I.IsReady) Report("ERROR: world did not become ready");
    }

    private async Task Shot(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = GetViewport().GetTexture().GetImage();
        string path = $"{_shotDir}/{_shotIndex++:00}_{name}.png";
        var err = img.SavePng(path);
        Report(err == Error.Ok ? $"shot {path}  fps={Engine.GetFramesPerSecond():0}" : $"ERROR saving {path}: {err}");
    }

    private async Task Teleport(Vector3 pos, float yaw, float pitch = 0f)
    {
        var g = GameWorld.I;
        if (g.Player.InVehicle) g.Player.ExitVehicle();
        g.Player.SetPhysicsProcess(false);
        pos.Y = g.World.Height(pos.X, pos.Z) + 0.2f;
        g.Player.GlobalPosition = pos;
        g.Player.SetLook(yaw, pitch);
        double t = 0;
        while (!g.Streamer.IsSolidAt(pos) && t < 30) { await Wait(0.1); t += 0.1; }
        await Wait(0.3);
        g.Player.SetPhysicsProcess(true);
    }

    private async Task Run()
    {
        try
        {
            foreach (var test in _tests)
            {
                Report($"--- {test} ---");
                switch (test)
                {
                    case "menu": await TestMenu(); break;
                    case "start": await TestStart(); break;
                    case "drive": await TestDrive(); break;
                    case "night": await TestNight(); break;
                    case "weather": await TestWeather(); break;
                    case "pois": await TestPois(); break;
                    case "life": await TestLife(); break;
                    case "ui": await TestUi(); break;
                    case "save": await TestSave(); break;
                    case "loop": await TestLoop(); break;
                    case "looks": await TestLooks(); break;
                    case "mapstart": await TestMapStart(); break;
                    case "mapair": await TestMapAir(); break;
                    case "mapdrive": await TestMapDrive(); break;
                    case "mapviews": await TestMapViews(); break;
                    case "mapscreen": await TestMapScreen(); break;
                    default: Report($"ERROR: unknown test '{test}'"); break;
                }
            }
        }
        catch (Exception e)
        {
            Report("ERROR: harness exception " + e);
        }
        Report("done");
        GetTree().Quit();
    }

    private async Task EnsureGame()
    {
        if (GameWorld.I != null && GameWorld.I.IsReady) return;
        if (_map.Length > 0)
        {
            if (_place.Length > 0)
            {
                var p = MapData.ReadPlaces(_map).Find(q => q.Name == _place);
                if (p == null) Report($"ERROR: place '{_place}' is not on the map");
                else { _startX = p.X; _startZ = p.Z; }
            }
            Main.I.StartNewMapGame(_map, _startX, _startZ);
        }
        else Main.I.StartNewGame(_seed);
        await WaitReady();
        await Wait(2.0);
    }

    // ---- real-map scenarios -----------------------------------------------------------------

    private async Task TestMapStart()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.Weather.Force(WeatherState.Clear, true);
        g.Time.TimeOfDay = 11f;
        Report($"map start: player {g.Player.GlobalPosition}, car {g.Vehicle.GlobalPosition}, chunks {g.Streamer.LoadedChunks}");
        await Wait(6);
        float yaw = g.Player.Yaw;
        for (int i = 0; i < 4; i++)
        {
            g.Player.SetLook(yaw + i * Mathf.Pi * 0.5f, -0.04f);
            await Wait(1.0);
            await Shot($"map_look{i}");
        }
        var p = g.Player.GlobalPosition;
        Report($"after settle: player y={p.Y:0.00} ground={g.World.Height(p.X, p.Z):0.00}; car up={g.Vehicle.GlobalTransform.Basis.Y.Y:0.00} y={g.Vehicle.GlobalPosition.Y:0.00} ground={g.World.Height(g.Vehicle.GlobalPosition.X, g.Vehicle.GlobalPosition.Z):0.00}");
    }

    /// <summary>Lifts the camera above the start point and looks down, to judge layout at a glance.</summary>
    private async Task TestMapAir()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.Weather.Force(WeatherState.Clear, true);
        g.Time.TimeOfDay = 11.5f;
        if (g.Player.InVehicle) g.Player.ExitVehicle();
        g.Player.SetPhysicsProcess(false);
        var home = g.Player.GlobalPosition;
        await Wait(8);
        foreach (var (height, pitch, name) in new[] { (70f, -0.75f, "low"), (190f, -1.1f, "mid"), (420f, -1.45f, "high") })
        {
            for (int i = 0; i < 2; i++)
            {
                g.Player.GlobalPosition = new Vector3(home.X, g.World.Height(home.X, home.Z) + height, home.Z);
                g.Player.SetLook(i * Mathf.Pi * 0.5f, pitch);
                await Wait(1.5);
                await Shot($"map_air_{name}{i}");
            }
        }
        g.Player.GlobalPosition = home;
        g.Player.SetPhysicsProcess(true);
    }

    /// <summary>
    /// Free camera shots: --views=x:z:height:yawDeg:pitchDeg;...  (yaw 0 looks north, 90 looks west).
    /// The world streams around each viewpoint before the picture is taken.
    /// </summary>
    private async Task TestMapViews()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.Weather.Force(WeatherState.Clear, true);
        g.Time.TimeOfDay = 11.5f;
        if (g.Player.InVehicle) g.Player.ExitVehicle();
        g.Player.SetPhysicsProcess(false);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int n = 0;
        foreach (string view in _views.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = view.Split(':');
            if (f.Length != 5) { Report($"ERROR: bad view '{view}'"); continue; }
            float x = float.Parse(f[0], inv), z = float.Parse(f[1], inv), height = float.Parse(f[2], inv);
            g.Player.GlobalPosition = new Vector3(x, g.World.Height(x, z) + height, z);
            g.Player.SetLook(Mathf.DegToRad(float.Parse(f[3], inv)), Mathf.DegToRad(float.Parse(f[4], inv)));
            double t = 0;
            await Wait(1.0);
            while (g.Streamer.PendingJobs > 0 && t < 40) { await Wait(0.5); t += 0.5; }
            await Wait(4.0);
            await Shot($"view{n++}");
        }
        g.Player.SetPhysicsProcess(true);
        var st = g.Streamer;
        Report($"chunk builds: {st.BuildCount}, average {st.TotalBuildMs / Math.Max(1, st.BuildCount):0.0} ms, slowest {st.SlowestBuildMs:0.0} ms (worker threads)");
    }

    private async Task TestMapScreen()
    {
        await EnsureGame();
        var g = GameWorld.I;
        if (g.MapView == null) { Report("ERROR: mapscreen needs --map"); return; }
        g.MapView.Toggle();
        for (int zoom = 0; zoom < 4; zoom++)
        {
            g.MapView.SetZoom(zoom);
            await Wait(zoom == 3 ? 14 : 6);
            await Shot($"map_screen_zoom{zoom}");
        }
        g.MapView.Toggle();
        Report($"map screen closed, visible={g.MapView.Visible}");
    }

    private async Task TestMapDrive()
    {
        await EnsureGame();
        var g = GameWorld.I;
        var map = g.World.Map;
        if (map == null) { Report("ERROR: mapdrive needs --map"); return; }
        g.Weather.Force(WeatherState.Clear, true);
        if (!g.Player.InVehicle) g.Player.EnterVehicle(g.Vehicle);
        g.Vehicle.Fuel = 40;
        g.Vehicle.ForceEngine(true);
        float cruise = 9f;
        g.Autopilot = (VehicleController v, ref float throttle, ref float brake, ref float steer) =>
        {
            var pos = v.GlobalPosition;
            var fwd = -v.GlobalTransform.Basis.Z;
            float look = 8f + MathF.Abs(v.Speed) * 0.6f;
            var probe = pos + fwd * look;
            float angle = 0f;
            if (map.NearestRoad(probe.X, probe.Z, 40f, 1.2f, out var target, out _))
            {
                var local = v.GlobalTransform.AffineInverse() * target;
                angle = MathF.Atan2(local.X, -local.Z);
            }
            steer = Mathf.Clamp(-angle * 2.0f, -1f, 1f);
            throttle = v.Speed < cruise ? 0.8f : 0f;
            brake = v.Speed > cruise + 3f ? 0.5f : 0f;
        };
        var startPos = g.Vehicle.GlobalPosition;
        float minUp = 1f;
        int offRoad = 0;
        for (int i = 0; i < 14; i++)
        {
            await Wait(2.5);
            var p = g.Vehicle.GlobalPosition;
            float edge = map.RoadEdgeDistance(p.X, p.Z, 40f);
            float up = g.Vehicle.GlobalTransform.Basis.Y.Y;
            minUp = MathF.Min(minUp, up);
            if (!g.Vehicle.OnRoad) offRoad++;
            Report($"t={i * 2.5f:0.0}s pos=({p.X:0.0},{p.Z:0.0}) y={p.Y:0.00} ground={g.World.Height(p.X, p.Z):0.00} speed={g.Vehicle.Speed * 3.6f:0} km/h roadEdge={edge:0.0} onRoad={g.Vehicle.OnRoad} up={up:0.00} fps={Engine.GetFramesPerSecond():0} chunks={g.Streamer.LoadedChunks} jobs={g.Streamer.PendingJobs}");
            if (i == 5 || i == 11) await Shot($"map_drive{i}");
        }
        g.Autopilot = null;
        float travelled = g.Vehicle.GlobalPosition.DistanceTo(startPos);
        Report($"map drive: travelled {travelled:0} m, min up {minUp:0.00}, off-road samples {offRoad}/14");
        if (travelled < 60f) Report("ERROR: the car barely moved");
        if (minUp < 0.5f) Report("ERROR: the car rolled over");
    }

    private async Task TestMenu()
    {
        Main.I.ShowMenu();
        await Wait(1.5);
        await Shot("menu");
        foreach (var child in Main.I.GetChildren())
        {
            if (child is not MainMenu menu) continue;
            menu.ShowNewGame();
            await Wait(0.5);
            await Shot("menu_new_game");
        }
    }

    private async Task TestStart()
    {
        await EnsureGame();
        var g = GameWorld.I;
        Report($"player at {g.Player.GlobalPosition}, car at {g.Vehicle.GlobalPosition}, chunks {g.Streamer.LoadedChunks}");
        await Wait(4);
        await Shot("start");
        float yaw = g.Player.Yaw;
        for (int i = 1; i < 4; i++)
        {
            g.Player.SetLook(yaw + i * Mathf.Pi * 0.5f, -0.05f);
            await Wait(1.2);
            await Shot($"start_look{i}");
        }
        g.Player.SetLook(yaw, 0);
        Report($"after settle: player {g.Player.GlobalPosition}, car {g.Vehicle.GlobalPosition} up={g.Vehicle.GlobalTransform.Basis.Y.Y:0.00}");
    }

    private async Task TestDrive()
    {
        await EnsureGame();
        var g = GameWorld.I;
        if (!g.Player.InVehicle) g.Player.EnterVehicle(g.Vehicle);
        g.Vehicle.Fuel = 40;
        g.Vehicle.ForceEngine(true);
        float cruise = 21f;
        g.Autopilot = (VehicleController v, ref float throttle, ref float brake, ref float steer) =>
        {
            var pos = v.GlobalPosition;
            float look = 9f + MathF.Abs(v.Speed) * 0.7f;
            var target = g.World.Road.PointAt(pos.Z + look, -1.8f); // keep to the right-hand lane heading +Z
            var local = v.GlobalTransform.AffineInverse() * target;
            float angle = MathF.Atan2(local.X, -local.Z); // + = target to the right
            steer = Mathf.Clamp(-angle * 2.2f, -1f, 1f);
            throttle = v.Speed < cruise ? 1f : 0f;
            brake = v.Speed > cruise + 3f ? 0.5f : 0f;
        };
        float maxLateral = 0, maxSpeed = 0;
        for (int i = 0; i < 16; i++)
        {
            await Wait(2.5);
            var p = g.Vehicle.GlobalPosition;
            float lat = g.World.Road.LateralDistance(p.X, p.Z);
            maxLateral = MathF.Max(maxLateral, MathF.Abs(lat + 1.8f));
            maxSpeed = MathF.Max(maxSpeed, g.Vehicle.Speed);
            Report($"t={i * 2.5f:0.0}s z={p.Z:0} speed={g.Vehicle.Speed * 3.6f:0} km/h lateral={lat:0.0} onRoad={g.Vehicle.OnRoad} up={g.Vehicle.GlobalTransform.Basis.Y.Y:0.00} fps={Engine.GetFramesPerSecond():0} chunks={g.Streamer.LoadedChunks} jobs={g.Streamer.PendingJobs}");
            if (i == 2) await Shot("drive_cockpit");
            if (i == 5) { g.Vehicle.SetViewForTest(false); await Wait(1.0); await Shot("drive_chase"); }
            if (i == 9) await Shot("drive_chase2");
            if (i == 11) { g.Vehicle.SetViewForTest(true); }
            if (i == 13) await Shot("drive_cockpit2");
        }
        g.Autopilot = (VehicleController v, ref float throttle, ref float brake, ref float steer) => { throttle = 0; brake = 1; steer = 0; };
        await Wait(4);
        g.Autopilot = null;
        Report($"drive summary: max speed {maxSpeed * 3.6f:0} km/h, worst lane error {maxLateral:0.0} m, fuel left {g.Vehicle.Fuel:0.0} L, engine {g.Vehicle.EngineHealth:0}%, final speed {g.Vehicle.Speed:0.0}");
        g.Player.ExitVehicle();
        await Wait(1.0);
        g.Player.SetLook(g.Player.Yaw + 2.2f, -0.1f);
        await Wait(0.8);
        await Shot("drive_exit");
    }

    private async Task TestNight()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.Time.TimeOfDay = 18.4f;
        await Wait(2);
        await Shot("dusk");
        g.Time.TimeOfDay = 23.5f;
        g.Weather.Force(WeatherState.Clear, true);
        if (!g.Player.InVehicle) g.Player.EnterVehicle(g.Vehicle);
        g.Vehicle.ForceEngine(true);
        g.Vehicle.SetLights(true);
        g.Vehicle.SetViewForTest(true);
        await Wait(2.5);
        await Shot("night_headlights");
        g.Vehicle.SetLights(false);
        await Wait(1.0);
        await Shot("night_dark");
        g.Player.ExitVehicle();
        g.Player.Flashlight.Visible = true;
        await Wait(1.5);
        await Shot("night_flashlight");
        g.Player.Flashlight.Visible = false;
        g.Time.TimeOfDay = 6.2f;
        await Wait(2);
        await Shot("dawn");
        g.Time.TimeOfDay = 13.5f;
    }

    private async Task TestWeather()
    {
        await EnsureGame();
        var g = GameWorld.I;
        foreach (WeatherState s in Enum.GetValues(typeof(WeatherState)))
        {
            g.Weather.Force(s, true);
            await Wait(3.5);
            await Shot("weather_" + s);
        }
        if (!g.Player.InVehicle) g.Player.EnterVehicle(g.Vehicle);
        g.Vehicle.ForceEngine(true);
        g.Vehicle.SetViewForTest(true);
        g.Weather.Force(WeatherState.HeavyRain, true);
        await Wait(3);
        await Shot("weather_rain_cockpit");
        g.Player.ExitVehicle();
        g.Weather.Force(WeatherState.Clear, true);
    }

    private async Task TestPois()
    {
        await EnsureGame();
        var g = GameWorld.I;
        var seen = new HashSet<PoiKind>();
        for (int k = 1; k < 80 && seen.Count < 9; k++)
        {
            foreach (var p in g.World.Pois.ForSegment(k))
            {
                if (!seen.Add(p.Kind)) continue;
                float back = MathF.Max(p.Radius, 6f) + 7f;
                var pos = p.ToWorld(new Vector3(back * 0.45f, 0, -back));
                var to = p.Center - pos;
                await Teleport(pos, MathF.Atan2(-to.X, -to.Z), -0.08f);
                await Wait(3.0);
                Report($"{p.Kind} at segment {k}, side {p.Side}, pad {p.Center}, track={p.HasTrack}");
                await Shot("poi_" + p.Kind);
                if (p.Kind is PoiKind.GasStation or PoiKind.Farmhouse)
                {
                    var road = g.World.Road.PointAt(p.AttachZ - 70f, -1.8f);
                    var look = g.World.Road.PointAt(p.AttachZ, 0) - road;
                    await Teleport(road, MathF.Atan2(-look.X, -look.Z), 0f);
                    await Wait(2.0);
                    await Shot("poi_" + p.Kind + "_from_road");
                }
            }
        }
        Report($"kinds found: {string.Join(", ", seen)}");
    }

    private async Task TestLife()
    {
        await EnsureGame();
        var g = GameWorld.I;
        var start = g.World.Road.PointAt(420f, -12f);
        await Teleport(start, g.World.Road.YawAt(420f), -0.05f);
        g.Time.TimeOfDay = 13.5f;
        var fwd = -g.Player.GlobalTransform.Basis.Z;
        var right = g.Player.GlobalTransform.Basis.X;
        Vector3 At(float f, float r) { var p = g.Player.GlobalPosition + fwd * f + right * r; p.Y = g.World.Height(p.X, p.Z); return p; }
        g.Encounters.SpawnAnimal(AnimalKind.Deer, At(60, -4));
        g.Encounters.SpawnAnimal(AnimalKind.Stag, At(64, 3));
        g.Encounters.SpawnAnimal(AnimalKind.Fox, At(55, 7));
        var friendly = g.Encounters.SpawnHuman(At(9, -2.5f) + Vector3.Up * 0.2f, Disposition.Friendly, false, "");
        var bandit = g.Encounters.SpawnHuman(At(10, 2.5f) + Vector3.Up * 0.2f, Disposition.Wary, true, "");
        await Wait(3.0);
        await Shot("life_people");
        var deer = g.Encounters.SpawnAnimal(AnimalKind.Deer, At(9, -3));
        var stag = g.Encounters.SpawnAnimal(AnimalKind.Stag, At(11, 1));
        var fox = g.Encounters.SpawnAnimal(AnimalKind.Fox, At(7, 3));
        g.Player.SetLook(g.Player.Yaw, -0.12f);
        await Wait(0.35);
        await Shot("life_animals_close");
        await Wait(1.2);
        await Shot("life_animals_flee");
        deer.Damage(500, g.Player, deer.GlobalPosition);
        await Wait(2.0);
        Report($"deer dead={deer.IsDead} prompt='{deer.GetPrompt(g.Player)}' at {deer.GlobalPosition.DistanceTo(g.Player.GlobalPosition):0} m");
        Report($"animals {g.Encounters.AnimalCount}, humans {g.Encounters.HumanCount}, health {g.Player.Stats.Health:0}");
        bandit.Damage(200, g.Player, bandit.GlobalPosition);
        friendly.Damage(200, g.Player, friendly.GlobalPosition);
        await Wait(1.0);
    }

    private async Task TestUi()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.InventoryUi.Open(g.Vehicle.Trunk);
        await Wait(0.6);
        await Shot("ui_inventory");
        g.InventoryUi.Close();
        g.Pause.Open();
        await Wait(0.6);
        await Shot("ui_pause");
        g.Pause.Close();
        await Wait(0.3);
    }

    /// <summary>Beauty shots: lake shore, deep forest, misty morning, night road.</summary>
    private async Task TestLooks()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.Weather.Force(WeatherState.Clear, true);
        g.Time.TimeOfDay = 10.5f;
        // nearest lake shore to the road
        Vector3? shore = null, forest = null;
        for (float z = 100; z < 6000 && (shore == null || forest == null); z += 24)
        {
            for (float lat = -160; lat <= 160; lat += 20)
            {
                var p = g.World.Road.PointAt(z, lat);
                float h = g.World.Height(p.X, p.Z);
                g.World.Biome(p.X, p.Z, out float f, out _);
                if (shore == null && h > WorldGenerator.WaterLevel + 0.15f && h < WorldGenerator.WaterLevel + 0.6f) shore = p;
                if (forest == null && f > 0.95f && MathF.Abs(lat) > 40 && h > WorldGenerator.WaterLevel + 3f) forest = p;
            }
        }
        foreach (var cab in g.World.Pois.ForSegment(3)) shore = cab.Center + new Vector3(0, 0, -16f);
        if (shore != null)
        {
            // face downhill, towards the water
            var s = shore.Value;
            // look towards the lowest ground nearby: that is where the water is
            float best = float.MaxValue, yawBest = 0;
            for (int i = 0; i < 16; i++)
            {
                float a = i * Mathf.Tau / 16f;
                float hh = g.World.Height(s.X - MathF.Sin(a) * 45f, s.Z - MathF.Cos(a) * 45f);
                if (hh < best) { best = hh; yawBest = a; }
            }
            await Teleport(s + new Vector3(-MathF.Sin(yawBest), 0, -MathF.Cos(yawBest)) * 22f, yawBest, -0.1f);
            await Wait(3);
            await Shot("look_lake");
        }
        else Report("no lake shore found");
        if (forest != null)
        {
            await Teleport(forest.Value, 0.6f, 0.05f);
            g.Time.TimeOfDay = 8.2f;
            await Wait(3);
            await Shot("look_forest_morning");
            g.Weather.Force(WeatherState.Fog, true);
            g.Time.TimeOfDay = 7.0f;
            await Wait(3);
            await Shot("look_forest_mist");
            g.Weather.Force(WeatherState.Clear, true);
            g.Time.TimeOfDay = 22.5f;
            await Wait(3.5);
            await Shot("look_forest_fireflies");
        }
        else Report("no dense forest found");
        var road = g.World.Road.PointAt(900f, -1.8f);
        await Teleport(road, g.World.Road.YawAt(900f), 0f);
        g.Weather.Force(WeatherState.Overcast, true);
        g.Weather.Cloud = 0.45f;
        g.Time.TimeOfDay = 15.5f;
        await Wait(3);
        await Shot("look_road_cloud_shadows");
        g.Time.TimeOfDay = 17.6f;
        g.Weather.Force(WeatherState.Clear, true);
        await Wait(3);
        await Shot("look_road_golden_hour");
        g.Weather.Force(WeatherState.Fog, true);
        g.Time.TimeOfDay = 23.0f;
        var carSpot = g.World.Road.PointAt(895f, -1.8f, 0.5f);
        g.Vehicle.GlobalTransform = new Transform3D(new Basis(Vector3.Up, g.World.Road.YawAt(895f)), carSpot);
        await Wait(1.5);
        g.Player.EnterVehicle(g.Vehicle);
        g.Vehicle.ForceEngine(true);
        g.Vehicle.SetLights(true);
        g.Vehicle.SetViewForTest(false);
        await Wait(3);
        await Shot("look_night_fog_headlights");
        g.Player.ExitVehicle();
        g.Weather.Force(WeatherState.Clear, true);
        g.Time.TimeOfDay = 13.5f;
        Report($"fps after looks: {Engine.GetFramesPerSecond():0}");
    }

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    /// <summary>The core loop through real input: search a container, take loot, pump fuel, refuel and repair.</summary>
    private async Task TestLoop()
    {
        await EnsureGame();
        var g = GameWorld.I;
        var poi = g.World.Pois.ForSegment(0)[0];
        var stand = poi.ToWorld(new Vector3(-1.7f, 0, 2.9f));
        var crate = poi.ToWorld(new Vector3(-1.7f, 0.35f, 4.4f));
        var to = crate - stand;
        await Teleport(stand, MathF.Atan2(-to.X, -to.Z), -0.75f);
        await Wait(0.6);
        Report($"crate prompt: '{g.Player.Prompt}'");
        Press("interact");
        await Wait(0.5);
        Report($"container open: {g.InventoryUi.Visible}");
        await Shot("loop_crate");
        int before = g.Player.Inventory.Items.Count;
        foreach (var st in g.GetContainer("poi0:c0", "", "starter", 0).Snapshot())
            g.GetContainer("poi0:c0", "", "starter", 0).MoveTo(g.Player.Inventory, st.Id, st.Count);
        Report($"backpack stacks {before} -> {g.Player.Inventory.Items.Count}, weight {g.Player.Inventory.Weight:0.0} kg, crate now: '{g.ContainerPrompt("poi0:c0", "Your supply crate")}'");
        Press("inventory");
        await Wait(0.3);
        Report($"inventory closed by key: {!g.InventoryUi.Visible}");

        // eat and drink through the item system
        float hunger = g.Player.Stats.Hunger, thirst = g.Player.Stats.Thirst;
        if (ItemDb.Use("beans", g)) g.Player.Inventory.Remove("beans", 1);
        if (ItemDb.Use("water", g)) g.Player.Inventory.Remove("water", 1);
        Report($"hunger {hunger:0} -> {g.Player.Stats.Hunger:0}, thirst {thirst:0} -> {g.Player.Stats.Thirst:0}, empty bottles {g.Player.Inventory.Count("empty_bottle")}");

        // the trunk, through the car's own interaction zones
        var car = g.Vehicle;
        var rear = car.GlobalTransform * new Vector3(0, 0, 3.6f);
        var look = car.GlobalPosition + Vector3.Up * 0.8f - (rear + Vector3.Up * 1.6f);
        await Teleport(rear, MathF.Atan2(-look.X, -look.Z), MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length()));
        await Wait(0.5);
        Report($"car rear prompt: '{g.Player.Prompt}'");
        Press("interact");
        await Wait(0.4);
        Report($"trunk open: {g.InventoryUi.Visible}");
        g.Vehicle.Trunk.MoveTo(g.Player.Inventory, "fuel_can", 1);
        g.Vehicle.Trunk.MoveTo(g.Player.Inventory, "crowbar", 1);
        g.InventoryUi.Close();
        float fuel = car.Fuel;
        if (ItemDb.Use("fuel_can", g)) g.Player.Inventory.Remove("fuel_can", 1);
        Report($"fuel can: {fuel:0.0} -> {car.Fuel:0.0} L, empty cans {g.Player.Inventory.Count("empty_can")}");
        g.Player.SetSlot(1);
        await Wait(0.3);
        await Shot("loop_crowbar");

        // door: get in through the interaction ray
        var side = car.GlobalTransform * new Vector3(-2.6f, 0, 0);
        look = car.GlobalPosition + Vector3.Up * 0.9f - (side + Vector3.Up * 1.6f);
        await Teleport(side, MathF.Atan2(-look.X, -look.Z), MathF.Atan2(look.Y, new Vector2(look.X, look.Z).Length()));
        await Wait(0.5);
        Report($"car side prompt: '{g.Player.Prompt}'");
        Press("interact");
        await Wait(0.4);
        Report($"in vehicle: {g.Player.InVehicle}");
        Press("engine_toggle");
        await Wait(1.6);
        Report($"engine on: {car.EngineOn}");
        if (g.Player.InVehicle) g.Player.ExitVehicle();

        // find the first gas station with a working pump and use it
        for (int k = 1; k < 60; k++)
        {
            foreach (var p in g.World.Pois.ForSegment(k))
            {
                if (p.Kind != PoiKind.GasStation) continue;
                var pumpStand = p.ToWorld(new Vector3(-1.6f, 0, -8.2f));
                var pump = p.ToWorld(new Vector3(-1.6f, 1.0f, -6.5f));
                var carSpot = p.ToWorld(new Vector3(3.6f, 0, -9.6f));
                var t2 = pump - pumpStand;
                await Teleport(pumpStand, MathF.Atan2(-t2.X, -t2.Z), -0.15f);
                carSpot.Y = g.World.Height(carSpot.X, carSpot.Z) + 0.5f;
                car.GlobalTransform = new Transform3D(new Basis(Vector3.Up, p.Yaw + Mathf.Pi * 0.5f), carSpot);
                car.LinearVelocity = Vector3.Zero;
                await Wait(1.5);
                float f0 = car.Fuel;
                Report($"pump prompt at segment {k}: '{g.Player.Prompt}'");
                Press("interact");
                await Wait(0.5);
                Report($"pumped: {f0:0.0} -> {car.Fuel:0.0} L; prompt now '{g.Player.Prompt}'");
                await Shot("loop_pump");
                k = 1000;
                break;
            }
        }
        car.EngineHealth = 40;
        g.Player.Inventory.Add("repair_parts", 1);
        if (ItemDb.Use("repair_parts", g)) g.Player.Inventory.Remove("repair_parts", 1);
        Report($"engine after repair: {car.EngineHealth:0}%");
        g.Sleep("Test nap.");
        await Wait(4.0);
        Report($"after sleep: day {g.Time.Day} {g.Time.ClockText}, health {g.Player.Stats.Health:0}, weather {g.Weather.State}");
    }

    private async Task TestSave()
    {
        await EnsureGame();
        var g = GameWorld.I;
        g.OpenContainer("test:crate", "Test crate", "garage", 99u);
        g.InventoryUi.Close();
        var before = g.Player.GlobalPosition;
        var carBefore = g.Vehicle.GlobalPosition;
        float time = g.Time.TimeOfDay, fuel = g.Vehicle.Fuel;
        int containers = g.State.Containers.Count;
        bool ok = g.SaveGame(false);
        Report($"saved={ok} player={before} car={carBefore} time={time:0.00} fuel={fuel:0.0} containers={containers}");
        Main.I.ContinueGame();
        await Wait(0.5);
        await WaitReady();
        await Wait(2.5);
        g = GameWorld.I;
        Report($"loaded player={g.Player.GlobalPosition} car={g.Vehicle.GlobalPosition} time={g.Time.TimeOfDay:0.00} fuel={g.Vehicle.Fuel:0.0} containers={g.State.Containers.Count} trunk={g.Vehicle.Trunk.Items.Count} items");
        bool same = g.Player.GlobalPosition.DistanceTo(before) < 1.5f && g.Vehicle.GlobalPosition.DistanceTo(carBefore) < 1.5f && g.State.Containers.Count == containers;
        Report(same ? "save/load round trip OK" : "ERROR: save/load mismatch");
        await Shot("save_loaded");
    }
}
