using System.Numerics;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.AI;

/// <summary>Per-squad AI controller (the land counterpart of <see cref="ShipBrain"/>).</summary>
public abstract class UnitBrain
{
    public abstract string State { get; }
    public abstract void Update(GameWorld world, LandUnit self, float dt);
}

/// <summary>Land manoeuvres shared by AI garrisons and the player's attack orders.</summary>
public static class UnitSteering
{
    /// <summary>
    /// Melee squads close to contact with enemy troops. Ranged squads advance until the target
    /// is within musket range, then stand and fire. Against a ship, ranged squads make for the
    /// nearest shore and fire if it comes within range.
    /// </summary>
    public static void Engage(GameWorld w, LandUnit u, MobileEntity target, float dt)
    {
        u.SteerTimer -= dt;
        float d = u.DistanceTo(target);
        float range = u.Def.Range;
        bool inRange = u.Def.IsRanged ? d <= range * 0.9f : d <= range * 0.8f;
        if (inRange)
        {
            if (u.IsMoving) u.ClearPath(); // hold position and fight
            FaceTowards(u, target.Position);
            return;
        }
        if (u.SteerTimer > 0 && (u.IsMoving || u.PathPending)) return;
        u.SteerTimer = 0.4f + w.Rng.NextFloat() * 0.25f;

        int region = w.Map.RegionAt(u.Tile);
        GridPoint goal = target is LandUnit && w.Map.RegionAt(target.Tile) == region
            ? target.Tile
            : w.Map.FindNearest(target.Tile, 8, p => w.Map.IsLand(p) && w.Map.Region[w.Map.Index(p)] == region);
        if (!goal.IsValid || goal == u.Tile)
        {
            // Nowhere closer to stand: the target is out of reach from this island.
            if (target is LandUnit) u.Target = null;
            return;
        }
        Steering.MoveTo(w, u, goal, 60_000);
    }

    public static void FaceTowards(MobileEntity m, Vector2 point)
    {
        var to = point - m.Position;
        if (to.LengthSquared() > 1e-6f) m.Heading = MathF.Atan2(to.Y, to.X);
    }

    /// <summary>Nearest hostile squad ashore within <paramref name="radius"/>.</summary>
    public static LandUnit? NearestHostileUnit(GameWorld w, MobileEntity from, float radius, Vector2? centre = null)
    {
        var c = centre ?? from.Position;
        LandUnit? best = null;
        float bestD = radius;
        foreach (var o in w.Units)
        {
            if (o.IsDead || !GameWorld.IsHostile(from, o)) continue;
            float d = Vector2.Distance(o.Position, c);
            if (d <= bestD) { bestD = d; best = o; }
        }
        return best;
    }
}

/// <summary>
/// A Navy garrison guarding a post ashore: drills around it, engages player troops that come
/// within reach, gives up the chase beyond its leash and returns. Musketeer garrisons also fire
/// on the player's ship whenever it sails within range (handled by the combat system).
/// </summary>
public sealed class GarrisonBrain : UnitBrain
{
    private enum Mode { Guard, Engage, Return }
    private Mode _mode = Mode.Guard;
    private float _timer;
    public GridPoint Home { get; private set; }

    public GarrisonBrain(GridPoint home) => Home = home;
    public override string State => _mode.ToString();

    public override void Update(GameWorld w, LandUnit u, float dt)
    {
        var cfg = w.Config;
        _timer -= dt;
        var home = Home.Center;

        switch (_mode)
        {
            case Mode.Guard:
            {
                var threat = UnitSteering.NearestHostileUnit(w, u, cfg.GarrisonAggroRadius);
                if (threat != null && Vector2.Distance(threat.Position, home) <= cfg.GarrisonLeashRadius)
                {
                    _mode = Mode.Engage;
                    u.Target = threat;
                    u.SteerTimer = 0;
                    break;
                }
                if (!u.IsMoving && _timer <= 0)
                {
                    int region = w.Map.RegionAt(Home);
                    var p = new GridPoint(Home.X + w.Rng.Next(-4, 5), Home.Y + w.Rng.Next(-4, 5));
                    if (w.Map.IsLand(p) && w.Map.RegionAt(p) == region) Steering.MoveTo(w, u, p, 5_000);
                    _timer = w.Rng.Range(4f, 9f);
                }
                break;
            }
            case Mode.Engage:
            {
                var t = u.Target;
                bool lost = t == null || t.IsDead || t is LandUnit { IsEmbarked: true } ||
                            Vector2.Distance(t.Position, home) > cfg.GarrisonLeashRadius + 3f;
                if (lost)
                {
                    u.Target = null;
                    u.ClearPath();
                    _mode = Mode.Return;
                    break;
                }
                // Switch to a much closer threat if one appears (e.g. melee rushing the musketeers).
                var closer = UnitSteering.NearestHostileUnit(w, u, Math.Min(u.DistanceTo(t!) * 0.5f, cfg.GarrisonAggroRadius));
                if (closer != null) u.Target = closer;
                UnitSteering.Engage(w, u, u.Target!, dt);
                break;
            }
            case Mode.Return:
                if (u.Tile.Distance(Home) <= 2f) { _mode = Mode.Guard; break; }
                var again = UnitSteering.NearestHostileUnit(w, u, cfg.GarrisonAggroRadius * 0.7f);
                if (again != null && Vector2.Distance(again.Position, home) <= cfg.GarrisonLeashRadius)
                {
                    _mode = Mode.Engage;
                    u.Target = again;
                    break;
                }
                if (!u.IsMoving && !u.PathPending && _timer <= 0)
                {
                    if (!Steering.MoveTo(w, u, Home, 60_000)) Home = u.Tile; // can't get back: guard here
                    _timer = 2f;
                }
                break;
        }
    }
}

/// <summary>
/// Assault troops of a siege: march on the town hall, fighting any defenders that get in the
/// way, then hold the hall so the town falls.
/// </summary>
public sealed class AssaultBrain : UnitBrain
{
    private readonly Politics.Settlement _target;
    private float _timer;
    public AssaultBrain(Politics.Settlement target) => _target = target;
    public override string State => "Assault";

    public override void Update(GameWorld w, LandUnit u, float dt)
    {
        _timer -= dt;
        var threat = UnitSteering.NearestHostileUnit(w, u, 6f);
        if (threat != null)
        {
            u.Target = threat;
            UnitSteering.Engage(w, u, threat, dt);
            return;
        }
        u.Target = null;
        if (u.Tile.Distance(_target.Tile) <= 1.5f || u.IsMoving || u.PathPending || _timer > 0) return;
        _timer = 1.5f;
        var spot = w.Map.FindNearest(_target.Tile, 2, p => w.Map.IsLand(p) && w.Map.RegionAt(p) == w.Map.RegionAt(u.Tile));
        Steering.MoveTo(w, u, spot, 60_000);
    }
}
