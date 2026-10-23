using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Simulation;

public enum PlacementError
{
    None, NotDocked, OutOfBounds, NotLand, Occupied, WrongBiome, NotCoast, DifferentIsland, OutOfReach,
    NotEnoughGold, NotEnoughResources, ForeignTerritory, TooCloseToSettlement,
}

// Building placement, production and ship ↔ building logistics.
public sealed partial class GameWorld
{
    public Building? GetBuilding(int id) => _buildingById.GetValueOrDefault(id);
    public Building? BuildingAt(GridPoint p) => GetBuilding(Map.BuildingAt(p));

    /// <summary>Buildings the docked player ship can build next to / trade with.</summary>
    public IEnumerable<Building> BuildingsInReach()
    {
        if (!Player.IsDocked) yield break;
        var dock = Player.DockTile;
        int region = Map.RegionAt(dock);
        float reach = Config.DockReach;
        foreach (var b in _buildings)
            if (b.Region == region && b.Tile.Distance(dock) <= reach && Politics.IsPlayerOwned(b))
                yield return b;
    }

    public bool IsInReach(GridPoint p) =>
        Player.IsDocked && Map.RegionAt(p) == Map.RegionAt(Player.DockTile) && p.Distance(Player.DockTile) <= Config.DockReach;

    /// <summary>Checks every placement rule without changing anything.</summary>
    public PlacementError CanPlace(BuildingDef def, GridPoint tile, bool checkCost = true)
    {
        if (!Player.IsDocked) return PlacementError.NotDocked;
        if (!Map.InBounds(tile)) return PlacementError.OutOfBounds;
        if (!Map.IsLand(tile)) return PlacementError.NotLand;
        if (Map.BuildingAt(tile) >= 0) return PlacementError.Occupied;
        if (Map.RegionAt(tile) != Map.RegionAt(Player.DockTile)) return PlacementError.DifferentIsland;
        if (tile.Distance(Player.DockTile) > Config.DockReach) return PlacementError.OutOfReach;
        if (!def.AllowsBiome(Map.GetBiome(tile))) return PlacementError.WrongBiome;
        if (def.RequiresCoast && !Map.IsCoast(tile)) return PlacementError.NotCoast;
        var territory = Politics.TerritoryAt(tile);
        if (def.Services.Contains("town_hall"))
        {
            if (territory != null) return PlacementError.ForeignTerritory;
            if (Politics.NearestSettlementDistance(tile) < Config.Politics.MinSettlementDistance) return PlacementError.TooCloseToSettlement;
        }
        else if (territory != null && territory.Owner != Politics.PlayerState) return PlacementError.ForeignTerritory;
        if (checkCost)
        {
            if (Gold < def.GoldCost) return PlacementError.NotEnoughGold;
            if (!Player.Cargo.HasAll(def.BuildCostList)) return PlacementError.NotEnoughResources;
        }
        return PlacementError.None;
    }

    public static string Describe(PlacementError e) => e switch
    {
        PlacementError.None => "OK",
        PlacementError.NotDocked => "Dock your ship first",
        PlacementError.OutOfBounds => "Off the map",
        PlacementError.NotLand => "Must build on land",
        PlacementError.Occupied => "Tile occupied",
        PlacementError.WrongBiome => "Wrong biome",
        PlacementError.NotCoast => "Must be on the coast",
        PlacementError.DifferentIsland => "Not on this island",
        PlacementError.OutOfReach => "Too far from the dock",
        PlacementError.NotEnoughGold => "Not enough gold",
        PlacementError.NotEnoughResources => "Missing materials in hold",
        PlacementError.ForeignTerritory => "Inside another settlement's territory",
        PlacementError.TooCloseToSettlement => "Too close to another settlement",
        _ => e.ToString(),
    };

    /// <summary>Places a building, paying gold and materials from the ship's hold.</summary>
    public Building? PlaceBuilding(BuildingDef def, GridPoint tile)
    {
        var err = CanPlace(def, tile);
        if (err != PlacementError.None)
        {
            Events.Post($"Can't build {def.Name}: {Describe(err)}.", MessageKind.Warning);
            return null;
        }
        Gold -= def.GoldCost;
        Player.Cargo.TryRemoveAll(def.BuildCostList);
        var b = AddBuildingInternal(def, tile);
        Politics.OnPlayerBuilding(b);
        Events.RaiseBuildingPlaced(b);
        Events.Post($"Built a {def.Name}.", MessageKind.Good);
        return b;
    }

    internal Building AddBuildingInternal(BuildingDef def, GridPoint tile, int id = -1)
    {
        if (id < 0) id = NextId();
        else _nextId = Math.Max(_nextId, id + 1);
        var b = new Building(id, def, tile, Map.RegionAt(tile), Defs.ResourceCount);
        _buildings.Add(b);
        _buildingById[id] = b;
        Map.SetBuilding(tile, id);
        return b;
    }

    /// <summary>Removes a building in reach, refunding half its gold and materials (if they fit).</summary>
    public bool Demolish(Building b)
    {
        if (!IsInReach(b.Tile) || !Politics.IsPlayerOwned(b)) return false;
        if (b.IsTownHall)
        {
            Events.Post("A town hall can't be demolished.", MessageKind.Warning);
            return false;
        }
        Politics.Detach(b);
        _buildings.Remove(b);
        _buildingById.Remove(b.Id);
        Map.ClearBuilding(b.Tile);
        Gold += b.Def.GoldCost / 2;
        foreach (var (res, amount) in b.Def.BuildCostList) Player.Cargo.Add(res, amount / 2);
        Events.RaiseBuildingRemoved(b);
        Events.Post($"Demolished the {b.Def.Name}.", MessageKind.Info);
        return true;
    }

    private void UpdateBuildings(float dt)
    {
        foreach (var b in _buildings)
        {
            int gold = b.Update(dt);
            if (gold > 0) Politics.RouteBuildingGold(b, gold);
        }
    }

    // ---------------------------------------------------------------------------
    // Logistics
    // ---------------------------------------------------------------------------

    /// <summary>Loads produced goods from a building's output store into the hold.</summary>
    public int LoadFromBuilding(Building b, int resource, int amount)
    {
        if (!IsInReach(b.Tile)) return 0;
        return b.Output.TransferTo(Player.Cargo, resource, amount);
    }

    /// <summary>Unloads goods from the hold into a building's input store (only resources it uses).</summary>
    public int UnloadToBuilding(Building b, int resource, int amount)
    {
        if (!IsInReach(b.Tile) || !b.Def.AcceptsInput[resource]) return 0;
        return Player.Cargo.TransferTo(b.Input, resource, amount);
    }

    /// <summary>
    /// One-click logistics for everything in reach: first collect all outputs, then deliver
    /// every input a building needs from the hold. Goods produced on this island therefore
    /// flow straight into local consumers. Returns (loaded, unloaded) unit counts.
    /// </summary>
    public (int loaded, int unloaded) AutoTransfer()
    {
        int loaded = 0, unloaded = 0;
        var inReach = BuildingsInReach().OrderBy(b => b.Tile.Distance(Player.DockTile)).ToList();

        foreach (var b in inReach)
            foreach (var (res, amount) in b.Output.NonZero().ToList())
                loaded += b.Output.TransferTo(Player.Cargo, res, amount);

        // Deliver inputs, sharing scarce goods fairly: each pass gives every consumer up to one
        // recipe's worth, repeating until stores are full or the hold runs out.
        // Never auto-unload ammo or repair materials: those are the ship's own supplies.
        int ammo = Defs.ResourceIndex(Config.AmmoResource), repair = Defs.ResourceIndex(Config.RepairResource);
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var b in inReach)
            {
                foreach (var recipe in b.Def.Recipes)
                {
                    foreach (var (res, need) in recipe.InputList)
                    {
                        if (res == ammo || res == repair) continue;
                        int moved = Player.Cargo.TransferTo(b.Input, res, need);
                        if (moved > 0) { unloaded += moved; progress = true; }
                    }
                }
            }
        }

        if (loaded + unloaded > 0)
            Events.Post($"Loaded {loaded}, unloaded {unloaded} goods.", MessageKind.Info);
        return (loaded, unloaded);
    }

    // ---------------------------------------------------------------------------
    // Ship services
    // ---------------------------------------------------------------------------

    /// <summary>Repairs hull using the repair resource (planks by default) from the hold. Requires docking.</summary>
    public int Repair()
    {
        if (!Player.IsDocked) return 0;
        int res = Defs.ResourceIndex(Config.RepairResource);
        int missing = (int)MathF.Ceiling(Player.MaxHull - Player.Hull);
        if (missing <= 0) return 0;
        int planksNeeded = (missing + Config.HullPerPlank - 1) / Config.HullPerPlank;
        int used = Player.Cargo.Remove(res, planksNeeded);
        Player.Hull = Math.Min(Player.MaxHull, Player.Hull + used * Config.HullPerPlank);
        if (used > 0) Events.Post($"Repaired the hull using {used} {Defs.Resources[res].Name}.", MessageKind.Good);
        else Events.Post($"Need {Defs.Resources[res].Name} in the hold to repair.", MessageKind.Warning);
        return used;
    }

    public bool ShipyardInReach() => BuildingsInReach().Any(b => b.HasService("shipyard"));

    public bool CanUpgradeShip(out string reason)
    {
        var next = Player.Class.UpgradeTarget;
        reason = "";
        if (next == null) { reason = "Already the finest ship"; return false; }
        if (!ShipyardInReach()) { reason = "Needs a Shipyard in reach"; return false; }
        if (Gold < Player.Class.UpgradeGold) { reason = "Not enough gold"; return false; }
        if (!Player.Cargo.HasAll(Player.Class.UpgradeCostList)) { reason = "Missing materials in hold"; return false; }
        return true;
    }

    public bool UpgradeShip()
    {
        if (!CanUpgradeShip(out var reason))
        {
            Events.Post($"Can't upgrade: {reason}.", MessageKind.Warning);
            return false;
        }
        var from = Player.Class;
        Gold -= from.UpgradeGold;
        Player.Cargo.TryRemoveAll(from.UpgradeCostList);
        Player.ChangeClass(from.UpgradeTarget!);
        Player.Hull = Player.MaxHull;
        Events.Post($"Your ship has been refitted as a {Player.Class.Name}!", MessageKind.Good);
        return true;
    }
}
