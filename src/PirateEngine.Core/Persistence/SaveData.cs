using PirateEngine.Core.Data;
using PirateEngine.Core.Entities;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Persistence;

// Save files store only what can't be regenerated: the map itself is rebuilt from its seed,
// so a save is a few KB of JSON regardless of map size. Resources and definitions are stored
// by string id, so saves survive content files being reordered or extended.

public sealed class SaveData
{
    public int Version { get; set; } = SaveVersion;
    public const int SaveVersion = 1;

    public WorldGenSettings Generation { get; set; } = new();
    public float Time { get; set; }
    public int Gold { get; set; }
    public int ShipsSunk { get; set; }
    public int GoldEarned { get; set; }
    public ulong RngState { get; set; }
    public int NextId { get; set; }

    public ShipSave Player { get; set; } = new();
    public List<ShipSave> AiShips { get; set; } = new();
    public List<BuildingSave> Buildings { get; set; } = new();
    public List<WreckSave> Wrecks { get; set; } = new();
    /// <summary>Squads ashore (any faction) and squads aboard the player's ship.</summary>
    public List<UnitSave> Units { get; set; } = new();
    public Dictionary<string, int> SquadNumbers { get; set; } = new();
    public int UnitsDefeated { get; set; }
    public PoliticsSave Politics { get; set; } = new();

    public bool FogEnabled { get; set; } = true;
    public string Fog { get; set; } = "";
}

public sealed class ShipSave
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public Faction Faction { get; set; }
    public ShipRole Role { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Heading { get; set; }
    public float Hull { get; set; }
    public Dictionary<string, int> Cargo { get; set; } = new();
    public int? DockX { get; set; }
    public int? DockY { get; set; }
    public bool Provoked { get; set; }
    /// <summary>Index into config spawns for AI ships (-1 for the player).</summary>
    public int SpawnIndex { get; set; } = -1;
}

public sealed class BuildingSave
{
    public int Id { get; set; }
    public string Def { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public Dictionary<string, int> Input { get; set; } = new();
    public Dictionary<string, int> Output { get; set; } = new();
    public int ActiveRecipe { get; set; } = -1;
    public float Progress { get; set; }
    public int Cycles { get; set; }
    public int NextRecipe { get; set; }
    public int? SettlementId { get; set; }
}

public sealed class WreckSave
{
    public float X { get; set; }
    public float Y { get; set; }
    public int Gold { get; set; }
    public Dictionary<string, int> Cargo { get; set; } = new();
    public float TimeLeft { get; set; }
}

public sealed class UnitSave
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public Faction Faction { get; set; }
    public float Hp { get; set; }
    /// <summary>True = aboard the player's ship (position ignored).</summary>
    public bool Embarked { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Heading { get; set; }
    /// <summary>Index into config unitSpawns for AI garrisons (-1 otherwise).</summary>
    public int SpawnIndex { get; set; } = -1;
    public int? HomeX { get; set; }
    public int? HomeY { get; set; }
}

public sealed class PoliticsSave
{
    public List<StateSave> States { get; set; } = new();
    public List<RelationSave> Relations { get; set; } = new();
    public List<SettlementSave> Settlements { get; set; } = new();
    public List<OfferSave> Offers { get; set; } = new();
    public List<string> UsedNames { get; set; } = new();
    public string? SetupId { get; set; }
}

public sealed class StateSave
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public string Behaviour { get; set; } = "";
    public bool IsPlayer { get; set; }
    public bool Founded { get; set; }
    public bool Collapsed { get; set; }
    public float Treasury { get; set; }
    public float FieldArmy { get; set; }
    public int? CapitalId { get; set; }
    public int InitialTowns { get; set; }
    public float NextWarAllowed { get; set; }
    public float NextCampaign { get; set; }
}

public sealed class RelationSave
{
    public int A { get; set; }
    public int B { get; set; }
    public Politics.Stance Stance { get; set; }
    public float Opinion { get; set; }
    public float TruceUntil { get; set; }
    public float WarWeariness { get; set; }
    public float Since { get; set; }
    public float Baseline { get; set; }
    public int GainsA { get; set; }
    public int GainsB { get; set; }
}

public sealed class SettlementSave
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? OwnerId { get; set; }
    public int TownHallId { get; set; }
    public int Population { get; set; }
    /// <summary>Includes soldiers mustered as militia at save time.</summary>
    public float Garrison { get; set; }
    public float Treasury { get; set; }
    public float FedRatio { get; set; }
    public float FoundedAt { get; set; }
    public Dictionary<string, int> Stores { get; set; } = new();
    public List<int> Aggressors { get; set; } = new();
    public SiegeSave? Siege { get; set; }
}

public sealed class SiegeSave
{
    public int AttackerId { get; set; }
    /// <summary>Includes soldiers on the map as assault squads at save time.</summary>
    public float Soldiers { get; set; }
    public float Progress { get; set; }
    public float StartedAt { get; set; }
}

public sealed class OfferSave
{
    public int From { get; set; }
    public Politics.OfferKind Kind { get; set; }
    public float Expires { get; set; }
}
