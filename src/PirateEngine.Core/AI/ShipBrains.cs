using System.Numerics;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.AI;

/// <summary>Per-ship AI controller. Brains decide where to go and what to target; the shared
/// movement and combat systems carry it out, exactly as for the player.</summary>
public abstract class ShipBrain
{
    public abstract string State { get; }
    public abstract void Update(GameWorld world, Ship self, float dt);
}

/// <summary>Shared manoeuvres used by both AI and the player's attack command.</summary>
public static class Steering
{
    /// <summary>
    /// Close to gun range, then circle the target at a stand-off distance. Circling means the
    /// ship moves roughly tangentially, which naturally presents its broadside to the target.
    /// The target may be another ship or troops ashore (the ship patrols the nearby coast).
    /// Re-plans a few times per second at most.
    /// </summary>
    public static void Engage(GameWorld w, Ship s, MobileEntity target, float dt)
    {
        s.SteerTimer -= dt;
        if (s.SteerTimer > 0 && s.IsMoving) return;
        s.SteerTimer = 0.5f + w.Rng.NextFloat() * 0.3f;

        float range = s.Class.Range;
        float d = s.DistanceTo(target);
        int region = w.Map.RegionAt(s.Tile);
        GridPoint goal;
        if (d > range * 0.85f)
        {
            goal = w.NearestWaterInRegion(target.Tile, region, 6);
        }
        else
        {
            var away = s.Position - target.Position;
            away = away.LengthSquared() > 1e-4f ? Vector2.Normalize(away) : new Vector2(1, 0);
            float ang = s.OrbitSign * 0.9f;
            var dir = new Vector2(
                away.X * MathF.Cos(ang) - away.Y * MathF.Sin(ang),
                away.X * MathF.Sin(ang) + away.Y * MathF.Cos(ang));
            var p = target.Position + dir * range * 0.65f;
            goal = w.NearestWaterInRegion(GridPoint.FromWorld(p), region, 4);
        }
        MoveTo(w, s, goal, 40_000);
    }

    /// <summary>Moves to <paramref name="goal"/>; straight there if in line of sight, else via A*.
    /// Works for ships and land units (each in its own domain).</summary>
    public static bool MoveTo(GameWorld w, MobileEntity s, GridPoint goal, int budget)
    {
        if (!goal.IsValid) return false;
        if (goal == s.Tile) return true;
        var pf = w.PathfinderFor(s.Domain);
        // A straight leg is fine on water; on land only if it doesn't cut across rougher ground.
        if (pf.SegmentClear(s.Position, goal.Center) &&
            (s.Domain == Navigation.NavDomain.Water || pf.HasLineOfSight(s.Tile, goal, Math.Max(pf.CostAt(s.Tile), pf.CostAt(goal)) + 1e-3f)))
        {
            s.PathBuffer.Clear();
            s.PathBuffer.Add(goal);
            s.PathIndex = 0;
            return true;
        }
        return w.TrySetPath(s, goal, budget);
    }

    /// <summary>Picks a reachable point roughly <paramref name="distance"/> tiles directly away from a threat.</summary>
    public static GridPoint FleePoint(GameWorld w, Ship s, Vector2 threat, float distance)
    {
        var away = s.Position - threat;
        away = away.LengthSquared() > 1e-4f ? Vector2.Normalize(away) : new Vector2(1, 0);
        int region = w.Map.RegionAt(s.Tile);
        // Try straight away first, then fan out either side.
        foreach (float ang in new[] { 0f, 0.6f, -0.6f, 1.2f, -1.2f })
        {
            var dir = new Vector2(away.X * MathF.Cos(ang) - away.Y * MathF.Sin(ang), away.X * MathF.Sin(ang) + away.Y * MathF.Cos(ang));
            var p = GridPoint.FromWorld(s.Position + dir * distance);
            var g = w.NearestWaterInRegion(p, region, 5);
            if (g.IsValid) return g;
        }
        return GridPoint.Invalid;
    }
}

/// <summary>Navy patrol: wanders near its station, chases the player on sight, gives up when
/// led too far away, and breaks off to flee when badly damaged.</summary>
public sealed class HunterBrain : ShipBrain
{
    private enum Mode { Patrol, Engage, Return, Flee }
    private Mode _mode = Mode.Patrol;
    private GridPoint _home;
    private float _timer;

    public HunterBrain(GridPoint home) => _home = home;
    public override string State => _mode.ToString();

    public override void Update(GameWorld w, Ship s, float dt)
    {
        var player = w.Player;
        bool playerAlive = !player.IsSunk;
        float dist = playerAlive ? s.DistanceTo(player) : float.MaxValue;
        var cfg = w.Config;
        _timer -= dt;

        // The home harbour is a refuge: the Navy won't pursue the player into it.
        bool playerSafe = playerAlive && player.Tile.Distance(w.Map.StartTile) < cfg.SafeHarbourRadius;

        switch (_mode)
        {
            case Mode.Patrol:
                if (dist < cfg.HunterAggroRadius && !playerSafe)
                {
                    _mode = Mode.Engage;
                    s.Target = player;
                    s.SteerTimer = 0;
                    w.Events.Post($"{s.Name} ({s.Class.Name}) is giving chase!", MessageKind.Danger);
                    break;
                }
                if (!s.IsMoving && _timer <= 0)
                {
                    var dest = w.RandomWaterNear(_home, 3, 16, w.Map.RegionAt(s.Tile));
                    Steering.MoveTo(w, s, dest, 30_000);
                    _timer = w.Rng.Range(2f, 6f);
                }
                break;

            case Mode.Engage:
                if (!playerAlive || s.Target != player || playerSafe)
                {
                    if (playerSafe && s.Target == player) w.Events.Post($"{s.Name} breaks off the chase.", MessageKind.Info);
                    _mode = Mode.Return;
                    s.Target = null;
                    s.ClearPath();
                    break;
                }
                if (s.HullFraction < 0.2f && s.Role == Data.ShipRole.Hunter)
                {
                    _mode = Mode.Flee;
                    s.Target = null;
                    _timer = 0;
                    break;
                }
                if (Vector2.Distance(player.Position, _home.Center) > cfg.HunterLeashRadius
                    && dist > cfg.HunterAggroRadius)
                {
                    _mode = Mode.Return;
                    s.Target = null;
                    s.ClearPath();
                    break;
                }
                Steering.Engage(w, s, player, dt);
                break;

            case Mode.Return:
                if (dist < cfg.HunterAggroRadius * 0.6f && playerAlive && !playerSafe && s.HullFraction >= 0.2f)
                {
                    _mode = Mode.Engage;
                    s.Target = player;
                    break;
                }
                if (s.Tile.Distance(_home) < 4) { _mode = Mode.Patrol; break; }
                if (!s.IsMoving && _timer <= 0)
                {
                    if (!Steering.MoveTo(w, s, _home, 60_000)) _home = s.Tile; // home unreachable: settle here
                    _timer = 2f;
                }
                break;

            case Mode.Flee:
                if (dist > 30f)
                {
                    _home = s.Tile;
                    _mode = Mode.Patrol;
                    break;
                }
                if (_timer <= 0)
                {
                    Steering.MoveTo(w, s, Steering.FleePoint(w, s, player.Position, 14f), 30_000);
                    _timer = 2.5f;
                }
                break;
        }
    }
}

/// <summary>Merchant: sails long trade routes; if attacked by the player it runs, shooting back
/// when the guns happen to bear, and calms down once well clear.</summary>
public sealed class MerchantBrain : ShipBrain
{
    private enum Mode { Trade, Flee }
    private Mode _mode = Mode.Trade;
    private float _timer;
    private float _calm;

    public MerchantBrain(GridPoint origin) { }
    public override string State => _mode.ToString();

    public override void Update(GameWorld w, Ship s, float dt)
    {
        var player = w.Player;
        float dist = player.IsSunk ? float.MaxValue : s.DistanceTo(player);
        _timer -= dt;

        switch (_mode)
        {
            case Mode.Trade:
                if (s.Provoked && dist < 20f)
                {
                    _mode = Mode.Flee;
                    _timer = 0;
                    s.Target = player;
                    break;
                }
                if (!s.IsMoving && !s.PathPending && _timer <= 0)
                {
                    var dest = w.RandomWaterNear(s.Tile, 50, 140, w.Map.RegionAt(s.Tile), 24);
                    _timer = 3f; // retry delay if this route fails
                    if (dest.IsValid) w.RequestPath(s, dest, 150_000);
                }
                break;

            case Mode.Flee:
                s.Target = s.Provoked && !player.IsSunk ? player : null;
                if (dist > 28f)
                {
                    _calm += dt;
                    if (_calm > 15f)
                    {
                        _mode = Mode.Trade;
                        s.Provoked = false;
                        s.Target = null;
                        _calm = 0;
                    }
                }
                else _calm = 0;
                if (_timer <= 0)
                {
                    Steering.MoveTo(w, s, Steering.FleePoint(w, s, player.Position, 16f), 30_000);
                    _timer = 2.5f;
                }
                break;
        }
    }
}
