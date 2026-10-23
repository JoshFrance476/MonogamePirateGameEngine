namespace PirateEngine.Core.Data;

// The initial political map: which states and towns exist when a playthrough starts, where they
// sit, and how they get on. Loaded from setup.json; a new game picks one preset by id.

/// <summary>Root of setup.json.</summary>
public sealed class WorldSetupFile
{
    /// <summary>Preset used when a new game names none.</summary>
    public string Default { get; set; } = "standard";
    /// <summary>State size classes shared by every preset (a preset may add or override its own).</summary>
    public Dictionary<string, StateSizeDef> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WorldSetupDef> Presets { get; set; } = new();
}

/// <summary>
/// One way to populate a world. Counts are written for <see cref="ReferenceArea"/> and scale
/// with the actual map's area, so the same preset suits any map size. Explicitly named states
/// (with a <see cref="StateSpecDef.Name"/> or <see cref="StateSpecDef.At"/>) never scale.
/// </summary>
public sealed class WorldSetupDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Map area (tiles) the counts below are written for. Default 1024x1024.</summary>
    public int ReferenceArea { get; set; } = 1024 * 1024;

    public DensityDef Density { get; set; } = new();
    /// <summary>Minimum distance between town halls, from the densest to the sparsest areas.</summary>
    public SpacingDef Spacing { get; set; } = new();
    /// <summary>States to create: groups by size class, or single hand-placed states.</summary>
    public List<StateSpecDef> States { get; set; } = new();
    /// <summary>State size classes referenced by <see cref="StateSpecDef.Size"/>.</summary>
    public Dictionary<string, StateSizeDef> Sizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public IndependentsDef Independents { get; set; } = new();
    /// <summary>A friendly harbour town near the player's start (null or disabled = none).</summary>
    public StartPortDef? StartPort { get; set; }
    /// <summary>No state capital closer than this to the player's start.</summary>
    public float CapitalClearance { get; set; } = 70f;
    /// <summary>No other town (except the start port) closer than this to the player's start.</summary>
    public float TownClearance { get; set; } = 40f;
    public RelationsDef Relations { get; set; } = new();
    /// <summary>Towns start with their population at this fraction (min, max) of what their buildings support.</summary>
    public float[] Fill { get; set; } = { 0.82f, 0.95f };
}

/// <summary>
/// Where people live. Density (0..1) is high in a few <i>heartlands</i> — rich centres of
/// civilisation where the great states sit — tapers into a sparsely settled frontier, and is
/// zero in the wilderness. Low-frequency noise breaks up the pattern.
/// </summary>
public sealed class DensityDef
{
    public int Heartlands { get; set; } = 4;
    /// <summary>Radius range (tiles) of a heartland's dense core.</summary>
    public float[] HeartlandRadius { get; set; } = { 70f, 110f };
    /// <summary>Heartland centres keep at least this many radii apart.</summary>
    public float HeartlandSpacing { get; set; } = 1.8f;
    /// <summary>Heartland centres keep at least this far from the player's start.</summary>
    public float HeartlandClearance { get; set; } = 120f;
    /// <summary>Weight of the noise term (0 = heartlands only).</summary>
    public float NoiseWeight { get; set; } = 0.3f;
    /// <summary>Feature size of the noise in tiles.</summary>
    public float NoiseScale { get; set; } = 220f;
    /// <summary>Land with density below this is wilderness: no towns are placed there at the start.</summary>
    public float Wilderness { get; set; } = 0.12f;
}

public sealed class SpacingDef
{
    public float Dense { get; set; } = 15f;
    public float Sparse { get; set; } = 34f;
}

/// <summary>
/// A group of <see cref="Count"/> states of one size class, or (with a name or a position) one
/// specific hand-designed state. Unset fields are chosen by the generator.
/// </summary>
public sealed class StateSpecDef
{
    public string Size { get; set; } = "regional";
    public int Count { get; set; } = 1;
    /// <summary>Behaviour id; null = pick one (preferring behaviours not yet used).</summary>
    public string? Behaviour { get; set; }
    /// <summary>Realm name, e.g. "Crown of Valemont"; null = from the behaviour's titles and the capital's name.</summary>
    public string? Name { get; set; }
    /// <summary>Capital's name; null = from the name list.</summary>
    public string? Capital { get; set; }
    /// <summary>Colour as #RRGGBB; null = next from the palette.</summary>
    public string? Color { get; set; }
    /// <summary>Capital position as fractions of map width and height, e.g. [0.3, 0.6]; null = generator's choice.</summary>
    public float[]? At { get; set; }

    internal bool IsExplicit => Name != null || At != null || Capital != null;
}

/// <summary>How big a state of a given size class starts.</summary>
public sealed class StateSizeDef
{
    /// <summary>Number of towns including the capital (min, max).</summary>
    public int[] Towns { get; set; } = { 3, 5 };
    /// <summary>Realm radius around the capital (tiles).</summary>
    public float Radius { get; set; } = 70f;
    /// <summary>Settlement level name of the capital, e.g. "City".</summary>
    public string CapitalLevel { get; set; } = "Town";
    /// <summary>Level names other towns are drawn from (denser areas favour later entries).</summary>
    public List<string> TownLevels { get; set; } = new() { "Hamlet", "Village" };
    /// <summary>Sits in a heartland (true) or out in the frontier (false).</summary>
    public bool Heartland { get; set; } = true;
    public float Treasury { get; set; } = 800f;
}

public sealed class IndependentsDef
{
    /// <summary>Self-governing towns scattered by density (scaled with map area).</summary>
    public int Count { get; set; } = 14;
    public List<string> Levels { get; set; } = new() { "Hamlet", "Village" };
    /// <summary>Large independent cities in the gaps between realms (scaled with map area).</summary>
    public int FreeCities { get; set; } = 2;
    public string FreeCityLevel { get; set; } = "Town";
}

public sealed class StartPortDef
{
    public bool Enabled { get; set; } = true;
    /// <summary>Distance range (tiles) from the player's start.</summary>
    public float[] Distance { get; set; } = { 14f, 30f };
    public string Level { get; set; } = "Village";
    /// <summary>Town name; null = from the name list.</summary>
    public string? Name { get; set; }
}

/// <summary>Starting diplomacy: long-standing friendships, grudges and wars.</summary>
public sealed class RelationsDef
{
    /// <summary>Alliances between friendly, peaceable states.</summary>
    public int Alliances { get; set; } = 2;
    /// <summary>Old rivalries between neighbours: low opinion, at peace, quick to flare up.</summary>
    public int Rivalries { get; set; } = 3;
    /// <summary>Wars already running when the game starts (chosen among rivals).</summary>
    public int Wars { get; set; } = 0;
    /// <summary>Random spread (+/-) added to each pair's natural opinion.</summary>
    public float Spread { get; set; } = 15f;
}
