using PirateEngine.Core.AI;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Simulation;

public enum CommandResult { Ok, Docking, AlreadyThere, Unreachable, Invalid }

// Player command API — the only way the input layer mutates the simulation.
public sealed partial class GameWorld
{
    /// <summary>
    /// Click-to-move. Water → sail there. Land → sail to the nearest reachable coast and dock.
    /// Clicking water you can't reach (e.g. a landlocked lake) sails to the closest reachable point.
    /// </summary>
    public CommandResult CommandMoveTo(GridPoint tile)
    {
        if (Player.IsSunk || !Map.InBounds(tile)) return CommandResult.Invalid;
        if (Map.IsLand(tile)) return CommandDock(tile);

        Player.Target = null;
        Player.PendingDock = GridPoint.Invalid;
        int region = Map.RegionAt(Player.Tile);
        var goal = Map.RegionAt(tile) == region ? tile : NearestWaterInRegion(tile, region, 16);
        if (!goal.IsValid)
        {
            Events.Post("No route to that water.", MessageKind.Warning);
            return CommandResult.Unreachable;
        }
        if (!RequestPath(Player, goal, 2_000_000, RouteFailedMessage))
            return CommandResult.Unreachable;
        Undock(Player);
        return Player.IsMoving || Player.PathPending ? CommandResult.Ok : CommandResult.AlreadyThere;
    }

    /// <summary>Sail to the coast nearest <paramref name="land"/> and dock there.</summary>
    public CommandResult CommandDock(GridPoint land)
    {
        if (Player.IsSunk || !Map.IsLand(land)) return CommandResult.Invalid;
        int seaRegion = Map.RegionAt(Player.Tile);
        int landRegion = Map.RegionAt(land);

        bool CoastOnOurSea(GridPoint p) =>
            Map.IsLand(p) && Map.Region[Map.Index(p)] == landRegion && AdjacentWater(p, seaRegion).IsValid;

        var coast = Map.FindNearest(land, 24, CoastOnOurSea);
        if (!coast.IsValid)
        {
            Events.Post("Can't reach that shore from these waters.", MessageKind.Warning);
            return CommandResult.Unreachable;
        }

        var berth = AdjacentWater(coast, seaRegion, prefer: Player.Tile);
        Player.Target = null;
        Player.PendingDock = coast;
        if (!RequestPath(Player, berth, 2_000_000, RouteFailedMessage))
        {
            Player.PendingDock = GridPoint.Invalid;
            return CommandResult.Unreachable;
        }
        Undock(Player);
        UpdateDocking(); // dock immediately if we're already alongside
        return CommandResult.Docking;
    }

    /// <summary>
    /// Engage a ship, or bombard troops on the shore: the player's ship manoeuvres to bring its
    /// broadside to bear and fires automatically.
    /// </summary>
    public CommandResult CommandAttack(MobileEntity target)
    {
        if (Player.IsSunk || target.IsDead || target == Player || target.Faction == Faction.Player) return CommandResult.Invalid;
        if (target is LandUnit { IsEmbarked: true }) return CommandResult.Invalid;
        Politics.OnPlayerAggression(target);
        Undock(Player);
        Player.PendingDock = GridPoint.Invalid;
        Player.Target = target;
        Player.PathTicket = 0;
        Player.SteerTimer = 0;
        if (target.Faction == Faction.Merchant && !target.Provoked)
            Events.Post($"Attacking the merchant {target.Name} — they'll fight back.", MessageKind.Warning);
        else
            Events.Post($"Engaging {target.Name}!", MessageKind.Info);
        return CommandResult.Ok;
    }

    private void RouteFailedMessage(MobileEntity m, bool ok)
    {
        if (ok) return;
        if (m is Ship s) s.PendingDock = GridPoint.Invalid;
        Events.Post("No route found.", MessageKind.Warning);
    }

    public void CommandStop()
    {
        Player.PathTicket = 0;
        Player.ClearPath();
        Player.Target = null;
        Player.PendingDock = GridPoint.Invalid;
    }

    // ---------------------------------------------------------------------------

    private void UpdatePlayerAttack(float dt)
    {
        var t = Player.Target!;
        if (t.IsDead || t is LandUnit { IsEmbarked: true } || Player.DistanceTo(t) > 45f)
        {
            if (!t.IsDead) Events.Post($"Lost sight of {t.Name}.", MessageKind.Info);
            Player.Target = null;
            Player.ClearPath();
            return;
        }
        Steering.Engage(this, Player, t, dt);
    }

    private void UpdateDocking()
    {
        var s = Player;
        if (s.IsMoving || s.PathPending || !s.PendingDock.IsValid) return;
        if (s.Tile.ChebyshevDistance(s.PendingDock) <= 1)
        {
            s.DockTile = s.PendingDock;
            s.PendingDock = GridPoint.Invalid;
            s.Speed = 0;
            Events.RaiseDocked(s);
            int n = BuildingsInReach().Count();
            string where = Biomes.Name(Map.GetBiome(s.DockTile));
            Events.Post(n > 0 ? $"Docked at the {where} shore — {n} building(s) in reach."
                              : $"Docked at the {where} shore. Choose a building to construct.", MessageKind.Info);
        }
        else
        {
            s.PendingDock = GridPoint.Invalid; // path ended somewhere else (e.g. interrupted)
        }
    }

    private void Undock(Ship s)
    {
        if (!s.IsDocked) return;
        s.DockTile = GridPoint.Invalid;
        Events.RaiseUndocked(s);
    }

    /// <summary>A water tile in <paramref name="seaRegion"/> touching <paramref name="land"/>, closest to <paramref name="prefer"/>.</summary>
    internal GridPoint AdjacentWater(GridPoint land, int seaRegion, GridPoint? prefer = null)
    {
        GridPoint best = GridPoint.Invalid;
        float bestD = float.MaxValue;
        foreach (var o in GridPoint.Neighbours8)
        {
            var w = land + o;
            if (!Map.IsWater(w) || Map.Region[Map.Index(w)] != seaRegion) continue;
            float d = prefer is { } p ? w.Distance(p) : (o.X != 0 && o.Y != 0 ? 1.5f : 1f);
            if (d < bestD) { bestD = d; best = w; }
        }
        return best;
    }
}
