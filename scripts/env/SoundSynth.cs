using System;
using Godot;

namespace DeadMiles;

/// <summary>
/// The project ships no audio files, so every sound is synthesised once at startup:
/// filtered noise for weather and surfaces, harmonic stacks for the engine, short
/// enveloped bursts for footsteps, tools and UI. Deterministic per sound name.
/// </summary>
public static class SoundSynth
{
    public const int Rate = 22050;

    // ---- building blocks -----------------------------------------------------------------

    private static float[] White(int n, uint seed)
    {
        var rng = new Rng(Hash.Mix(seed + 77u));
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = rng.F() * 2f - 1f;
        return a;
    }

    private static void LowPass(float[] a, float hz)
    {
        float k = 1f - MathF.Exp(-Mathf.Tau * hz / Rate);
        float y = 0;
        for (int i = 0; i < a.Length; i++) { y += (a[i] - y) * k; a[i] = y; }
    }

    private static void HighPass(float[] a, float hz)
    {
        float k = 1f - MathF.Exp(-Mathf.Tau * hz / Rate);
        float y = 0;
        for (int i = 0; i < a.Length; i++) { y += (a[i] - y) * k; a[i] -= y; }
    }

    private static void Normalize(float[] a, float peak)
    {
        float m = 0.0001f;
        foreach (float v in a) m = MathF.Max(m, MathF.Abs(v));
        float g = peak / m;
        for (int i = 0; i < a.Length; i++) a[i] *= g;
    }

    /// <summary>Crossfades the tail into the head so a noise bed loops without a click.</summary>
    private static float[] MakeLoop(float[] a, float fadeSeconds)
    {
        int f = (int)(fadeSeconds * Rate);
        int n = a.Length - f;
        var o = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i < f)
            {
                float t = i / (float)f;
                o[i] = a[i] * MathF.Sqrt(t) + a[n + i] * MathF.Sqrt(1f - t);
            }
            else o[i] = a[i];
        }
        return o;
    }

    private static float Env(float t, float attack, float decay)
        => t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);

    public static AudioStreamWav ToStream(float[] samples, bool loop)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32000f);
            bytes[i * 2] = (byte)(s & 0xff);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        var w = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes };
        if (loop)
        {
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopBegin = 0;
            w.LoopEnd = samples.Length;
        }
        return w;
    }

    // ---- ambience beds -------------------------------------------------------------------

    public static AudioStreamWav Wind()
    {
        int n = Rate * 9;
        var a = White(n, 1);
        LowPass(a, 520); LowPass(a, 520); HighPass(a, 70);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            a[i] *= 0.55f + 0.3f * MathF.Sin(t * 0.9f) + 0.15f * MathF.Sin(t * 2.3f + 1f);
        }
        Normalize(a, 0.8f);
        return ToStream(MakeLoop(a, 1.5f), true);
    }

    public static AudioStreamWav Rain(bool heavy)
    {
        int n = Rate * 6;
        var a = White(n, heavy ? 3u : 2u);
        HighPass(a, heavy ? 900 : 2200);
        LowPass(a, heavy ? 7000 : 9000);
        var rng = new Rng(heavy ? 91u : 92u);
        var drops = new float[n];
        int count = heavy ? 2600 : 900;
        for (int d = 0; d < count; d++)
        {
            int at = rng.Int(n - 400);
            float amp = rng.Range(0.15f, 0.6f), f = rng.Range(1500, 5200);
            for (int i = 0; i < 300; i++)
                drops[at + i] += amp * MathF.Sin(Mathf.Tau * f * i / Rate) * MathF.Exp(-i / 40f);
        }
        for (int i = 0; i < n; i++) a[i] = a[i] * (heavy ? 0.75f : 0.5f) + drops[i] * 0.5f;
        Normalize(a, 0.8f);
        return ToStream(MakeLoop(a, 1.0f), true);
    }

    /// <summary>Rain heard from inside the car: a dull roar with drumming on sheet metal.</summary>
    public static AudioStreamWav RoofRain()
    {
        int n = Rate * 6;
        var a = White(n, 4);
        LowPass(a, 1100); LowPass(a, 1100);
        var rng = new Rng(93u);
        for (int d = 0; d < 1500; d++)
        {
            int at = rng.Int(n - 900);
            float amp = rng.Range(0.2f, 0.9f), f = rng.Range(170, 520);
            for (int i = 0; i < 800; i++)
                a[at + i] += amp * 0.35f * MathF.Sin(Mathf.Tau * f * i / Rate) * MathF.Exp(-i / 130f);
        }
        Normalize(a, 0.8f);
        return ToStream(MakeLoop(a, 1.0f), true);
    }

    public static AudioStreamWav Birdsong()
    {
        int n = Rate * 12;
        var a = new float[n];
        var rng = new Rng(94u);
        for (int c = 0; c < 26; c++)
        {
            int at = rng.Int(n - Rate);
            float f0 = rng.Range(2400, 4600), sweep = rng.Range(-900, 1200), amp = rng.Range(0.1f, 0.4f);
            int notes = rng.IntRange(1, 4);
            for (int k = 0; k < notes; k++)
            {
                int len = (int)(rng.Range(0.05f, 0.13f) * Rate);
                float ph = 0;
                for (int i = 0; i < len; i++)
                {
                    float t = i / (float)len;
                    ph += Mathf.Tau * (f0 + sweep * t) / Rate;
                    a[at + i] += amp * MathF.Sin(ph) * MathF.Sin(t * Mathf.Pi);
                }
                at += len + (int)(rng.Range(0.03f, 0.09f) * Rate);
                if (at > n - Rate / 2) break;
            }
        }
        return ToStream(a, true);
    }

    public static AudioStreamWav Crickets()
    {
        int n = Rate * 4;
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float burst = MathF.Max(0, MathF.Sin(Mathf.Tau * 1.5f * t)) * MathF.Max(0, MathF.Sin(Mathf.Tau * 0.25f * t + 0.4f));
            float pulse = 0.5f + 0.5f * MathF.Sin(Mathf.Tau * 31f * t);
            a[i] = 0.3f * burst * pulse * (MathF.Sin(Mathf.Tau * 4300f * t) + 0.5f * MathF.Sin(Mathf.Tau * 4750f * t));
        }
        return ToStream(a, true);
    }

    public static AudioStreamWav Fire()
    {
        int n = Rate * 4;
        var a = White(n, 6);
        LowPass(a, 900);
        var rng = new Rng(95u);
        for (int d = 0; d < 60; d++)
        {
            int at = rng.Int(n - 300);
            float amp = rng.Range(0.3f, 1f);
            for (int i = 0; i < 200; i++) a[at + i] += amp * (rng.F() * 2 - 1) * MathF.Exp(-i / 22f);
        }
        Normalize(a, 0.7f);
        return ToStream(MakeLoop(a, 0.6f), true);
    }

    // ---- vehicle -------------------------------------------------------------------------

    public static AudioStreamWav Engine()
    {
        const float f = 44f;
        int n = Rate * 2;
        var a = new float[n];
        var noise = White(n, 7);
        LowPass(noise, 500);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, s = 0;
            for (int h = 1; h <= 9; h++)
                s += MathF.Sin(Mathf.Tau * f * h * t + h * 1.3f) / MathF.Pow(h, 0.75f);
            s += 0.8f * MathF.Sin(Mathf.Tau * f * 0.5f * t);
            float lope = 0.82f + 0.18f * MathF.Sin(Mathf.Tau * f * 0.25f * t);
            a[i] = MathF.Tanh(s * 0.5f * lope) + noise[i] * 0.5f;
        }
        Normalize(a, 0.75f);
        return ToStream(a, true);
    }

    public static AudioStreamWav Tires(bool gravel)
    {
        int n = Rate * 3;
        var a = White(n, gravel ? 9u : 8u);
        if (gravel)
        {
            HighPass(a, 500); LowPass(a, 5000);
            var grain = White(n, 10);
            LowPass(grain, 45);
            Normalize(grain, 1f);
            for (int i = 0; i < n; i++) a[i] *= 0.35f + 0.65f * MathF.Abs(grain[i]);
        }
        else { LowPass(a, 850); LowPass(a, 850); HighPass(a, 120); }
        Normalize(a, 0.75f);
        return ToStream(MakeLoop(a, 0.6f), true);
    }

    public static AudioStreamWav EngineStart(bool fails)
    {
        int n = (int)(Rate * (fails ? 1.0f : 1.25f));
        var a = new float[n];
        var noise = White(n, 11);
        LowPass(noise, 700);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float crank = 0.5f + 0.5f * MathF.Sin(Mathf.Tau * 9.5f * t);
            float s = (MathF.Sin(Mathf.Tau * 66f * t) + noise[i] * 1.2f) * crank * crank;
            float catchT = fails ? 0f : MathX.Smooth(0.75f, 1.05f, t);
            float run = MathF.Tanh(MathF.Sin(Mathf.Tau * 52f * t) + 0.6f * MathF.Sin(Mathf.Tau * 104f * t) + noise[i]);
            float fade = fails ? 1f - MathX.Smooth(0.7f, 1.0f, t) : 1f - MathX.Smooth(1.1f, 1.25f, t);
            a[i] = (s * (1f - catchT) * 0.7f + run * catchT * 0.8f) * fade * MathX.Smooth(0f, 0.03f, t);
        }
        Normalize(a, 0.8f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Horn()
    {
        int n = (int)(Rate * 0.55f);
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float s = MathF.Sign(MathF.Sin(Mathf.Tau * 415f * t)) + MathF.Sign(MathF.Sin(Mathf.Tau * 523f * t));
            a[i] = s * 0.3f * MathX.Smooth(0f, 0.02f, t) * (1f - MathX.Smooth(0.45f, 0.55f, t));
        }
        LowPass(a, 2600);
        return ToStream(a, false);
    }

    // ---- one-shots -----------------------------------------------------------------------

    public static AudioStreamWav Footstep(int surface, uint variant)
    {
        // 0 grass, 1 gravel / asphalt, 2 hard floor
        int n = (int)(Rate * 0.16f);
        var a = White(n, 20u + variant * 7u + (uint)surface * 31u);
        if (surface == 0) { LowPass(a, 1100 + variant * 150); HighPass(a, 150); }
        else if (surface == 1) { HighPass(a, 700); LowPass(a, 5200); }
        else { LowPass(a, 500); }
        var rng = new Rng(55u + variant);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float e = Env(t, 0.004f, surface == 1 ? 0.035f : 0.028f);
            if (surface == 1) e *= 0.5f + 0.5f * rng.F();
            a[i] *= e;
            if (surface == 2) a[i] += 0.8f * MathF.Sin(Mathf.Tau * (120f - 200f * t) * t) * Env(t, 0.002f, 0.03f);
        }
        Normalize(a, surface == 0 ? 0.45f : 0.6f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Crow(uint variant)
    {
        int caws = 2 + (int)(variant % 2);
        int n = (int)(Rate * (0.38f * caws + 0.1f));
        var a = new float[n];
        var noise = White(n, 30u + variant);
        for (int c = 0; c < caws; c++)
        {
            int at = (int)(c * 0.38f * Rate);
            int len = (int)(0.26f * Rate);
            float ph = 0, baseF = 560f + variant * 40f;
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)len;
                ph += Mathf.Tau * (baseF - 170f * t) / Rate;
                float saw = 2f * (ph / Mathf.Tau - MathF.Floor(ph / Mathf.Tau + 0.5f));
                float rasp = 0.55f + 0.45f * MathF.Sin(Mathf.Tau * 68f * i / Rate);
                a[at + i] += (saw * rasp + noise[at + i] * 0.35f) * MathF.Sin(MathF.Min(1f, t * 1.15f) * Mathf.Pi);
            }
        }
        HighPass(a, 700); LowPass(a, 3200);
        Normalize(a, 0.6f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Thunder(uint variant)
    {
        int n = Rate * 6;
        var a = White(n, 40u + variant);
        LowPass(a, 160); LowPass(a, 220);
        var crack = White(n, 41u + variant);
        HighPass(crack, 600); LowPass(crack, 3000);
        var rng = new Rng(96u + variant);
        float s1 = rng.Range(0.6f, 1.4f), s2 = rng.Range(1.8f, 3.2f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float rumble = MathF.Exp(-t / 2.2f) * (0.6f + 0.4f * MathF.Sin(t * 5.1f + variant))
                           + 0.5f * MathF.Exp(-MathF.Pow((t - s1) / 0.35f, 2f)) + 0.35f * MathF.Exp(-MathF.Pow((t - s2) / 0.5f, 2f));
            a[i] = a[i] * rumble * 6f + crack[i] * MathF.Exp(-t / 0.12f) * 0.6f;
            a[i] *= 1f - MathX.Smooth(5.0f, 6.0f, t);
        }
        Normalize(a, 0.9f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Gunshot()
    {
        int n = (int)(Rate * 0.9f);
        var a = White(n, 50);
        var tail = White(n, 51);
        LowPass(tail, 900);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            a[i] = a[i] * MathF.Exp(-t / 0.035f) + MathF.Sin(Mathf.Tau * (95f - 40f * t) * t) * MathF.Exp(-t / 0.09f) * 0.9f + tail[i] * MathF.Exp(-t / 0.28f) * 1.4f;
        }
        Normalize(a, 0.95f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Burst(uint seed, float seconds, float lowHz, float highHz, float attack, float decay, float peak, float toneHz = 0f, float toneAmp = 0f, float toneDrop = 0f)
    {
        int n = (int)(Rate * seconds);
        var a = White(n, seed);
        if (lowHz > 0) HighPass(a, lowHz);
        if (highHz > 0) LowPass(a, highHz);
        Normalize(a, 1f);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            a[i] *= Env(t, attack, decay);
            if (toneAmp > 0) a[i] += toneAmp * MathF.Sin(Mathf.Tau * (toneHz - toneDrop * t) * t) * Env(t, 0.002f, decay);
        }
        Normalize(a, peak);
        return ToStream(a, false);
    }

    /// <summary>A few repeats of a short enveloped sound: crunches, gulps, ratchet clicks.</summary>
    public static AudioStreamWav Sequence(uint seed, int count, float gap, float lowHz, float highHz, float decay, float peak, float toneHz = 0f, float toneAmp = 0f, float toneRise = 0f)
    {
        int n = (int)(Rate * (count * gap + 0.25f));
        var a = new float[n];
        var noise = White(n, seed);
        if (lowHz > 0) HighPass(noise, lowHz);
        if (highHz > 0) LowPass(noise, highHz);
        Normalize(noise, 1f);
        var rng = new Rng(Hash.Mix(seed));
        for (int c = 0; c < count; c++)
        {
            int at = (int)((c * gap + rng.Range(0f, gap * 0.25f)) * Rate);
            float amp = rng.Range(0.6f, 1f);
            int len = Math.Min(n - at, (int)(decay * 6f * Rate));
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)Rate;
                float e = Env(t, 0.003f, decay) * amp;
                a[at + i] += noise[at + i] * e * (toneAmp > 0 ? 0.4f : 1f);
                if (toneAmp > 0) a[at + i] += toneAmp * e * MathF.Sin(Mathf.Tau * (toneHz + toneRise * t) * t);
            }
        }
        Normalize(a, peak);
        return ToStream(a, false);
    }

    public static AudioStreamWav Pour()
    {
        int n = (int)(Rate * 1.8f);
        var a = White(n, 60);
        LowPass(a, 1400); HighPass(a, 200);
        var rng = new Rng(97u);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            a[i] *= (0.6f + 0.4f * MathF.Sin(Mathf.Tau * 7f * t)) * MathX.Smooth(0f, 0.1f, t) * (1f - MathX.Smooth(1.5f, 1.8f, t));
        }
        for (int d = 0; d < 30; d++)
        {
            int at = rng.Int(n - 700);
            float f = rng.Range(300, 800);
            for (int i = 0; i < 600; i++)
                a[at + i] += 0.25f * MathF.Sin(Mathf.Tau * (f + i * 0.5f) * i / Rate) * MathF.Sin(i / 600f * Mathf.Pi);
        }
        Normalize(a, 0.6f);
        return ToStream(a, false);
    }

    public static AudioStreamWav Blip(float f0, float f1, float seconds, float peak)
    {
        int n = (int)(Rate * seconds);
        var a = new float[n];
        float ph = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            ph += Mathf.Tau * Mathf.Lerp(f0, f1, t) / Rate;
            a[i] = MathF.Sin(ph) * MathF.Sin(t * Mathf.Pi) * peak;
        }
        return ToStream(a, false);
    }
}
