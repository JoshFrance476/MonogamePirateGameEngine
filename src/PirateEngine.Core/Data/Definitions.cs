using System.Text.Json.Serialization;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Data;

// All gameplay content is data-driven. These classes mirror the JSON files in Data/;
// string ids are resolved to integer indices once at load time (see DefinitionDatabase).

public sealed class ResourceDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Hex colour used for UI chips, e.g. "#8B5A2B".</summary>
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>0 = raw, 1 = processed, 2 = advanced. Informational.</summary>
    public int Tier { get; set; }
    /// <summary>Base trade price in gold; settlements adjust it by scarcity.</summary>
    public int Value { get; set; } = 5;
    /// <summary>Food value per unit for settlement populations (0 = not food).</summary>
    public int Food { get; set; }

    [JsonIgnore] public int Index { get; internal set; }
}

/// <summary>One production recipe: consumes inputs, waits, then emits outputs.</summary>
public sealed class RecipeDef
{
    public Dictionary<string, int> Inputs { get; set; } = new();
    public Dictionary<string, int> Outputs { get; set; } = new();
    /// <summary>Gold paid straight to the owner's treasury when a cycle completes (e.g. markets).</summary>
    public int GoldOutput { get; set; }
    public float CycleSeconds { get; set; } = 10f;

    [JsonIgnore] public (int res, int amount)[] InputList { get; internal set; } = Array.Empty<(int, int)>();
    [JsonIgnore] public (int res, int amount)[] OutputList { get; internal set; } = Array.Empty<(int, int)>();
}

public sealed class BuildingDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Single character drawn on the building icon.</summary>
    public string Glyph { get; set; } = "?";
    public string Color { get; set; } = "#AAAAAA";

    /// <summary>Biomes this building may be placed on. Empty = any buildable land.</summary>
    public List<string> AllowedBiomes { get; set; } = new();
    /// <summary>Must be placed on a land tile touching water.</summary>
    public bool RequiresCoast { get; set; }

    public int GoldCost { get; set; }
    /// <summary>Resources taken from the docked ship's hold when building.</summary>
    public Dictionary<string, int> BuildCost { get; set; } = new();

    /// <summary>Recipes tried in order (round-robin) each time the building is idle.</summary>
    public List<RecipeDef> Recipes { get; set; } = new();

    /// <summary>Max units held in the input store and in the output store, each.</summary>
    public int Storage { get; set; } = 40;

    /// <summary>Non-production features this building unlocks while docked in reach, e.g. "shipyard", "repair".</summary>
    public List<string> Services { get; set; } = new();

    [JsonIgnore] public int Index { get; internal set; }
    [JsonIgnore] public bool[] AllowedBiomeMask { get; internal set; } = Array.Empty<bool>();
    [JsonIgnore] public (int res, int amount)[] BuildCostList { get; internal set; } = Array.Empty<(int, int)>();
    /// <summary>Resource indices any recipe consumes — what the building will accept as input.</summary>
    [JsonIgnore] public bool[] AcceptsInput { get; internal set; } = Array.Empty<bool>();

    public bool AllowsBiome(Biome b) => AllowedBiomeMask[(int)b];
}

public enum ShipRole { Player, Hunter, Merchant }

public enum UnitKind { Melee, Ranged }

/// <summary>
/// A land unit type. A unit on the map is a squad of up to <see cref="Members"/> soldiers
/// sharing one hit-point pool; losses reduce the head-count and with it the squad's damage.
/// </summary>
public sealed class UnitDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public UnitKind Kind { get; set; } = UnitKind.Melee;
    /// <summary>Soldiers in a full-strength squad. Also the troop-deck space it needs.</summary>
    public int Members { get; set; } = 10;
    public int HpPerMember { get; set; } = 10;
    /// <summary>Damage per soldier per attack against other troops.</summary>
    public int Damage { get; set; } = 2;
    /// <summary>Damage per musket ball that strikes a ship (ranged units only).</summary>
    public int DamageVsShips { get; set; }
    /// <summary>Reach in tiles (about 1.2 for melee).</summary>
    public float Range { get; set; } = 1.2f;
    /// <summary>Seconds between attacks / volleys.</summary>
    public float Reload { get; set; } = 1.5f;
    /// <summary>Tiles per second on open ground (terrain slows this).</summary>
    public float Speed { get; set; } = 2f;
    /// <summary>0..1, ranged only: tighter shot grouping when higher.</summary>
    public float Accuracy { get; set; } = 0.6f;
    public int SightRadius { get; set; } = 7;
    /// <summary>Idle squads automatically engage hostile troops this close.</summary>
    public float AutoEngageRadius { get; set; } = 3f;
    public int RecruitGold { get; set; }
    public Dictionary<string, int> RecruitCost { get; set; } = new();

    [JsonIgnore] public int Index { get; internal set; }
    [JsonIgnore] public (int res, int amount)[] RecruitCostList { get; internal set; } = Array.Empty<(int, int)>();
    [JsonIgnore] public int MaxHp => Members * HpPerMember;
    [JsonIgnore] public bool IsRanged => Kind == UnitKind.Ranged;
}

/// <summary>An entry in the AI land-garrison spawn table.</summary>
public sealed class UnitSpawnDef
{
    public string Unit { get; set; } = "";
    public float Weight { get; set; } = 1f;
    public float MinDanger { get; set; }
    public float MaxDanger { get; set; } = 1f;
    /// <summary>Gold looted when the player wipes the squad out.</summary>
    public int LootGold { get; set; } = 30;

    [JsonIgnore] public UnitDef? Def { get; internal set; }
}

public sealed class ShipClassDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Tiles per second at full sail.</summary>
    public float Speed { get; set; } = 5f;
    /// <summary>Radians per second.</summary>
    public float TurnRate { get; set; } = 3f;
    public int Hull { get; set; } = 100;
    /// <summary>Guns fired per broadside volley (one cannonball each).</summary>
    public int Guns { get; set; } = 2;
    public int Damage { get; set; } = 8;
    /// <summary>Firing range in tiles.</summary>
    public float Range { get; set; } = 6f;
    public float Reload { get; set; } = 2.5f;
    /// <summary>0..1; higher = tighter shot grouping.</summary>
    public float Accuracy { get; set; } = 0.7f;
    public int Cargo { get; set; } = 60;
    /// <summary>Soldiers (squad members) the ship can carry.</summary>
    public int TroopCapacity { get; set; }
    public int SightRadius { get; set; } = 12;
    /// <summary>Hex colours for the procedural sprite.</summary>
    public string HullColor { get; set; } = "#6B4226";
    public string SailColor { get; set; } = "#F0EAD6";

    /// <summary>Id of the class this ship can be upgraded to at a shipyard, if any.</summary>
    public string? UpgradesTo { get; set; }
    public int UpgradeGold { get; set; }
    public Dictionary<string, int> UpgradeCost { get; set; } = new();

    [JsonIgnore] public int Index { get; internal set; }
    [JsonIgnore] public (int res, int amount)[] UpgradeCostList { get; internal set; } = Array.Empty<(int, int)>();
    [JsonIgnore] public ShipClassDef? UpgradeTarget { get; internal set; }
}

/// <summary>An entry in the AI spawn table.</summary>
public sealed class SpawnDef
{
    public string Ship { get; set; } = "";
    public ShipRole Role { get; set; } = ShipRole.Hunter;
    public float Weight { get; set; } = 1f;
    /// <summary>Only spawns once danger (0 at the start harbour → 1 at the far seas) reaches this.</summary>
    public float MinDanger { get; set; }
    public float MaxDanger { get; set; } = 1f;
    /// <summary>Gold dropped in the wreck.</summary>
    public int LootGold { get; set; } = 50;
    /// <summary>Cargo the AI carries (and drops), resource id → amount.</summary>
    public Dictionary<string, int> Cargo { get; set; } = new();

    [JsonIgnore] public ShipClassDef? Class { get; internal set; }
}

public sealed class GameConfig
{
    public int StartGold { get; set; } = 300;
    public string StartShip { get; set; } = "sloop";
    public Dictionary<string, int> StartCargo { get; set; } = new();

    /// <summary>Buildings must be within this many tiles of the dock to be built or traded with.</summary>
    public float DockReach { get; set; } = 8f;
    /// <summary>Hull points restored per plank when repairing.</summary>
    public int HullPerPlank { get; set; } = 5;
    public string RepairResource { get; set; } = "planks";
    public string AmmoResource { get; set; } = "cannonballs";
    /// <summary>If true the player needs ammo in the hold to fire. AI ships never run out.</summary>
    public bool PlayerUsesAmmo { get; set; } = true;
    /// <summary>Guns only bear within this many degrees of the beam (90° off the bow).</summary>
    public float BroadsideArcDegrees { get; set; } = 65f;
    public float CannonballSpeed { get; set; } = 14f;

    // AI population ("bubble" around the player so huge maps cost nothing far away).
    public int MaxAiShips { get; set; } = 18;
    public float SpawnMinDistance { get; set; } = 35f;
    public float SpawnMaxDistance { get; set; } = 70f;
    public float DespawnDistance { get; set; } = 120f;
    /// <summary>No hostile spawns within this radius of the start harbour, and Navy ships break off
    /// any chase that enters it — the player's refuge. Set to 0 to disable.</summary>
    public float SafeHarbourRadius { get; set; } = 45f;
    public float HunterAggroRadius { get; set; } = 14f;
    public float HunterLeashRadius { get; set; } = 35f;

    public float WreckLifetime { get; set; } = 150f;
    /// <summary>Fraction of gold lost when the player's ship sinks.</summary>
    public float DeathGoldPenalty { get; set; } = 0.25f;

    public List<SpawnDef> Spawns { get; set; } = new();

    // ---- land units ------------------------------------------------------------------
    /// <summary>Squads aboard the player's ship at the start (unit ids).</summary>
    public List<string> StartTroops { get; set; } = new();
    /// <summary>Cannonball damage against troops is the ball's ship damage times this.</summary>
    public float CannonVsTroopsMultiplier { get; set; } = 2f;
    /// <summary>A squad can board the docked ship from this many tiles away.</summary>
    public float EmbarkDistance { get; set; } = 1.8f;
    public int MaxAiUnits { get; set; } = 8;
    public float UnitSpawnMinDistance { get; set; } = 25f;
    public float UnitSpawnMaxDistance { get; set; } = 60f;
    /// <summary>Garrisons never spawn within this many tiles of a player building.</summary>
    public float GarrisonBuildingClearance { get; set; } = 14f;
    public float GarrisonAggroRadius { get; set; } = 7f;
    public float GarrisonLeashRadius { get; set; } = 14f;
    public List<UnitSpawnDef> UnitSpawns { get; set; } = new();

    public PoliticsConfig Politics { get; set; } = new();
}

/// <summary>A development tier of a settlement, reached by population.</summary>
public sealed class SettlementLevelDef
{
    public string Name { get; set; } = "";
    public int MinPopulation { get; set; }
    /// <summary>Territory radius in tiles around the town hall.</summary>
    public float Radius { get; set; } = 6;
    /// <summary>How many buildings (besides the town hall) the settlement may grow to.</summary>
    public int MaxBuildings { get; set; } = 3;
    /// <summary>Stockpile capacity of the town stores.</summary>
    public int Storage { get; set; } = 120;
    /// <summary>Defenders fight this much harder in an abstract siege.</summary>
    public float Fortification { get; set; } = 1f;
}

/// <summary>
/// A behaviour type for computer-controlled states. Each weight is 0..1 and biases a family
/// of decisions; see PoliticsSystem for exactly where each is used.
/// </summary>
public sealed class StateBehaviourDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Propensity to declare war, and share of income spent on the field army.</summary>
    public float Aggression { get; set; } = 0.5f;
    /// <summary>Founding new settlements and seizing independent ones; speed of building up.</summary>
    public float Expansion { get; set; } = 0.5f;
    /// <summary>Tax and market income, preference for markets, goodwill from trade.</summary>
    public float Trade { get; set; } = 0.5f;
    /// <summary>Garrison size and fortification.</summary>
    public float Defence { get; set; } = 0.5f;
    /// <summary>Willingness to make peace and form alliances.</summary>
    public float Peacefulness { get; set; } = 0.5f;
    /// <summary>Attacking states that are weak or already at war.</summary>
    public float Opportunism { get; set; } = 0.2f;
    /// <summary>Title formats, e.g. "Kingdom of {0}".</summary>
    public List<string> Titles { get; set; } = new();
}

public sealed class PoliticsConfig
{
    /// <summary>Above this many towns a realm is overextended: it tires of war and may lose towns.</summary>
    public int MaxSettlementsPerState { get; set; } = 16;
    /// <summary>No town hall (the player's or a new colony's) closer than this to another.</summary>
    public float MinSettlementDistance { get; set; } = 20f;

    public float EconomyInterval { get; set; } = 10f;
    public float DiplomacyInterval { get; set; } = 20f;
    public float SiegeInterval { get; set; } = 3f;

    public float FoodPerPop { get; set; } = 0.1f;
    /// <summary>
    /// Each economy tick a fed town closes this fraction of the gap between its population and
    /// what its buildings support — slow, so the world changes over hours, not minutes.
    /// </summary>
    public float GrowthRate { get; set; } = 0.002f;
    /// <summary>The player's towns grow this many times faster (they start small).</summary>
    public float PlayerGrowthMultiplier { get; set; } = 2f;
    /// <summary>Chance per economy tick that a crowded (or hungry) computer-run town builds something.</summary>
    public float ConstructionChance { get; set; } = 0.004f;
    /// <summary>Chance per economy tick that a prosperous state founds a colony (scaled by expansion).</summary>
    public float ColonyChance { get; set; } = 0.00025f;
    /// <summary>
    /// Chance per diplomacy tick that a state acts on an appetite for war that nothing provoked.
    /// Kept low so AI states rarely start wars on their own.
    /// </summary>
    public float ProactiveWarChance { get; set; } = 0.001f;
    /// <summary>
    /// The same chance when there is a reason: the target is already at war, has grown too
    /// strong, is an old rival, or is the player and has provoked them.
    /// </summary>
    public float ReactiveWarChance { get; set; } = 0.2f;
    /// <summary>After a war ends, a state won't start an unprovoked one for this long (seconds).</summary>
    public float WarCooldown { get; set; } = 1800f;
    /// <summary>Seconds (min, max) an army regroups between sieges.</summary>
    public float[] CampaignCooldown { get; set; } = { 240f, 480f };
    /// <summary>Chance per siege tick that an expansionist state moves on an independent town in reach.</summary>
    public float IndependentConquestChance { get; set; } = 0.0001f;
    /// <summary>Fraction per diplomacy tick by which opinions return to their natural level.</summary>
    public float OpinionRecovery { get; set; } = 0.02f;
    public int PopBase { get; set; } = 30;
    public int PopPerBuilding { get; set; } = 18;
    public float TaxPerPop { get; set; } = 0.15f;
    public float GarrisonPerPop { get; set; } = 0.22f;
    public int SoldierCost { get; set; } = 4;
    public int FoundCost { get; set; } = 500;
    public int StartPopulation { get; set; } = 12;
    public int StartTreasury { get; set; } = 400;

    /// <summary>Settlements this close to the player (ship or squads) fight sieges with real squads.</summary>
    public float PhysicalRadius { get; set; } = 80f;
    /// <summary>Seconds hostile troops must hold a town hall uncontested to capture it.</summary>
    public float CaptureSeconds { get; set; } = 12f;
    public float CaptureRadius { get; set; } = 2.5f;
    /// <summary>A peace treaty prevents a new war between the pair for this long (seconds).</summary>
    public float TruceSeconds { get; set; } = 1800f;
    /// <summary>Seconds a newly founded player state is safe from declarations of war.</summary>
    public float NewStateGrace { get; set; } = 600f;
    public int GiftGold { get; set; } = 100;

    public List<SettlementLevelDef> Levels { get; set; } = new();
    public List<StateBehaviourDef> Behaviours { get; set; } = new();
    public List<string> SettlementNames { get; set; } = new();
    public List<string> StateColors { get; set; } = new();
}
