using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Politics;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;
using static PirateEngine.Tests.TestRunner;

namespace PirateEngine.Tests;

public static class PoliticsTests
{
    // ---- helpers ------------------------------------------------------------------------

    private static GameWorld Island(bool refuge = false) => Fixtures.IslandWorld(refuge: refuge);

    private static GameWorld DockedWest(GameWorld? w = null)
    {
        w ??= Island();
        w.CommandDock(new GridPoint(60, 100));
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 30), "docked");
        return w;
    }

    private static State AiState(GameWorld w, string behaviour) =>
        w.Politics.CreateState(w.Defs.Behaviour(behaviour), "Testland" + w.Politics.States.Count, "#AA3333");

    private static void GiveTownHallMaterials(GameWorld w)
    {
        w.Player.Cargo.Clear();
        w.Player.Cargo.Add(w.Defs.ResourceIndex("planks"), 10);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("stone"), 8);
        w.GrantGold(1000);
    }

    private static GridPoint FreeSpot(GameWorld w, BuildingDef def)
    {
        for (int r = 1; r <= (int)w.Config.DockReach; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var p = new GridPoint(w.Player.DockTile.X + dx, w.Player.DockTile.Y + dy);
                    if (w.CanPlace(def, p, checkCost: false) == PlacementError.None) return p;
                }
        return GridPoint.Invalid;
    }

    private static void Invariants(GameWorld w)
    {
        var p = w.Politics;
        foreach (var s in p.Settlements)
        {
            if (s.Owner != null)
            {
                Assert.True(s.Owner.Settlements.Contains(s), $"{s} missing from its owner's list");
                Assert.False(s.Owner.Collapsed, $"{s} owned by a collapsed state");
            }
            Assert.True(s.TownHall.Settlement == s, "town hall link");
            foreach (var b in s.Buildings) Assert.True(b.Settlement == s && w.Buildings.Contains(b), "building link");
            Assert.True(s.Population >= 3, "population floor");
        }
        foreach (var st in p.States)
            foreach (var s in st.Settlements) Assert.True(s.Owner == st, "owner list consistent");
    }

    // ---- world setup and development ------------------------------------------------

    [Test]
    public static void TownLogisticsFeedWorkshopsAutomatically()
    {
        var w = Island();
        var s = w.Politics.Found(new GridPoint(70, 100), null, "Testville");
        var camp = w.AddBuildingInternal(w.Defs.Building("lumber_camp"), new GridPoint(72, 100));
        var mill = w.AddBuildingInternal(w.Defs.Building("sawmill"), new GridPoint(68, 102));
        w.Politics.Attach(camp, s);
        w.Politics.Attach(mill, s);
        int planks = w.Defs.ResourceIndex("planks");
        Fixtures.Run(w, 120); // 12 economy ticks
        Assert.True(s.Stores[planks] > 0, $"planks reached the stores without any ship ({s.Stores[planks]})");
        Assert.True(mill.CyclesCompleted > 0);
        var cap = w.Politics.ProductionPerMinute(s);
        Assert.True(cap.Any(c => c.resource == planks && c.perMinute > 0), "capacity reported for the settlement screen");
        Assert.True(s.LastProduced.Sum() > 0, "actual deliveries tracked per tick");
    }

    [Test]
    public static void StarvingTownsShrinkFedTownsGrow()
    {
        var w = Island();
        var hungry = w.Politics.Found(new GridPoint(70, 100), null, "Hungry", population: 120);
        var fed = w.Politics.Found(new GridPoint(120, 100), null, "Fed", population: 20);
        for (int i = 0; i < 3; i++) // room to grow: buildings raise the population cap
            w.Politics.Attach(w.AddBuildingInternal(w.Defs.Building("lumber_camp"), new GridPoint(122 + i, 104)), fed);
        fed.Stores.Add(w.Defs.ResourceIndex("grain"), 100);
        w.Politics.ForceTicks(economy: 30);
        Info($"hungry {hungry.Population} (fed {hungry.FedRatio:P0}), fed {fed.Population}");
        Assert.True(hungry.Population < 120, "starving town shrinks");
        Assert.True(fed.Population > 20, "fed town grows");
    }

    // ---- player settlement ------------------------------------------------------------

    [Test]
    public static void TownHallFoundsPlayerSettlementAndState()
    {
        var w = DockedWest();
        var camp = w.PlaceBuilding(w.Defs.Building("lumber_camp"), new GridPoint(63, 98))!;
        Assert.True(camp.Settlement == null, "an outpost before the town hall");
        GiveTownHallMaterials(w);
        var hall = Assert.NotNull(w.PlaceBuilding(w.Defs.TownHall, new GridPoint(64, 101)));
        var s = Assert.NotNull(hall.Settlement);
        Assert.Equal(w.Politics.PlayerState, s.Owner);
        Assert.True(w.Politics.PlayerState.Founded, "player state founded");
        Assert.True(w.Politics.PlayerState.Name.Contains(s.Name), "state named after its capital");
        Assert.Equal(s, camp.Settlement, "existing outpost joined the town");
        var (farmDef, spot) = w.Defs.Buildings.Where(d => !d.IsTownHallDef())
            .Select(d => (d, p: FreeSpot(w, d))).First(x => x.p.IsValid);
        w.Player.Cargo.Clear();
        foreach (var (res, n) in farmDef.BuildCostList) w.Player.Cargo.Add(res, n);
        w.GrantGold(1000);
        var farm = Assert.NotNull(w.PlaceBuilding(farmDef, spot), "second building");
        Assert.Equal(s, farm.Settlement, "new building in territory joins");
        GiveTownHallMaterials(w);
        Assert.Equal(PlacementError.ForeignTerritory, w.CanPlace(w.Defs.TownHall, new GridPoint(62, 96)), "inside own territory");
        Info($"founded {s.Name}; state {w.Politics.PlayerState.Name}");
    }

    [Test]
    public static void NewPlayerStateGetsAGracePeriod()
    {
        var w = DockedWest();
        var brute = AiState(w, "warlike");
        w.Politics.Found(new GridPoint(120, 100), brute);
        brute.FieldArmy = 300;
        GiveTownHallMaterials(w);
        w.PlaceBuilding(w.Defs.TownHall, new GridPoint(64, 101));
        Assert.True(w.Politics.WarScore(brute, w.Politics.PlayerState) > 40, "they would love a war");
        w.Config.Politics.ProactiveWarChance = 1f; // no dice: test the rule itself
        Fixtures.Run(w, w.Config.Politics.NewStateGrace - 30);
        Assert.False(w.Politics.Diplomacy.AtWar(brute, w.Politics.PlayerState), "but not during the grace period");
        Fixtures.Run(w, 120);
        Assert.True(w.Politics.Diplomacy.AtWar(brute, w.Politics.PlayerState), "and soon after it");
    }

    [Test]
    public static void PlayerTownGrowsAndPaysTaxes()
    {
        var w = DockedWest();
        GiveTownHallMaterials(w);
        var s = w.PlaceBuilding(w.Defs.TownHall, new GridPoint(64, 101))!.Settlement!;
        s.Stores.Add(w.Defs.ResourceIndex("fish"), 100);
        int gold = w.Gold, pop = s.Population;
        w.Politics.ForceTicks(economy: 40);
        Assert.True(s.Population > pop, "grew");
        Assert.True(w.Gold > gold, "taxes paid into the player's treasury");
        Assert.Equal(0, s.Buildings.Count, "the AI never builds in the player's town");
        Info($"pop {pop} -> {s.Population}, gold {gold} -> {w.Gold}");
    }

    [Test]
    public static void PlayerCannotBuildInForeignTerritory()
    {
        var w = DockedWest();
        var st = AiState(w, "mercantile");
        w.Politics.Found(new GridPoint(66, 100), st, "Foreign");
        Assert.Equal(PlacementError.ForeignTerritory, w.CanPlace(w.Defs.Building("lumber_camp"), new GridPoint(64, 98)));
        Assert.False(w.BuildingsInReach().Any(b => b.Settlement?.Owner == st), "foreign buildings aren't the player's to load from");
    }

    // ---- diplomacy ----------------------------------------------------------------------

    [Test]
    public static void BehaviourDrivesWarDecisions()
    {
        var w = Island();
        var warlike = AiState(w, "warlike");
        var isolationist = AiState(w, "isolationist");
        var victim = AiState(w, "mercantile");
        w.Politics.Found(new GridPoint(70, 100), warlike);
        w.Politics.Found(new GridPoint(100, 75), isolationist);
        w.Politics.Found(new GridPoint(120, 120), victim);
        warlike.FieldArmy = 120;
        isolationist.FieldArmy = 120;
        float sWar = w.Politics.WarScore(warlike, victim), sIso = w.Politics.WarScore(isolationist, victim);
        Info($"war score vs a weak neighbour: warlike {sWar:0}, isolationist {sIso:0} (declares above 40)");
        Assert.True(sWar > 40 && sIso < 20);
        w.Config.Politics.ProactiveWarChance = 1f; // no dice: test the decision itself
        w.Politics.ForceTicks(economy: 0, diplomacy: 10);
        Assert.True(w.Politics.Diplomacy.AtWar(warlike, victim), "warlike state attacked");
        Assert.False(w.Politics.Diplomacy.AtWar(isolationist, victim), "isolationist never starts a war");
    }

    [Test]
    public static void UnprovokedWarsAreRare()
    {
        var w = Island();
        var warlike = AiState(w, "warlike");
        var victim = AiState(w, "mercantile");
        w.Politics.Found(new GridPoint(70, 100), warlike);
        w.Politics.Found(new GridPoint(120, 120), victim);
        warlike.FieldArmy = 120;
        Assert.True(w.Politics.WarScore(warlike, victim) > 40, "it would like a war");
        int wars = 0;
        for (int trial = 0; trial < 20; trial++)
        {
            w.Politics.ForceTicks(economy: 0, diplomacy: 15); // five minutes of diplomacy
            if (w.Politics.Diplomacy.AtWar(warlike, victim)) { wars++; w.Politics.MakePeace(warlike, victim); warlike.NextWarAllowed = 0; w.Politics.Diplomacy.Get(warlike, victim).TruceUntil = 0; }
        }
        Info($"{wars} wars in 20 five-minute spells (chance per tick {w.Config.Politics.ProactiveWarChance})");
        Assert.True(wars <= 3, "an appetite for war rarely becomes a war without a reason");
    }

    [Test]
    public static void AlliesJoinADefensiveWar()
    {
        var w = Island();
        var a = AiState(w, "warlike");
        var b = AiState(w, "mercantile");
        var c = AiState(w, "mercantile");
        foreach (var (st, x, y) in new[] { (a, 70, 100), (b, 120, 100), (c, 100, 130) }) w.Politics.Found(new GridPoint(x, y), st);
        w.Politics.FormAlliance(b, c);
        w.Politics.DeclareWar(a, b);
        Assert.True(w.Politics.Diplomacy.AtWar(c, a), "ally joined");
    }

    [Test]
    public static void AbstractSiegeConquersAndStatesCollapse()
    {
        var w = Island();
        var a = AiState(w, "warlike");
        var b = AiState(w, "mercantile");
        w.Politics.Found(new GridPoint(70, 100), a);
        var target = w.Politics.Found(new GridPoint(120, 100), b);
        var mill = w.AddBuildingInternal(w.Defs.Building("farm"), new GridPoint(122, 98));
        w.Politics.Attach(mill, target);
        target.Garrison = 5;
        a.FieldArmy = 200;
        w.Politics.DeclareWar(a, b);
        w.Politics.ForceTicks(economy: 0, diplomacy: 0, sieges: 120);
        Assert.Equal(a, target.Owner, "captured");
        Assert.True(mill.Settlement == target && w.Politics.IsPlayerOwned(mill) == false, "buildings came with it");
        Assert.True(b.Collapsed, "a state with no settlements falls");
        Invariants(w);
    }

    [Test]
    public static void PlayerDiplomacy()
    {
        var w = Island();
        var p = w.Politics;
        var trader = AiState(w, "mercantile");
        var brute = AiState(w, "warlike");
        p.Found(new GridPoint(120, 100), trader);
        p.Found(new GridPoint(100, 125), brute);
        w.GrantGold(1000);
        float before = p.Diplomacy.Opinion(p.PlayerState, trader);
        Assert.True(p.PlayerGift(trader));
        Assert.True(p.Diplomacy.Opinion(p.PlayerState, trader) > before + 10, "gift improves opinion");
        Assert.Equal("Found a settlement first", p.PlayerCannot(trader, "alliance"));

        Assert.True(p.PlayerDeclareWar(brute));
        brute.FieldArmy = 500;
        Assert.False(p.PlayerProposePeace(brute), "a strong warlike state refuses peace");
        Assert.True(p.PlayerDeclareWar(trader));
        p.Diplomacy.Get(p.PlayerState, trader).WarWeariness = 40;
        Assert.True(p.PlayerProposePeace(trader), "a war-weary trader accepts");
        Assert.Equal(Stance.Peace, p.Diplomacy.StanceOf(p.PlayerState, trader));
        Assert.Equal("Not at war", p.PlayerCannot(trader, "peace"));
    }

    [Test]
    public static void AttackingAStatesTroopsMeansWar()
    {
        var w = DockedWest();
        var st = AiState(w, "mercantile");
        var town = w.Politics.Found(new GridPoint(75, 100), st);
        town.Garrison = 10;
        var crew = w.Player.Troops.First(t => !t.Def.IsRanged);
        w.Disembark(crew);
        var musk = w.Player.Troops.First();
        w.Disembark(musk);
        w.Politics.PlayerAttackSettlement(town); // war → militia muster when our troops come near
        Assert.True(w.Politics.Diplomacy.AtWar(w.Politics.PlayerState, st));
        w.CommandUnitMove(crew, new GridPoint(70, 100));
        Fixtures.RunUntil(w, () => town.MusteredMilitia.Count > 0, 30);
        Assert.True(town.MusteredMilitia.Count > 0, "militia mustered against nearby hostile troops");
        var m = town.MusteredMilitia[0];
        Assert.True(GameWorld.IsHostile(crew, m), "and they're hostile to our squads");
    }

    [Test]
    public static void PlayerTroopsCaptureAnIndependentTown()
    {
        var w = DockedWest();
        var town = w.Politics.Found(new GridPoint(74, 100), null, "Freeport");
        town.Garrison = 10;
        w.DisembarkAll();
        var squads = w.Units.Where(u => u.Faction == Faction.Player).ToList();
        Assert.Null(w.Politics.PlayerCannotAttack(town));
        w.Politics.PlayerAttackSettlement(town);
        bool taken = Fixtures.RunUntil(w, () => town.Owner == w.Politics.PlayerState, 240, () =>
        {
            if ((int)(w.Time * 30) % 30 != 0) return;
            foreach (var sq in squads.Where(s => !s.IsDead))
            {
                var enemy = town.MusteredMilitia.FirstOrDefault(m => !m.IsDead);
                if (enemy != null) { if (sq.Target != enemy) w.CommandUnitAttack(sq, enemy); }
                else if (!sq.IsMoving && sq.Tile.Distance(town.Tile) > 1.5f) w.CommandUnitMove(sq, town.Tile);
            }
        });
        Info($"t={w.Time:0}s: owner {town.Owner?.Name ?? "independent"}, survivors {string.Join(", ", squads.Where(s => !s.IsDead).Select(s => $"{s.Name} {s.Members}"))}");
        Assert.True(taken, "captured by holding the town hall");
        Assert.True(w.Politics.PlayerState.Founded, "capturing a town founds the player's state");
        Assert.Equal(0, town.MusteredMilitia.Count, "old militia gone");
    }

    [Test]
    public static void AiAssaultOnAPlayerTownIsFoughtWithRealTroops()
    {
        var w = DockedWest();
        GiveTownHallMaterials(w);
        var town = w.PlaceBuilding(w.Defs.TownHall, new GridPoint(64, 101))!.Settlement!;
        var enemy = AiState(w, "warlike");
        w.Politics.Found(new GridPoint(120, 100), enemy);
        w.Politics.DeclareWar(enemy, w.Politics.PlayerState);
        enemy.FieldArmy = 60;
        w.Politics.ForceTicks(economy: 0, diplomacy: 0, sieges: 1);
        Assert.True(town.Siege?.Attacker == enemy, "siege launched on the player's town");
        Fixtures.Run(w, 1);
        Assert.True(town.Siege!.IsPhysical, "near the player the attack arrives as squads");
        var attackers = w.Units.Where(u => u.Allegiance == enemy).ToList();
        Assert.True(attackers.Count > 0 && attackers.All(a => GameWorld.IsHostile(a, w.Player)), "hostile squads on the map");
        Info($"{attackers.Count} assault squads, {town.Siege.SoldiersTotal} soldiers");
        Fixtures.RunUntil(w, () => town.MusteredMilitia.Count > 0, 40);
        Assert.True(town.MusteredMilitia.Count > 0 || town.Owner != w.Politics.PlayerState, "the town's militia turned out");
    }

    // ---- trade ------------------------------------------------------------------------

    [Test]
    public static void TradeWithAFriendlyTown()
    {
        var w = DockedWest();
        var st = AiState(w, "mercantile");
        var town = w.Politics.Found(new GridPoint(63, 104), st, "Market Town");
        int grain = w.Defs.ResourceIndex("grain"), rum = w.Defs.ResourceIndex("rum");
        town.Stores.Add(grain, 60);
        town.Treasury = 1000; // the merchants' purse
        w.Player.Cargo.Clear();
        Assert.Null(w.Politics.CannotTrade(town));
        int cheap = w.Politics.BuyPrice(town, grain);
        int gold = w.Gold;
        Assert.Equal(5, w.Politics.BuyFrom(town, grain, 5));
        Assert.True(w.Gold < gold && w.Player.Cargo[grain] == 5, "bought grain");
        w.Player.Cargo.Add(rum, 4);
        int sellRum = w.Politics.SellPrice(town, rum);
        gold = w.Gold;
        Assert.Equal(4, w.Politics.SellTo(town, rum, 10));
        Assert.True(w.Gold >= gold + 4 * (sellRum - 5), "sold rum");
        Assert.True(w.Politics.SellPrice(town, rum) <= sellRum, "price falls as they stock up");
        Info($"grain {cheap}g each (plentiful), rum {sellRum}g each (scarce)");
        w.Politics.PlayerDeclareWar(st);
        Assert.Equal("You are at war with it", w.Politics.CannotTrade(town));
    }

    // ---- persistence and soak --------------------------------------------------------------

    [Test]
    public static void SaveLoadKeepsThePoliticalMap()
    {
        var w = GameWorld.CreateNew(new WorldGenSettings { Width = 512, Height = 512, Seed = 21 }, Fixtures.Defs.Value);
        var p = w.Politics;
        p.ForceTicks(economy: 20, diplomacy: 5, sieges: 5);
        var ai = p.States.Where(s => !s.IsPlayer && s.IsActive).ToList();
        Assert.True(ai.Count >= 2, $"need two AI states on this map ({ai.Count})");
        if (!p.Diplomacy.AtWar(ai[0], ai[1])) p.DeclareWar(ai[0], ai[1]);
        string path = Path.Combine(Path.GetTempPath(), $"pirate_politics_{Guid.NewGuid():N}.json");
        w.SaveToFile(path);
        var l = GameWorld.LoadFromFile(path, Fixtures.Defs.Value);
        File.Delete(path);
        var lp = l.Politics;
        Assert.Equal(p.States.Count, lp.States.Count);
        Assert.Equal(p.Settlements.Count, lp.Settlements.Count);
        foreach (var s in p.Settlements)
        {
            var ls = lp.FindSettlement(s.Id)!;
            Assert.Equal(s.Name, ls.Name);
            Assert.Equal(s.Owner?.Id, ls.Owner?.Id);
            Assert.Equal(s.Population, ls.Population);
            Assert.Equal(s.Buildings.Count, ls.Buildings.Count);
        }
        Assert.True(lp.Diplomacy.AtWar(lp.FindState(ai[0].Id)!, lp.FindState(ai[1].Id)!), "war survived");
        Invariants(l);
        Fixtures.Run(l, 30);
        Invariants(l);
    }

}