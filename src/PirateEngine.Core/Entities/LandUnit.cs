using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Entities;

/// <summary>
/// A squad of soldiers. On the map it is one physical entity occupying a tile and moving in
/// real time over land, exactly like a ship moves over water. Aboard a ship it isn't on the
/// map at all: it sits in the ship's troop list until landed.
/// <para>
/// The squad shares one hit-point pool; <see cref="Members"/> is derived from it, so damage
/// thins the ranks and a weakened squad hits less hard.
/// </para>
/// </summary>
public sealed class LandUnit : MobileEntity
{
    public UnitDef Def { get; }

    public float Hp;
    public float MaxHp => Def.MaxHp;
    /// <summary>Soldiers still standing.</summary>
    public int Members => Hp <= 0 ? 0 : (int)MathF.Ceiling(Hp / Def.HpPerMember);
    public bool IsDestroyed { get; internal set; }

    /// <summary>The ship carrying this squad, or null if it is ashore.</summary>
    public Ship? Carrier { get; internal set; }
    public bool IsEmbarked => Carrier != null;

    /// <summary>Ship this squad is walking back to in order to board.</summary>
    public Ship? PendingEmbark { get; internal set; }

    public AI.UnitBrain? Brain { get; internal set; }
    /// <summary>Spawn table entry for AI garrisons (loot comes from here).</summary>
    public UnitSpawnDef? Spawn { get; internal set; }

    public LandUnit(int id, string name, UnitDef def, Faction faction, Vector2 position)
        : base(id, name, faction, position)
    {
        Def = def;
        Hp = def.MaxHp;
    }

    public override bool IsDead => IsDestroyed;
    public override NavDomain Domain => NavDomain.Land;
    public override float TopSpeed => Def.Speed;
    public override float TurnRate => 10f;
    public override float HitRadius => 0.42f;
    public override float HealthFraction => Math.Clamp(Hp / MaxHp, 0f, 1f);
    public override float AttackRange => Def.Range;

    /// <summary>Last tile fog was revealed from (player squads scout as they march).</summary>
    internal GridPoint LastRevealTile = GridPoint.Invalid;

    public override string ToString() => $"{Name} [{Def.Name} {Members}/{Def.Members}, {Faction}] @ {Position}";
}
