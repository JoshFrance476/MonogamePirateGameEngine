using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;
using static PirateEngine.Tests.TestRunner;

namespace PirateEngine.Tests;

public static class LandUnitTests
{
    /// <summary>Island world with the player docked on the west beach at (60,100).</summary>
    private static GameWorld Docked(bool refuge = false)
    {
        var w = Fixtures.IslandWorld(refuge: refuge);
        w.CommandDock(new GridPoint(60, 100));
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 30), "docked");
        return w;
    }

    private static LandUnit Squad(GameWorld w, string unitId) => w.Player.Troops.First(t => t.Def.Id == unitId);

    [Test]
    public static void StartTroopsRideAboard()
    {
        var w = Fixtures.IslandWorld();
        Assert.Equal(2, w.Player.Troops.Count);
        Assert.Equal(22, w.Player.TroopsAboard, "12 cutlass + 10 musketeers");
        Assert.Equal(24, w.Player.TroopCapacity);
        Assert.Equal(0, w.Units.Count, "nobody ashore yet");
        Assert.True(w.Player.Troops.All(t => t.IsEmbarked && t.Carrier == w.Player));
        Info(string.Join(", ", w.Player.Troops.Select(t => t.Name)));
    }

    [Test]
    public static void CanOnlyLandWhenDocked()
    {
        var w = Fixtures.IslandWorld();
        var crew = Squad(w, "cutlass_crew");
        Assert.False(w.Disembark(crew), "at sea");
        Assert.Equal("Dock at a shore first", w.CannotDisembark(crew));

        w = Docked();
        crew = Squad(w, "cutlass_crew");
        Assert.True(w.Disembark(crew), "docked");
        Assert.False(crew.IsEmbarked);
        Assert.True(w.Units.Contains(crew), "now a physical entity on the map");
        Assert.True(w.Map.IsLand(crew.Tile), "landed on land");
        Assert.True(crew.Tile.Distance(w.Player.DockTile) <= 3, "near the dock");
        Assert.Equal(10, w.Player.TroopsAboard, "musketeers still aboard");
    }

    [Test]
    public static void SquadsMarchOverLandOnlyAndTerrainSlowsThem()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        var start = crew.Position;
        var goal = new GridPoint(125, 100); // across the forest into the grassland
        Assert.Equal(CommandResult.Ok, w.CommandUnitMove(crew, goal));
        float t = 0;
        bool arrived = Fixtures.RunUntil(w, () => !crew.IsMoving, 120, () =>
        {
            t += GameWorld.FixedStep;
            Assert.True(w.Map.IsLand(crew.Tile), $"squad stepped into water at {crew.Position}");
        });
        Assert.True(arrived, "arrived");
        Assert.Equal(goal, crew.Tile);
        float straight = Vector2.Distance(start, goal.Center) / crew.Def.Speed;
        Info($"{Vector2.Distance(start, goal.Center):0} tiles in {t:0.0}s (open-ground time would be {straight:0.0}s)");
        Assert.True(t > straight * 1.15f, "forest should slow the march");
    }

    [Test]
    public static void SquadsCannotWalkToAnotherIsland()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        Assert.Equal(CommandResult.Unreachable, w.CommandUnitMove(crew, new GridPoint(100, 25)), "the islet is across water");
        // Clicking the sea right off the beach goes to the nearest land instead.
        Assert.Equal(CommandResult.Ok, w.CommandUnitMove(crew, new GridPoint(55, 110)));
        Fixtures.RunUntil(w, () => !crew.IsMoving, 30);
        Assert.True(w.Map.IsLand(crew.Tile));
    }

    [Test]
    public static void SquadsWalkBackAndBoard()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        w.CommandUnitMove(crew, new GridPoint(72, 100));
        Fixtures.RunUntil(w, () => !crew.IsMoving, 30);
        Assert.True(w.CommandEmbark(crew), "embark order accepted");
        Assert.True(Fixtures.RunUntil(w, () => crew.IsEmbarked, 40), "walked back and boarded");
        Assert.False(w.Units.Contains(crew), "off the map");
        Assert.Equal(22, w.Player.TroopsAboard);
    }

    [Test]
    public static void TroopCapacityIsEnforced()
    {
        var w = Docked();
        // 22/24 aboard: a 12-man squad ashore can't board.
        var extra = w.CreatePlayerUnitAshore("cutlass_crew", new GridPoint(61, 100));
        Assert.False(w.CommandEmbark(extra), "no room");
        Assert.True(w.CannotEmbark(extra)!.Contains("room"));
        // Make room and it can.
        w.Disembark(Squad(w, "cutlass_crew"));
        Assert.True(w.CommandEmbark(extra), "room now");
        Assert.True(Fixtures.RunUntil(w, () => extra.IsEmbarked, 10));
    }

    [Test]
    public static void BoardingCancelledIfShipLeaves()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        w.CommandUnitMove(crew, new GridPoint(75, 100));
        Fixtures.RunUntil(w, () => !crew.IsMoving, 30);
        w.CommandEmbark(crew);
        w.CommandMoveTo(new GridPoint(40, 100)); // sail away
        Fixtures.Run(w, 3);
        Assert.True(crew.PendingEmbark == null && !crew.IsEmbarked, "stranded, not teleported aboard");
    }

    [Test]
    public static void MeleeSquadsFightToADecision()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        var enemy = w.SpawnAiUnit("cutlass_crew", new GridPoint(66, 100));
        int gold = w.Gold;
        w.CommandUnitAttack(crew, enemy);
        bool decided = Fixtures.RunUntil(w, () => crew.IsDead || enemy.IsDead, 60);
        Assert.True(decided, "one side wiped out");
        var winner = crew.IsDead ? enemy : crew;
        Info($"winner: {winner.Name} with {winner.Members}/{winner.Def.Members} men, t={w.Time:0.0}s");
        Assert.True(winner.Members < winner.Def.Members, "the winner took casualties too");
        if (enemy.IsDead) Assert.True(w.Gold > gold, "looted the garrison");
    }

    [Test]
    public static void CasualtiesReduceHeadcountAndDamage()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        Assert.Equal(12, crew.Members);
        crew.Hp -= 25; // 2.5 men's worth
        Assert.Equal(10, crew.Members, "ceil(95/10)");
    }

    [Test]
    public static void MusketeersShootShipsInRange()
    {
        var w = Docked();
        var muskets = Squad(w, "musketeers");
        w.Disembark(muskets);
        var cutter = w.SpawnAiShip("navy_cutter", ShipRole.Hunter, new GridPoint(55, 96)); // just offshore
        float hull = cutter.Hull;
        int musketHits = 0;
        w.Events.ShipDamaged += (v, a, d) => { if (v == cutter && a == muskets) musketHits++; };
        Fixtures.RunUntil(w, () => musketHits >= 3 || cutter.IsSunk, 20);
        Assert.True(musketHits > 0, "musket balls struck the ship");
        Info($"{musketHits} musket hits; cutter hull {hull} -> {cutter.Hull}");
    }

    [Test]
    public static void MeleeSquadsCannotAttackShips()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        var cutter = w.SpawnAiShip("navy_cutter", ShipRole.Hunter, new GridPoint(57, 98));
        Assert.Equal(CommandResult.Invalid, w.CommandUnitAttack(crew, cutter));
    }

    [Test]
    public static void ShoreGarrisonAndShipExchangeFire()
    {
        // Navy musketeers on the beach fire on the docked player ship; the ship's guns answer
        // with cannon fire that does extra damage to troops.
        var w = Docked();
        var garrison = w.SpawnAiUnit("musketeers", new GridPoint(61, 104));
        float shipHull = w.Player.Hull, troopsHp = garrison.Hp;
        int ballsOnTroops = 0;
        w.Events.UnitDamaged += (v, a, d) => { if (v == garrison && a == w.Player) ballsOnTroops++; };
        Fixtures.Run(w, 20);
        Assert.True(w.Player.Hull < shipHull, "garrison musket fire hit the ship");
        Assert.True(ballsOnTroops > 0 || garrison.IsDead, "ship's cannon hit the troops");
        Info($"ship hull {shipHull} -> {w.Player.Hull}; garrison hp {troopsHp} -> {Math.Max(0, garrison.Hp)} ({ballsOnTroops} cannon hits)");
    }

    [Test]
    public static void GarrisonGuardsItsPostAndEngagesIntruders()
    {
        var w = Docked();
        var crew = Squad(w, "cutlass_crew");
        w.Disembark(crew);
        var garrison = w.SpawnAiUnit("cutlass_crew", new GridPoint(85, 100));
        Fixtures.Run(w, 6);
        Assert.Equal("Guard", garrison.Brain!.State, "player squad is far away");
        w.CommandUnitMove(crew, new GridPoint(80, 100));
        Assert.True(Fixtures.RunUntil(w, () => garrison.Brain.State == "Engage", 30), "garrison engages an intruder");
        // Pull back out of the leash: the garrison should give up and go home.
        w.CommandUnitMove(crew, new GridPoint(62, 112));
        Fixtures.RunUntil(w, () => garrison.Brain.State != "Engage" || crew.IsDead, 40);
        Info($"garrison state after retreat: {garrison.Brain.State}, crew alive: {!crew.IsDead}");
    }

    [Test]
    public static void RecruitAtBarracks()
    {
        var w = Docked();
        var defs = w.Defs;
        var muskets = defs.Unit("musketeers");
        Assert.Equal("Needs a Barracks in reach", w.CannotRecruit(muskets));
        w.Player.Cargo.Clear();
        w.Player.Cargo.Add(defs.ResourceIndex("wood"), 10);
        w.Player.Cargo.Add(defs.ResourceIndex("stone"), 6);
        var spot = w.Map.FindNearest(w.Player.DockTile, 6, p => w.CanPlace(defs.Building("barracks"), p) == PlacementError.None);
        Assert.NotNull(w.PlaceBuilding(defs.Building("barracks"), spot));
        w.Player.Cargo.Add(defs.ResourceIndex("rations"), 8);
        w.Player.Cargo.Add(defs.ResourceIndex("iron"), 4);
        w.GrantGold(1000);
        Assert.Equal("Not enough room aboard", w.CannotRecruit(muskets), "22/24 aboard");
        w.DisembarkAll();
        int gold = w.Gold;
        var recruit = Assert.NotNull(w.RecruitUnit(muskets));
        Assert.Equal(gold - muskets.RecruitGold, w.Gold);
        Assert.True(recruit.IsEmbarked && recruit.Members == 10);
        Assert.Equal("2nd Musketeers", recruit.Name);
    }

    [Test]
    public static void TroopsAboardGoDownWithTheShip()
    {
        var w = Fixtures.IslandWorld();
        var crew = Squad(w, "cutlass_crew");
        var attacker = w.SpawnAiShip("navy_cutter", ShipRole.Hunter, new GridPoint(20, 20));
        w.Combat.ApplyDamage(w.Player, attacker, 10_000);
        Assert.True(crew.IsDead, "lost with the ship");
        Assert.Equal(0, w.Player.Troops.Count, "the new ship starts without troops");
    }

    [Test]
    public static void SaveLoadKeepsSquadsAshoreAndAboard()
    {
        var gen = new WorldGenSettings { Width = 256, Height = 256, Seed = 99 };
        var w = GameWorld.CreateNew(gen, Fixtures.Defs.Value);
        w.Spawner.Enabled = false;
        var land = w.Map.FindNearest(w.Player.Tile, 3, p => w.Map.IsLand(p));
        w.CommandDock(land);
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 20), "docked");
        var crew = w.Player.Troops[0];
        w.Disembark(crew);
        crew.Hp -= 17;
        var garrisonTile = w.Map.FindNearest(w.Player.DockTile, 12, p => w.Map.IsLand(p) && p.Distance(w.Player.DockTile) > 9);
        var garrison = w.SpawnAiUnit("musketeers", garrisonTile);
        Fixtures.Run(w, 1);

        string path = Path.Combine(Path.GetTempPath(), $"pirate_units_{Guid.NewGuid():N}.json");
        w.SaveToFile(path);
        var l = GameWorld.LoadFromFile(path, Fixtures.Defs.Value);
        File.Delete(path);

        Assert.Equal(w.Player.Troops.Count, l.Player.Troops.Count, "aboard");
        Assert.Equal(w.Units.Count, l.Units.Count, "ashore");
        var lc = l.FindUnit(crew.Id)!;
        Assert.Equal(crew.Name, lc.Name);
        Assert.Equal(crew.Hp, lc.Hp);
        Assert.Equal(crew.Position, lc.Position);
        var lg = l.FindUnit(garrison.Id)!;
        Assert.Equal(Faction.Navy, lg.Faction);
        Assert.True(lg.Brain is Core.AI.GarrisonBrain);
        Fixtures.Run(l, 3);
    }

    [Test]
    public static void GarrisonsSpawnAshoreOutsideTheHarbour()
    {
        using var w = GameWorld.CreateWithMap(Fixtures.BigMap.Value, DefinitionDatabase.Load(), Fixtures.BigSettings);
        int spawned = 0;
        for (int i = 0; i < 40; i++) if (w.Spawner.TrySpawnGarrison() != null) spawned++;
        Info($"{spawned}/40 garrison spawn attempts found a site");
        Assert.True(spawned > 0, "some garrisons spawned");
        foreach (var u in w.Units)
        {
            Assert.True(w.Map.IsLand(u.Tile), "on land");
            Assert.True(u.Tile.Distance(w.Map.StartTile) >= w.Config.SafeHarbourRadius, "outside the harbour");
            Assert.Equal(Faction.Navy, u.Faction);
        }
    }

    [Test]
    public static void LandCampaignSoak()
    {
        // Full-size map: land troops, fight garrisons with random orders for five minutes,
        // re-board and re-land, with invariants checked every step.
        using var w = GameWorld.CreateWithMap(Fixtures.BigMap.Value, DefinitionDatabase.Load(), Fixtures.BigSettings);
        w.AsyncPathfinding = true;
        w.Config.SafeHarbourRadius = 0;
        w.Config.UnitSpawnMinDistance = 6;
        w.Config.UnitSpawnMaxDistance = 25;
        w.Config.GarrisonBuildingClearance = 0;
        var land = w.Map.FindNearest(w.Player.Tile, 6, p => w.Map.IsLand(p));
        w.CommandDock(land);
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 30), "docked");
        Assert.Equal(2, w.DisembarkAll());
        var rng = new Rng(9);
        int steps = 0, orders = 0, kills = 0, losses = 0, boardings = 0, maxGarrisons = 0;
        w.Events.UnitKilled += (v, k) => { if (v.Faction == Faction.Player) losses++; else kills++; };
        w.Events.UnitEmbarked += _ => boardings++;
        while (w.Time < 300)
        {
            if (steps % 150 == 0)
            {
                foreach (var u in w.Units.Where(u => u.Faction == Faction.Player && !u.IsDead).ToList())
                {
                    orders++;
                    var enemy = UnitTestsHelpers.NearestEnemy(w, u);
                    switch (rng.Next(6))
                    {
                        case 0: w.CommandUnitMove(u, new GridPoint(u.Tile.X + rng.Next(-12, 13), u.Tile.Y + rng.Next(-12, 13))); break;
                        case 1: w.CommandEmbark(u); break;
                        default: if (enemy != null) w.CommandUnitAttack(u, enemy); break; // mostly fight
                    }
                }
                if (w.Player.Troops.Count > 0 && rng.Chance(0.5f)) w.DisembarkAll();
                if (!w.Units.Any(u => u.Faction == Faction.Player) && w.Player.Troops.Count == 0)
                {
                    w.CreatePlayerUnitAshore("cutlass_crew", w.Player.DockTile); // all lost: send reinforcements
                }
            }
            w.Update(GameWorld.FixedStep);
            steps++;
            maxGarrisons = Math.Max(maxGarrisons, w.Units.Count(u => u.Faction == Faction.Navy));
            foreach (var u in w.Units)
            {
                Assert.True(w.Map.IsLand(u.Tile), $"{u} in the water");
                Assert.False(u.IsEmbarked, "embarked squad still on the map");
                Assert.True(u.Hp <= u.MaxHp, "hp over max");
            }
            foreach (var t in w.Player.Troops) Assert.True(t.Carrier == w.Player && !w.Units.Contains(t), "troop bookkeeping");
            Assert.True(w.Player.TroopsAboard <= w.Player.TroopCapacity, "over capacity");
        }
        Info($"{steps} steps: {orders} squad orders, {kills} garrisons defeated, {losses} squads lost, {boardings} boardings, peak {maxGarrisons} garrisons");
        Assert.True(kills > 0 && losses + kills > 1, "fighting happened on both sides");
    }
}

internal static class UnitTestsHelpers
{
    public static LandUnit? NearestEnemy(GameWorld w, LandUnit u) =>
        w.Units.Where(o => !o.IsDead && o.Faction == Faction.Navy && w.Map.RegionAt(o.Tile) == w.Map.RegionAt(u.Tile))
               .OrderBy(o => o.DistanceTo(u)).FirstOrDefault();
}
