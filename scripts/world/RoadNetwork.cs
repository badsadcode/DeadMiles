using System;
using Godot;

namespace DeadMiles;

/// <summary>Cross-section of the main road at a given world Z.</summary>
public struct RoadSample
{
    public float X;       // centre line X
    public float H;       // road surface height
    public float Slope;   // dX/dZ of the centre line
    public float InvLen;  // 1/sqrt(1+Slope^2): converts X offsets into true lateral distance
}

/// <summary>
/// The main route: an endless, connected road described as X = f(Z) so that it is deterministic,
/// cheap to query from any thread and can never produce disconnected pieces.
/// Its height follows a low-pass filtered version of the terrain to keep grades gentle.
/// Branch tracks to points of interest are owned by <see cref="PoiPlacer"/>.
/// </summary>
public sealed class RoadNetwork
{
    public const float HalfWidth = 3.6f;        // asphalt half width
    public const float ShoulderHalfWidth = 5.6f;

    private readonly WorldGenerator _world;
    private readonly Noise2 _n;

    public RoadNetwork(WorldGenerator world, int seed)
    {
        _world = world;
        _n = new Noise2(seed * 31 + 7);
    }

    public float CenterX(float z)
    {
        // Ease the meander in around the spawn so the first kilometre is easy to read.
        return _n.Get(z / 760f, 11.3f) * 240f + _n.Get(z / 230f, 47.7f) * 36f;
    }

    public float Height(float z)
    {
        float sum = 0;
        for (int i = -2; i <= 2; i++)
        {
            float zz = z + i * 32f;
            sum += _world.MacroHeight(CenterX(zz), zz);
        }
        float h = sum / 5f;
        // Smooth-max against the water line so the road becomes a causeway rather than a ford.
        float floor = WorldGenerator.WaterLevel + 1.7f;
        const float k = 2.5f;
        float d = h - floor;
        return floor + 0.5f * (d + MathF.Sqrt(d * d + k * k)) - 0.0f;
    }

    public RoadSample Sample(float z)
    {
        float x = CenterX(z);
        float slope = (CenterX(z + 1.5f) - CenterX(z - 1.5f)) / 3f;
        return new RoadSample { X = x, H = Height(z), Slope = slope, InvLen = 1f / MathF.Sqrt(1f + slope * slope) };
    }

    /// <summary>World position on the road at Z with a lateral offset (positive = +X side).</summary>
    public Vector3 PointAt(float z, float lateral = 0f, float lift = 0f)
    {
        var s = Sample(z);
        return new Vector3(s.X + lateral / s.InvLen, s.H + lift, z);
    }

    /// <summary>Unit direction of travel towards +Z.</summary>
    public Vector3 DirectionAt(float z)
    {
        var s = Sample(z);
        float grade = (Height(z + 2f) - Height(z - 2f)) / 4f;
        return new Vector3(s.Slope, grade, 1f).Normalized();
    }

    /// <summary>Yaw (radians) for an object whose -Z should face along +Z travel.</summary>
    public float YawAt(float z)
    {
        var s = Sample(z);
        return MathF.Atan2(-s.Slope, -1f);
    }

    /// <summary>Signed lateral distance from the centre line (approximate, good near the road).</summary>
    public float LateralDistance(float x, float z)
    {
        var s = Sample(z);
        return (x - s.X) * s.InvLen;
    }
}
