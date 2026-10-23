using PirateEngine.Core.Data;

namespace PirateEngine.Core.Politics;

/// <summary>
/// A polity: owns settlements, keeps a treasury and a field army, and holds diplomatic
/// relations with every other state. The player has a state too (from the start, so
/// diplomacy always has someone to talk to); it becomes "founded" with the first town hall.
/// </summary>
public sealed class State
{
    public int Id { get; }
    public string Name { get; internal set; }
    /// <summary>Hex colour for flags, territory and map markers.</summary>
    public string Color { get; internal set; }
    public StateBehaviourDef Behaviour { get; internal set; }
    public bool IsPlayer { get; }
    /// <summary>The player's state is unfounded until it builds or captures its first settlement.</summary>
    public bool Founded { get; internal set; } = true;
    /// <summary>True once an AI state has lost every settlement and its army.</summary>
    public bool Collapsed { get; internal set; }

    /// <summary>AI treasury. The player's treasury is <c>GameWorld.Gold</c>.</summary>
    public float Treasury { get; internal set; }
    /// <summary>Soldiers available for sieges (abstract).</summary>
    /// <summary>Towns held when the game began: growth well beyond this alarms other states.</summary>
    public int InitialTowns { get; internal set; }
    /// <summary>Game time before which the state won't start an unprovoked war.</summary>
    public float NextWarAllowed { get; internal set; }
    /// <summary>Game time before which the state won't start another siege.</summary>
    public float NextCampaign { get; set; }
    public float FieldArmy { get; internal set; }
    public Settlement? Capital { get; internal set; }

    internal readonly List<Settlement> SettlementList = new();
    public IReadOnlyList<Settlement> Settlements => SettlementList;

    /// <summary>Shared diplomacy table (lets static hostility checks reach it).</summary>
    public Diplomacy Diplomacy { get; }

    public State(int id, string name, string color, StateBehaviourDef behaviour, bool isPlayer, Diplomacy diplomacy)
    {
        Id = id;
        Name = name;
        Color = color;
        Behaviour = behaviour;
        IsPlayer = isPlayer;
        Diplomacy = diplomacy;
    }

    public bool IsActive => !Collapsed && (IsPlayer || Founded);
    public int Population => SettlementList.Sum(s => s.Population);
    public float GarrisonTotal => SettlementList.Sum(s => s.Garrison);
    /// <summary>Military strength used in AI reasoning.</summary>
    public float Strength => FieldArmy + GarrisonTotal * 0.6f;

    public override string ToString() => Name;
}
