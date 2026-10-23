using PirateEngine.Core.Util;

namespace PirateEngine.Core.World;

public sealed class WorldGenSettings
{
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Seed { get; set; } = 1337;

    /// <summary>Target fraction of the map that is land (0..1). Sea level is chosen to hit it.</summary>
    public float LandFraction { get; set; } = 0.32f;

    /// <summary>Feature size of continents in tiles. Larger = bigger, fewer landmasses.</summary>
    public float ContinentScale { get; set; } = 220f;

    /// <summary>How strongly the map edge is forced to ocean (0 = not at all).</summary>
    public float EdgeFalloff { get; set; } = 1.0f;

    /// <summary>Temperature drop per unit of normalised height above sea level.</summary>
    public float LapseRate { get; set; } = 0.55f;

    /// <summary>World-setup preset (setup.json) for the starting states and towns; null = default.</summary>
    public string? Setup { get; set; }
}

/// <summary>
/// Procedural world generator. Pipeline:
/// elevation (warped fBm + ridges + edge falloff) → sea level by percentile →
/// distance-to-coast field → temperature (latitude, altitude) → moisture (noise, coast) →
/// Whittaker-style biome classification → connected-region labelling → start location.
/// Fully deterministic for a given seed and size.
/// </summary>
public static class WorldGenerator
{
    public static TileMap Generate(WorldGenSettings s, IProgress<(string stage, float progress)>? progress = null)
    {
        var map = new TileMap(s.Width, s.Height) { Seed = s.Seed };
        int w = s.Width, h = s.Height;

        var baseSeed = Rng.Mix((ulong)(uint)s.Seed);
        var elevNoise = new Noise(baseSeed);
        var warpNoise = new Noise(Rng.Mix(baseSeed + 1));
        var ridgeNoise = new Noise(Rng.Mix(baseSeed + 2));
        var tempNoise = new Noise(Rng.Mix(baseSeed + 3));
        var moistNoise = new Noise(Rng.Mix(baseSeed + 4));

        // ---- 1. Elevation ---------------------------------------------------------
        progress?.Report(("Raising continents", 0.0f));
        float inv = 1f / s.ContinentScale;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float fx = x * inv, fy = y * inv;
                // Domain warp gives coastlines a natural, non-blobby shape.
                float wx = warpNoise.Fbm(fx * 1.3f, fy * 1.3f, 3) * 0.6f;
                float wy = warpNoise.Fbm(fx * 1.3f + 41.2f, fy * 1.3f - 13.7f, 3) * 0.6f;
                float e = elevNoise.Fbm(fx + wx, fy + wy, 6) * 0.5f + 0.5f;

                // Mountain chains on the higher ground.
                float ridge = ridgeNoise.Ridged(fx * 2.2f, fy * 2.2f, 4);
                e += ridge * 0.22f * MathUtil.SmoothStep(0.45f, 0.75f, e);

                // Push the borders under water so the map is enclosed by sea.
                if (s.EdgeFalloff > 0)
                {
                    float nx = (float)x / (w - 1) * 2 - 1, ny = (float)y / (h - 1) * 2 - 1;
                    float d = MathF.Max(MathF.Abs(nx), MathF.Abs(ny));
                    e -= MathUtil.SmoothStep(0.78f, 1.0f, d) * 0.6f * s.EdgeFalloff;
                }
                map.Elevation[y * w + x] = e;
            }
        });

        Normalise(map.Elevation);
        map.SeaLevel = Percentile(map.Elevation, 1f - s.LandFraction);
        progress?.Report(("Measuring coastlines", 0.3f));

        // ---- 2. Distance to the nearest water / land (multi-source BFS) --------------
        var distToWater = DistanceField(map, i => map.Elevation[i] < map.SeaLevel);
        var distToLand = DistanceField(map, i => map.Elevation[i] >= map.SeaLevel);

        // ---- 3. Climate -----------------------------------------------------------
        progress?.Report(("Simulating climate", 0.5f));
        float sea = map.SeaLevel;
        Parallel.For(0, h, y =>
        {
            float lat = 1f - MathF.Abs((float)y / (h - 1) * 2f - 1f); // 1 at equator, 0 at poles
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                float e = map.Elevation[i];
                float height = e > sea ? (e - sea) / (1f - sea) : 0f;

                float t = lat * 0.85f + 0.1f + tempNoise.Fbm(x / 160f, y / 160f, 3) * 0.12f;
                t -= height * s.LapseRate;
                map.Temperature[i] = MathUtil.Clamp01(t);

                float coast = MathF.Exp(-distToWater[i] / 22f);
                float m = moistNoise.Fbm(x / 120f, y / 120f, 4) * 0.5f + 0.5f;
                m = m * 0.7f + coast * 0.35f;
                // Hot air holds more moisture; the poles are dry.
                m *= 0.75f + 0.25f * lat;
                map.Moisture[i] = MathUtil.Clamp01(m);
            }
        });

        // ---- 4. Biomes -----------------------------------------------------------
        progress?.Report(("Painting biomes", 0.7f));
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                map.Biome[i] = Classify(map.Elevation[i], sea, map.Temperature[i], map.Moisture[i],
                    distToWater[i], distToLand[i]);
            }
        });

        // ---- 5. Regions & start --------------------------------------------------
        progress?.Report(("Charting the seas", 0.85f));
        LabelRegions(map);
        map.StartTile = ChooseStart(map, distToLand);

        progress?.Report(("Done", 1f));
        return map;
    }

    public static Biome Classify(float e, float sea, float t, float m, int distToWater, int distToLand)
    {
        if (e < sea)
        {
            float depth = (sea - e) / sea;
            if (distToLand <= 2 && depth < 0.12f) return Biome.ShallowWater;
            if (depth < 0.18f) return Biome.Ocean;
            return Biome.DeepOcean;
        }

        float height = (e - sea) / (1f - sea);
        if (height > 0.62f) return t < 0.25f || height > 0.85f ? Biome.Snow : Biome.Mountain;
        if (height > 0.45f) return t < 0.15f ? Biome.Snow : Biome.Hills;
        if (distToWater <= 1 && height < 0.06f && t > 0.25f) return Biome.Beach;

        if (t < 0.12f) return Biome.Snow;
        if (t < 0.26f) return Biome.Tundra;
        if (t < 0.42f) return m > 0.38f ? Biome.Taiga : Biome.Tundra;
        if (t < 0.66f)
        {
            if (m < 0.20f) return Biome.Desert;
            if (m < 0.42f) return Biome.Grassland;
            if (m > 0.78f && height < 0.12f) return Biome.Swamp;
            return Biome.TemperateForest;
        }
        if (m < 0.26f) return Biome.Desert;
        if (m < 0.45f) return Biome.Savanna;
        if (m > 0.80f && height < 0.10f) return Biome.Swamp;
        return Biome.Jungle;
    }

    // ---------------------------------------------------------------------------

    private static void Normalise(float[] a)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in a) { if (v < min) min = v; if (v > max) max = v; }
        float range = max - min;
        if (range < 1e-6f) return;
        for (int i = 0; i < a.Length; i++) a[i] = (a[i] - min) / range;
    }

    /// <summary>Value below which <paramref name="p"/> of the samples fall (histogram, O(n)).</summary>
    private static float Percentile(float[] a, float p)
    {
        const int bins = 4096;
        var hist = new int[bins];
        foreach (var v in a) hist[Math.Clamp((int)(v * (bins - 1)), 0, bins - 1)]++;
        long target = (long)(a.Length * p), acc = 0;
        for (int b = 0; b < bins; b++)
        {
            acc += hist[b];
            if (acc >= target) return (b + 0.5f) / bins;
        }
        return 1f;
    }

    /// <summary>Chebyshev BFS distance (capped at 255) from every tile to the nearest source tile.</summary>
    private static int[] DistanceField(TileMap map, Func<int, bool> isSource)
    {
        int n = map.Count, w = map.Width, h = map.Height;
        var dist = new int[n];
        var queue = new int[n];
        int head = 0, tail = 0;
        for (int i = 0; i < n; i++)
        {
            if (isSource(i)) { dist[i] = 0; queue[tail++] = i; }
            else dist[i] = 255;
        }
        while (head < tail)
        {
            int i = queue[head++];
            int d = dist[i] + 1;
            if (d >= 255) continue;
            int x = i % w, y = i / w;
            for (int k = 0; k < 8; k++)
            {
                var o = GridPoint.Neighbours8[k];
                int nx = x + o.X, ny = y + o.Y;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int j = ny * w + nx;
                if (dist[j] > d) { dist[j] = d; queue[tail++] = j; }
            }
        }
        return dist;
    }

    /// <summary>Flood-fills 4-connected regions of same water/land type.</summary>
    public static void LabelRegions(TileMap map)
    {
        int n = map.Count, w = map.Width, h = map.Height;
        Array.Fill(map.Region, -1);
        var sizes = new List<int>();
        var isWater = new List<bool>();
        var stack = new Stack<int>();

        for (int start = 0; start < n; start++)
        {
            if (map.Region[start] != -1) continue;
            int id = sizes.Count;
            bool water = Biomes.IsWater(map.Biome[start]);
            int size = 0;
            map.Region[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                size++;
                int x = i % w, y = i / w;
                for (int k = 0; k < 4; k++)
                {
                    var o = GridPoint.Neighbours8[k];
                    int nx = x + o.X, ny = y + o.Y;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    int j = ny * w + nx;
                    if (map.Region[j] != -1 || Biomes.IsWater(map.Biome[j]) != water) continue;
                    map.Region[j] = id;
                    stack.Push(j);
                }
            }
            sizes.Add(size);
            isWater.Add(water);
        }

        map.RegionSizes = sizes.ToArray();
        map.RegionIsWater = isWater.ToArray();
        int best = -1;
        for (int r = 0; r < sizes.Count; r++)
            if (isWater[r] && (best < 0 || sizes[r] > sizes[best])) best = r;
        map.MainOceanRegion = best;
    }

    /// <summary>
    /// Picks a water tile in the main ocean, adjacent to a decent-sized, buildable, temperate
    /// landmass, as close to the map centre as possible.
    /// </summary>
    private static GridPoint ChooseStart(TileMap map, int[] distToLand)
    {
        var centre = new GridPoint(map.Width / 2, map.Height / 2);
        int maxR = Math.Max(map.Width, map.Height);
        var best = map.FindNearest(centre, maxR, p =>
        {
            int i = map.Index(p);
            if (map.Region[i] != map.MainOceanRegion || distToLand[i] != 1) return false;
            // Look for an adjacent land tile whose landmass is big and hospitable.
            for (int k = 0; k < 4; k++)
            {
                var q = p + GridPoint.Neighbours8[k];
                if (!map.IsLand(q)) continue;
                int r = map.Region[map.Index(q)];
                if (map.RegionSizes[r] < 400) continue;
                float t = map.Temperature[map.Index(q)];
                if (t > 0.45f && t < 0.85f) return true;
            }
            return false;
        });
        if (best.IsValid) return best;
        // Fallback: any main-ocean tile.
        return map.FindNearest(centre, maxR, p => map.Region[map.Index(p)] == map.MainOceanRegion);
    }
}
