using System;
using System.Collections.Generic;
using Godot;

namespace DeadMiles;

/// <summary>Stateless integer hashing used for all deterministic, seed-based decisions.</summary>
public static class Hash
{
    public static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    public static uint Of(int a, int b = 0, int c = 0, int d = 0)
    {
        uint h = Mix((uint)a + 0x9e3779b9U);
        h = Mix(h ^ ((uint)b * 0x85ebca6bU + 0x01234567U));
        h = Mix(h ^ ((uint)c * 0xc2b2ae35U + 0x89abcdefU));
        h = Mix(h ^ ((uint)d * 0x27d4eb2fU + 0x3c6ef372U));
        return h;
    }

    public static uint OfString(string s)
    {
        uint h = 2166136261U;
        foreach (char ch in s) { h ^= ch; h *= 16777619U; }
        return Mix(h);
    }

    public static float F01(uint h) => (h >> 8) * (1f / 16777216f);
}

/// <summary>Small deterministic RNG (xorshift32). Always seed it from <see cref="Hash"/>.</summary>
public struct Rng
{
    private uint _s;
    public Rng(uint seed) { _s = seed == 0 ? 0x1234abcdU : seed; }
    public uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
    public float F() => (NextU() >> 8) * (1f / 16777216f);
    public float Range(float a, float b) => a + (b - a) * F();
    public int Int(int n) => n <= 0 ? 0 : (int)(NextU() % (uint)n);
    public int IntRange(int a, int bInclusive) => a + Int(bInclusive - a + 1);
    public bool Chance(float p) => F() < p;
    public T Pick<T>(IReadOnlyList<T> list) => list[Int(list.Count)];
}

/// <summary>Seeded 2D gradient noise, pure C# so it is thread-safe and engine-version independent.</summary>
public sealed class Noise2
{
    private readonly uint _seed;
    public Noise2(int seed) { _seed = Hash.Mix((uint)seed * 747796405U + 2891336453U); }

    private float Grad(int ix, int iy, float dx, float dy)
    {
        uint h = Hash.Mix((uint)ix * 0x1f1f1f1fU ^ Hash.Mix((uint)iy + _seed));
        float a = (h & 0xffff) * (Mathf.Tau / 65536f);
        return MathF.Cos(a) * dx + MathF.Sin(a) * dy;
    }

    /// <summary>Returns roughly -1..1.</summary>
    public float Get(float x, float y)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float u = fx * fx * fx * (fx * (fx * 6 - 15) + 10);
        float v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float n00 = Grad(ix, iy, fx, fy);
        float n10 = Grad(ix + 1, iy, fx - 1, fy);
        float n01 = Grad(ix, iy + 1, fx, fy - 1);
        float n11 = Grad(ix + 1, iy + 1, fx - 1, fy - 1);
        float nx0 = n00 + (n10 - n00) * u;
        float nx1 = n01 + (n11 - n01) * u;
        return (nx0 + (nx1 - nx0) * v) * 1.41f;
    }

    public float Fbm(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Get(x, y) * amp;
            norm += amp;
            amp *= gain;
            x = x * lacunarity + 17.3f;
            y = y * lacunarity - 9.1f;
        }
        return sum / norm;
    }
}

public static class MathX
{
    public static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Approach(float cur, float target, float maxDelta)
        => cur < target ? MathF.Min(cur + maxDelta, target) : MathF.Max(cur - maxDelta, target);

    /// <summary>Frame-rate independent exponential smoothing factor.</summary>
    public static float Damp(float rate, float dt) => 1f - MathF.Exp(-rate * dt);
}

public static class Log
{
    public static void Info(string system, string msg) => GD.Print($"[{system}] {msg}");
    public static void Warn(string system, string msg) => GD.PushWarning($"[{system}] {msg}");
    public static void Error(string system, string msg) => GD.PushError($"[{system}] {msg}");
}
