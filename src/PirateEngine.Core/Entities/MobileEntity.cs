using System.Numerics;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Entities;

/// <summary>
/// Broad side an entity fights for. <see cref="Navy"/> and <see cref="Merchant"/> are the
/// pirate-hunting sea layer, outside state politics. <see cref="Player"/> and <see cref="State"/>
/// entities also carry an <see cref="MobileEntity.Allegiance"/> (their state), and hostility
/// between them follows diplomacy.
/// </summary>
public enum Faction { Player, Navy, Merchant, State }

/// <summary>
/// Anything that moves under its own power: ships on water, troops on land. Holds the shared
/// real-time movement state (position, heading, speed, tile path, background-path ticket) and
/// combat state (target, reload, provocation), so movement, pathfinding, steering, targeting
/// and rendering interpolation are written once for both.
/// Position is in world units where tile (x,y) spans [x, x+1) × [y, y+1).
/// </summary>
public abstract class MobileEntity
{
    public int Id { get; }
    public string Name { get; set; }
    public Faction Faction { get; }
    /// <summary>The state this entity serves (player's state for the player's ship and squads).
    /// Null for Navy/merchant ships and for militia of an independent settlement.</summary>
    public Politics.State? Allegiance { get; internal set; }
    /// <summary>For militia mustered from a settlement's garrison: that settlement.</summary>
    public Politics.Settlement? HomeSettlement { get; internal set; }
    public bool IsMilitia => HomeSettlement != null;

    public Vector2 Position;
    /// <summary>Position/heading at the start of the last simulation step, for render interpolation.</summary>
    public Vector2 PreviousPosition;
    public float PreviousHeading;
    /// <summary>Radians, 0 = east (+X), π/2 = south (+Y, screen down).</summary>
    public float Heading;
    public Vector2 Velocity { get; internal set; }
    public float Speed { get; internal set; }

    // --- navigation -----------------------------------------------------------
    internal readonly List<GridPoint> PathBuffer = new();
    public IReadOnlyList<GridPoint> Path => PathBuffer;
    public int PathIndex { get; internal set; }
    public bool IsMoving => PathIndex < PathBuffer.Count;
    public GridPoint Destination => PathBuffer.Count > 0 ? PathBuffer[^1] : GridPoint.Invalid;
    /// <summary>Non-zero while a path is being planned on the background worker.</summary>
    internal int PathTicket;
    internal Action<MobileEntity, bool>? PathCallback;
    public bool PathPending => PathTicket != 0;
    /// <summary>Countdown until steering next re-plans its path (throttles pathfinding).</summary>
    internal float SteerTimer;

    // --- combat ---------------------------------------------------------------
    /// <summary>Explicit attack target (player order or AI decision): a ship or a land unit.</summary>
    public MobileEntity? Target { get; internal set; }
    public float ReloadTimer { get; internal set; }
    /// <summary>Merchants turn hostile toward the player once attacked.</summary>
    public bool Provoked { get; internal set; }
    public MobileEntity? LastAttacker { get; internal set; }
    public float TimeSinceDamaged { get; internal set; } = 999f;
    /// <summary>+1 or -1: which way this entity circles a target. Fixed per entity for variety.</summary>
    public int OrbitSign { get; }

    protected MobileEntity(int id, string name, Faction faction, Vector2 position)
    {
        Id = id;
        Name = name;
        Faction = faction;
        Position = PreviousPosition = position;
        OrbitSign = (id & 1) == 0 ? 1 : -1;
    }

    /// <summary>Destroyed (sunk / wiped out). Dead entities are removed at the end of the step.</summary>
    public abstract bool IsDead { get; }
    public abstract NavDomain Domain { get; }
    /// <summary>Top speed in tiles/s on open ground or water, before terrain modifiers.</summary>
    public abstract float TopSpeed { get; }
    /// <summary>Radians per second.</summary>
    public abstract float TurnRate { get; }
    /// <summary>Radius within which projectiles strike it.</summary>
    public abstract float HitRadius { get; }
    /// <summary>0..1 health remaining.</summary>
    public abstract float HealthFraction { get; }
    /// <summary>Weapon reach in tiles (0 if unarmed).</summary>
    public abstract float AttackRange { get; }

    public GridPoint Tile => GridPoint.FromWorld(Position);
    public Vector2 Forward => new(MathF.Cos(Heading), MathF.Sin(Heading));
    public float DistanceTo(MobileEntity other) => Vector2.Distance(Position, other.Position);

    internal void ClearPath()
    {
        PathBuffer.Clear();
        PathIndex = 0;
    }
}
