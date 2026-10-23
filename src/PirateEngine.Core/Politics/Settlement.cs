using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Politics;

/// <summary>
/// A town around a town hall. Owns the buildings in its territory, keeps shared town stores,
/// grows (or starves) on a slow economy tick, pays taxes to its owner and keeps a garrison.
/// The owner may be a state or nobody (independent).
/// </summary>
public sealed class Settlement
{
    public int Id { get; }
    public string Name { get; internal set; }
    public State? Owner { get; internal set; }
    public bool IsIndependent => Owner == null;
    public Building TownHall { get; internal set; }
    public GridPoint Tile => TownHall.Tile;
    public int Region => TownHall.Region;
    public float FoundedAt { get; internal set; }

    public int Population { get; internal set; }
    public int PopulationCap { get; internal set; }
    /// <summary>Fraction of food needs met on the last economy tick (0..1).</summary>
    public float FedRatio { get; internal set; } = 1f;
    public int LevelIndex { get; internal set; }
    /// <summary>Soldiers stationed here (abstract until mustered as squads).</summary>
    public float Garrison { get; internal set; }
    /// <summary>Own treasury (used while independent).</summary>
    public float Treasury { get; internal set; }

    public Inventory Stores { get; }
    internal readonly List<Building> BuildingList = new();
    /// <summary>Buildings belonging to the settlement, excluding the town hall.</summary>
    public IReadOnlyList<Building> Buildings => BuildingList;

    /// <summary>States currently attacking this settlement (needed for independents, which have no diplomacy).</summary>
    internal readonly HashSet<State> Aggressors = new();
    public IReadOnlyCollection<State> AttackedBy => Aggressors;

    public Siege? Siege { get; internal set; }
    /// <summary>Defender squads currently mustered from the garrison.</summary>
    internal readonly List<LandUnit> Militia = new();
    public IReadOnlyList<LandUnit> MusteredMilitia => Militia;
    internal float CalmTimer;
    /// <summary>Seconds hostile troops have held the town hall uncontested.</summary>
    public float OccupationProgress { get; internal set; }
    public State? Occupier { get; internal set; }

    /// <summary>Gold produced last economy tick (taxes + markets), for the settlement screen.</summary>
    public float LastIncome { get; internal set; }
    /// <summary>Goods the town's workshops delivered to its stores during the last economy tick.</summary>
    public int[] LastProduced { get; }
    /// <summary>Gold its workshops (markets) earned during the last economy tick.</summary>
    public float LastBuildingGold { get; internal set; }
    internal float GoldAccumulator;
    /// <summary>Fractional population growth carried between ticks.</summary>
    internal float GrowthProgress;

    public Settlement(int id, string name, Building townHall, int resourceTypes, int storage)
    {
        Id = id;
        Name = name;
        TownHall = townHall;
        LastProduced = new int[resourceTypes];
        Stores = new Inventory(resourceTypes, storage);
    }

    public SettlementLevelDef Level(PoliticsConfig cfg) => cfg.Levels[Math.Clamp(LevelIndex, 0, cfg.Levels.Count - 1)];
    public float Radius(PoliticsConfig cfg) => Level(cfg).Radius;

    public override string ToString() => $"{Name} ({Owner?.Name ?? "independent"})";
}

/// <summary>An attack on a settlement by a state's field army.</summary>
public sealed class Siege
{
    public State Attacker { get; }
    public Settlement Target { get; }
    /// <summary>Attacking soldiers not currently present as squads.</summary>
    public float Soldiers { get; internal set; }
    /// <summary>0..100 once the garrison is beaten (abstract siege).</summary>
    public float Progress { get; internal set; }
    public float StartedAt { get; }
    /// <summary>Squads on the map while the siege is near the player.</summary>
    internal readonly List<LandUnit> Squads = new();
    public bool IsPhysical => Squads.Count > 0;
    public int SoldiersTotal => (int)Soldiers + Squads.Where(s => !s.IsDead).Sum(s => s.Members);

    public Siege(State attacker, Settlement target, float soldiers, float time)
    {
        Attacker = attacker;
        Target = target;
        Soldiers = soldiers;
        StartedAt = time;
    }
}
