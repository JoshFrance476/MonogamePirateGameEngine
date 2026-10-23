using System.Diagnostics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;
using static PirateEngine.Tests.TestRunner;

namespace PirateEngine.Tests;

public static class DataTests
{
    [Test]
    public static void EmbeddedDefinitionsLoadAndLink()
    {
        var db = Fixtures.Defs.Value;
        Assert.True(db.Resources.Count >= 10, "resources");
        Assert.True(db.Buildings.Count >= 10, "buildings");
        Assert.True(db.Ships.Count >= 3, "ships");
        Assert.Equal("Planks", db.Resource("planks").Name);
        Assert.True(db.Building("lumber_camp").AllowsBiome(Biome.TemperateForest));
        Assert.False(db.Building("lumber_camp").AllowsBiome(Biome.Desert));
        Assert.True(db.Building("sawmill").AcceptsInput[db.ResourceIndex("wood")], "sawmill accepts wood");
        Assert.Equal("brigantine", db.Ship("sloop").UpgradeTarget!.Id);
        Info($"{db.Resources.Count} resources, {db.Buildings.Count} buildings, {db.Ships.Count} ship classes, {db.Config.Spawns.Count} spawn entries");
    }

    [Test]
    public static void EveryProducedResourceHasAUse()
    {
        // Sanity check of the shipped content: nothing is produced that can't be consumed,
        // sold, or used by the ship (ammo, repairs, refits).
        var db = Fixtures.Defs.Value;
        var consumed = new HashSet<string>(db.Buildings.SelectMany(b => b.Recipes).SelectMany(r => r.Inputs.Keys));
        consumed.UnionWith(db.Ships.SelectMany(s => s.UpgradeCost.Keys));
        consumed.UnionWith(db.Buildings.SelectMany(b => b.BuildCost.Keys));
        consumed.Add(db.Config.AmmoResource);
        consumed.Add(db.Config.RepairResource);
        foreach (var r in db.Resources) Assert.True(consumed.Contains(r.Id), $"'{r.Id}' has no use");
    }

    [Test]
    public static void ReadmeUnitModExampleLoads()
    {
        // The grenadiers example from README.md, appended to the shipped units.
        string units = DefinitionDatabase.ReadEmbedded("units.json").TrimEnd().TrimEnd(']') + """
          , { "id": "grenadiers", "name": "Grenadiers", "kind": "ranged",
              "description": "Hurl grenades a short distance.",
              "members": 8, "hpPerMember": 12, "damage": 9, "damageVsShips": 4, "range": 3.5, "reload": 5,
              "accuracy": 0.5, "speed": 1.9, "sightRadius": 7, "autoEngageRadius": 4,
              "recruitGold": 120, "recruitCost": { "rations": 4, "iron": 4 } } ]
          """;
        var db = DefinitionDatabase.FromJson(DefinitionDatabase.ReadEmbedded("resources.json"), DefinitionDatabase.ReadEmbedded("buildings.json"),
            DefinitionDatabase.ReadEmbedded("ships.json"), DefinitionDatabase.ReadEmbedded("config.json"), units);
        var g = db.Unit("grenadiers");
        Assert.True(g.IsRanged && g.MaxHp == 96 && g.RecruitCostList.Length == 2);
        string badUnits = """[ { "id": "x", "name": "X", "recruitCost": { "gunpowder": 1 } } ]""";
        Assert.Throws<InvalidDataException>(() => DefinitionDatabase.FromJson(DefinitionDatabase.ReadEmbedded("resources.json"),
            DefinitionDatabase.ReadEmbedded("buildings.json"), DefinitionDatabase.ReadEmbedded("ships.json"),
            DefinitionDatabase.ReadEmbedded("config.json"), badUnits), "unknown resource in unit cost");
    }

    [Test]
    public static void BadContentFailsLoudly()
    {
        string res = DefinitionDatabase.ReadEmbedded("resources.json");
        string ships = DefinitionDatabase.ReadEmbedded("ships.json");
        string cfg = DefinitionDatabase.ReadEmbedded("config.json");
        string badBuilding = """[ { "id": "x", "name": "X", "recipes": [ { "inputs": { "unobtainium": 1 } } ] } ]""";
        Assert.Throws<InvalidDataException>(() => DefinitionDatabase.FromJson(res, badBuilding, ships, cfg), "unknown resource");
        string badBiome = """[ { "id": "x", "name": "X", "allowedBiomes": ["Moon"] } ]""";
        Assert.Throws<InvalidDataException>(() => DefinitionDatabase.FromJson(res, badBiome, ships, cfg), "unknown biome");
    }
}

public static class WorldGenTests
{
    [Test]
    public static void GeneratesLargeMapQuickly()
    {
        _ = Fixtures.BigMap.Value; // warm up JIT on the shared map first
        var sw = Stopwatch.StartNew();
        var map = WorldGenerator.Generate(new WorldGenSettings { Width = 1024, Height = 1024, Seed = 2024 });
        Info($"fresh 1024x1024 world generated in {sw.ElapsedMilliseconds} ms on {Environment.ProcessorCount} cores; {map.RegionSizes.Length} regions");
        Assert.Equal(1024 * 1024, map.Count);
        Assert.True(sw.ElapsedMilliseconds < 10_000, "generation should take seconds, not minutes");
    }

    [Test]
    public static void LandFractionAndBiomeVariety()
    {
        var map = Fixtures.BigMap.Value;
        var counts = new int[Biomes.Count];
        foreach (var b in map.Biome) counts[(int)b]++;
        int land = 0;
        for (int b = 0; b < Biomes.Count; b++) if (!Biomes.IsWater((Biome)b)) land += counts[b];
        double frac = land / (double)map.Count;
        Assert.InRange(frac, 0.25, 0.40, "land fraction");
        int present = counts.Count(c => c > 0);
        Info($"land {frac:P1}; biomes present {present}/{Biomes.Count}: " +
             string.Join(", ", Enumerable.Range(0, Biomes.Count).Where(i => counts[i] > 0)
                 .Select(i => $"{Biomes.Name((Biome)i)} {100.0 * counts[i] / map.Count:0.0}%")));
        Assert.True(present >= 13, $"expected rich biome variety, got {present}");
    }

    [Test]
    public static void BordersAreOcean()
    {
        var map = Fixtures.BigMap.Value;
        for (int i = 0; i < map.Width; i++)
        {
            Assert.True(map.IsWater(i, 0) && map.IsWater(i, map.Height - 1), $"top/bottom border at x={i}");
            Assert.True(map.IsWater(0, i) && map.IsWater(map.Width - 1, i), $"left/right border at y={i}");
        }
    }

    [Test]
    public static void DeterministicForSeed()
    {
        var s = new WorldGenSettings { Width = 256, Height = 256, Seed = 42 };
        var a = WorldGenerator.Generate(s);
        var b = WorldGenerator.Generate(s);
        var c = WorldGenerator.Generate(new WorldGenSettings { Width = 256, Height = 256, Seed = 43 });
        Assert.True(a.Biome.AsSpan().SequenceEqual(b.Biome), "same seed must give identical maps");
        Assert.False(a.Biome.AsSpan().SequenceEqual(c.Biome), "different seeds should differ");
    }

    [Test]
    public static void StartIsNavigableNextToHospitableLand()
    {
        var map = Fixtures.BigMap.Value;
        var st = map.StartTile;
        Assert.True(map.IsWater(st), "start is water");
        Assert.Equal(map.MainOceanRegion, map.RegionAt(st), "start in main ocean");
        bool nearLand = GridPoint.Neighbours8.Any(o => map.IsLand(st + o));
        Assert.True(nearLand, "start touches land");
        Info($"start {st}, main ocean {map.RegionSizes[map.MainOceanRegion]} tiles");
    }

    [Test]
    public static void ClimateFollowsLatitude()
    {
        var map = Fixtures.BigMap.Value;
        double Avg(int y0, int y1)
        {
            double s = 0; int n = 0;
            for (int y = y0; y < y1; y++) for (int x = 0; x < map.Width; x++) { s += map.Temperature[map.Index(x, y)]; n++; }
            return s / n;
        }
        double pole = Avg(0, 60), equator = Avg(482, 542);
        Info($"mean temperature pole {pole:0.00}, equator {equator:0.00}");
        Assert.True(equator > pole + 0.3, "equator should be much warmer than the poles");
    }
}

public static class PathfindingTests
{
    [Test]
    public static void PathsAreAllWaterAndSmoothed()
    {
        var map = Fixtures.BigMap.Value;
        var pf = new Pathfinder(map);
        var rng = new Rng(7);
        var path = new List<GridPoint>();
        int found = 0;
        double totalMs = 0, maxMs = 0;
        int ocean = map.MainOceanRegion;
        for (int i = 0; i < 40; i++)
        {
            var a = RandomInRegion(map, rng, ocean);
            var b = RandomInRegion(map, rng, ocean);
            var sw = Stopwatch.StartNew();
            var status = pf.FindPath(a, b, path, maxExpansions: 2_000_000);
            double ms = sw.Elapsed.TotalMilliseconds;
            totalMs += ms; maxMs = Math.Max(maxMs, ms);
            Assert.Equal(PathStatus.Found, status, $"path {a}->{b}");
            found++;
            Assert.Equal(b, path[^1], "path ends at goal");
            var prev = a;
            foreach (var p in path)
            {
                Assert.True(map.IsWater(p), $"waypoint {p} on land");
                Assert.True(pf.HasLineOfSight(prev, p), $"leg {prev}->{p} crosses land");
                prev = p;
            }
        }
        Info($"{found} long random paths on 1024² ocean: avg {totalMs / found:0.0} ms, max {maxMs:0.0} ms");
    }

    [Test]
    public static void UnreachableIsRejectedInstantly()
    {
        var map = Fixtures.IslandMap();
        var pf = new Pathfinder(map);
        var path = new List<GridPoint>();
        var lake = new GridPoint(112, 82);
        var sw = Stopwatch.StartNew();
        var status = pf.FindPath(map.StartTile, lake, path);
        Assert.Equal(PathStatus.Unreachable, status);
        Assert.Equal(0, pf.LastExpansions, "should not search at all");
        Assert.True(sw.ElapsedMilliseconds < 5, "instant");
        Assert.Equal(PathStatus.GoalBlocked, pf.FindPath(map.StartTile, new GridPoint(80, 80), path));
    }

    [Test]
    public static void NoDiagonalCornerCutting()
    {
        // Two land tiles touching diagonally must block the diagonal gap between them.
        var map = Fixtures.IslandMap();
        map.Biome[map.Index(20, 20)] = Biome.Grassland;
        map.Biome[map.Index(21, 21)] = Biome.Grassland;
        WorldGenerator.LabelRegions(map);
        var pf = new Pathfinder(map);
        Assert.False(pf.HasLineOfSight(new GridPoint(20, 21), new GridPoint(21, 20)), "diagonal squeeze");
        var path = new List<GridPoint>();
        Assert.Equal(PathStatus.Found, pf.FindPath(new GridPoint(20, 21), new GridPoint(21, 20), path, smooth: false));
        Assert.True(path.Count >= 3, "must go around, not through the gap");
    }

    private static GridPoint RandomInRegion(TileMap map, Rng rng, int region)
    {
        while (true)
        {
            var p = new GridPoint(rng.Next(map.Width), rng.Next(map.Height));
            if (map.RegionAt(p) == region) return p;
        }
    }
}

public static class SegmentTests
{
    [Test]
    public static void SegmentClearMatchesDenseSampling()
    {
        // Cross-check the grid traversal against brute-force sampling on random segments.
        var map = Fixtures.BigMap.Value;
        var pf = new Pathfinder(map);
        var rng = new Rng(11);
        int disagreements = 0, checkedSegs = 0;
        for (int i = 0; i < 20000; i++)
        {
            var a = new System.Numerics.Vector2(rng.Range(400, 560), rng.Range(400, 560));
            var b = a + new System.Numerics.Vector2(rng.Range(-12, 12), rng.Range(-12, 12));
            bool fast = pf.SegmentClear(a, b);
            bool slow = true;
            for (int k = 0; k <= 2000 && slow; k++)
                slow = map.IsWater(GridPoint.FromWorld(System.Numerics.Vector2.Lerp(a, b, k / 2000f)));
            checkedSegs++;
            // The traversal may be stricter (it rejects exact-corner grazes); it must never be laxer.
            if (fast && !slow) disagreements++;
        }
        Info($"{checkedSegs} segments checked");
        Assert.Equal(0, disagreements, "SegmentClear said clear but sampling hit land");
    }
}
