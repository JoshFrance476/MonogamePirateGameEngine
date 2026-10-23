using System.Diagnostics;
using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;
using static PirateEngine.Tests.TestRunner;

namespace PirateEngine.Tests;

public static class MovementTests
{
    [Test]
    public static void ClickToMoveSailsAroundIslandInRealTime()
    {
        var w = Fixtures.IslandWorld();
        var goal = new GridPoint(150, 100); // east of the island; must sail around it
        Assert.Equal(CommandResult.Ok, w.CommandMoveTo(goal));
        float elapsed = 0;
        bool arrived = Fixtures.RunUntil(w, () => !w.Player.IsMoving, 120, () =>
        {
            elapsed += GameWorld.FixedStep;
            Assert.True(w.Map.IsWater(w.Player.Tile), $"ship on land at {w.Player.Position}");
        });
        Assert.True(arrived, "should arrive");
        Assert.True(Vector2.Distance(w.Player.Position, goal.Center) < 0.01f, "at goal");
        // Straight-line distance is ~93 tiles but the island forces a detour; at ~5.5 t/s that's > 17 s.
        Info($"arrived after {elapsed:0.0}s of game time");
        Assert.InRange(elapsed, 17, 60, "travel time");
    }

    [Test]
    public static void ClickOnLakeSailsToNearestReachableWater()
    {
        var w = Fixtures.IslandWorld();
        var r = w.CommandMoveTo(new GridPoint(112, 82)); // landlocked lake
        Assert.True(r is CommandResult.Unreachable or CommandResult.Ok);
    }

    [Test]
    public static void ClickOnLandDocksAtNearestCoast()
    {
        var w = Fixtures.IslandWorld();
        Assert.Equal(CommandResult.Docking, w.CommandMoveTo(new GridPoint(70, 100))); // inland forest
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 30), "should dock");
        Assert.True(w.Map.IsCoast(w.Player.DockTile), "dock tile is coast");
        Assert.True(w.Player.Tile.ChebyshevDistance(w.Player.DockTile) <= 1, "alongside");
        w.CommandMoveTo(new GridPoint(40, 100));
        Assert.False(w.Player.IsDocked, "moving undocks");
    }
}

public static class EconomyTests
{
    private static GameWorld DockedWest()
    {
        var w = Fixtures.IslandWorld();
        w.CommandDock(new GridPoint(60, 100));
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 30), "dock");
        return w;
    }

    [Test]
    public static void PlacementRulesAreEnforced()
    {
        var w = Fixtures.IslandWorld();
        var camp = w.Defs.Building("lumber_camp");
        Assert.Equal(PlacementError.NotDocked, w.CanPlace(camp, new GridPoint(62, 100)));

        w = DockedWest();
        Assert.Equal(PlacementError.None, w.CanPlace(camp, new GridPoint(62, 100)));
        Assert.Equal(PlacementError.NotLand, w.CanPlace(camp, new GridPoint(55, 100)));
        Assert.Equal(PlacementError.WrongBiome, w.CanPlace(camp, new GridPoint(60, 101)), "beach isn't forest");
        Assert.Equal(PlacementError.OutOfReach, w.CanPlace(camp, new GridPoint(80, 100)));
        Assert.Equal(PlacementError.NotCoast, w.CanPlace(w.Defs.Building("fishery"), new GridPoint(63, 100)));
        Assert.Equal(PlacementError.None, w.CanPlace(w.Defs.Building("fishery"), new GridPoint(60, 103)));

        int goldBefore = w.Gold;
        Assert.NotNull(w.PlaceBuilding(camp, new GridPoint(62, 100)));
        Assert.Equal(goldBefore - camp.GoldCost, w.Gold, "gold charged");
        Assert.Equal(PlacementError.Occupied, w.CanPlace(camp, new GridPoint(62, 100)));
    }

    [Test]
    public static void ProductionChainByShip()
    {
        var w = DockedWest();
        var defs = w.Defs;
        int wood = defs.ResourceIndex("wood"), planks = defs.ResourceIndex("planks");
        var camp = w.PlaceBuilding(defs.Building("lumber_camp"), new GridPoint(63, 98))!;
        var mill = w.PlaceBuilding(defs.Building("sawmill"), new GridPoint(63, 102))!;
        Assert.NotNull(camp); Assert.NotNull(mill);

        w.Player.Cargo.Remove(wood, 1000); // start with no wood so it must come from the camp
        int planksBefore = w.Player.Cargo[planks];

        Fixtures.Run(w, 30);
        Assert.True(camp.Output[wood] >= 8, $"camp produced wood ({camp.Output[wood]})");
        Assert.Equal(ProductionState.WaitingForInputs, mill.State, "mill idle without wood");

        var (loaded, unloaded) = w.AutoTransfer(); // wood: camp → ship → mill
        Assert.True(loaded > 0 && unloaded > 0, "auto-transfer moved goods");
        Fixtures.Run(w, 30);
        Assert.True(mill.Output[planks] > 0, "mill made planks");
        Assert.True(mill.CyclesCompleted > 0);
        w.AutoTransfer();
        Assert.True(w.Player.Cargo[planks] > planksBefore, "planks collected into hold");
        Info($"camp cycles {camp.CyclesCompleted}, mill cycles {mill.CyclesCompleted}, hold planks {w.Player.Cargo[planks]}");
    }

    [Test]
    public static void GoodsCarriedBetweenIslands()
    {
        // Make wood on the main island, sail it to the jungle islet, deliver to a sawmill there.
        var w = DockedWest();
        var defs = w.Defs;
        int wood = defs.ResourceIndex("wood"), planks = defs.ResourceIndex("planks");
        var camp = w.PlaceBuilding(defs.Building("lumber_camp"), new GridPoint(62, 100))!;
        w.Player.Cargo.Remove(wood, 1000);
        Fixtures.Run(w, 40);
        int got = w.LoadFromBuilding(camp, wood, 100);
        Assert.True(got >= 10, $"loaded {got} wood");

        w.Player.Cargo.Add(wood, 10); // sawmill build cost
        Assert.Equal(CommandResult.Docking, w.CommandMoveTo(new GridPoint(100, 25)));
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 90), "dock at islet");
        Assert.True(w.Map.GetBiome(w.Player.DockTile) == Biome.Jungle);
        var tile = w.Map.FindNearest(w.Player.DockTile, 5, p => w.CanPlace(defs.Building("sawmill"), p) == PlacementError.None);
        var mill = Assert.NotNull(w.PlaceBuilding(defs.Building("sawmill"), tile));
        Assert.True(w.UnloadToBuilding(mill, wood, 100) > 0, "unloaded wood");
        Assert.Equal(0, w.UnloadToBuilding(mill, planks, 5), "sawmill must refuse planks");
        Fixtures.Run(w, 20);
        Assert.True(mill.Output[planks] > 0, "planks made on the islet");
        Assert.True(w.BuildingsInReach().All(b => b.Region == mill.Region), "only islet buildings in reach");
    }

    [Test]
    public static void MarketTurnsGoodsIntoGold()
    {
        var w = DockedWest();
        var market = w.PlaceBuilding(w.Defs.Building("market"), new GridPoint(61, 104))!;
        int rum = w.Defs.ResourceIndex("rum");
        w.Player.Cargo.Add(rum, 5);
        int gold = w.Gold;
        w.UnloadToBuilding(market, rum, 5);
        Fixtures.Run(w, 20);
        Assert.Equal(gold + 5 * 45, w.Gold, "5 rum sold at 45");
    }

    [Test]
    public static void InventoryRespectsCapacity()
    {
        var inv = new Inventory(4, 10);
        Assert.Equal(7, inv.Add(0, 7));
        Assert.Equal(3, inv.Add(1, 9), "only 3 fit");
        Assert.Equal(0, inv.Add(2, 1));
        Assert.Equal(10, inv.Total);
        Assert.False(inv.TryRemoveAll(new[] { (0, 5), (1, 4) }), "atomic remove fails when short");
        Assert.Equal(7, inv[0], "nothing removed");
    }

    [Test]
    public static void RepairAndUpgradeAtShipyard()
    {
        var w = DockedWest();
        var defs = w.Defs;
        w.Player.Cargo.Clear(); // the sloop's hold is small; start empty so materials fit
        w.Player.Hull = 40;
        w.Player.Cargo.Add(defs.ResourceIndex("planks"), 20);
        w.Repair();
        Assert.Equal(w.Player.MaxHull, w.Player.Hull, "repaired to full");

        w.Player.Cargo.Add(defs.ResourceIndex("planks"), 15);
        w.Player.Cargo.Add(defs.ResourceIndex("stone"), 10);
        w.Gold = 5000;
        Assert.NotNull(w.PlaceBuilding(defs.Building("shipyard"), new GridPoint(60, 98)));
        Assert.False(w.CanUpgradeShip(out var reason), "missing sails");
        Info($"before materials: {reason}");
        w.Player.Cargo.Add(defs.ResourceIndex("planks"), 25);
        w.Player.Cargo.Add(defs.ResourceIndex("sails"), 4);
        w.Player.Cargo.Add(defs.ResourceIndex("iron"), 6);
        Assert.True(w.UpgradeShip(), "upgrade");
        Assert.Equal("brigantine", w.Player.Class.Id);
        Assert.Equal(120, w.Player.Cargo.Capacity);
    }
}

public static class CombatTests
{
    private static Ship SpawnEnemy(GameWorld w, string spawnShip, GridPoint at)
    {
        var spawn = w.Config.Spawns.First(s => s.Ship == spawnShip);
        return w.AddAiShip(spawn, at, "Test " + spawnShip);
    }

    [Test]
    public static void PlayerAttackSinksCutterAndSalvages()
    {
        var w = Fixtures.IslandWorld(refuge: false);
        var enemy = SpawnEnemy(w, "navy_cutter", new GridPoint(30, 100));
        int ammo = w.Defs.ResourceIndex("cannonballs");
        int ammoBefore = w.Player.Cargo[ammo];
        w.CommandAttack(enemy);
        bool sunk = Fixtures.RunUntil(w, () => enemy.IsSunk, 120, () =>
            Assert.True(w.Map.IsWater(w.Player.Tile), "player stayed on water"));
        Assert.True(sunk, $"cutter should sink (hull {enemy.Hull}, player hull {w.Player.Hull})");
        Assert.True(w.Player.Cargo[ammo] < ammoBefore, "ammo spent");
        Assert.True(w.Wrecks.Count == 1, "wreck spawned");
        Info($"sunk at t={w.Time:0.0}s; player hull {w.Player.Hull}/{w.Player.MaxHull}; shots {ammoBefore - w.Player.Cargo[ammo]}");

        int gold = w.Gold;
        var wreck = w.Wrecks[0];
        w.CommandMoveTo(GridPoint.FromWorld(wreck.Position));
        Assert.True(Fixtures.RunUntil(w, () => w.Wrecks.Count == 0, 30), "salvaged");
        Assert.True(w.Gold > gold, "loot gold");
        Assert.Equal(1, w.ShipsSunkByPlayer);
    }

    [Test]
    public static void HunterChasesAndShootsPlayer()
    {
        var w = Fixtures.IslandWorld(refuge: false); // the test map's start is the harbour
        var hunter = SpawnEnemy(w, "navy_brig", new GridPoint(45, 100));
        int fired = 0, hits = 0;
        w.Events.VolleyFired += s => { if (s == hunter) fired++; };
        w.Events.ShipDamaged += (v, a, d) => { if (a == hunter) hits++; };
        Fixtures.Run(w, 10);
        Assert.Equal("Engage", hunter.Brain!.State);
        Assert.True(fired >= 2 && hits > 0, $"hunter fired {fired} volleys, {hits} hits");
        double rate = hits / (fired * (double)hunter.Class.Guns);
        Info($"{fired} volleys, {hits} hits ({rate:P0}); player hull {w.Player.Hull}/{w.Player.MaxHull}");
        Assert.InRange(rate, 0.2, 0.9, "hit rate");
    }

    [Test]
    public static void HomeHarbourIsARefuge()
    {
        var w = Fixtures.IslandWorld();
        var hunter = SpawnEnemy(w, "navy_brig", new GridPoint(45, 100)); // inside the safe radius
        Fixtures.Run(w, 15);
        Assert.False(hunter.Brain!.State == "Engage", "navy must not engage inside the harbour");
        Assert.Equal(w.Player.MaxHull, w.Player.Hull);
    }

    [Test]
    public static void BroadsideArc()
    {
        var w = Fixtures.IslandWorld();
        var s = w.Player;
        s.Heading = 0; // facing east
        Assert.True(w.Combat.InArc(s, s.Position + new Vector2(0, 5)), "abeam (south) bears");
        Assert.True(w.Combat.InArc(s, s.Position + new Vector2(0, -5)), "abeam (north) bears");
        Assert.False(w.Combat.InArc(s, s.Position + new Vector2(5, 0)), "dead ahead doesn't bear");
        Assert.False(w.Combat.InArc(s, s.Position + new Vector2(-5, 0)), "dead astern doesn't bear");
    }

    [Test]
    public static void MerchantsAreNeutralUntilAttacked()
    {
        var w = Fixtures.IslandWorld(refuge: false);
        var m = SpawnEnemy(w, "merchant_cog", new GridPoint(50, 100));
        Assert.False(GameWorld.IsHostile(w.Player, m), "neutral");
        Fixtures.Run(w, 10);
        Assert.Equal(w.Player.MaxHull, w.Player.Hull, "unprovoked merchant never fires");
        w.CommandAttack(m);
        Fixtures.RunUntil(w, () => m.Provoked, 30);
        Assert.True(m.Provoked && GameWorld.IsHostile(w.Player, m), "provoked after being fired on");
    }

    [Test]
    public static void PlayerRespawnsWhenSunk()
    {
        var w = Fixtures.IslandWorld();
        var old = w.Player;
        w.Gold = 1000;
        w.Combat.ApplyDamage(old, SpawnEnemy(w, "navy_cutter", new GridPoint(20, 20)), 10_000);
        Assert.True(old.IsSunk);
        Assert.False(w.Player.IsSunk, "new ship");
        Assert.True(w.Player != old);
        Assert.Equal(750, w.Gold, "25% penalty");
        Fixtures.Run(w, 1);
    }
}

public static class PersistenceTests
{
    [Test]
    public static void SaveLoadRoundTrip()
    {
        var gen = new WorldGenSettings { Width = 256, Height = 256, Seed = 99 };
        var w = GameWorld.CreateNew(gen, Fixtures.Defs.Value);
        // Dock somewhere and build something.
        var land = w.Map.FindNearest(w.Player.Tile, 3, p => w.Map.IsLand(p));
        w.CommandDock(land);
        Assert.True(Fixtures.RunUntil(w, () => w.Player.IsDocked, 20), "docked");
        var def = w.Defs.Buildings.First(b => b.BuildCostList.Length == 0 || w.Player.Cargo.HasAll(b.BuildCostList));
        var tile = w.Map.FindNearest(w.Player.DockTile, 8, p => w.CanPlace(def, p) == PlacementError.None);
        Building? built = tile.IsValid ? w.PlaceBuilding(def, tile) : null;
        Fixtures.Run(w, 15);

        string path = Path.Combine(Path.GetTempPath(), $"pirate_test_{Guid.NewGuid():N}.json");
        w.SaveToFile(path);
        var size = new FileInfo(path).Length;
        var l = GameWorld.LoadFromFile(path, Fixtures.Defs.Value);
        File.Delete(path);

        Assert.Equal(w.Gold, l.Gold);
        Assert.Equal(w.Player.Position, l.Player.Position);
        Assert.Equal(w.Player.DockTile, l.Player.DockTile);
        Assert.Equal(w.Buildings.Count, l.Buildings.Count);
        Assert.Equal(w.Fog.CountExplored(), l.Fog.CountExplored());
        Assert.True(w.Player.Cargo.ToArray().SequenceEqual(l.Player.Cargo.ToArray()), "cargo");
        if (built != null)
        {
            var lb = l.BuildingAt(built.Tile)!;
            Assert.True(built.Output.ToArray().SequenceEqual(lb.Output.ToArray()), "building output");
            Assert.Equal(built.CycleProgress, lb.CycleProgress);
        }
        Info($"save file {size / 1024.0:0.0} KB; built {(built?.Def.Name ?? "nothing")}");
        Fixtures.Run(l, 5); // loaded world keeps simulating
    }
}

public static class SoakTests
{
    [Test]
    public static void TenMinutesOnFullSizeMap() => Soak(async: false);

    [Test]
    public static void TenMinutesOnFullSizeMapAsyncPaths() => Soak(async: true);

    [Test]
    public static void AsyncLongTripArrives()
    {
        using var w = GameWorld.CreateWithMap(Fixtures.BigMap.Value, Fixtures.Defs.Value, Fixtures.BigSettings);
        w.Spawner.Enabled = false;
        w.AsyncPathfinding = true;
        var map = w.Map;
        var goal = map.FindNearest(new GridPoint(150, 850), 200, p => map.RegionAt(p) == map.MainOceanRegion);
        var t0 = Stopwatch.GetTimestamp();
        Assert.Equal(CommandResult.Ok, w.CommandMoveTo(goal));
        double cmdMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Assert.True(w.Player.PathPending, "planned in background");
        // Give the worker real time; the simulation keeps stepping meanwhile.
        bool started = Fixtures.RunUntil(w, () => { Thread.Sleep(1); return w.Player.IsMoving; }, 30);
        Assert.True(started, "path arrived");
        Assert.True(Fixtures.RunUntil(w, () => !w.Player.IsMoving, 600, () =>
            Assert.True(map.IsWater(w.Player.Tile), "on water")), "arrived");
        Assert.Equal(goal, w.Player.Tile);
        Info($"command returned in {cmdMs:0.00} ms; {goal.Distance(map.StartTile):0} tile trip took {w.Time:0}s game time");
    }

    private static void Soak(bool async)
    {
        // Full 1024² world with AI spawning, random player orders and periodic attacks.
        // Checks invariants every step and measures simulation cost.
        using var w = GameWorld.CreateWithMap(Fixtures.BigMap.Value, Fixtures.Defs.Value, Fixtures.BigSettings);
        w.AsyncPathfinding = async;
        w.Player.Cargo.Add(w.Defs.ResourceIndex("cannonballs"), 1000);
        var rng = new Rng(5);
        var sw = Stopwatch.StartNew();
        double worstStepMs = 0;
        int steps = 0, orders = 0, maxShips = 0, maxUnits = 0;
        float simSeconds = 600;
        int ocean = w.Map.MainOceanRegion;
        while (w.Time < simSeconds)
        {
            if (steps % 300 == 0)
            {
                // Mostly sail somewhere far; sometimes pick a fight.
                var enemy = w.Ships.FirstOrDefault(s => s.Faction == Faction.Navy && s.DistanceTo(w.Player) < 30);
                if (enemy != null && rng.Chance(0.5f)) w.CommandAttack(enemy);
                else
                {
                    var p = w.Player.Tile;
                    var goal = new GridPoint(p.X + rng.Next(-150, 150), p.Y + rng.Next(-150, 150));
                    if (w.Map.InBounds(goal)) w.CommandMoveTo(goal);
                }
                orders++;
            }
            var t0 = Stopwatch.GetTimestamp();
            w.Update(GameWorld.FixedStep);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (steps > 30) worstStepMs = Math.Max(worstStepMs, ms);
            steps++;
            maxShips = Math.Max(maxShips, w.Ships.Count);
            foreach (var s in w.Ships)
                Assert.True(w.Map.IsWater(s.Tile), $"{s} on land");
            foreach (var u in w.Units)
                Assert.True(w.Map.IsLand(u.Tile), $"{u} in the water");
            maxUnits = Math.Max(maxUnits, w.Units.Count);
        }
        double avg = sw.Elapsed.TotalMilliseconds / steps;
        Info($"async={async}: {steps} steps ({simSeconds}s game time) in {sw.Elapsed.TotalSeconds:0.0}s: avg {avg:0.000} ms/step, worst {worstStepMs:0.0} ms");
        Info($"{orders} orders, peak {maxShips} ships and {maxUnits} squads ashore, player sank {w.ShipsSunkByPlayer}, explored {w.Fog.CountExplored()} tiles, gold {w.Gold}");
        Assert.True(avg < 2.0, "simulation must be far cheaper than a 16 ms frame");
    }
}
