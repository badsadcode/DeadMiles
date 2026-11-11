using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Builds the handcrafted set of roadside places from primitives and the car models.
/// Each place is authored once in local space (local -Z faces the road) and varied by the
/// POI seed: which props appear, what is wrecked, what the containers hold.
/// </summary>
public sealed class PoiBuilder
{
    private readonly WorldGenerator _world;
    private readonly Dictionary<string, PackedScene?> _cars = new();
    private readonly Dictionary<Material, Material> _rusted = new();
    private static readonly string[] WreckModels = { "NormalCar1", "Taxi", "SportsCar", "SportsCar2", "Cop", "SUV" };

    // ---- shared materials ----------------------------------------------------------------
    private static readonly Dictionary<string, StandardMaterial3D> Mats = new();

    public static StandardMaterial3D Mat(string name, float r, float g, float b, float rough = 0.9f, float metal = 0f)
    {
        if (Mats.TryGetValue(name, out var m)) return m;
        m = new StandardMaterial3D { AlbedoColor = new Color(r, g, b), Roughness = rough, Metallic = metal };
        Mats[name] = m;
        return m;
    }

    public static StandardMaterial3D Wood => Mat("wood", 0.30f, 0.21f, 0.13f);
    public static StandardMaterial3D DarkWood => Mat("darkwood", 0.17f, 0.12f, 0.08f);
    public static StandardMaterial3D PaleWood => Mat("palewood", 0.46f, 0.38f, 0.26f);
    public static StandardMaterial3D Plaster => Mat("plaster", 0.62f, 0.58f, 0.50f);
    public static StandardMaterial3D FadedBlue => Mat("fadedblue", 0.30f, 0.40f, 0.44f);
    public static StandardMaterial3D FadedRed => Mat("fadedred", 0.42f, 0.16f, 0.12f);
    public static StandardMaterial3D Cream => Mat("cream", 0.66f, 0.60f, 0.44f);
    public static StandardMaterial3D Concrete => Mat("concrete", 0.38f, 0.37f, 0.35f);
    public static StandardMaterial3D Metal => Mat("metal", 0.32f, 0.33f, 0.34f, 0.55f, 0.6f);
    public static StandardMaterial3D Rust => Mat("rust", 0.30f, 0.15f, 0.08f, 0.9f, 0.2f);
    public static StandardMaterial3D RoofTin => Mat("rooftin", 0.25f, 0.20f, 0.18f, 0.7f, 0.3f);
    public static StandardMaterial3D RoofDark => Mat("roofdark", 0.11f, 0.11f, 0.12f, 0.85f);
    public static StandardMaterial3D Glass => Mat("glass", 0.03f, 0.045f, 0.055f, 0.12f, 0.3f);
    public static StandardMaterial3D White => Mat("white", 0.72f, 0.72f, 0.70f, 0.6f);
    public static StandardMaterial3D Black => Mat("black", 0.03f, 0.03f, 0.03f, 0.8f);
    public static StandardMaterial3D Olive => Mat("olive", 0.22f, 0.25f, 0.13f);
    public static StandardMaterial3D Orange => Mat("orange", 0.70f, 0.30f, 0.07f);
    public static StandardMaterial3D Yellow => Mat("yellow", 0.72f, 0.58f, 0.10f, 0.7f);
    public static StandardMaterial3D Fabric => Mat("fabric", 0.30f, 0.34f, 0.40f);
    public static StandardMaterial3D Stone => Mat("stone", 0.27f, 0.26f, 0.24f);
    public static StandardMaterial3D SignGreen => Mat("signgreen", 0.04f, 0.20f, 0.11f, 0.6f);
    public static StandardMaterial3D SignAmber => Mat("signamber", 0.75f, 0.42f, 0.04f, 0.6f);

    private static readonly string[] TownNames = { "ASHFORD", "MILLBROOK", "HARLOW", "PINECREST", "DUNMORE", "RED HOLLOW", "KESTREL", "WICKHAM", "ELDER CREEK", "STONEBRIDGE", "LOW MARSH", "CALDER" };
    public static string TownName(uint seed) => TownNames[Hash.Mix(seed) % (uint)TownNames.Length];

    public PoiBuilder(WorldGenerator world) { _world = world; }

    // ---- construction kit ----------------------------------------------------------------

    private sealed class Kit
    {
        public Node3D Root = null!;
        public StaticBody3D Body = null!;
        public PoiDesc Poi = null!;
        public Rng Rng;
        public WorldGenerator World = null!;
        public Transform3D Frame = Transform3D.Identity;
        private int _containers;

        public Vector3 P(Vector3 local) => Frame * local;

        /// <summary>Local position at (x,z) lifted onto the real terrain, for props off the flat pad.</summary>
        public Vector3 Ground(float x, float z, float lift = 0f)
        {
            var l = Frame * new Vector3(x, 0, z);
            var w = Poi.ToWorld(l);
            float y = World.Height(w.X, w.Z) - Poi.Center.Y;
            return Frame.AffineInverse() * new Vector3(l.X, y + lift, l.Z);
        }

        private Basis Rot(float yaw, float pitch, float roll) => Frame.Basis * Basis.FromEuler(new Vector3(pitch, yaw, roll));

        public MeshInstance3D Box(Vector3 c, Vector3 size, Material m, bool collide = true, float yaw = 0, float pitch = 0, float roll = 0)
        {
            var t = new Transform3D(Rot(yaw, pitch, roll), P(c));
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m, Transform = t };
            Root.AddChild(mi);
            if (collide) Body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = t });
            return mi;
        }

        public MeshInstance3D Cyl(Vector3 c, float radius, float height, Material m, bool collide = true, float topRadius = -1f, float pitch = 0, float roll = 0)
        {
            var t = new Transform3D(Rot(0, pitch, roll), P(c));
            var mesh = new CylinderMesh { TopRadius = topRadius < 0 ? radius : topRadius, BottomRadius = radius, Height = height, RadialSegments = 10, Rings = 1 };
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = m, Transform = t };
            Root.AddChild(mi);
            if (collide) Body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = radius, Height = height }, Transform = t });
            return mi;
        }

        public MeshInstance3D Prism(Vector3 c, Vector3 size, Material m, float yaw = 0)
        {
            var mi = new MeshInstance3D { Mesh = new PrismMesh { Size = size }, MaterialOverride = m, Transform = new Transform3D(Rot(yaw, 0, 0), P(c)) };
            Root.AddChild(mi);
            return mi;
        }

        public void Collider(Vector3 c, Vector3 size, float yaw = 0)
            => Body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = new Transform3D(Rot(yaw, 0, 0), P(c)) });

        /// <summary>Keeps rain particles out of a roofed volume.</summary>
        public void RainBlock(Vector3 c, Vector3 size)
            => Root.AddChild(new GpuParticlesCollisionBox3D { Size = size, Transform = new Transform3D(Rot(0, 0, 0), P(c)) });

        public Label3D Text(string text, Vector3 c, float yaw, int fontSize, Color color, float pixel = 0.005f)
        {
            var l = new Label3D
            {
                Text = text, FontSize = fontSize, PixelSize = pixel, Modulate = color, Shaded = true,
                OutlineSize = 0, DoubleSided = false, Transform = new Transform3D(Rot(yaw, 0, 0), P(c)),
            };
            Root.AddChild(l);
            return l;
        }

        /// <summary>A solid prop the player can use.</summary>
        public Interactable Use(Vector3 c, Vector3 size, Material? m, Func<PlayerController, string?> prompt, Action<PlayerController> use, float yaw = 0)
        {
            var it = new Interactable { PromptFn = prompt, UseFn = use, Transform = new Transform3D(Rot(yaw, 0, 0), P(c)), CollisionLayer = 1, CollisionMask = 0 };
            it.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            if (m != null) it.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = m });
            Root.AddChild(it);
            return it;
        }

        public string NextContainerId() => $"{Poi.Id}:c{_containers++}";

        public Interactable Container(Vector3 c, Vector3 size, Material m, string title, string table, float yaw = 0)
        {
            string id = NextContainerId();
            uint seed = Hash.Mix(Poi.Seed ^ Hash.OfString(id));
            return Use(c, size, m,
                _ => GameWorld.I.ContainerPrompt(id, title),
                _ => GameWorld.I.OpenContainer(id, title, table, seed), yaw);
        }
    }

    // ---- entry point ---------------------------------------------------------------------

    public Node3D Build(PoiDesc p)
    {
        var root = new Node3D { Name = $"{p.Kind}_{p.Segment}", Transform = p.Transform };
        var body = new StaticBody3D { CollisionLayer = 1, CollisionMask = 0 };
        body.SetMeta("surface", "hard");
        root.AddChild(body);
        var k = new Kit { Root = root, Body = body, Poi = p, Rng = new Rng(p.Seed), World = _world };
        switch (p.Kind)
        {
            case PoiKind.Starter: BuildStarter(k); break;
            case PoiKind.GasStation: BuildGasStation(k); break;
            case PoiKind.Diner: BuildDiner(k); break;
            case PoiKind.Workshop: BuildWorkshop(k); break;
            case PoiKind.Farmhouse: BuildFarm(k); break;
            case PoiKind.Cabin: BuildCabin(k); break;
            case PoiKind.Checkpoint: BuildCheckpoint(k); break;
            case PoiKind.Campsite: BuildCampsite(k); break;
            case PoiKind.Settlement: BuildSettlement(k); break;
            case PoiKind.Wreck: BuildWreckSite(k); break;
        }
        return root;
    }

    // ---- reusable pieces -----------------------------------------------------------------

    /// <summary>
    /// Enterable shell: floor slab with a deep foundation, four walls with a doorway on the -Z side,
    /// a ceiling (so the inside is dry and dark) and a gabled or flat roof.
    /// </summary>
    private void House(Kit k, Vector3 origin, float yaw, float w, float d, float wallH, Material wall, Material roof,
        bool gable = true, float doorX = 0f, float doorW = 1.15f, float doorH = 2.1f, bool windows = true, bool doorLeaf = true)
    {
        var saved = k.Frame;
        k.Frame = saved * new Transform3D(new Basis(Vector3.Up, yaw), origin);
        const float t = 0.2f, fl = 0.08f;
        k.Box(new Vector3(0, fl - 0.75f, 0), new Vector3(w + 0.3f, 1.5f, d + 0.3f), Concrete);
        float wy = fl + wallH * 0.5f;
        k.Box(new Vector3(0, wy, d * 0.5f - t * 0.5f), new Vector3(w, wallH, t), wall);
        k.Box(new Vector3(-w * 0.5f + t * 0.5f, wy, 0), new Vector3(t, wallH, d), wall);
        k.Box(new Vector3(w * 0.5f - t * 0.5f, wy, 0), new Vector3(t, wallH, d), wall);
        float fz = -d * 0.5f + t * 0.5f;
        float left = doorX - doorW * 0.5f, right = doorX + doorW * 0.5f;
        if (left > -w * 0.5f + 0.05f)
            k.Box(new Vector3((-w * 0.5f + left) * 0.5f, wy, fz), new Vector3(left + w * 0.5f, wallH, t), wall);
        if (right < w * 0.5f - 0.05f)
            k.Box(new Vector3((w * 0.5f + right) * 0.5f, wy, fz), new Vector3(w * 0.5f - right, wallH, t), wall);
        if (wallH - doorH > 0.05f)
            k.Box(new Vector3(doorX, fl + doorH + (wallH - doorH) * 0.5f, fz), new Vector3(doorW, wallH - doorH, t), wall);
        if (doorLeaf && doorW < 1.6f) // a door left hanging open
            k.Box(new Vector3(left - 0.05f, fl + doorH * 0.5f, fz - doorW * 0.5f), new Vector3(0.06f, doorH, doorW - 0.05f), DarkWood, true, 0.25f);

        k.Box(new Vector3(0, fl + wallH + 0.06f, 0), new Vector3(w + 0.1f, 0.12f, d + 0.1f), gable ? wall : roof);
        if (gable)
        {
            float rh = MathF.Min(w, d) * 0.32f;
            bool ridgeAlongZ = d >= w;
            var size = ridgeAlongZ ? new Vector3(w + 0.9f, rh, d + 0.9f) : new Vector3(d + 0.9f, rh, w + 0.9f);
            k.Prism(new Vector3(0, fl + wallH + 0.12f + rh * 0.5f, 0), size, roof, ridgeAlongZ ? 0f : Mathf.Pi * 0.5f);
        }
        else
        {
            k.Box(new Vector3(0, fl + wallH + 0.2f, 0), new Vector3(w + 0.7f, 0.18f, d + 0.7f), roof, false);
        }

        if (windows)
        {
            float wyy = fl + 1.5f;
            foreach (float sx in new[] { -1f, 1f })
            {
                for (float z = -d * 0.5f + 1.6f; z < d * 0.5f - 1.0f; z += 2.6f)
                    k.Box(new Vector3(sx * (w * 0.5f + 0.01f), wyy, z), new Vector3(0.06f, 0.95f, 1.1f), Glass, false);
            }
            for (float x = -w * 0.5f + 1.3f; x < w * 0.5f - 0.8f; x += 2.5f)
            {
                if (MathF.Abs(x - doorX) < doorW * 0.5f + 0.75f) continue;
                k.Box(new Vector3(x, wyy, -d * 0.5f - 0.01f), new Vector3(1.1f, 0.95f, 0.06f), Glass, false);
            }
        }
        k.RainBlock(new Vector3(0, fl + wallH * 0.5f + 0.5f, 0), new Vector3(w + 0.6f, wallH + 1.2f, d + 0.6f));
        k.Frame = saved;
    }

    private void Bed(Kit k, Vector3 c, float yaw, Material blanket)
    {
        k.Box(c + new Vector3(0, 0.22f, 0), new Vector3(0.95f, 0.3f, 1.95f), DarkWood, true, yaw);
        k.Use(c + new Vector3(0, 0.45f, 0), new Vector3(0.9f, 0.2f, 1.9f), blanket,
            _ => "Sleep", _ => GameWorld.I.Sleep("You sleep in a stranger's bed."), yaw);
    }

    private void Campfire(Kit k, Vector3 c)
    {
        k.Cyl(c + new Vector3(0, 0.08f, 0), 0.55f, 0.16f, Stone, false);
        k.Box(c + new Vector3(0, 0.2f, 0), new Vector3(0.7f, 0.1f, 0.12f), Black, false, 0.5f);
        k.Box(c + new Vector3(0, 0.22f, 0), new Vector3(0.7f, 0.1f, 0.12f), Black, false, -0.9f);
        Node3D? flame = null;
        var root = k.Root;
        var pos = k.P(c);
        k.Use(c + new Vector3(0, 0.2f, 0), new Vector3(1.1f, 0.5f, 1.1f), null,
            p =>
            {
                if (flame == null) return "Light the campfire";
                return p.Inventory.Has("raw_meat") ? "Cook the venison" : "Warm yourself by the fire";
            },
            p =>
            {
                var g = GameWorld.I;
                if (flame == null)
                {
                    flame = MakeFlame();
                    flame.Position = pos + new Vector3(0, 0.3f, 0);
                    root.AddChild(flame);
                    g.Audio.Play("fire_light");
                    g.Toast("The fire catches.");
                    return;
                }
                int n = p.Inventory.Count("raw_meat");
                if (n > 0)
                {
                    p.Inventory.Remove("raw_meat", n);
                    p.Inventory.Add("cooked_meat", n);
                    g.Audio.Play("fire_light");
                    g.Toast($"You cook {n} cut{(n > 1 ? "s" : "")} of venison.");
                }
                else
                {
                    p.Stats.Stamina = 100;
                    g.Toast("The warmth eases your shoulders.");
                }
            });
    }

    private static Node3D MakeFlame()
    {
        var n = new Node3D();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.55f, 0.12f),
            EmissionEnabled = true, Emission = new Color(1f, 0.5f, 0.1f), EmissionEnergyMultiplier = 3f,
        };
        var cone = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.22f, Height = 0.7f, RadialSegments = 6, Rings = 1 },
            MaterialOverride = mat, Position = new Vector3(0, 0.25f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        n.AddChild(cone);
        var light = new OmniLight3D { LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 3f, OmniRange = 11f, ShadowEnabled = true, Position = new Vector3(0, 0.6f, 0) };
        n.AddChild(light);
        var tween = n.CreateTween().SetLoops();
        tween.TweenProperty(light, "light_energy", 2.2f, 0.13f);
        tween.Parallel().TweenProperty(cone, "scale", new Vector3(0.85f, 1.15f, 0.85f), 0.13f);
        tween.TweenProperty(light, "light_energy", 3.3f, 0.19f);
        tween.Parallel().TweenProperty(cone, "scale", new Vector3(1.1f, 0.9f, 1.1f), 0.19f);
        tween.TweenProperty(light, "light_energy", 2.7f, 0.09f);
        return n;
    }

    private void WaterPump(Kit k, Vector3 c)
    {
        k.Box(c + new Vector3(0, 0.1f, 0), new Vector3(1.2f, 0.2f, 1.2f), Concrete);
        k.Cyl(c + new Vector3(0, 0.75f, 0), 0.07f, 1.1f, FadedRed, false);
        k.Box(c + new Vector3(0, 1.25f, -0.25f), new Vector3(0.06f, 0.06f, 0.7f), FadedRed, false, 0, 0.4f);
        k.Box(c + new Vector3(0, 1.0f, 0.22f), new Vector3(0.07f, 0.07f, 0.3f), FadedRed, false);
        k.Use(c + new Vector3(0, 0.8f, 0), new Vector3(0.5f, 1.4f, 0.5f), null,
            p => p.Inventory.Has("empty_bottle") ? "Drink and fill your bottles" : "Drink from the pump",
            p =>
            {
                var g = GameWorld.I;
                p.Stats.Drink(60);
                int n = p.Inventory.Count("empty_bottle");
                int filled = 0;
                for (int i = 0; i < n; i++)
                {
                    p.Inventory.Remove("empty_bottle", 1);
                    if (p.Inventory.Add("water", 1) == 1) filled++; else { p.Inventory.Add("empty_bottle", 1); break; }
                }
                g.Audio.Play("drink");
                g.Toast(filled > 0 ? $"Cold, clean water. You fill {filled} bottle{(filled > 1 ? "s" : "")}." : "Cold, clean water.");
            });
    }

    private void FuelPump(Kit k, Vector3 c, int index)
    {
        string id = $"{k.Poi.Id}:pump{index}";
        var rng = new Rng(Hash.Mix(k.Poi.Seed ^ (uint)(index * 7919 + 13)));
        float initial = rng.Chance(0.7f) ? rng.Range(10f, 38f) : 0f;
        k.Box(c + new Vector3(0, 0.1f, 0), new Vector3(1.0f, 0.2f, 0.7f), Concrete);
        k.Box(c + new Vector3(0, 0.95f, 0), new Vector3(0.7f, 1.5f, 0.45f), index % 2 == 0 ? FadedRed : Cream, false);
        k.Box(c + new Vector3(0, 1.3f, -0.235f), new Vector3(0.45f, 0.3f, 0.02f), Black, false);
        k.Box(c + new Vector3(0.4f, 0.9f, 0), new Vector3(0.06f, 0.5f, 0.1f), Black, false);
        k.Use(c + new Vector3(0, 0.95f, 0), new Vector3(0.75f, 1.55f, 0.5f), null,
            p =>
            {
                var g = GameWorld.I;
                float left = g.State.Pumps.TryGetValue(id, out float v) ? v : initial;
                if (left < 0.5f) return "Fuel pump (dry)";
                if (g.NearbyVehicle(11f, p.GlobalPosition) != null) return $"Pump fuel into your car ({left:0} L left)";
                if (p.Inventory.Has("empty_can")) return $"Fill a fuel can ({left:0} L left)";
                return $"Fuel pump ({left:0} L left) - bring your car or an empty can";
            },
            p =>
            {
                var g = GameWorld.I;
                float left = g.State.Pumps.TryGetValue(id, out float v) ? v : initial;
                if (left < 0.5f) { g.Toast("Nothing but air. This pump is dry."); g.Audio.Play("click"); return; }
                var car = g.NearbyVehicle(11f, p.GlobalPosition);
                if (car != null)
                {
                    float room = VehicleController.TankSize - car.Fuel;
                    if (room < 0.5f) { g.Toast("The tank is already full."); return; }
                    float amount = MathF.Min(room, left);
                    car.Fuel += amount;
                    g.State.Pumps[id] = left - amount;
                    g.Audio.Play("pour");
                    g.Toast($"The pump still has pressure. +{amount:0.0} L (tank {car.Fuel:0} L).");
                }
                else if (p.Inventory.Has("empty_can") && left >= 5f)
                {
                    p.Inventory.Remove("empty_can", 1);
                    if (p.Inventory.Add("fuel_can", 1) == 0)
                    {
                        p.Inventory.Add("empty_can", 1);
                        g.Toast("A full can is too heavy to carry right now.");
                        return;
                    }
                    g.State.Pumps[id] = left - 5f;
                    g.Audio.Play("pour");
                    g.Toast("You fill the can.");
                }
                else g.Toast(left < 5f ? "Not enough left to fill a can." : "Park your car next to the pump, or bring an empty can.");
            });
    }

    private PackedScene? CarScene(string name)
    {
        if (_cars.TryGetValue(name, out var s)) return s;
        string path = $"res://assets/vehicles/{name}.glb";
        s = ResourceLoader.Exists(path) ? GD.Load<PackedScene>(path) : null;
        if (s == null) Log.Warn("PoiBuilder", $"Missing vehicle model {path}, wrecks will use a placeholder box");
        _cars[name] = s;
        return s;
    }

    private Material Rusted(Material src, float amount)
    {
        if (_rusted.TryGetValue(src, out var m)) return m;
        if (src is BaseMaterial3D b)
        {
            var d = (BaseMaterial3D)b.Duplicate();
            bool glass = b.ResourceName.Contains("Window") || b.ResourceName.Contains("light", StringComparison.OrdinalIgnoreCase);
            d.AlbedoColor = glass ? new Color(0.03f, 0.035f, 0.04f) : b.AlbedoColor.Lerp(new Color(0.22f, 0.12f, 0.07f), amount);
            d.Roughness = glass ? 0.3f : 0.95f;
            d.Metallic = 0.1f;
            d.EmissionEnabled = false;
            m = d;
        }
        else m = src;
        _rusted[src] = m;
        return m;
    }

    /// <summary>A dead car. The whole body is a searchable container.</summary>
    private void WreckCar(Kit k, Vector3 c, float yaw, bool searchable = true, string? model = null)
    {
        model ??= WreckModels[k.Rng.Int(WreckModels.Length - 1)];
        const float scale = 1.12f;
        float roll = k.Rng.Range(-0.05f, 0.05f);
        var size = new Vector3(2.0f, 1.25f, 4.5f);
        Node3D holder;
        if (searchable)
        {
            string id = k.NextContainerId();
            uint seed = Hash.Mix(k.Poi.Seed ^ Hash.OfString(id));
            var it = k.Use(c + new Vector3(0, 0.62f, 0), size, null,
                _ => GameWorld.I.ContainerPrompt(id, "Abandoned car"),
                _ => GameWorld.I.OpenContainer(id, "Abandoned car", "car", seed), yaw);
            holder = new Node3D { Position = new Vector3(0, -0.66f, 0), Rotation = new Vector3(0, 0, roll) };
            it.AddChild(holder);
        }
        else
        {
            holder = new Node3D { Transform = new Transform3D(k.Frame.Basis * Basis.FromEuler(new Vector3(0, yaw, roll)), k.P(c) + new Vector3(0, -0.04f, 0)) };
            k.Root.AddChild(holder);
            k.Collider(c + new Vector3(0, 0.62f, 0), size, yaw);
        }
        var scene = CarScene(model);
        if (scene == null)
        {
            holder.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.9f, 1.0f, 4.3f) }, MaterialOverride = Rust, Position = new Vector3(0, 0.6f, 0) });
            return;
        }
        var car = scene.Instantiate<Node3D>();
        car.Scale = Vector3.One * scale;
        holder.AddChild(car);
        float rustAmount = k.Rng.Range(0.3f, 0.65f);
        bool missingWheel = k.Rng.Chance(0.35f);
        foreach (var n in car.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            if (missingWheel && mi.Name.ToString().Contains("Front")) { mi.Visible = false; missingWheel = false; continue; }
            if (mi.Mesh == null) continue;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var src = mi.Mesh.SurfaceGetMaterial(s);
                if (src != null) mi.SetSurfaceOverrideMaterial(s, Rusted(src, rustAmount));
            }
        }
    }

    private void Fence(Kit k, Vector3 a, Vector3 b, Material m)
    {
        float len = a.DistanceTo(b);
        int posts = Math.Max(2, (int)(len / 2.4f) + 1);
        var dir = (b - a) / len;
        float yaw = MathF.Atan2(dir.X, dir.Z);
        for (int i = 0; i < posts; i++)
        {
            var p = a.Lerp(b, i / (float)(posts - 1));
            var g = k.Ground(p.X, p.Z);
            k.Box(g + new Vector3(0, 0.55f, 0), new Vector3(0.1f, 1.1f, 0.1f), m, false);
        }
        var mid = (a + b) * 0.5f;
        var gm = k.Ground(mid.X, mid.Z);
        k.Box(gm + new Vector3(0, 0.85f, 0), new Vector3(0.05f, 0.1f, len), m, false, yaw);
        k.Box(gm + new Vector3(0, 0.45f, 0), new Vector3(0.05f, 0.1f, len), m, false, yaw);
        k.Collider(gm + new Vector3(0, 0.55f, 0), new Vector3(0.12f, 1.1f, len), yaw);
    }

    private void Barrel(Kit k, Vector3 c, Material m) => k.Cyl(c + new Vector3(0, 0.45f, 0), 0.3f, 0.9f, m);

    private void TireStack(Kit k, Vector3 c, int n)
    {
        for (int i = 0; i < n; i++) k.Cyl(c + new Vector3(0, 0.11f + i * 0.22f, 0), 0.34f, 0.2f, Black, i == 0);
    }

    private void Shelf(Kit k, Vector3 c, float yaw, string title, string table)
    {
        k.Container(c + new Vector3(0, 0.9f, 0), new Vector3(1.6f, 1.8f, 0.4f), Metal, title, table, yaw);
    }

    public static Node3D MakeSign(string text, Vector3 pos, float yaw, bool warning)
    {
        var n = new Node3D { Position = pos, Rotation = new Vector3(0, yaw, 0) };
        float width = MathF.Max(1.5f, text.Length * 0.24f + 0.5f);
        n.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.045f, Height = 2.7f, RadialSegments = 6, Rings = 1 }, Position = new Vector3(0, 1.35f, 0), MaterialOverride = Metal });
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, 0.62f, 0.04f) }, Position = new Vector3(0, 2.35f, 0.03f), MaterialOverride = warning ? SignAmber : SignGreen });
        n.AddChild(new Label3D
        {
            Text = text, FontSize = 72, PixelSize = 0.0048f, Position = new Vector3(0, 2.35f, 0.055f), Shaded = true,
            Modulate = warning ? new Color(0.05f, 0.04f, 0.02f) : new Color(0.85f, 0.85f, 0.8f), OutlineSize = 0,
        });
        return n;
    }

    // ---- the places ----------------------------------------------------------------------

    private void BuildStarter(Kit k)
    {
        // An old bus shelter that somebody has been living in.
        k.Box(new Vector3(0, 0.05f, 5f), new Vector3(4.4f, 0.1f, 2.6f), Concrete, false);
        k.Box(new Vector3(0, 1.25f, 6.2f), new Vector3(4.4f, 2.4f, 0.12f), FadedBlue);
        k.Box(new Vector3(-2.14f, 1.25f, 5f), new Vector3(0.12f, 2.4f, 2.5f), FadedBlue);
        k.Box(new Vector3(2.14f, 1.25f, 5f), new Vector3(0.12f, 2.4f, 2.5f), FadedBlue);
        k.Box(new Vector3(0, 2.5f, 4.8f), new Vector3(4.9f, 0.12f, 3.2f), RoofTin, true, 0, -0.06f);
        k.RainBlock(new Vector3(0, 1.3f, 4.9f), new Vector3(4.8f, 2.6f, 3.1f));
        k.Box(new Vector3(-0.9f, 0.45f, 5.8f), new Vector3(2.0f, 0.08f, 0.45f), Wood);
        k.Box(new Vector3(-0.9f, 0.22f, 5.8f), new Vector3(1.8f, 0.4f, 0.1f), Wood, false);
        k.Use(new Vector3(1.1f, 0.16f, 5.4f), new Vector3(0.8f, 0.14f, 1.9f), Olive,
            _ => "Sleep in your bedroll", _ => GameWorld.I.Sleep("You sleep in the shelter."));
        k.Container(new Vector3(-1.7f, 0.3f, 4.4f), new Vector3(0.6f, 0.5f, 0.45f), Wood, "Your supply crate", "starter");
        Campfire(k, new Vector3(-4.5f, 0, 0.5f));
        k.Box(new Vector3(-5.6f, 0.2f, 1.6f), new Vector3(1.5f, 0.35f, 0.35f), DarkWood, true, 0.6f);
        Barrel(k, new Vector3(3.2f, 0, 5.6f), Rust);
        k.Root.AddChild(MakeSign("BUS", k.P(new Vector3(-3.4f, 0, 3.0f)), Mathf.Pi, false));
    }

    private void BuildGasStation(Kit k)
    {
        bool mirrored = k.Rng.Chance(0.5f);
        float mx = mirrored ? -1f : 1f;
        // canopy over the forecourt
        foreach (float x in new[] { -5f, 5f })
            foreach (float z in new[] { -9f, -4f })
                k.Box(new Vector3(x, 2.3f, z), new Vector3(0.3f, 4.6f, 0.3f), White);
        k.Box(new Vector3(0, 4.75f, -6.5f), new Vector3(12.5f, 0.45f, 7.5f), White);
        k.Box(new Vector3(0, 4.75f, -10.27f), new Vector3(12.5f, 0.46f, 0.04f), FadedRed, false);
        k.RainBlock(new Vector3(0, 2.4f, -6.5f), new Vector3(12.5f, 4.8f, 7.5f));
        k.Box(new Vector3(0, 0.08f, -6.5f), new Vector3(5.5f, 0.16f, 1.2f), Concrete);
        FuelPump(k, new Vector3(-1.6f, 0.1f, -6.5f), 0);
        FuelPump(k, new Vector3(1.6f, 0.1f, -6.5f), 1);

        // shop
        House(k, new Vector3(0, 0, 8f), 0f, 11f, 6.5f, 3.0f, Cream, RoofDark, gable: false, doorX: -2.5f * mx);
        k.Text("FUEL & SUPPLIES", new Vector3(0, 3.55f, 4.55f), Mathf.Pi, 96, new Color(0.5f, 0.12f, 0.08f));
        k.Box(new Vector3(0, 3.55f, 4.62f), new Vector3(7.5f, 0.75f, 0.08f), White, false);
        Shelf(k, new Vector3(3.2f * mx, 0.08f, 10.6f), 0, "Shop shelf", "shop");
        Shelf(k, new Vector3(0.6f * mx, 0.08f, 10.6f), 0, "Shop shelf", "shop");
        k.Box(new Vector3(2.5f * mx, 0.58f, 6.6f), new Vector3(3.2f, 1.0f, 0.6f), Wood);
        k.Container(new Vector3(4.6f * mx, 0.98f, 8.2f), new Vector3(0.75f, 1.8f, 0.7f), White, "Drinks cooler", "fridge", Mathf.Pi * 0.5f);
        k.Container(new Vector3(-4.6f * mx, 0.5f, 10.4f), new Vector3(0.9f, 0.85f, 0.6f), Metal, "Back room locker", k.Rng.Chance(0.5f) ? "garage" : "medical");

        // tall price sign: the landmark you spot from the road
        k.Cyl(new Vector3(-12.5f * mx, 4f, -13f), 0.14f, 8f, Metal);
        k.Box(new Vector3(-12.5f * mx, 7.4f, -13f), new Vector3(2.6f, 1.5f, 0.25f), FadedRed, false);
        k.Text("GAS", new Vector3(-12.5f * mx, 7.45f, -13.14f), Mathf.Pi, 160, new Color(0.9f, 0.88f, 0.8f));
        k.Text("GAS", new Vector3(-12.5f * mx, 7.45f, -12.86f), 0, 160, new Color(0.9f, 0.88f, 0.8f));

        k.Container(new Vector3(9.5f * mx, 0.6f, 9f), new Vector3(1.8f, 1.2f, 1.0f), Olive, "Dumpster", "trash", 0.2f);
        Barrel(k, new Vector3(7.2f * mx, 0, 5.2f), Rust);
        TireStack(k, new Vector3(-7f * mx, 0, 5.4f), 3);
        if (k.Rng.Chance(0.6f)) WreckCar(k, new Vector3(-9.5f * mx, 0, -1.5f), k.Rng.Range(-0.5f, 0.5f));
    }

    private void BuildDiner(Kit k)
    {
        House(k, new Vector3(0, 0, 6f), 0f, 14f, 7.5f, 3.0f, FadedBlue, RoofTin, gable: false, doorX: 0f);
        k.Box(new Vector3(0, 4.05f, 2.4f), new Vector3(6.0f, 1.3f, 0.2f), FadedRed, false);
        k.Text("DINER", new Vector3(0, 4.05f, 2.28f), Mathf.Pi, 200, new Color(0.92f, 0.88f, 0.75f));
        // booths and counter
        for (int i = 0; i < 3; i++)
        {
            float x = -5.4f + i * 2.4f;
            if (MathF.Abs(x) < 1.2f) continue;
            k.Box(new Vector3(x, 0.83f, 3.5f), new Vector3(1.1f, 0.06f, 0.7f), White);
            k.Box(new Vector3(x, 0.45f, 3.5f), new Vector3(0.12f, 0.75f, 0.12f), Metal, false);
            k.Box(new Vector3(x - 0.85f, 0.5f, 3.5f), new Vector3(0.45f, 0.85f, 0.75f), FadedRed);
            k.Box(new Vector3(x + 0.85f, 0.5f, 3.5f), new Vector3(0.45f, 0.85f, 0.75f), FadedRed);
        }
        k.Box(new Vector3(2.5f, 0.6f, 6.6f), new Vector3(7.5f, 1.05f, 0.6f), White);
        k.Container(new Vector3(5.4f, 0.98f, 9.2f), new Vector3(0.9f, 1.8f, 0.7f), Metal, "Kitchen fridge", "fridge");
        k.Container(new Vector3(2.6f, 0.5f, 9.3f), new Vector3(1.6f, 0.9f, 0.6f), Metal, "Kitchen cupboard", "kitchen");
        k.Container(new Vector3(-0.2f, 0.5f, 9.3f), new Vector3(1.6f, 0.9f, 0.6f), Metal, "Pantry box", "kitchen");
        k.Container(new Vector3(-6.1f, 0.5f, 8.8f), new Vector3(0.5f, 0.6f, 0.4f), FadedRed, "First aid cabinet", "medical");
        k.Container(new Vector3(-9.5f, 0.6f, 9f), new Vector3(1.8f, 1.2f, 1.0f), Olive, "Dumpster", "trash", -0.15f);
        if (k.Rng.Chance(0.7f)) WreckCar(k, new Vector3(6.5f, 0, -7f), Mathf.Pi * 0.5f + k.Rng.Range(-0.3f, 0.3f));
        if (k.Rng.Chance(0.4f)) WreckCar(k, new Vector3(-7f, 0, -8f), k.Rng.Range(-0.4f, 0.4f));
    }

    private void BuildWorkshop(Kit k)
    {
        House(k, new Vector3(0, 0, 5f), 0f, 10f, 9f, 3.6f, Concrete, RoofTin, gable: true, doorX: -1.5f, doorW: 4.4f, doorH: 3.0f, windows: false, doorLeaf: false);
        k.Box(new Vector3(0, 4.3f, 0.35f), new Vector3(4.4f, 0.7f, 0.1f), Yellow, false);
        k.Text("GARAGE", new Vector3(0, 4.3f, 0.28f), Mathf.Pi, 110, new Color(0.06f, 0.05f, 0.04f));
        k.Container(new Vector3(4.3f, 0.98f, 8.6f), new Vector3(1.0f, 1.8f, 0.55f), FadedRed, "Tool cabinet", "garage", 0);
        k.Container(new Vector3(2.4f, 0.48f, 8.9f), new Vector3(1.5f, 0.8f, 0.6f), Metal, "Parts bin", "garage");
        k.Box(new Vector3(-3.6f, 0.9f, 8.9f), new Vector3(2.4f, 0.08f, 0.8f), Wood);
        k.Box(new Vector3(-3.6f, 0.45f, 8.9f), new Vector3(2.2f, 0.8f, 0.6f), DarkWood, false);
        TireStack(k, new Vector3(4.2f, 0.08f, 2.4f), 4);
        TireStack(k, new Vector3(6.2f, 0, 1.4f), 2);
        Barrel(k, new Vector3(-6.2f, 0, 1.6f), Rust);
        Barrel(k, new Vector3(-6.9f, 0, 2.3f), FadedBlue);
        // a car that never got finished, up on blocks inside
        WreckCar(k, new Vector3(-1.5f, 0.3f, 5.5f), Mathf.Pi + k.Rng.Range(-0.08f, 0.08f));
        k.Box(new Vector3(-1.5f, 0.2f, 5.5f), new Vector3(1.5f, 0.3f, 3.2f), Concrete, false);
        if (k.Rng.Chance(0.5f)) WreckCar(k, new Vector3(8f, 0, -6f), k.Rng.Range(0, Mathf.Tau));
    }

    private void BuildFarm(Kit k)
    {
        House(k, new Vector3(-10f, 0, 7f), 0.15f, 9f, 8f, 2.9f, Plaster, RoofDark, gable: true, doorX: 1.5f);
        var saved = k.Frame;
        k.Frame = saved * new Transform3D(new Basis(Vector3.Up, 0.15f), new Vector3(-10f, 0.08f, 7f));
        Bed(k, new Vector3(-3.4f, 0, 2.6f), 0, Fabric);
        k.Container(new Vector3(-3.6f, 0.95f, -1.5f), new Vector3(0.55f, 1.9f, 1.3f), DarkWood, "Wardrobe", "bedroom");
        k.Container(new Vector3(3.3f, 0.5f, 3.4f), new Vector3(1.8f, 0.9f, 0.6f), PaleWood, "Kitchen cupboard", "kitchen");
        k.Container(new Vector3(3.9f, 0.9f, 1.4f), new Vector3(0.7f, 1.7f, 0.7f), White, "Fridge", "fridge");
        k.Box(new Vector3(0.6f, 0.75f, 1.2f), new Vector3(1.5f, 0.07f, 0.9f), Wood);
        k.Box(new Vector3(0.6f, 0.38f, 1.2f), new Vector3(1.3f, 0.7f, 0.1f), Wood, false);
        k.Frame = saved;

        // barn
        House(k, new Vector3(12f, 0, 5f), -0.2f, 9f, 13f, 4.2f, FadedRed, RoofTin, gable: true, doorX: 0f, doorW: 4.6f, doorH: 3.4f, windows: false, doorLeaf: false);
        k.Frame = saved * new Transform3D(new Basis(Vector3.Up, -0.2f), new Vector3(12f, 0.08f, 5f));
        k.Container(new Vector3(-3.4f, 0.45f, 5.3f), new Vector3(1.3f, 0.8f, 0.9f), Wood, "Barn crate", "barn");
        k.Container(new Vector3(3.5f, 0.4f, 4.8f), new Vector3(0.9f, 0.7f, 0.9f), Wood, "Feed box", "barn");
        for (int i = 0; i < 3; i++) k.Box(new Vector3(3.2f, 0.35f + (i % 2) * 0.7f, -1.5f + i * 1.2f), new Vector3(1.4f, 0.7f, 1.0f), Yellow);
        k.Frame = saved;

        WaterPump(k, new Vector3(0f, 0, -2f));
        Fence(k, new Vector3(-22f, 0, -14f), new Vector3(-22f, 0, 18f), PaleWood);
        Fence(k, new Vector3(-22f, 0, 18f), new Vector3(20f, 0, 20f), PaleWood);
        Fence(k, new Vector3(-22f, 0, -14f), new Vector3(-5f, 0, -20f), PaleWood);
        k.Box(new Vector3(-3.5f, 0.4f, 12.5f), new Vector3(2.4f, 0.8f, 0.9f), DarkWood);   // woodpile
        if (k.Rng.Chance(0.6f)) WreckCar(k, new Vector3(3f, 0, 10f), k.Rng.Range(0, Mathf.Tau));
        Barrel(k, new Vector3(6.4f, 0, -1.2f), FadedBlue);
    }

    private void BuildCabin(Kit k)
    {
        House(k, new Vector3(0, 0, 3f), 0f, 6.5f, 5.5f, 2.6f, DarkWood, RoofDark, gable: true, doorX: -1.2f);
        var saved = k.Frame;
        k.Frame = saved * new Transform3D(Basis.Identity, new Vector3(0, 0.08f, 3f));
        Bed(k, new Vector3(2.4f, 0, 1.3f), 0, FadedRed);
        k.Container(new Vector3(-2.5f, 0.35f, 2.1f), new Vector3(1.1f, 0.6f, 0.6f), Wood, "Hunter's chest", "hunter");
        k.Container(new Vector3(0.2f, 0.5f, 2.3f), new Vector3(1.2f, 0.9f, 0.5f), PaleWood, "Cupboard", "kitchen");
        k.Cyl(new Vector3(-2.6f, 0.45f, -0.4f), 0.3f, 0.8f, Black);            // stove
        k.Cyl(new Vector3(-2.6f, 1.9f, -0.4f), 0.07f, 2.2f, Black, false);
        k.Frame = saved;
        k.Box(new Vector3(4.6f, 0.45f, 3f), new Vector3(0.9f, 0.9f, 2.6f), Wood);                  // woodpile
        k.Box(new Vector3(-5.5f, 1.1f, 6f), new Vector3(1.1f, 2.2f, 1.1f), DarkWood);              // outhouse
        k.Prism(new Vector3(-5.5f, 2.4f, 6f), new Vector3(1.4f, 0.4f, 1.4f), RoofDark);
        k.Box(new Vector3(1.5f, 0.25f, -2.4f), new Vector3(0.5f, 0.5f, 0.5f), Wood);               // chopping block
        Campfire(k, new Vector3(4f, 0, -3f));
    }

    private void BuildCampsite(Kit k)
    {
        Campfire(k, new Vector3(0.5f, 0, -0.5f));
        k.Box(new Vector3(2.4f, 0.2f, -0.2f), new Vector3(0.4f, 0.4f, 1.7f), DarkWood, true, 0.2f);
        k.Box(new Vector3(0.2f, 0.2f, -2.6f), new Vector3(1.7f, 0.4f, 0.4f), DarkWood, true, 0.1f);
        // tent: you can crawl in and sleep
        var tent = k.Rng.Chance(0.5f) ? Orange : Olive;
        k.Prism(new Vector3(-3.2f, 0.75f, 2.2f), new Vector3(2.4f, 1.5f, 3.0f), tent, 0.4f);
        k.RainBlock(new Vector3(-3.2f, 0.8f, 2.2f), new Vector3(2.4f, 1.6f, 3.0f));
        k.Use(new Vector3(-3.2f, 0.25f, 2.2f), new Vector3(1.6f, 0.5f, 2.6f), null,
            _ => "Sleep in the tent", _ => GameWorld.I.Sleep("You sleep in an abandoned tent."), 0.4f);
        k.Container(new Vector3(2.6f, 0.25f, 2.4f), new Vector3(0.7f, 0.45f, 0.45f), FadedBlue, "Cooler", "camp");
        k.Container(new Vector3(-0.8f, 0.22f, 3.4f), new Vector3(0.45f, 0.45f, 0.35f), Olive, "Backpack", "camp", 0.7f);
        if (k.Rng.Chance(0.4f))
        {
            k.Prism(new Vector3(3.5f, 0.6f, -3.6f), new Vector3(2.0f, 1.2f, 2.4f), Fabric, -0.8f);
            k.Container(new Vector3(4.6f, 0.2f, -1.6f), new Vector3(0.5f, 0.4f, 0.4f), Fabric, "Duffel bag", "camp");
        }
        if (k.Rng.Chance(0.35f)) WreckCar(k, k.Ground(-6f, -5f), k.Rng.Range(0, Mathf.Tau));
    }

    private void BuildCheckpoint(Kit k)
    {
        // Pad is beside the road; barriers are placed on the shoulders so the lanes stay passable.
        float roadZ = -15f;
        k.Box(new Vector3(0, 1.1f, 2.5f), new Vector3(3.2f, 2.2f, 2.6f), Olive);                     // guard hut
        k.Box(new Vector3(0, 2.3f, 2.3f), new Vector3(3.8f, 0.14f, 3.4f), RoofTin, false, 0, -0.08f);
        k.Text("CHECKPOINT", new Vector3(0, 1.75f, 1.18f), Mathf.Pi, 60, new Color(0.8f, 0.8f, 0.7f));
        k.Container(new Vector3(2.6f, 0.35f, 2.2f), new Vector3(1.2f, 0.6f, 0.7f), Olive, "Military crate", "military");
        k.Container(new Vector3(-2.5f, 0.3f, 1.4f), new Vector3(0.9f, 0.5f, 0.6f), Olive, "Supply box", k.Rng.Chance(0.5f) ? "medical" : "camp");
        // sandbags
        for (int i = 0; i < 5; i++) k.Box(new Vector3(-3f + i * 1.5f, 0.3f, -2.5f), new Vector3(1.4f, 0.6f, 0.5f), Mat("sand", 0.36f, 0.32f, 0.22f), true, k.Rng.Range(-0.08f, 0.08f));
        // floodlight mast, long dead
        k.Cyl(new Vector3(4.5f, 3f, -1f), 0.08f, 6f, Metal);
        k.Box(new Vector3(4.5f, 6f, -1.2f), new Vector3(0.9f, 0.4f, 0.3f), Black, false, 0, 0.5f);
        foreach (float side in new[] { -1f, 1f })
        {
            float z = roadZ + side * 4.75f;
            for (int i = 0; i < 4; i++)
            {
                float x = -7.5f + i * 5f + (side > 0 ? 2f : 0f);
                var g = k.Ground(x, z);
                k.Box(g + new Vector3(0, 0.4f, 0), new Vector3(2.2f, 0.8f, 0.45f), Concrete, true, k.Rng.Range(-0.12f, 0.12f));
            }
        }
        foreach (float x in new[] { -11f, 11f })
        {
            var g = k.Ground(x, roadZ + 4.3f);
            Barrel(k, g, Orange);
            g = k.Ground(x, roadZ - 4.3f);
            Barrel(k, g, Orange);
        }
        WreckCar(k, k.Ground(6f, roadZ - 6.4f), 1.1f, true, "Cop");
        if (k.Rng.Chance(0.6f)) WreckCar(k, k.Ground(-9f, roadZ + 7.2f), -0.4f);
    }

    private void BuildSettlement(Kit k)
    {
        string name = TownName(k.Poi.Seed);
        var walls = new Material[] { Plaster, FadedBlue, Cream, PaleWood };
        var spots = new (Vector3 pos, float yaw, float w, float d)[]
        {
            (new Vector3(-19f, 0, 6f), -Mathf.Pi * 0.5f, 7f, 8f),
            (new Vector3(-6f, 0, 21f), 0f, 8f, 7f),
            (new Vector3(9f, 0, 21f), 0f, 7f, 7.5f),
            (new Vector3(21f, 0, 5f), Mathf.Pi * 0.5f, 7.5f, 8f),
        };
        int idx = 0;
        foreach (var s in spots)
        {
            if (idx > 0 && k.Rng.Chance(0.15f)) { idx++; continue; }
            House(k, s.pos, s.yaw, s.w, s.d, 2.8f, walls[(idx + (int)(k.Poi.Seed % 4)) % 4], idx % 2 == 0 ? RoofDark : RoofTin, gable: true, doorX: 0.8f);
            var saved = k.Frame;
            k.Frame = saved * new Transform3D(new Basis(Vector3.Up, s.yaw), s.pos + new Vector3(0, 0.08f, 0));
            k.Container(new Vector3(-s.w * 0.5f + 0.6f, 0.5f, s.d * 0.5f - 0.9f), new Vector3(0.6f, 0.9f, 1.4f), PaleWood, "Cupboard", idx % 2 == 0 ? "kitchen" : "bedroom");
            if (idx % 2 == 1) Bed(k, new Vector3(s.w * 0.5f - 0.8f, 0, s.d * 0.5f - 1.3f), 0, Fabric);
            else k.Container(new Vector3(s.w * 0.5f - 0.6f, 0.9f, s.d * 0.5f - 0.7f), new Vector3(0.7f, 1.7f, 0.7f), White, "Fridge", "fridge");
            k.Frame = saved;
            idx++;
        }
        // general store facing the road
        House(k, new Vector3(-17f, 0, -12f), -Mathf.Pi * 0.5f, 8f, 6f, 3.0f, FadedRed, RoofDark, gable: false, doorX: 0f);
        var sv = k.Frame;
        k.Frame = sv * new Transform3D(new Basis(Vector3.Up, -Mathf.Pi * 0.5f), new Vector3(-17f, 0.08f, -12f));
        Shelf(k, new Vector3(-2.2f, 0, 2.4f), 0, "Store shelf", "shop");
        Shelf(k, new Vector3(0.2f, 0, 2.4f), 0, "Store shelf", "shop");
        k.Container(new Vector3(3.2f, 0.5f, 1.8f), new Vector3(0.7f, 0.9f, 1.4f), Metal, "Store room", "garage");
        k.Text("STORE", new Vector3(0, 3.5f, -3.1f), Mathf.Pi, 110, new Color(0.9f, 0.88f, 0.8f));
        k.Frame = sv;

        WaterPump(k, new Vector3(1f, 0, 6f));
        k.Root.AddChild(MakeSign(name, k.P(new Vector3(8f, 0, -30f)), Mathf.Pi, false));
        Fence(k, new Vector3(-12f, 0, 12f), new Vector3(-12f, 0, 17f), PaleWood);
        Fence(k, new Vector3(15f, 0, 12f), new Vector3(15f, 0, 17f), PaleWood);
        WreckCar(k, new Vector3(8f, 0, -8f), k.Rng.Range(0, Mathf.Tau));
        if (k.Rng.Chance(0.5f)) WreckCar(k, new Vector3(-3f, 0, 10f), k.Rng.Range(0, Mathf.Tau));
        Barrel(k, new Vector3(13f, 0, -3f), Rust);
        k.Container(new Vector3(14f, 0.6f, -14f), new Vector3(1.8f, 1.2f, 1.0f), Olive, "Dumpster", "trash", 0.3f);
    }

    private void BuildWreckSite(Kit k)
    {
        // Something went wrong here. No pad: everything sits on the real shoulder.
        float yaw = k.Rng.Range(-0.5f, 0.5f) + (k.Rng.Chance(0.5f) ? Mathf.Pi * 0.5f : 0f);
        WreckCar(k, k.Ground(0, 0.6f, 0.05f), yaw);
        int variant = k.Rng.Int(3);
        if (variant == 0)
        {
            WreckCar(k, k.Ground(4.6f, 2.6f, 0.05f), yaw + k.Rng.Range(0.6f, 1.4f));
        }
        else if (variant == 1)
        {
            // someone tried to live out of the car
            var g = k.Ground(-3f, 4.5f);
            k.Container(g + new Vector3(0, 0.25f, 0), new Vector3(0.8f, 0.5f, 0.5f), FadedBlue, "Suitcase", "bedroom", 0.4f);
            g = k.Ground(-1.2f, 5.6f);
            k.Box(g + new Vector3(0, 0.05f, 0), new Vector3(0.7f, 0.08f, 1.8f), Fabric, false, 1.1f);
        }
        else
        {
            foreach (float x in new[] { -4.5f, -6.5f })
            {
                var g = k.Ground(x, -1.5f);
                k.Cyl(g + new Vector3(0, 0.35f, 0), 0.2f, 0.7f, Orange, true, 0.04f);
            }
        }
    }
}
