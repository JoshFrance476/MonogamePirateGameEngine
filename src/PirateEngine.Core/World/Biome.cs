namespace PirateEngine.Core.World;

/// <summary>Every tile has exactly one biome. Water biomes are navigable; the rest are land.</summary>
public enum Biome : byte
{
    DeepOcean,
    Ocean,
    ShallowWater,
    Beach,
    Desert,
    Savanna,
    Grassland,
    TemperateForest,
    Jungle,
    Swamp,
    Taiga,
    Tundra,
    Snow,
    Hills,
    Mountain,
}

/// <summary>Static per-biome properties. Colours are plain RGB bytes so Core stays render-agnostic.</summary>
public readonly record struct BiomeInfo(string Name, byte R, byte G, byte B, bool IsWater, bool IsBuildable);

public static class Biomes
{
    public const int Count = 15;

    private static readonly BiomeInfo[] Table =
    {
        new("Deep Ocean",       18,  40,  88, true,  false),
        new("Ocean",            28,  70, 132, true,  false),
        new("Shallows",         52, 126, 170, true,  false),
        new("Beach",           222, 206, 150, false, true),
        new("Desert",          214, 182, 110, false, true),
        new("Savanna",         170, 168,  80, false, true),
        new("Grassland",        98, 160,  70, false, true),
        new("Temperate Forest", 46, 112,  50, false, true),
        new("Jungle",           22,  92,  40, false, true),
        new("Swamp",            70,  92,  60, false, true),
        new("Taiga",            56,  94,  78, false, true),
        new("Tundra",          150, 156, 132, false, true),
        new("Snow",            236, 240, 244, false, false),
        new("Hills",           122, 118,  88, false, true),
        new("Mountain",        110, 104, 100, false, true),
    };

    /// <summary>
    /// Movement cost for land units (1 = open ground). Pathfinding prefers cheap ground and units
    /// move at 1/cost of their speed on each tile. Water is impassable on foot.
    /// </summary>
    private static readonly float[] LandCosts =
    {
        0, 0, 0,          // water
        1.0f,             // Beach
        1.3f,             // Desert
        1.0f,             // Savanna
        1.0f,             // Grassland
        1.35f,            // Temperate Forest
        1.7f,             // Jungle
        1.9f,             // Swamp
        1.35f,            // Taiga
        1.15f,            // Tundra
        2.0f,             // Snow
        1.5f,             // Hills
        2.6f,             // Mountain
    };

    public static float LandCost(Biome b) => LandCosts[(int)b];

    public static BiomeInfo Info(Biome b) => Table[(int)b];
    public static bool IsWater(Biome b) => Table[(int)b].IsWater;
    public static string Name(Biome b) => Table[(int)b].Name;

    public static Biome Parse(string name)
    {
        if (Enum.TryParse<Biome>(name, ignoreCase: true, out var b)) return b;
        throw new FormatException($"Unknown biome '{name}'. Valid: {string.Join(", ", Enum.GetNames<Biome>())}");
    }
}
