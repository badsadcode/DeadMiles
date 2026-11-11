using System;
using System.Collections.Generic;
using System.Linq;

namespace DeadMiles;

public sealed class ItemDef
{
    public string Id = "";
    public string Name = "";
    public string Desc = "";
    public float Weight;
    public bool Usable;
}

/// <summary>A stack of identical items. Plain properties so it serialises straight into the save file.</summary>
public sealed class ItemStack
{
    public string Id { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>All item definitions and what happens when one is used.</summary>
public static class ItemDb
{
    private static readonly Dictionary<string, ItemDef> Defs = new();

    static ItemDb()
    {
        Add("water", "Water bottle", "Clean water. Leaves an empty bottle you can refill.", 0.6f, true);
        Add("empty_bottle", "Empty bottle", "Refill it at a hand pump or a lake.", 0.1f, false);
        Add("soda", "Can of soda", "Warm and flat, but it is liquid and sugar.", 0.4f, true);
        Add("beans", "Canned beans", "A solid meal.", 0.45f, true);
        Add("peaches", "Canned peaches", "Sweet, and the syrup helps with thirst.", 0.45f, true);
        Add("jerky", "Jerky", "Salty dried meat.", 0.15f, true);
        Add("chocolate", "Chocolate bar", "A little food and a burst of energy.", 0.1f, true);
        Add("raw_meat", "Raw venison", "Cook it on a campfire. Eating it raw will make you ill.", 0.8f, true);
        Add("cooked_meat", "Cooked venison", "The best meal on the road.", 0.7f, true);
        Add("bandage", "Bandage", "Treats minor wounds.", 0.1f, true);
        Add("medkit", "First aid kit", "Treats serious wounds.", 0.6f, true);
        Add("fuel_can", "Fuel can (5 L)", "Use it next to your car to pour it into the tank.", 4.5f, true);
        Add("empty_can", "Empty fuel can", "Fill it at a working pump.", 0.8f, false);
        Add("repair_parts", "Engine parts", "Belts, plugs and hoses. Use next to your car to repair the engine.", 2.5f, true);
        Add("tire_kit", "Tire repair kit", "Plugs and a pump. Use next to your car to restore the tires.", 1.5f, true);
        Add("crowbar", "Crowbar", "A tool, and a weapon when it has to be. Equip with [2].", 1.8f, false);
        Add("rifle", "Hunting rifle", "Bolt action. Equip with [3]. Needs rifle rounds.", 3.6f, false);
        Add("ammo", "Rifle round", "Ammunition for the hunting rifle.", 0.03f, false);
    }

    private static void Add(string id, string name, string desc, float weight, bool usable)
        => Defs[id] = new ItemDef { Id = id, Name = name, Desc = desc, Weight = weight, Usable = usable };

    public static ItemDef Get(string id)
        => Defs.TryGetValue(id, out var d) ? d : new ItemDef { Id = id, Name = id, Desc = "Unknown item", Weight = 0.1f };

    public static bool Exists(string id) => Defs.ContainsKey(id);

    /// <summary>Applies an item's effect. Returns true when one unit was consumed.</summary>
    public static bool Use(string id, GameWorld g)
    {
        var p = g.Player;
        var s = p.Stats;
        switch (id)
        {
            case "water": s.Drink(40); p.Inventory.Add("empty_bottle", 1); g.Audio.Play("drink"); g.Toast("You drink the water."); return true;
            case "soda": s.Drink(22); s.Eat(4); g.Audio.Play("drink"); g.Toast("Flat, sweet, wonderful."); return true;
            case "beans": s.Eat(35); g.Audio.Play("eat"); g.Toast("You eat the beans cold from the can."); return true;
            case "peaches": s.Eat(25); s.Drink(8); g.Audio.Play("eat"); g.Toast("You eat the peaches and drink the syrup."); return true;
            case "jerky": s.Eat(18); s.Drink(-4); g.Audio.Play("eat"); g.Toast("Salty. Now you are thirsty."); return true;
            case "chocolate": s.Eat(12); s.Stamina = MathF.Min(100, s.Stamina + 35); g.Audio.Play("eat"); g.Toast("A little energy returns."); return true;
            case "raw_meat": s.Eat(20); s.Hurt(8, "bad food"); g.Audio.Play("eat"); g.Toast("Raw meat sits badly. Cook it next time."); return true;
            case "cooked_meat": s.Eat(45); g.Audio.Play("eat"); g.Toast("A hot meal. You feel human again."); return true;
            case "bandage":
                if (s.Health >= 99.5f) { g.Toast("You are not hurt."); return false; }
                s.Heal(20); g.Audio.Play("cloth"); g.Toast("You dress the wound."); return true;
            case "medkit":
                if (s.Health >= 99.5f) { g.Toast("You are not hurt."); return false; }
                s.Heal(60); g.Audio.Play("cloth"); g.Toast("You patch yourself up properly."); return true;
            case "fuel_can":
            {
                var v = g.NearbyVehicle(6f);
                if (v == null) { g.Toast("Get closer to your car to refuel it."); return false; }
                if (v.Fuel >= VehicleController.TankSize - 0.5f) { g.Toast("The tank is already full."); return false; }
                v.Fuel = MathF.Min(VehicleController.TankSize, v.Fuel + 5f);
                p.Inventory.Add("empty_can", 1);
                g.Audio.Play("pour");
                g.Toast($"You pour 5 L into the tank ({v.Fuel:0} L).");
                return true;
            }
            case "repair_parts":
            {
                var v = g.NearbyVehicle(6f);
                if (v == null) { g.Toast("Get closer to your car to work on it."); return false; }
                if (v.EngineHealth >= 99f) { g.Toast("The engine does not need work."); return false; }
                v.EngineHealth = MathF.Min(100, v.EngineHealth + 35f);
                g.Audio.Play("repair");
                g.Toast($"You replace the worn parts (engine {v.EngineHealth:0}%).");
                return true;
            }
            case "tire_kit":
            {
                var v = g.NearbyVehicle(6f);
                if (v == null) { g.Toast("Get closer to your car to work on it."); return false; }
                if (v.TireHealth >= 99f) { g.Toast("The tires are fine."); return false; }
                v.TireHealth = MathF.Min(100, v.TireHealth + 45f);
                g.Audio.Play("repair");
                g.Toast($"You patch and pump the tires ({v.TireHealth:0}%).");
                return true;
            }
        }
        return false;
    }
}

/// <summary>Weight-limited list of item stacks. Used for the backpack, the trunk and every container.</summary>
public sealed class Inventory
{
    public readonly List<ItemStack> Items = new();
    public float Capacity;
    public string Title;
    public event Action? Changed;

    public Inventory(string title, float capacity) { Title = title; Capacity = capacity; }

    public float Weight => Items.Sum(i => ItemDb.Get(i.Id).Weight * i.Count);
    public int Count(string id) => Items.Where(i => i.Id == id).Sum(i => i.Count);
    public bool Has(string id) => Count(id) > 0;

    /// <summary>Adds as many as fit. Returns how many were added.</summary>
    public int Add(string id, int count)
    {
        float w = ItemDb.Get(id).Weight;
        int fit = w <= 0 ? count : (int)MathF.Floor((Capacity - Weight + 0.0001f) / w);
        int n = Math.Clamp(fit, 0, count);
        if (n <= 0) return 0;
        var st = Items.Find(i => i.Id == id);
        if (st == null) Items.Add(new ItemStack { Id = id, Count = n }); else st.Count += n;
        Changed?.Invoke();
        return n;
    }

    public bool Remove(string id, int count = 1)
    {
        var st = Items.Find(i => i.Id == id);
        if (st == null || st.Count < count) return false;
        st.Count -= count;
        if (st.Count <= 0) Items.Remove(st);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves items to another inventory. Returns how many moved.</summary>
    public int MoveTo(Inventory other, string id, int count)
    {
        count = Math.Min(count, Count(id));
        int moved = other.Add(id, count);
        if (moved > 0) Remove(id, moved);
        return moved;
    }

    public List<ItemStack> Snapshot() => Items.Select(i => new ItemStack { Id = i.Id, Count = i.Count }).ToList();

    public void Load(IEnumerable<ItemStack>? items)
    {
        Items.Clear();
        if (items != null)
            foreach (var i in items)
                if (i.Count > 0) Items.Add(new ItemStack { Id = i.Id, Count = i.Count });
        Changed?.Invoke();
    }
}
