using PirateEngine.Core.Data;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Tests;

public static class Fixtures
{
    public static readonly Lazy<DefinitionDatabase> Defs = new(() => DefinitionDatabase.Load());

    public static readonly WorldGenSettings BigSettings = new() { Width = 1024, Height = 1024, Seed = 1337 };
    /// <summary>One full-size generated world shared by the read-only tests.</summary>
    public static readonly Lazy<TileMap> BigMap = new(() => WorldGenerator.Generate(BigSettings));

    /// <summary>
    /// A hand-built 200×200 test map so gameplay tests are exact and seed-independent:
    /// open ocean around a square island (x,y ∈ [60,139]) ringed with beach; forest on the
    /// west half, grassland north-east, hills south-east, a landlocked lake inside, plus a
    /// separate small jungle islet to the north.
    /// </summary>
    public static TileMap IslandMap()
    {
        var map = new TileMap(200, 200);
        for (int y = 0; y < 200; y++)
        for (int x = 0; x < 200; x++)
        {
            int i = map.Index(x, y);
            Biome b = Biome.Ocean;
            if (x >= 60 && x < 140 && y >= 60 && y < 140)
            {
                if (x == 60 || x == 139 || y == 60 || y == 139) b = Biome.Beach;
                else if (x < 100) b = Biome.TemperateForest;
                else if (y < 120) b = Biome.Grassland;
                else b = Biome.Hills;
                if (x >= 110 && x < 115 && y >= 80 && y < 85) b = Biome.ShallowWater; // lake
            }
            if (x >= 95 && x < 105 && y >= 20 && y < 30) b = Biome.Jungle; // islet
            map.Biome[i] = b;
            map.Elevation[i] = Biomes.IsWater(b) ? 0.2f : 0.6f;
            map.Temperature[i] = 0.6f;
            map.Moisture[i] = 0.5f;
        }
        map.SeaLevel = 0.5f;
        WorldGenerator.LabelRegions(map);
        map.StartTile = new GridPoint(57, 100); // west of the island
        return map;
    }

    /// <param name="refuge">If false, the start harbour is not a safe zone (so combat tests can fight there).</param>
    public static GameWorld IslandWorld(bool spawns = false, bool refuge = true)
    {
        // Fresh definitions per world so tests may tweak config without affecting each other.
        var defs = DefinitionDatabase.Load();
        if (!refuge) defs.Config.SafeHarbourRadius = 0;
        var w = GameWorld.CreateWithMap(IslandMap(), defs, new WorldGenSettings { Width = 200, Height = 200, Seed = 1 }, populatePolitics: false);
        w.Spawner.Enabled = spawns;
        return w;
    }

    /// <summary>Steps the world until <paramref name="until"/> holds or the time limit passes.
    /// Calls <paramref name="eachStep"/> after every step (for invariant checks).</summary>
    public static bool RunUntil(GameWorld w, Func<bool> until, float maxSeconds, Action? eachStep = null)
    {
        float t = 0;
        while (t < maxSeconds)
        {
            w.Update(GameWorld.FixedStep);
            t += GameWorld.FixedStep;
            eachStep?.Invoke();
            if (until()) return true;
        }
        return false;
    }

    public static void Run(GameWorld w, float seconds, Action? eachStep = null) => RunUntil(w, () => false, seconds, eachStep);
}
