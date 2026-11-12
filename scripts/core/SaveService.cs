using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace DeadMiles;

/// <summary>
/// The whole persistent state of a run. The world itself is never stored: it is rebuilt from
/// <see cref="Seed"/>, and only the differences (looted containers, drained pumps, flags) are kept.
/// </summary>
public sealed class SaveData
{
    public int Version { get; set; } = 1;
    public int Seed { get; set; }
    /// <summary>Name of the real map this run plays on ("mazowsze"), or empty for the generated road.</summary>
    public string Map { get; set; } = "";
    /// <summary>Where a new game on a real map starts (world X, Z).</summary>
    public float[] MapStart { get; set; } = { 0, 0 };
    /// <summary>Pins the player dropped on the map (world X, Z each).</summary>
    public List<float[]> Markers { get; set; } = new();
    /// <summary>Where the GPS is heading (world X, Z), or null.</summary>
    public float[]? Destination { get; set; }
    public double PlayTime { get; set; }
    public int Day { get; set; } = 1;
    public float TimeOfDay { get; set; } = 8.5f;
    public int WeatherState { get; set; }
    public float WeatherTimer { get; set; } = 240f;
    public float Wetness { get; set; }

    public float[] PlayerPos { get; set; } = { 0, 0, 0 };
    public float PlayerYaw { get; set; }
    public float Health { get; set; } = 100;
    public float Hunger { get; set; } = 80;
    public float Thirst { get; set; } = 80;
    public float Stamina { get; set; } = 100;
    public bool InVehicle { get; set; }
    public List<ItemStack> PlayerItems { get; set; } = new();

    public bool HasVehicle { get; set; }
    public float[] VehiclePos { get; set; } = { 0, 0, 0 };
    public float[] VehicleRot { get; set; } = { 0, 0, 0, 1 };
    public float Fuel { get; set; } = 9f;
    public float EngineHealth { get; set; } = 78f;
    public float TireHealth { get; set; } = 85f;
    public bool Headlights { get; set; }
    public float Odometer { get; set; }
    public List<ItemStack> Trunk { get; set; } = new();

    public Dictionary<string, List<ItemStack>> Containers { get; set; } = new();
    public Dictionary<string, float> Pumps { get; set; } = new();
    public List<string> Flags { get; set; } = new();
}

/// <summary>Reads and writes the single save slot as JSON under user://.</summary>
public static class SaveService
{
    /// <summary>The save slot. Automated tests point this somewhere else.</summary>
    public static string Path = "user://save.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool Exists() => FileAccess.FileExists(Path);

    public static bool Write(SaveData data)
    {
        try
        {
            string json = JsonSerializer.Serialize(data, Options);
            using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
            if (f == null)
            {
                Log.Error("SaveService", $"Cannot open {Path} for writing: {FileAccess.GetOpenError()}");
                return false;
            }
            f.StoreString(json);
            Log.Info("SaveService", $"Saved day {data.Day}, {data.TimeOfDay:0.0}h, {data.Containers.Count} container deltas");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("SaveService", $"Save failed: {e.Message}");
            return false;
        }
    }

    public static SaveData? Load()
    {
        if (!Exists()) return null;
        try
        {
            using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
            if (f == null)
            {
                Log.Error("SaveService", $"Cannot open {Path}: {FileAccess.GetOpenError()}");
                return null;
            }
            var data = JsonSerializer.Deserialize<SaveData>(f.GetAsText(), Options);
            if (data == null) Log.Error("SaveService", "Save file is empty or invalid");
            return data;
        }
        catch (Exception e)
        {
            Log.Error("SaveService", $"Load failed, the save file may be corrupt: {e.Message}");
            return null;
        }
    }

    public static void Delete()
    {
        if (Exists()) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(Path));
    }
}
