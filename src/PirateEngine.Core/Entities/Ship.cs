using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Entities;

/// <summary>
/// A ship: one tile in size, moves continuously (real-time) along a smoothed path over water.
/// Carries cargo in its hold and land units (squads) in its troop deck.
/// </summary>
public sealed class Ship : MobileEntity
{
    public ShipClassDef Class { get; private set; }
    public ShipRole Role { get; }

    public float Hull;
    public float MaxHull => Class.Hull;
    public float HullFraction => Math.Clamp(Hull / MaxHull, 0f, 1f);
    public bool IsSunk { get; internal set; }

    public Inventory Cargo { get; }

    // --- troops ---------------------------------------------------------------
    internal readonly List<LandUnit> TroopList = new();
    /// <summary>Squads currently aboard.</summary>
    public IReadOnlyList<LandUnit> Troops => TroopList;
    /// <summary>Maximum soldiers (squad members) the ship can carry.</summary>
    public int TroopCapacity => Class.TroopCapacity;
    public int TroopsAboard => TroopList.Sum(t => t.Members);
    public int TroopSpaceFree => Math.Max(0, TroopCapacity - TroopsAboard);

    // --- docking --------------------------------------------------------------
    /// <summary>Land tile the ship will dock at when it reaches the end of its path.</summary>
    public GridPoint PendingDock { get; internal set; } = GridPoint.Invalid;
    /// <summary>Land tile the ship is docked at, or Invalid.</summary>
    public GridPoint DockTile { get; internal set; } = GridPoint.Invalid;
    public bool IsDocked => DockTile.IsValid;

    // --- AI -------------------------------------------------------------------
    public AI.ShipBrain? Brain { get; internal set; }
    /// <summary>Spawn table entry (AI ships) — loot and cargo come from here.</summary>
    public SpawnDef? Spawn { get; internal set; }

    public Ship(int id, string name, ShipClassDef cls, Faction faction, ShipRole role, Vector2 position, int resourceTypes)
        : base(id, name, faction, position)
    {
        Class = cls;
        Role = role;
        Hull = cls.Hull;
        Cargo = new Inventory(resourceTypes, cls.Cargo);
    }

    public override bool IsDead => IsSunk;
    public override NavDomain Domain => NavDomain.Water;
    /// <summary>Damaged ships are slower.</summary>
    public override float TopSpeed => Class.Speed * (0.55f + 0.45f * HullFraction);
    public override float TurnRate => Class.TurnRate;
    public override float HitRadius => 0.45f;
    public override float HealthFraction => HullFraction;
    public override float AttackRange => Class.Range;

    internal void ChangeClass(ShipClassDef cls)
    {
        float frac = HullFraction;
        Class = cls;
        Hull = Math.Max(Hull, cls.Hull * frac);
        Cargo.Capacity = cls.Cargo;
    }

    public override string ToString() => $"{Name} [{Class.Name}, {Faction}] @ {Position}";
}

public enum ProjectileKind { Cannonball, Musket }

/// <summary>A cannonball or musket ball in flight.</summary>
public sealed class Projectile
{
    public ProjectileKind Kind;
    public Vector2 Position;
    public Vector2 PreviousPosition;
    public Vector2 Velocity;
    public float RemainingDistance;
    /// <summary>Damage dealt if it strikes a ship's hull.</summary>
    public int DamageVsShips;
    /// <summary>Damage dealt if it strikes a squad of troops.</summary>
    public int DamageVsUnits;
    public MobileEntity Owner = null!;
    public bool Dead;
}

/// <summary>Floating salvage left by a sunk ship.</summary>
public sealed class Wreck
{
    public int Id;
    public Vector2 Position;
    public int Gold;
    public int[] Cargo = Array.Empty<int>();
    public float TimeLeft;
    public bool WarnedFull;
    public bool IsEmpty => Gold <= 0 && Cargo.All(c => c <= 0);
}

/// <summary>Short-lived visual event emitted by the simulation (splashes, hits, sinkings).</summary>
public readonly record struct Effect(EffectKind Kind, Vector2 Position, float Time);

public enum EffectKind { Splash, Hit, Muzzle, Sink, MusketSmoke, Clash, UnitHit, UnitDeath, Dust }
