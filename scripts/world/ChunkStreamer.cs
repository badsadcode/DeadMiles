using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace DeadMiles;

/// <summary>
/// Keeps the world loaded around a focus point: terrain chunks (three levels of detail, built on
/// worker threads), the road ribbon, roadside furniture, near-field grass and points of interest.
/// Everything is rebuilt from the seed, so unloading is just freeing nodes.
/// </summary>
public partial class ChunkStreamer : Node3D
{
    private sealed class Chunk
    {
        public Node3D? Node;
        public int Lod = -1;
        public int PendingLod = -1;
    }

    private const float Size = ChunkBuilder.ChunkSize;
    private const float PieceLength = 48f;
    private const float GrassCell = 20f;

    public Node3D? Focus;
    public int ViewRadius = 6;
    public float Density = 1f;
    public bool GrassEnabled = true;
    /// <summary>Raised after a POI has been built and added to the tree.</summary>
    public event Action<PoiDesc, Node3D>? PoiSpawned;
    public event Action<PoiDesc>? PoiRemoved;

    private WorldGenerator _world = null!;
    private VegetationLibrary _veg = null!;
    private PoiBuilder _poiBuilder = null!;
    private ShaderMaterial _terrainMat = null!, _roadMat = null!, _waterMat = null!;
    private StandardMaterial3D _poleMat = null!, _wireMat = null!;

    private readonly Dictionary<Vector2I, Chunk> _chunks = new();
    private readonly ConcurrentQueue<ChunkBuild> _done = new();
    private int _running;
    private readonly Dictionary<int, Node3D> _pieces = new();
    private readonly Dictionary<string, (PoiDesc desc, Node3D node)> _pois = new();
    private readonly Dictionary<Vector2I, Node3D> _grass = new();
    private Node3D _chunkRoot = null!, _roadRoot = null!, _poiRoot = null!, _grassRoot = null!;
    private double _slowTimer;
    private readonly List<(float dist, Vector2I c, int lod)> _wanted = new();

    public void Setup(WorldGenerator world, VegetationLibrary veg, PoiBuilder poiBuilder)
    {
        _world = world;
        _veg = veg;
        _poiBuilder = poiBuilder;
        _terrainMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _terrainMat.SetShaderParameter("water_level", WorldGenerator.WaterLevel);
        _roadMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/road.gdshader") };
        _roadMat.SetShaderParameter("half_width", RoadNetwork.HalfWidth);
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _poleMat = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.12f, 0.09f), Roughness = 0.95f };
        _wireMat = new StandardMaterial3D { AlbedoColor = new Color(0.03f, 0.03f, 0.03f), Roughness = 0.7f };
        AddChild(_chunkRoot = new Node3D { Name = "Chunks" });
        AddChild(_roadRoot = new Node3D { Name = "Road" });
        AddChild(_poiRoot = new Node3D { Name = "Pois" });
        AddChild(_grassRoot = new Node3D { Name = "Grass" });
    }

    public IEnumerable<(PoiDesc desc, Node3D node)> ActivePois => _pois.Values;

    /// <summary>True once the ground under this position has collision.</summary>
    public bool IsSolidAt(Vector3 pos)
    {
        var c = new Vector2I(Mathf.FloorToInt(pos.X / Size), Mathf.FloorToInt(pos.Z / Size));
        return _chunks.TryGetValue(c, out var ch) && ch.Lod == 0;
    }

    public int LoadedChunks => _chunks.Count;
    public int PendingJobs => _running;

    public override void _Process(double delta)
    {
        if (Focus == null || !IsInstanceValid(Focus)) return;
        var fp = Focus.GlobalPosition;
        UpdateChunks(fp);
        ApplyFinished(fp);
        UpdateRoad(fp);
        if (GrassEnabled) UpdateGrass(fp);
        _slowTimer -= delta;
        if (_slowTimer <= 0)
        {
            _slowTimer = 0.5;
            UpdatePois(fp);
        }
    }

    // ---- terrain chunks ----------------------------------------------------------------

    private static int LodForRing(int ring) => ring <= 1 ? 0 : ring <= 3 ? 1 : 2;

    private void UpdateChunks(Vector3 fp)
    {
        var fc = new Vector2I(Mathf.FloorToInt(fp.X / Size), Mathf.FloorToInt(fp.Z / Size));
        _wanted.Clear();
        for (int dz = -ViewRadius; dz <= ViewRadius; dz++)
        {
            for (int dx = -ViewRadius; dx <= ViewRadius; dx++)
            {
                var c = new Vector2I(fc.X + dx, fc.Y + dz);
                int lod = LodForRing(Math.Max(Math.Abs(dx), Math.Abs(dz)));
                if (!_chunks.TryGetValue(c, out var ch)) { ch = new Chunk(); _chunks[c] = ch; }
                if (ch.Lod == lod || ch.PendingLod == lod) continue;
                var centre = new Vector2((c.X + 0.5f) * Size, (c.Y + 0.5f) * Size);
                _wanted.Add((centre.DistanceSquaredTo(new Vector2(fp.X, fp.Z)), c, lod));
            }
        }
        if (_wanted.Count > 0 && _running < 4)
        {
            _wanted.Sort((a, b) => a.dist.CompareTo(b.dist));
            foreach (var (_, c, lod) in _wanted)
            {
                if (_running >= 4) break;
                _chunks[c].PendingLod = lod;
                _running++;
                var world = _world; var locals = _veg.Locals; float density = Density;
                Task.Run(() =>
                {
                    try { _done.Enqueue(ChunkBuilder.Build(world, locals, c, lod, density)); }
                    catch (Exception e)
                    {
                        GD.PushError($"[ChunkStreamer] Chunk {c} failed: {e}");
                        _done.Enqueue(new ChunkBuild { Coord = c, Lod = -1 });
                    }
                });
            }
        }

        // Unload with one chunk of hysteresis so standing on a border does not thrash.
        List<Vector2I>? remove = null;
        foreach (var (c, ch) in _chunks)
        {
            if (Math.Max(Math.Abs(c.X - fc.X), Math.Abs(c.Y - fc.Y)) > ViewRadius + 1 && ch.PendingLod < 0)
                (remove ??= new List<Vector2I>()).Add(c);
        }
        if (remove != null)
        {
            foreach (var c in remove)
            {
                _chunks[c].Node?.QueueFree();
                _chunks.Remove(c);
            }
        }
    }

    private void ApplyFinished(Vector3 fp)
    {
        int budget = 3;
        while (budget-- > 0 && _done.TryDequeue(out var b))
        {
            _running--;
            if (!_chunks.TryGetValue(b.Coord, out var ch)) continue;
            if (b.Lod < 0) { ch.PendingLod = -1; continue; }
            if (ch.PendingLod != b.Lod) continue; // superseded
            ch.PendingLod = -1;
            ch.Node?.QueueFree();
            ch.Node = BuildChunkNode(b);
            ch.Lod = b.Lod;
            _chunkRoot.AddChild(ch.Node);
        }
    }

    private Node3D BuildChunkNode(ChunkBuild b)
    {
        var node = new Node3D { Position = new Vector3(b.Coord.X * Size, 0, b.Coord.Y * Size) };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.Verts;
        arrays[(int)Mesh.ArrayType.Normal] = b.Normals;
        arrays[(int)Mesh.ArrayType.Color] = b.Colors;
        arrays[(int)Mesh.ArrayType.Index] = b.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _terrainMat);
        node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        if (b.CollisionFaces != null)
        {
            var body = new StaticBody3D { CollisionLayer = 1, CollisionMask = 0 };
            var shape = new ConcavePolygonShape3D();
            shape.SetFaces(b.CollisionFaces);
            body.AddChild(new CollisionShape3D { Shape = shape });
            if (b.Trunks != null)
            {
                foreach (var t in b.Trunks)
                {
                    body.AddChild(new CollisionShape3D
                    {
                        Shape = new CylinderShape3D { Radius = t.W, Height = 5f },
                        Position = new Vector3(t.X, t.Y + 2.3f, t.Z),
                    });
                }
            }
            body.SetMeta("surface", "ground");
            node.AddChild(body);
        }

        for (int t = 0; t < VegetationLibrary.Count; t++)
        {
            if (b.VegBuffers[t] == null) continue;
            var type = _veg.Types[t];
            node.AddChild(MakeMultiMesh(type.Mesh, b.VegBuffers[t]!, b.VegCounts[t], type.Shadows && b.Lod < 2));
        }
        if (b.ImpostorBuffer != null)
            node.AddChild(MakeMultiMesh(_veg.ImpostorDeciduous, b.ImpostorBuffer, b.ImpostorCount, false));

        if (b.HasWater)
        {
            var water = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(Size, Size) },
                Position = new Vector3(Size * 0.5f, WorldGenerator.WaterLevel, Size * 0.5f),
                MaterialOverride = _waterMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(water);
        }
        return node;
    }

    private static MultiMeshInstance3D MakeMultiMesh(Mesh mesh, float[] buffer, int count, bool shadows)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = count,
        };
        mm.Buffer = buffer;
        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // ---- road ribbon and roadside furniture --------------------------------------------

    private void UpdateRoad(Vector3 fp)
    {
        float range = ViewRadius * Size;
        int k0 = Mathf.FloorToInt((fp.Z - range) / PieceLength), k1 = Mathf.FloorToInt((fp.Z + range) / PieceLength);
        int fk = Mathf.FloorToInt(fp.Z / PieceLength);
        int built = 0;
        // nearest first, so the road is never missing underfoot
        for (int o = 0; o <= k1 - k0 && built < 2; o++)
        {
            foreach (int k in new[] { fk + o, fk - o })
            {
                if (k < k0 || k > k1 || _pieces.ContainsKey(k)) continue;
                var piece = BuildRoadPiece(k);
                _pieces[k] = piece;
                _roadRoot.AddChild(piece);
                built++;
            }
        }
        List<int>? remove = null;
        foreach (var k in _pieces.Keys)
            if (k < k0 - 2 || k > k1 + 2) (remove ??= new List<int>()).Add(k);
        if (remove != null)
            foreach (int k in remove) { _pieces[k].QueueFree(); _pieces.Remove(k); }
    }

    private static readonly float[] RibbonU = { -RoadNetwork.ShoulderHalfWidth, -3.75f, 3.75f, RoadNetwork.ShoulderHalfWidth };
    private static readonly float[] RibbonY = { -0.55f, 0f, 0f, -0.55f };

    private Node3D BuildRoadPiece(int k)
    {
        float zStart = k * PieceLength;
        var origin = _world.Road.PointAt(zStart);
        var node = new Node3D { Position = origin };
        const int steps = 12;
        float dz = PieceLength / steps;
        var verts = new Vector3[(steps + 1) * 4];
        var normals = new Vector3[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (int j = 0; j <= steps; j++)
        {
            float z = zStart + j * dz;
            var s = _world.Road.Sample(z);
            float grade = (_world.Road.Height(z + 2f) - _world.Road.Height(z - 2f)) / 4f;
            var nrm = new Vector3(0, 1, -grade).Normalized();
            for (int i = 0; i < 4; i++)
            {
                int v = j * 4 + i;
                verts[v] = new Vector3(s.X + RibbonU[i] / s.InvLen, s.H + RibbonY[i], z) - origin;
                normals[v] = i == 0 ? (nrm + new Vector3(-0.28f, 0, 0)).Normalized() : i == 3 ? (nrm + new Vector3(0.28f, 0, 0)).Normalized() : nrm;
                uvs[v] = new Vector2(RibbonU[i], z);
            }
        }
        var idx = new int[steps * 3 * 6];
        int n = 0;
        for (int j = 0; j < steps; j++)
        {
            for (int i = 0; i < 3; i++)
            {
                int v00 = j * 4 + i, v10 = v00 + 1, v01 = v00 + 4, v11 = v01 + 1;
                idx[n++] = v00; idx[n++] = v10; idx[n++] = v01;
                idx[n++] = v10; idx[n++] = v11; idx[n++] = v01;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _roadMat);
        node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        var faces = new Vector3[idx.Length];
        for (int i = 0; i < idx.Length; i++) faces[i] = verts[idx[i]];
        var body = new StaticBody3D { CollisionLayer = 1, CollisionMask = 0 };
        var shape = new ConcavePolygonShape3D();
        shape.SetFaces(faces);
        body.AddChild(new CollisionShape3D { Shape = shape });
        body.SetMeta("surface", "asphalt");
        node.AddChild(body);

        AddPole(node, body, k, origin);
        AddSigns(node, body, zStart, origin);
        return node;
    }

    private bool PoleSpot(int k, out Vector3 pos, out float lean)
    {
        float z = k * PieceLength + 6f;
        var rng = new Rng(Hash.Of(_world.Seed, k, 313));
        lean = rng.Chance(0.07f) ? rng.Range(0.12f, 0.3f) : rng.Range(-0.015f, 0.015f);
        var rs = _world.Road.Sample(z);
        float x = rs.X + 8.2f / rs.InvLen;
        pos = new Vector3(x, 0, z);
        if (!_world.IsWild(x, z, rs, _world.Pois.Near(z), 0.5f)) return false;
        float h = _world.Height(x, z);
        if (h < WorldGenerator.WaterLevel + 0.3f) return false;
        pos.Y = h;
        return true;
    }

    private void AddPole(Node3D node, StaticBody3D body, int k, Vector3 origin)
    {
        if (!PoleSpot(k, out var pos, out float lean)) return;
        const float height = 8.5f;
        var pole = new Node3D { Position = pos - origin, Rotation = new Vector3(lean, 0, lean * 0.5f) };
        pole.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.13f, Height = height, RadialSegments = 6, Rings = 1 },
            Position = new Vector3(0, height * 0.5f - 0.3f, 0), MaterialOverride = _poleMat,
        });
        pole.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.9f, 0.1f, 0.1f) },
            Position = new Vector3(0, height - 1.0f, 0), MaterialOverride = _poleMat,
        });
        node.AddChild(pole);
        body.AddChild(new CollisionShape3D
        {
            Shape = new CylinderShape3D { Radius = 0.14f, Height = height },
            Position = pos - origin + new Vector3(0, height * 0.5f, 0),
        });

        // Wires to the next pole, as two sagging spans per side.
        if (MathF.Abs(lean) > 0.05f || !PoleSpot(k + 1, out var next, out float nextLean) || MathF.Abs(nextLean) > 0.05f) return;
        foreach (float side in new[] { -0.8f, 0.8f })
        {
            var a = pos + new Vector3(side, height - 1.25f, 0);
            var b = next + new Vector3(side, height - 1.25f, 0);
            var mid = (a + b) * 0.5f - new Vector3(0, 0.75f, 0);
            AddWire(node, a - origin, mid - origin);
            AddWire(node, mid - origin, b - origin);
        }
    }

    private void AddWire(Node3D node, Vector3 a, Vector3 b)
    {
        float len = a.DistanceTo(b);
        var wire = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.025f, len) },
            MaterialOverride = _wireMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        node.AddChild(wire);
        wire.Position = (a + b) * 0.5f;
        wire.Basis = Basis.LookingAt((b - a).Normalized(), Vector3.Up);
    }

    private void AddSigns(Node3D node, StaticBody3D body, float zStart, Vector3 origin)
    {
        foreach (var p in _world.Pois.InRange(zStart - 260f, zStart + PieceLength + 260f))
        {
            string? text = p.Kind switch
            {
                PoiKind.GasStation => "FUEL",
                PoiKind.Diner => "DINER",
                PoiKind.Workshop => "GARAGE",
                PoiKind.Campsite => "CAMPING",
                PoiKind.Farmhouse => "FARM",
                PoiKind.Checkpoint => "CHECKPOINT",
                PoiKind.Settlement => PoiBuilder.TownName(p.Seed),
                _ => null,
            };
            if (text == null) continue;
            // One sign for each direction of travel, on the driver's right, 120 m before the turn.
            foreach (int dir in new[] { 1, -1 })
            {
                float z = p.AttachZ - dir * 120f;
                if (z < zStart || z >= zStart + PieceLength) continue;
                var rs = _world.Road.Sample(z);
                float lateral = -dir * 6.9f; // right-hand side when driving towards +Z is -X
                float x = rs.X + lateral / rs.InvLen;
                var pos = new Vector3(x, _world.Height(x, z), z) - origin;
                bool turnsRight = p.Side * dir < 0;
                string arrow = p.Radius > 0 && p.Kind != PoiKind.Checkpoint ? (turnsRight ? "  >" : "<  ") : "";
                string label = turnsRight ? text + arrow : arrow + text;
                // The sign face is local +Z, so it must look back at the approaching driver.
                float yaw = _world.Road.YawAt(z) + (dir > 0 ? 0f : Mathf.Pi);
                node.AddChild(PoiBuilder.MakeSign(label, pos, yaw, p.Kind == PoiKind.Checkpoint));
                body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.06f, Height = 2.6f }, Position = pos + new Vector3(0, 1.3f, 0) });
            }
        }
    }

    // ---- points of interest ------------------------------------------------------------

    private void UpdatePois(Vector3 fp)
    {
        const float spawn = 470f, despawn = 560f;
        foreach (var p in _world.Pois.InRange(fp.Z - spawn, fp.Z + spawn))
        {
            if (_pois.ContainsKey(p.Id)) continue;
            Node3D node;
            try { node = _poiBuilder.Build(p); }
            catch (Exception e)
            {
                Log.Error("ChunkStreamer", $"Building {p.Kind} {p.Id} failed: {e.Message}");
                node = new Node3D();
            }
            _poiRoot.AddChild(node);
            _pois[p.Id] = (p, node);
            PoiSpawned?.Invoke(p, node);
        }
        List<string>? remove = null;
        foreach (var (id, e) in _pois)
            if (MathF.Abs(e.desc.Center.Z - fp.Z) > despawn) (remove ??= new List<string>()).Add(id);
        if (remove != null)
        {
            foreach (var id in remove)
            {
                PoiRemoved?.Invoke(_pois[id].desc);
                _pois[id].node.QueueFree();
                _pois.Remove(id);
            }
        }
    }

    // ---- near-field grass --------------------------------------------------------------

    private void UpdateGrass(Vector3 fp)
    {
        if (_veg.Grass.Count == 0) return;
        const int radius = 3;
        var fc = new Vector2I(Mathf.FloorToInt(fp.X / GrassCell), Mathf.FloorToInt(fp.Z / GrassCell));
        int built = 0;
        for (int ring = 0; ring <= radius && built < 2; ring++)
        {
            for (int dz = -ring; dz <= ring && built < 2; dz++)
            {
                for (int dx = -ring; dx <= ring && built < 2; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    var c = new Vector2I(fc.X + dx, fc.Y + dz);
                    if (_grass.ContainsKey(c)) continue;
                    var cell = BuildGrassCell(c);
                    _grass[c] = cell;
                    _grassRoot.AddChild(cell);
                    built++;
                }
            }
        }
        List<Vector2I>? remove = null;
        foreach (var c in _grass.Keys)
            if (Math.Max(Math.Abs(c.X - fc.X), Math.Abs(c.Y - fc.Y)) > radius + 1) (remove ??= new List<Vector2I>()).Add(c);
        if (remove != null)
            foreach (var c in remove) { _grass[c].QueueFree(); _grass.Remove(c); }
    }

    private Node3D BuildGrassCell(Vector2I c)
    {
        float x0 = c.X * GrassCell, z0 = c.Y * GrassCell;
        var node = new Node3D { Position = new Vector3(x0, 0, z0) };
        var grassA = new List<float>(); var grassB = new List<float>(); var flowers = new List<float>();
        const float spacing = 0.95f;
        int count = (int)(GrassCell / spacing);
        var rng = new Rng(Hash.Of(_world.Seed, c.X, c.Y, 77));
        int flowerType = rng.Int(Math.Max(1, _veg.Flowers.Count));
        for (int j = 0; j < count; j++)
        {
            for (int i = 0; i < count; i++)
            {
                float x = x0 + (i + rng.F()) * spacing, z = z0 + (j + rng.F()) * spacing;
                float roll = rng.F(), yaw = rng.Range(0, Mathf.Tau), sc = rng.Range(0.55f, 1.15f), shade = rng.Range(0.75f, 1.05f);
                var rs = _world.Road.Sample(z);
                var pois = _world.Pois.Near(z);
                if (!_world.IsWild(x, z, rs, pois, 0.3f)) continue;
                _world.Biome(x, z, out float forest, out float field);
                float p = (1f - forest * 0.7f) * (1f - field) * Density;
                if (roll > p) continue;
                float h = _world.Compose(x, z, rs, pois, 0f, out float dirt);
                if (dirt > 0.3f || h < WorldGenerator.WaterLevel + 0.35f) continue;
                var local = new Vector3(x - x0, h, z - z0);
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(sc, sc * rng.Range(0.8f, 1.4f), sc));
                var col = new Color(shade * 0.62f, shade * 0.72f, shade * 0.42f);
                if (_veg.Flowers.Count > 0 && roll < 0.018f && forest < 0.5f)
                    ChunkBuilder.Write(flowers, new Transform3D(basis, local) * _veg.Flowers[flowerType].Local, new Color(1, 1, 1));
                else if (_veg.Grass.Count > 1 && roll > p * 0.55f)
                    ChunkBuilder.Write(grassB, new Transform3D(basis, local) * _veg.Grass[1].Local, col);
                else
                    ChunkBuilder.Write(grassA, new Transform3D(basis, local) * _veg.Grass[0].Local, col);
            }
        }
        void Add(List<float> buf, Mesh mesh)
        {
            if (buf.Count == 0) return;
            var mmi = MakeMultiMesh(mesh, buf.ToArray(), buf.Count / 16, false);
            mmi.VisibilityRangeEnd = 62f;
            mmi.VisibilityRangeEndMargin = 12f;
            mmi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;
            node.AddChild(mmi);
        }
        Add(grassA, _veg.Grass[0].Mesh);
        if (_veg.Grass.Count > 1) Add(grassB, _veg.Grass[1].Mesh);
        if (_veg.Flowers.Count > 0) Add(flowers, _veg.Flowers[flowerType].Mesh);
        return node;
    }

    public void ClearGrass()
    {
        foreach (var n in _grass.Values) n.QueueFree();
        _grass.Clear();
    }
}
