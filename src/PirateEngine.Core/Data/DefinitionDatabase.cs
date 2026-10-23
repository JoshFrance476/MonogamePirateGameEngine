using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Data;

/// <summary>
/// Loads and cross-links all content definitions. Content comes from JSON files in a data
/// directory if present (so the game can be modded without recompiling), otherwise from the
/// copies embedded in this assembly. Every string reference is validated at load time so a
/// typo in a mod fails loudly at startup rather than mid-game.
/// </summary>
public sealed class DefinitionDatabase
{
    public IReadOnlyList<ResourceDef> Resources { get; private set; } = Array.Empty<ResourceDef>();
    public IReadOnlyList<BuildingDef> Buildings { get; private set; } = Array.Empty<BuildingDef>();
    public IReadOnlyList<ShipClassDef> Ships { get; private set; } = Array.Empty<ShipClassDef>();
    public IReadOnlyList<UnitDef> Units { get; private set; } = Array.Empty<UnitDef>();
    public GameConfig Config { get; private set; } = new();
    /// <summary>Initial political map presets (setup.json).</summary>
    public WorldSetupFile Setup { get; private set; } = new();

    private readonly Dictionary<string, ResourceDef> _resById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuildingDef> _bldById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ShipClassDef> _shipById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnitDef> _unitById = new(StringComparer.OrdinalIgnoreCase);

    public int ResourceCount => Resources.Count;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Loads from <paramref name="dataDirectory"/>, falling back per-file to embedded defaults.</summary>
    public static DefinitionDatabase Load(string? dataDirectory = null)
    {
        string Read(string file)
        {
            if (dataDirectory != null)
            {
                var path = Path.Combine(dataDirectory, file);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            return ReadEmbedded(file);
        }

        return FromJson(Read("resources.json"), Read("buildings.json"), Read("ships.json"), Read("config.json"), Read("units.json"),
            Read("setup.json"));
    }

    public static DefinitionDatabase FromJson(string resourcesJson, string buildingsJson, string shipsJson, string configJson,
        string? unitsJson = null, string? setupJson = null)
    {
        var db = new DefinitionDatabase
        {
            Resources = Deserialize<List<ResourceDef>>(resourcesJson, "resources.json"),
            Buildings = Deserialize<List<BuildingDef>>(buildingsJson, "buildings.json"),
            Ships = Deserialize<List<ShipClassDef>>(shipsJson, "ships.json"),
            Config = Deserialize<GameConfig>(configJson, "config.json"),
            Units = Deserialize<List<UnitDef>>(unitsJson ?? ReadEmbedded("units.json"), "units.json"),
            Setup = Deserialize<WorldSetupFile>(setupJson ?? ReadEmbedded("setup.json"), "setup.json"),
        };
        db.Link();
        return db;
    }

    public static string ReadEmbedded(string file)
    {
        var asm = typeof(DefinitionDatabase).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FileNotFoundException($"Embedded definition '{file}' not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static T Deserialize<T>(string json, string file)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidDataException("empty document");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file}: {ex.Message}", ex);
        }
    }

    // ---- lookups ----------------------------------------------------------------

    public ResourceDef Resource(string id) =>
        _resById.TryGetValue(id, out var r) ? r : throw new KeyNotFoundException($"Unknown resource '{id}'.");
    public BuildingDef Building(string id) =>
        _bldById.TryGetValue(id, out var b) ? b : throw new KeyNotFoundException($"Unknown building '{id}'.");
    public ShipClassDef Ship(string id) =>
        _shipById.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"Unknown ship class '{id}'.");

    public UnitDef Unit(string id) =>
        _unitById.TryGetValue(id, out var u) ? u : throw new KeyNotFoundException($"Unknown unit '{id}'.");
    public bool TryUnit(string id, out UnitDef def) => _unitById.TryGetValue(id, out def!);

    public bool TryResource(string id, out ResourceDef def) => _resById.TryGetValue(id, out def!);

    public BuildingDef TownHall => Building("town_hall");

    public StateBehaviourDef Behaviour(string id) =>
        Config.Politics.Behaviours.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown state behaviour '{id}'.");
    public int ResourceIndex(string id) => Resource(id).Index;

    /// <summary>A world-setup preset by id (null = the file's default).</summary>
    public WorldSetupDef SetupPreset(string? id)
    {
        id ??= Setup.Default;
        return Setup.Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"Unknown world setup '{id}'. Available: {string.Join(", ", Setup.Presets.Select(p => p.Id))}.");
    }

    /// <summary>Index of a settlement level by name.</summary>
    public int LevelIndex(string name)
    {
        int i = Config.Politics.Levels.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new KeyNotFoundException($"Unknown settlement level '{name}'.");
    }

    // ---- linking ----------------------------------------------------------------

    private (int, int)[] ResolveCounts(Dictionary<string, int> d, string context) =>
        d.Select(kv =>
        {
            if (!_resById.TryGetValue(kv.Key, out var r))
                throw new InvalidDataException($"{context}: unknown resource '{kv.Key}'.");
            if (kv.Value <= 0) throw new InvalidDataException($"{context}: amount for '{kv.Key}' must be > 0.");
            return (r.Index, kv.Value);
        }).ToArray();

    private void ValidateSetup()
    {
        if (Setup.Presets.Count == 0) throw new InvalidDataException("setup.json: no presets defined.");
        SetupPreset(null); // the default must exist
        void Level(string name, string ctx)
        {
            if (!Config.Politics.Levels.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{ctx}: unknown settlement level '{name}'.");
        }
        void Pair(float[]? v, string ctx)
        {
            if (v == null || v.Length != 2 || v[0] > v[1]) throw new InvalidDataException($"{ctx}: expected [min, max].");
        }
        foreach (var p in Setup.Presets)
        {
            var merged = new Dictionary<string, StateSizeDef>(Setup.Sizes, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in p.Sizes) merged[k] = v;
            p.Sizes = merged;
            string ctx = $"setup.json preset '{p.Id}'";
            if (string.IsNullOrWhiteSpace(p.Id)) throw new InvalidDataException("setup.json: a preset has no id.");
            Pair(p.Density.HeartlandRadius, ctx + " density.heartlandRadius");
            Pair(p.Fill, ctx + " fill");
            foreach (var (name, size) in p.Sizes)
            {
                if (size.Towns.Length != 2 || size.Towns[0] < 1 || size.Towns[0] > size.Towns[1])
                    throw new InvalidDataException($"{ctx} size '{name}': towns must be [min >= 1, max].");
                Level(size.CapitalLevel, $"{ctx} size '{name}' capitalLevel");
                if (size.TownLevels.Count == 0) throw new InvalidDataException($"{ctx} size '{name}': townLevels is empty.");
                foreach (var l in size.TownLevels) Level(l, $"{ctx} size '{name}' townLevels");
            }
            foreach (var st in p.States)
            {
                if (!p.Sizes.ContainsKey(st.Size)) throw new InvalidDataException($"{ctx}: state size '{st.Size}' is not defined in sizes.");
                if (st.Behaviour != null) Behaviour(st.Behaviour);
                if (st.At != null && (st.At.Length != 2 || st.At.Any(v => v < 0 || v > 1)))
                    throw new InvalidDataException($"{ctx}: 'at' must be [x, y] fractions between 0 and 1.");
                if (st.Count < 0) throw new InvalidDataException($"{ctx}: state count must be >= 0.");
            }
            foreach (var l in p.Independents.Levels) Level(l, ctx + " independents.levels");
            Level(p.Independents.FreeCityLevel, ctx + " independents.freeCityLevel");
            if (p.StartPort is { Enabled: true } sp)
            {
                Level(sp.Level, ctx + " startPort.level");
                Pair(sp.Distance, ctx + " startPort.distance");
            }
        }
    }

    private void Link()
    {
        for (int i = 0; i < Resources.Count; i++)
        {
            var r = Resources[i];
            r.Index = i;
            if (!_resById.TryAdd(r.Id, r)) throw new InvalidDataException($"Duplicate resource id '{r.Id}'.");
        }

        for (int i = 0; i < Buildings.Count; i++)
        {
            var b = Buildings[i];
            b.Index = i;
            if (!_bldById.TryAdd(b.Id, b)) throw new InvalidDataException($"Duplicate building id '{b.Id}'.");

            var mask = new bool[Biomes.Count];
            if (b.AllowedBiomes.Count == 0)
            {
                for (int k = 0; k < Biomes.Count; k++) mask[k] = Biomes.Info((Biome)k).IsBuildable;
            }
            else
            {
                foreach (var name in b.AllowedBiomes)
                {
                    Biome biome;
                    try { biome = Biomes.Parse(name); }
                    catch (FormatException ex) { throw new InvalidDataException($"Building '{b.Id}': {ex.Message}"); }
                    if (Biomes.IsWater(biome)) throw new InvalidDataException($"Building '{b.Id}': cannot build on water biome '{name}'.");
                    mask[(int)biome] = true;
                }
            }
            b.AllowedBiomeMask = mask;
            b.BuildCostList = ResolveCounts(b.BuildCost, $"Building '{b.Id}' buildCost");

            var accepts = new bool[Resources.Count];
            foreach (var recipe in b.Recipes)
            {
                recipe.InputList = ResolveCounts(recipe.Inputs, $"Building '{b.Id}' recipe inputs");
                recipe.OutputList = ResolveCounts(recipe.Outputs, $"Building '{b.Id}' recipe outputs");
                if (recipe.CycleSeconds <= 0) throw new InvalidDataException($"Building '{b.Id}': cycleSeconds must be > 0.");
                foreach (var (res, amount) in recipe.InputList)
                {
                    accepts[res] = true;
                    if (amount > b.Storage) throw new InvalidDataException($"Building '{b.Id}': recipe needs {amount} but storage is {b.Storage}.");
                }
            }
            b.AcceptsInput = accepts;
        }

        for (int i = 0; i < Ships.Count; i++)
        {
            var s = Ships[i];
            s.Index = i;
            if (!_shipById.TryAdd(s.Id, s)) throw new InvalidDataException($"Duplicate ship id '{s.Id}'.");
            s.UpgradeCostList = ResolveCounts(s.UpgradeCost, $"Ship '{s.Id}' upgradeCost");
        }
        foreach (var s in Ships)
        {
            if (string.IsNullOrEmpty(s.UpgradesTo)) continue;
            if (!_shipById.TryGetValue(s.UpgradesTo, out var target))
                throw new InvalidDataException($"Ship '{s.Id}': upgradesTo unknown class '{s.UpgradesTo}'.");
            s.UpgradeTarget = target;
        }

        for (int i = 0; i < Units.Count; i++)
        {
            var u = Units[i];
            u.Index = i;
            if (!_unitById.TryAdd(u.Id, u)) throw new InvalidDataException($"Duplicate unit id '{u.Id}'.");
            if (u.Members <= 0 || u.HpPerMember <= 0) throw new InvalidDataException($"Unit '{u.Id}': members and hpPerMember must be > 0.");
            if (u.Range <= 0 || u.Reload <= 0 || u.Speed <= 0) throw new InvalidDataException($"Unit '{u.Id}': range, reload and speed must be > 0.");
            u.RecruitCostList = ResolveCounts(u.RecruitCost, $"Unit '{u.Id}' recruitCost");
        }
        foreach (var id in Config.StartTroops)
            if (!_unitById.ContainsKey(id)) throw new InvalidDataException($"config startTroops: unknown unit '{id}'.");
        foreach (var us in Config.UnitSpawns)
        {
            if (!_unitById.TryGetValue(us.Unit, out var ud)) throw new InvalidDataException($"config unitSpawns: unknown unit '{us.Unit}'.");
            us.Def = ud;
        }

        var pol = Config.Politics;
        if (pol.Levels.Count == 0) throw new InvalidDataException("config politics.levels: at least one settlement level is required.");
        if (pol.Levels[0].MinPopulation != 0) throw new InvalidDataException("config politics.levels: the first level must start at population 0.");
        for (int i = 1; i < pol.Levels.Count; i++)
            if (pol.Levels[i].MinPopulation <= pol.Levels[i - 1].MinPopulation)
                throw new InvalidDataException("config politics.levels: minPopulation must increase.");
        if (pol.Behaviours.Count == 0) throw new InvalidDataException("config politics.behaviours: none defined.");
        if (pol.CampaignCooldown.Length != 2) throw new InvalidDataException("config politics.campaignCooldown: expected [min, max].");
        ValidateSetup();
        if (!_bldById.TryGetValue("town_hall", out var th) || !th.Services.Contains("town_hall"))
            throw new InvalidDataException("buildings.json: a 'town_hall' building with the 'town_hall' service is required.");

        if (!_shipById.ContainsKey(Config.StartShip)) throw new InvalidDataException($"config: unknown startShip '{Config.StartShip}'.");
        ResolveCounts(Config.StartCargo, "config startCargo");
        if (!_resById.ContainsKey(Config.RepairResource)) throw new InvalidDataException($"config: unknown repairResource '{Config.RepairResource}'.");
        if (!_resById.ContainsKey(Config.AmmoResource)) throw new InvalidDataException($"config: unknown ammoResource '{Config.AmmoResource}'.");
        foreach (var sp in Config.Spawns)
        {
            if (!_shipById.TryGetValue(sp.Ship, out var cls)) throw new InvalidDataException($"config spawns: unknown ship '{sp.Ship}'.");
            sp.Class = cls;
            ResolveCounts(sp.Cargo, $"config spawn '{sp.Ship}' cargo");
        }
    }
}
