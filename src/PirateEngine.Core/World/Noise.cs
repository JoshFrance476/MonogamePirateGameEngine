using PirateEngine.Core.Util;

namespace PirateEngine.Core.World;

/// <summary>
/// Seeded 2D gradient (Perlin-style) noise with fractal helpers. Output of
/// <see cref="Sample"/> is roughly in [-1, 1]. Thread-safe once constructed.
/// </summary>
public sealed class Noise
{
    private readonly int[] _perm = new int[512];
    private static readonly float[] GradX = { 1, -1, 1, -1, 1, -1, 0, 0, 0.7071f, -0.7071f, 0.7071f, -0.7071f };
    private static readonly float[] GradY = { 0, 0, 1, -1, 0, 0, 1, -1, 0.7071f, 0.7071f, -0.7071f, -0.7071f };

    public Noise(ulong seed)
    {
        var rng = new Rng(seed);
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);

    private float Grad(int hash, float x, float y)
    {
        int h = hash % 12;
        return GradX[h] * x + GradY[h] * y;
    }

    public float Sample(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        xi &= 255; yi &= 255;

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];

        float u = Fade(xf), v = Fade(yf);
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v) * 1.41f;
    }

    /// <summary>Fractal Brownian motion: several octaves summed, normalised to ~[-1, 1].</summary>
    public float Fbm(float x, float y, int octaves, float lacunarity = 2f, float gain = 0.5f)
    {
        float sum = 0, amp = 1, norm = 0, freq = 1;
        for (int i = 0; i < octaves; i++)
        {
            // Offset each octave so lattice artifacts don't line up.
            sum += Sample(x * freq + i * 17.31f, y * freq - i * 9.77f) * amp;
            norm += amp;
            amp *= gain;
            freq *= lacunarity;
        }
        return sum / norm;
    }

    /// <summary>Ridged multifractal — sharp crests, good for mountain chains.</summary>
    public float Ridged(float x, float y, int octaves)
    {
        float sum = 0, amp = 0.5f, freq = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1f - MathF.Abs(Sample(x * freq + i * 31.7f, y * freq + i * 5.3f));
            sum += n * n * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm; // [0, 1]
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
