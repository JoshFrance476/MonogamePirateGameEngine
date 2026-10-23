using PirateEngine.Core.Data;
using PirateEngine.Core.Politics;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;
using static PirateEngine.Tests.TestRunner;

namespace PirateEngine.Tests;

/// <summary>
/// The established starting world (setup.json presets), its equilibrium over time, and how it
/// reacts to the player.
/// </summary>
public static class WorldSetupTests
{
    private static GameWorld Big(string? setup = null, DefinitionDatabase? defs = null)
    {
        var gen = new WorldGenSettings { Width = 1024, Height = 1024, Seed = 1337, Setup = setup };
        return GameWorld.CreateWithMap(Fixtures.BigMap.Value, defs ?? DefinitionDatabase.Load(), gen);
    }

    private static List<float> NearestNeighbourDistances(PoliticsSystem p) =>
        p.Settlements.Select(s => p.Settlements.Where(o => o != s).Min(o => o.Tile.Distance(s.Tile))).OrderBy(d => d).ToList();

    // ---- the starting world ------------------------------------------------------------------

    [Test]
    public static void StandardWorldStartsEstablished()
    {
        using var w = Big();
        var p = w.Politics;
        var preset = w.Defs.SetupPreset(null);
        var cfg = w.Config.Politics;
        var ai = p.States.Where(s => !s.IsPlayer).ToList();
        Info($"setup '{p.SetupId}': {ai.Count} states, {p.Settlements.Count(s => s.IsIndependent)} independent towns, " +
             $"{p.Settlements.Count} settlements, {p.Settlements.Sum(s => s.Buildings.Count)} buildings, population {p.Settlements.Sum(s => s.Population)}");
        Info("levels: " + string.Join(" ", cfg.Levels.Select((l, i) => $"{l.Name} {p.Settlements.Count(s => s.LevelIndex == i)}")));
        foreach (var st in ai)
            Info($"  {st.Name} [{st.Behaviour.Name}] {st.Settlements.Count} towns, capital {st.Capital!.Name} ({st.Capital.Level(cfg).Name})");

        Assert.True(ai.Count >= 7, "majors, regionals and minors all placed");
        Assert.True(ai.Select(s => s.Behaviour.Id).Distinct().Count() >= 5, "every behaviour represented");
        Assert.True(ai.Count(s => s.Settlements.Count >= 6) >= 2, "great realms with many towns");
        Assert.True(ai.Count(s => s.Capital!.Level(cfg).Name == "City") >= 4, "capitals are established cities");
        Assert.True(p.Settlements.Count(s => s.LevelIndex >= 2) >= 15, "many towns and cities already exist");
        Assert.True(p.Settlements.Count(s => s.IsIndependent) >= 10, "independent towns");
        foreach (var st in ai)
        {
            Assert.True(st.Capital!.Owner == st, "capital belongs to its state");
            Assert.True(st.Capital.Tile.Distance(w.Map.StartTile) >= preset.CapitalClearance, "capitals keep clear of the player's start");
            Assert.Equal(st.Settlements.Count, st.InitialTowns, "starting size recorded");
            Assert.True(st.FieldArmy > 0 && st.Treasury > 0, "armies and treasuries in place");
        }

        // Established, not freshly founded: real buildings, near (not at) their population cap, fed.
        foreach (var s in p.Settlements)
        {
            Assert.True(s.FoundedAt < 0, "an old town");
            Assert.True(s.FedRatio >= 0.99f, $"{s.Name} is fed at the start");
            Assert.True(s.Population <= s.PopulationCap, $"{s.Name} within its cap");
            if (s.LevelIndex >= 1) Assert.True(s.Buildings.Count >= 1, $"{s.Name} has buildings");
            Assert.True(s.Garrison >= p.GarrisonTarget(s) * 0.9f, $"{s.Name} garrisoned");
            Assert.True(p.DockingSpot(s).IsValid, "every town is a port");
        }
        float fill = p.Settlements.Average(s => s.Population / (float)s.PopulationCap);
        Info($"average fill {fill:P0} of population cap");
        Assert.InRange(fill, 0.6, 0.97, "towns sit just below what their buildings support");

        // Dense heartlands and a sparse frontier.
        var nn = NearestNeighbourDistances(p);
        Info($"nearest-town distances: min {nn[0]:0}, quartiles {nn[nn.Count / 4]:0} / {nn[nn.Count / 2]:0} / {nn[nn.Count * 3 / 4]:0}, max {nn[^1]:0}");
        Assert.True(nn[0] >= preset.Spacing.Dense - 0.01f, "never closer than the dense spacing");
        Assert.True(nn.Count(d => d <= preset.Spacing.Dense * 1.4f) >= nn.Count / 5, "a good share of towns sit in dense clusters");
        Assert.True(nn.Count(d => d >= preset.Spacing.Sparse * 0.9f) >= 5, "and some stand alone in the frontier");
        float dense = p.Settlements.Count(s => p.DensityAt(s.Tile) >= 0.5f), sparse = p.Settlements.Count(s => p.DensityAt(s.Tile) < 0.3f);
        Info($"{dense} towns in dense land, {sparse} in sparse land");
        Assert.True(dense >= 10 && sparse >= 5, "both kinds of land are settled");

        // A friendly harbour near the start.
        var port = p.Settlements.OrderBy(s => s.Tile.Distance(w.Map.StartTile)).First();
        Info($"start port: {port.Name} ({port.Level(cfg).Name}) {port.Tile.Distance(w.Map.StartTile):0} tiles from the start");
        Assert.True(port.IsIndependent, "independent, so it trades with you");
        Assert.InRange(port.Tile.Distance(w.Map.StartTile), preset.StartPort!.Distance[0], preset.StartPort.Distance[1]);

        // A world at peace, with history: friendships and old rivalries.
        Assert.False(p.Diplomacy.All.Any(r => r.Stance == Stance.War), "no wars at the start (standard preset)");
        Assert.True(p.Diplomacy.All.Count(r => r.Stance == Stance.Alliance) >= 1, "old alliances");
        Assert.True(p.Diplomacy.All.Count(r => r.IsRivalry) >= 2, "old rivalries");
        Assert.True(ai.All(s => s.NextWarAllowed > 0), "everyone starts settled");
    }

    [Test]
    public static void EveryPresetPopulates()
    {
        var defs = DefinitionDatabase.Load();
        foreach (var preset in defs.Setup.Presets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var w = Big(preset.Id, defs);
            var p = w.Politics;
            int states = p.States.Count(s => !s.IsPlayer);
            var nn = p.Settlements.Count > 1 ? NearestNeighbourDistances(p) : new List<float> { 0 };
            Info($"{preset.Id,-9} {sw.ElapsedMilliseconds,5} ms: {states,2} states, {p.Settlements.Count,3} towns " +
                 $"({p.Settlements.Count(s => s.IsIndependent)} independent), median gap {nn[nn.Count / 2]:0}, " +
                 $"wars {p.Diplomacy.All.Count(r => r.Stance == Stance.War)}");
            Assert.Equal(preset.Id, p.SetupId);
            if (preset.States.Count == 0) Assert.Equal(0, states, "empty preset");
            else Assert.True(states >= 3, $"{preset.Id}: states placed");
            Assert.Equal(preset.Relations.Wars, p.Diplomacy.All.Count(r => r.Stance == Stance.War), $"{preset.Id}: standing wars");
        }
    }

    [Test]
    public static void HandDesignedStatesAppearWhereAndAsSpecified()
    {
        using var w = Big("charted");
        var p = w.Politics;
        var valemont = Assert.NotNull(p.States.FirstOrDefault(s => s.Name == "Crown of Valemont"), "named state exists");
        Assert.Equal("expansionist", valemont.Behaviour.Id);
        Assert.Equal("#9B59B6", valemont.Color);
        Assert.Equal("Valemont", valemont.Capital!.Name, "capital named as specified");
        var at = new System.Numerics.Vector2(0.32f * 1023, 0.38f * 1023);
        float off = System.Numerics.Vector2.Distance(valemont.Capital.Tile.Center, at);
        Info($"Valemont placed {off:0} tiles from its requested spot; {valemont.Settlements.Count} towns");
        Assert.True(off < 100, "near the requested position (the nearest good harbour to it)");
        Assert.True(valemont.Settlements.Count >= 4, "a major realm around it");
        Assert.NotNull(p.States.FirstOrDefault(s => s.Name == "Most Serene Republic of Corvo"));
    }

    [Test]
    public static void SetupCanBeWrittenInJson()
    {
        const string json = """
        {
          "default": "tiny",
          "presets": [{
            "id": "tiny",
            "density": { "heartlands": 1, "heartlandRadius": [60, 80] },
            "sizes": { "duchy": { "towns": [3, 3], "radius": 60, "capitalLevel": "Town", "townLevels": ["Village"] } },
            "states": [ { "name": "Duchy of Testmark", "capital": "Testmark", "behaviour": "isolationist", "size": "duchy", "color": "#123456" } ],
            "independents": { "count": 3, "freeCities": 0 },
            "startPort": { "enabled": false },
            "relations": { "alliances": 0, "rivalries": 0 }
          }]
        }
        """;
        static string Embedded(string f) => DefinitionDatabase.ReadEmbedded(f);
        var defs = DefinitionDatabase.FromJson(Embedded("resources.json"), Embedded("buildings.json"), Embedded("ships.json"),
            Embedded("config.json"), Embedded("units.json"), json);
        using var w = Big(null, defs);
        var p = w.Politics;
        var st = Assert.NotNull(p.States.SingleOrDefault(s => !s.IsPlayer), "exactly one state");
        Assert.Equal("Duchy of Testmark", st.Name);
        Assert.Equal(3, st.Settlements.Count, "towns as specified");
        Assert.Equal("Town", st.Capital!.Level(w.Config.Politics).Name);
        Assert.Equal(3, p.Settlements.Count(s => s.IsIndependent));

        // Mistakes are caught at load time.
        Assert.Throws<InvalidDataException>(() => DefinitionDatabase.FromJson(Embedded("resources.json"), Embedded("buildings.json"),
            Embedded("ships.json"), Embedded("config.json"), Embedded("units.json"), json.Replace("\"Village\"", "\"Metropolis\"")), "unknown level");
    }

    // ---- equilibrium -----------------------------------------------------------------------

    [Test]
    public static void TheWorldHoldsSteadyWithoutThePlayer()
    {
        using var w = Big();
        w.AsyncPathfinding = true;
        var p = w.Politics;
        int wars = 0, captures = 0, colonies = 0, collapses = 0;
        w.Events.WarDeclared += (_, _) => wars++;
        w.Events.SettlementCaptured += (_, _, _) => captures++;
        w.Events.StateCollapsed += _ => collapses++;
        int towns0 = p.Settlements.Count, pop0 = p.Settlements.Sum(s => s.Population), b0 = p.Settlements.Sum(s => s.Buildings.Count);
        w.Events.SettlementFounded += _ => colonies++;
        var owners0 = p.Settlements.ToDictionary(s => s, s => s.Owner);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int steps = 0, worstStarving = 0;
        while (w.Time < 40 * 60)
        {
            w.Update(GameWorld.FixedStep);
            if (++steps % 900 == 0) worstStarving = Math.Max(worstStarving, p.Settlements.Count(s => s.FedRatio < 0.8f));
        }
        int pop1 = p.Settlements.Sum(s => s.Population), b1 = p.Settlements.Sum(s => s.Buildings.Count);
        int changed = owners0.Count(kv => p.Settlements.Contains(kv.Key) && kv.Key.Owner != kv.Value);
        Info($"40 min in {sw.Elapsed.TotalSeconds:0.0}s: wars {wars}, captures {captures}, colonies {colonies}, collapses {collapses}");
        Info($"towns {towns0} -> {p.Settlements.Count}, population {pop0} -> {pop1} ({(pop1 - pop0) * 100f / pop0:+0.0;-0.0}%), " +
             $"buildings {b0} -> {b1}, towns that changed hands {changed}, worst moment {worstStarving} towns under 80% fed");
        Assert.Equal(0, collapses, "no state falls");
        Assert.True(changed <= 2, "the map barely changes hands");
        Assert.True(wars <= 2, "few wars start on their own");
        Assert.True(p.Settlements.Count - towns0 <= 2, "new towns are rare");
        Assert.InRange((pop1 - pop0) / (float)pop0, -0.03, 0.15, "population drifts slowly");
        Assert.True(b1 - b0 <= b0 * 0.08f, "construction is slow");
        Assert.True(worstStarving <= 3, "towns stay fed");
    }

    [Test]
    public static void TownsStillGrowOverHours()
    {
        var w = Fixtures.IslandWorld();
        var p = w.Politics;
        var s = p.Found(new GridPoint(100, 100), null, "Slowgrow", population: 20);
        foreach (var (id, x, y) in new[] { ("farm", 96, 98), ("farm", 104, 102), ("lumber_camp", 100, 95) })
        {
            var def = w.Defs.Building(id);
            var spot = w.Map.FindNearest(new GridPoint(x, y), 6, q => p.CanSettlementPlace(s, def, q));
            if (spot.IsValid) p.Attach(w.AddBuildingInternal(def, spot), s);
        }
        int cap = w.Config.Politics.PopBase + w.Config.Politics.PopPerBuilding * s.Buildings.Count;
        Fixtures.Run(w, 300);
        int after5 = s.Population;
        Fixtures.Run(w, 3300);
        Info($"{s.Buildings.Count} buildings (cap ~{cap}): population 20 -> {after5} after 5 min -> {s.Population} after an hour ({s.Level(w.Config.Politics).Name})");
        Assert.True(after5 <= 26, "barely changes in minutes");
        Assert.True(s.Population >= 35, "but grows over an hour");
    }

    // ---- reactions to the player -------------------------------------------------------------

    private static (GameWorld w, State target, State ally, State rival, State bystander) Neighbours()
    {
        var w = Fixtures.IslandWorld();
        var p = w.Politics;
        State Make(string beh, int x, int y)
        {
            var st = p.CreateState(w.Defs.Behaviour(beh), "Land" + p.States.Count, "#AA3333");
            p.Found(new GridPoint(x, y), st);
            st.InitialTowns = st.Settlements.Count;
            return st;
        }
        var target = Make("warlike", 120, 100);
        var ally = Make("mercantile", 100, 130);
        var rival = Make("opportunist", 100, 75);
        var bystander = Make("mercantile", 92, 122);
        p.FormAlliance(target, ally);
        var rr = p.Diplomacy.Get(target, rival);
        rr.Baseline = rr.Opinion = -60;
        return (w, target, ally, rival, bystander);
    }

    [Test]
    public static void DeclaringWarMakesTheWorldTakeSides()
    {
        var (w, target, ally, rival, bystander) = Neighbours();
        var p = w.Politics;
        var me = p.PlayerState;
        float allyBefore = p.Diplomacy.Opinion(me, ally), rivalBefore = p.Diplomacy.Opinion(me, rival);
        Assert.True(p.PlayerDeclareWar(target));
        Info($"ally opinion {allyBefore:0} -> {p.Diplomacy.Opinion(me, ally):0}, rival {rivalBefore:0} -> {p.Diplomacy.Opinion(me, rival):0}");
        Assert.True(p.Diplomacy.AtWar(me, ally), "the target's ally joins against you");
        Assert.True(p.Diplomacy.Opinion(me, rival) > rivalBefore + 5, "its old rival warms to you");
        Assert.False(p.Diplomacy.AtWar(me, bystander), "bystanders stay out");
    }

    [Test]
    public static void ConquestAlarmsTheNeighbours()
    {
        var (w, target, ally, rival, bystander) = Neighbours();
        var p = w.Politics;
        var me = p.PlayerState;
        p.PlayerDeclareWar(target);
        float by0 = p.Diplomacy.Opinion(me, bystander), rival0 = p.Diplomacy.Opinion(me, rival);
        p.Transfer(target.Capital!, me, 5);
        Info($"bystander {by0:0} -> {p.Diplomacy.Opinion(me, bystander):0}, rival {rival0:0} -> {p.Diplomacy.Opinion(me, rival):0}");
        Assert.True(p.Diplomacy.Opinion(me, bystander) < by0 - 3, "a peaceful neighbour grows wary of a conqueror");
        Assert.True(p.Diplomacy.Opinion(me, rival) > rival0, "the loser's rival approves");
    }

    [Test]
    public static void ProvocationOverridesTheQuietTimes()
    {
        var (w, target, _, _, _) = Neighbours();
        var p = w.Politics;
        var me = p.PlayerState;
        w.Config.Politics.ReactiveWarChance = 1f;   // make the roll deterministic
        w.Config.Politics.ProactiveWarChance = 1f;
        w.Config.Politics.NewStateGrace = 0;
        w.CommandDock(new GridPoint(60, 100));
        Fixtures.RunUntil(w, () => w.Player.IsDocked, 30);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("planks"), 10);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("stone"), 8);
        w.GrantGold(1000);
        Assert.NotNull(w.PlaceBuilding(w.Defs.TownHall, new GridPoint(64, 101)));
        target.FieldArmy = 300;
        foreach (var st in p.States.Where(s => !s.IsPlayer)) st.NextWarAllowed = w.Time + 10_000; // everyone just made peace
        p.ForceTicks(economy: 0, diplomacy: 5);
        Assert.False(p.Diplomacy.AtWar(target, me), "a realm between wars leaves an inoffensive neighbour alone");
        p.Diplomacy.AdjustOpinion(me, target, -100);
        p.ForceTicks(economy: 0, diplomacy: 1);
        Assert.True(p.Diplomacy.AtWar(target, me), "but answers a provocation at once");
    }

    [Test]
    public static void ARisingPlayerPowerDrawsAttention()
    {
        var (w, target, _, _, bystander) = Neighbours();
        var p = w.Politics;
        var me = p.PlayerState;
        Assert.False(p.IsDominant(me));
        foreach (var (x, y) in new[] { (60, 85), (60, 120), (140, 80), (140, 125) })
            p.Found(new GridPoint(x, y), me);
        Info($"you hold {me.Settlements.Count} towns: dominant {p.IsDominant(me)}, casus belli for the warlike neighbour {p.HasCasusBelli(target, me)}");
        Assert.True(p.IsDominant(me), "four towns from nothing is a rising power");
        Assert.True(p.HasCasusBelli(target, me), "which gives others a reason to act");
        float before = p.Diplomacy.Opinion(me, bystander);
        p.ForceTicks(economy: 0, diplomacy: 10);
        Assert.True(p.Diplomacy.Opinion(me, bystander) < before, "and wears down goodwill");
    }
}
