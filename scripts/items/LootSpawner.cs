using System.Collections.Generic;

namespace DeadMiles;

/// <summary>
/// Deterministic container contents. A container is only rolled the first time it is opened;
/// after that its remaining contents live in the save file as a delta against this baseline.
/// </summary>
public static class LootSpawner
{
    private readonly record struct Entry(string Id, float Chance, int Min, int Max);

    private static readonly Dictionary<string, Entry[]> Tables = new()
    {
        ["starter"] = new Entry[] { new("water", 1f, 2, 2), new("beans", 1f, 1, 1), new("bandage", 1f, 1, 1), new("chocolate", 1f, 1, 1) },
        ["kitchen"] = new Entry[] { new("beans", 0.55f, 1, 2), new("peaches", 0.4f, 1, 2), new("water", 0.45f, 1, 2), new("soda", 0.3f, 1, 2), new("jerky", 0.2f, 1, 1), new("empty_bottle", 0.25f, 1, 1) },
        ["fridge"] = new Entry[] { new("soda", 0.6f, 1, 3), new("water", 0.5f, 1, 2), new("peaches", 0.2f, 1, 1) },
        ["shop"] = new Entry[] { new("soda", 0.5f, 1, 3), new("chocolate", 0.55f, 1, 3), new("jerky", 0.45f, 1, 2), new("water", 0.4f, 1, 2), new("beans", 0.3f, 1, 1), new("bandage", 0.2f, 1, 1) },
        ["garage"] = new Entry[] { new("repair_parts", 0.55f, 1, 2), new("tire_kit", 0.4f, 1, 1), new("fuel_can", 0.35f, 1, 1), new("empty_can", 0.35f, 1, 1), new("crowbar", 0.2f, 1, 1) },
        ["medical"] = new Entry[] { new("bandage", 0.8f, 1, 3), new("medkit", 0.4f, 1, 1) },
        ["bedroom"] = new Entry[] { new("bandage", 0.3f, 1, 1), new("chocolate", 0.3f, 1, 1), new("ammo", 0.3f, 2, 6), new("water", 0.25f, 1, 1), new("rifle", 0.12f, 1, 1) },
        ["camp"] = new Entry[] { new("beans", 0.5f, 1, 1), new("water", 0.5f, 1, 2), new("jerky", 0.35f, 1, 2), new("bandage", 0.25f, 1, 1), new("empty_bottle", 0.3f, 1, 1), new("ammo", 0.2f, 2, 5) },
        ["hunter"] = new Entry[] { new("rifle", 0.6f, 1, 1), new("ammo", 0.9f, 4, 10), new("jerky", 0.5f, 1, 2), new("bandage", 0.3f, 1, 1) },
        ["military"] = new Entry[] { new("ammo", 0.8f, 5, 12), new("medkit", 0.5f, 1, 1), new("rifle", 0.3f, 1, 1), new("fuel_can", 0.3f, 1, 1), new("water", 0.4f, 1, 2) },
        ["car"] = new Entry[] { new("fuel_can", 0.22f, 1, 1), new("empty_can", 0.2f, 1, 1), new("repair_parts", 0.22f, 1, 1), new("tire_kit", 0.2f, 1, 1), new("water", 0.3f, 1, 1), new("chocolate", 0.2f, 1, 1), new("bandage", 0.15f, 1, 1) },
        ["barn"] = new Entry[] { new("fuel_can", 0.45f, 1, 1), new("repair_parts", 0.3f, 1, 1), new("empty_can", 0.3f, 1, 1), new("crowbar", 0.25f, 1, 1), new("tire_kit", 0.2f, 1, 1) },
        ["body"] = new Entry[] { new("ammo", 0.5f, 1, 4), new("jerky", 0.4f, 1, 1), new("water", 0.35f, 1, 1), new("bandage", 0.35f, 1, 1), new("chocolate", 0.2f, 1, 1) },
        ["trash"] = new Entry[] { new("empty_bottle", 0.5f, 1, 2), new("empty_can", 0.12f, 1, 1), new("soda", 0.12f, 1, 1) },
    };

    public static List<ItemStack> Generate(string table, uint seed)
    {
        var result = new List<ItemStack>();
        if (!Tables.TryGetValue(table, out var entries))
        {
            Log.Warn("LootSpawner", $"Unknown loot table '{table}'");
            return result;
        }
        var rng = new Rng(Hash.Mix(seed ^ 0x51ed270bU));
        foreach (var e in entries)
        {
            if (!rng.Chance(e.Chance)) continue;
            result.Add(new ItemStack { Id = e.Id, Count = rng.IntRange(e.Min, e.Max) });
        }
        return result;
    }
}
