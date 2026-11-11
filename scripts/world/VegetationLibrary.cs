using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>
/// Loads the tree/bush/grass meshes once and prepares them for MultiMesh use. Missing asset
/// files are replaced by procedural stand-ins so the world still builds, with a single warning each.
/// </summary>
public sealed class VegetationLibrary
{
    public const int PineA = 0, PineB = 1, Birch0 = 2, Maple0 = 5, Dead0 = 8, Bush0 = 10, Count = 12;

    public sealed class VegType
    {
        public Mesh Mesh = null!;
        public Transform3D Local = Transform3D.Identity; // bakes the source node transform and grounds the mesh
        public float TrunkRadius;                        // 0 = no collider
        public bool Deciduous;
        public bool Shadows = true;
    }

    public readonly VegType[] Types = new VegType[Count];
    /// <summary>Plain copies of the local transforms, safe to read from worker threads.</summary>
    public readonly Transform3D[] Locals = new Transform3D[Count];
    public Mesh ImpostorDeciduous = null!;
    public readonly List<VegType> Grass = new();
    public readonly List<VegType> Flowers = new();

    private readonly Shader _foliageShader;
    private readonly ShaderMaterial _solidFoliage;

    public VegetationLibrary()
    {
        _foliageShader = GD.Load<Shader>("res://shaders/foliage.gdshader");
        _solidFoliage = new ShaderMaterial { Shader = _foliageShader };
        _solidFoliage.SetShaderParameter("use_tex", false);
        _solidFoliage.SetShaderParameter("sway", 0.6f);

        Types[PineA] = new VegType { Mesh = BuildConifer(9.5f, 2.3f, 5, 8, 0.2f), TrunkRadius = 0.22f };
        Types[PineB] = new VegType { Mesh = BuildConifer(12.5f, 2.0f, 6, 8, 0.32f), TrunkRadius = 0.25f };
        string[] files =
        {
            "BirchTree_4", "BirchTree_5", "BirchTree_1",
            "MapleTree_4", "MapleTree_1", "MapleTree_5",
            "DeadTree_5", "DeadTree_9",
            "Bush", "Bush_Large",
        };
        for (int i = 0; i < files.Length; i++)
        {
            int idx = Birch0 + i;
            bool bush = idx >= Bush0;
            var tint = bush ? new Color(0.42f, 0.55f, 0.30f) : new Color(0.78f, 0.76f, 0.70f);
            var t = LoadType("res://assets/nature/" + files[i] + ".gltf", bush ? 1.0f : 0.5f, tint);
            if (t == null)
            {
                t = new VegType { Mesh = bush ? BuildBlob(1.1f, 0.2f) : BuildBlob(6f, 2.2f) };
            }
            t.TrunkRadius = bush ? 0f : 0.2f;
            t.Deciduous = !bush && idx < Dead0;
            t.Shadows = true;
            Types[idx] = t;
        }
        for (int i = 0; i < Count; i++) Locals[i] = Types[i].Local;

        ImpostorDeciduous = BuildBlob(6f, 2.2f);

        foreach (var g in new[] { "Grass_Large", "Grass_Small" })
        {
            var t = LoadType("res://assets/nature/" + g + ".gltf", 1.6f, new Color(1.25f, 1.2f, 0.9f));
            if (t != null) { t.Shadows = false; Grass.Add(t); }
        }
        foreach (var f in new[] { "Flower_1_Clump", "Flower_3_Clump", "Flower_4_Clump" })
        {
            var t = LoadType("res://assets/nature/" + f + ".gltf", 1.2f);
            if (t != null) { t.Shadows = false; Flowers.Add(t); }
        }
        Log.Info("Vegetation", $"Loaded {Count} tree types, {Grass.Count} grass, {Flowers.Count} flower meshes");
    }

    private VegType? LoadType(string path, float sway, Color? tint = null)
    {
        if (!ResourceLoader.Exists(path))
        {
            Log.Warn("Vegetation", $"Missing asset {path}, using a placeholder");
            return null;
        }
        var scene = GD.Load<PackedScene>(path);
        if (scene == null) { Log.Warn("Vegetation", $"Could not load {path}"); return null; }
        var root = scene.Instantiate<Node3D>();
        var mi = FindMesh(root);
        if (mi == null || mi.Mesh == null)
        {
            Log.Warn("Vegetation", $"No mesh inside {path}");
            root.Free();
            return null;
        }
        // Accumulate the transform from the scene root down to the mesh node.
        var xf = Transform3D.Identity;
        Node n = mi;
        while (n != root && n is Node3D n3) { xf = n3.Transform * xf; n = n.GetParent(); }
        var mesh = mi.Mesh;
        var aabb = xf * mesh.GetAabb();
        var type = new VegType { Mesh = mesh, Local = new Transform3D(Basis.Identity, new Vector3(0, -aabb.Position.Y - 0.03f, 0)) * xf };

        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not BaseMaterial3D src) continue;
            bool cutout = src.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
            if (!cutout && !path.Contains("Grass")) { src.Roughness = 0.95f; continue; }
            var m = new ShaderMaterial { Shader = _foliageShader };
            m.SetShaderParameter("use_tex", src.AlbedoTexture != null);
            if (src.AlbedoTexture != null) m.SetShaderParameter("albedo_tex", src.AlbedoTexture);
            m.SetShaderParameter("tint", src.AlbedoColor * (tint ?? Colors.White));
            m.SetShaderParameter("sway", sway);
            mesh.SurfaceSetMaterial(s, m);
        }
        root.Free();
        return type;
    }

    private static MeshInstance3D? FindMesh(Node n)
    {
        if (n is MeshInstance3D mi) return mi;
        foreach (var c in n.GetChildren())
        {
            var r = FindMesh(c);
            if (r != null) return r;
        }
        return null;
    }

    // ---- procedural meshes ------------------------------------------------------------

    /// <summary>Stylised pine: tapered trunk and stacked drooping cones. Vertex-coloured.</summary>
    private Mesh BuildConifer(float height, float radius, int tiers, int sides, float bare)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var bark = new Color(0.20f, 0.14f, 0.09f);
        float trunkTop = height * 0.8f;
        AddCone(st, sides > 6 ? 6 : sides, 0f, trunkTop, radius * 0.1f, 0.02f, bark, bark, false);
        float start = height * bare;
        float span = height - start;
        for (int i = 0; i < tiers; i++)
        {
            float f = i / (float)tiers;
            float y0 = start + span * f * 0.92f;
            float y1 = y0 + span / tiers * 1.75f;
            float r = radius * (1f - f * 0.78f);
            float shade = 0.75f + 0.25f * f;
            var low = new Color(0.055f * shade, 0.13f * shade, 0.07f * shade);
            var high = new Color(0.10f * shade, 0.21f * shade, 0.10f * shade);
            AddCone(st, sides, y0, MathF.Min(y1, height), r, 0.0f, low, high, true, i * 0.4f);
        }
        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, _solidFoliage);
        return mesh;
    }

    /// <summary>Rounded low-poly canopy on a trunk, used as far stand-in for deciduous trees.</summary>
    private Mesh BuildBlob(float height, float radius)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var bark = new Color(0.25f, 0.2f, 0.15f);
        var leafLow = new Color(0.10f, 0.19f, 0.06f);
        var leafHigh = new Color(0.20f, 0.30f, 0.09f);
        if (height > 2f) AddCone(st, 5, 0f, height * 0.55f, radius * 0.07f, radius * 0.05f, bark, bark, false);
        float y0 = height > 2f ? height * 0.38f : 0f;
        float mid = Mathf.Lerp(y0, height, 0.45f);
        AddCone(st, 6, y0, mid, radius * 0.55f, radius, leafLow, leafLow.Lerp(leafHigh, 0.5f), false, 0.3f);
        AddCone(st, 6, mid, height, radius, radius * 0.25f, leafLow.Lerp(leafHigh, 0.5f), leafHigh, true, 0.3f);
        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, _solidFoliage);
        return mesh;
    }

    private static void AddCone(SurfaceTool st, int sides, float y0, float y1, float r0, float r1, Color c0, Color c1, bool capTop, float twist = 0f)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = twist + i * Mathf.Tau / sides, a1 = twist + (i + 1) * Mathf.Tau / sides;
            var p00 = new Vector3(MathF.Cos(a0) * r0, y0, MathF.Sin(a0) * r0);
            var p10 = new Vector3(MathF.Cos(a1) * r0, y0, MathF.Sin(a1) * r0);
            var p01 = new Vector3(MathF.Cos(a0) * r1, y1, MathF.Sin(a0) * r1);
            var p11 = new Vector3(MathF.Cos(a1) * r1, y1, MathF.Sin(a1) * r1);
            st.SetColor(c0); st.AddVertex(p00);
            st.SetColor(c1); st.AddVertex(p01);
            st.SetColor(c0); st.AddVertex(p10);
            if (r1 > 0.001f)
            {
                st.SetColor(c0); st.AddVertex(p10);
                st.SetColor(c1); st.AddVertex(p01);
                st.SetColor(c1); st.AddVertex(p11);
            }
            if (capTop && r1 > 0.001f)
            {
                st.SetColor(c1); st.AddVertex(p01);
                st.SetColor(c1); st.AddVertex(new Vector3(0, y1 + r1 * 0.35f, 0));
                st.SetColor(c1); st.AddVertex(p11);
            }
        }
    }
}
