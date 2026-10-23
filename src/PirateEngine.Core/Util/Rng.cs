namespace PirateEngine.Core.Util;

/// <summary>
/// Small, fast, deterministic PRNG (xorshift64*). Unlike System.Random its output
/// is guaranteed stable across .NET versions, which matters because worlds are
/// regenerated from their seed when a save is loaded.
/// </summary>
public sealed class Rng
{
    private ulong _state;

    public Rng(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        // Warm up so nearby seeds diverge quickly.
        for (int i = 0; i < 4; i++) NextULong();
    }

    public Rng(int seed) : this(Mix((ulong)(uint)seed)) { }

    public ulong State { get => _state; set => _state = value == 0 ? 1 : value; }

    public ulong NextULong()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform int in [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        return (int)(NextULong() % (ulong)maxExclusive);
    }

    /// <summary>Uniform int in [min, maxExclusive).</summary>
    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    /// <summary>Uniform float in [0, 1).</summary>
    public float NextFloat() => (NextULong() >> 40) * (1.0f / (1UL << 24));

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    public bool Chance(float p) => NextFloat() < p;

    /// <summary>SplitMix64 finaliser — turns sequential ints into well-spread seeds.</summary>
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Deterministic hash of a 2D integer coordinate, for per-tile variation.</summary>
    public static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 374761393u + (uint)x * 668265263u + (uint)y * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
