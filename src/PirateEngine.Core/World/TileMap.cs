using PirateEngine.Core.Util;

namespace PirateEngine.Core.World;

/// <summary>
/// The world grid. Stored as flat structure-of-arrays (index = y * Width + x) so a
/// 1000×1000+ map is a handful of contiguous buffers rather than a million objects.
/// Tiles are 1 world unit wide; a ship occupies one tile.
/// </summary>
public sealed class TileMap
{
    public int Width { get; }
    public int Height { get; }
    public int Seed { get; internal set; }

    /// <summary>Biome per tile.</summary>
    public readonly Biome[] Biome;
    /// <summary>Normalised elevation [0,1]; values below <see cref="SeaLevel"/> are water.</summary>
    public readonly float[] Elevation;
    /// <summary>Climate temperature [0,1] (0 = polar, 1 = equatorial).</summary>
    public readonly float[] Temperature;
    /// <summary>Climate moisture [0,1].</summary>
    public readonly float[] Moisture;
    /// <summary>Connected-region id per tile (4-connectivity). Water and land regions share one id space.</summary>
    public readonly int[] Region;

    public float SeaLevel { get; internal set; }

    /// <summary>Size (tile count) of each region, indexed by region id.</summary>
    public int[] RegionSizes { get; internal set; } = Array.Empty<int>();
    /// <summary>Whether each region is water.</summary>
    public bool[] RegionIsWater { get; internal set; } = Array.Empty<bool>();
    /// <summary>The largest water body — the open sea that ships sail on.</summary>
    public int MainOceanRegion { get; internal set; } = -1;

    /// <summary>Recommended player start (a water tile next to a sizeable landmass).</summary>
    public GridPoint StartTile { get; internal set; }

    // Building occupancy is sparse (thousands at most vs a million tiles), so a dictionary
    // beats a 4 MB array.
    private readonly Dictionary<int, int> _buildingAt = new();

    public TileMap(int width, int height)
    {
        if (width < 16 || height < 16) throw new ArgumentException("Map must be at least 16x16.");
        Width = width;
        Height = height;
        int n = width * height;
        Biome = new Biome[n];
        Elevation = new float[n];
        Temperature = new float[n];
        Moisture = new float[n];
        Region = new int[n];
    }

    public int Count => Width * Height;

    public int Index(int x, int y) => y * Width + x;
    public int Index(GridPoint p) => p.Y * Width + p.X;
    public GridPoint PointOf(int index) => new(index % Width, index / Width);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(GridPoint p) => InBounds(p.X, p.Y);

    public Biome GetBiome(int x, int y) => Biome[Index(x, y)];
    public Biome GetBiome(GridPoint p) => Biome[Index(p)];

    public bool IsWater(int x, int y) => InBounds(x, y) && Biomes.IsWater(Biome[Index(x, y)]);
    public bool IsWater(GridPoint p) => IsWater(p.X, p.Y);
    public bool IsLand(int x, int y) => InBounds(x, y) && !Biomes.IsWater(Biome[Index(x, y)]);
    public bool IsLand(GridPoint p) => IsLand(p.X, p.Y);

    public int RegionAt(GridPoint p) => InBounds(p) ? Region[Index(p)] : -1;

    /// <summary>True if the land tile touches water (4-neighbourhood).</summary>
    public bool IsCoast(GridPoint p)
    {
        if (!IsLand(p)) return false;
        for (int i = 0; i < 4; i++)
        {
            var n = p + GridPoint.Neighbours8[i];
            if (IsWater(n)) return true;
        }
        return false;
    }

    // ---- building occupancy -------------------------------------------------

    public int BuildingAt(GridPoint p) => InBounds(p) && _buildingAt.TryGetValue(Index(p), out var id) ? id : -1;
    internal void SetBuilding(GridPoint p, int buildingId) => _buildingAt[Index(p)] = buildingId;
    internal void ClearBuilding(GridPoint p) => _buildingAt.Remove(Index(p));
    internal void ClearAllBuildings() => _buildingAt.Clear();

    // ---- queries ------------------------------------------------------------

    /// <summary>
    /// Finds the nearest tile (by ring search, Chebyshev distance) satisfying <paramref name="pred"/>.
    /// Returns <see cref="GridPoint.Invalid"/> if none is found within <paramref name="maxRadius"/>.
    /// </summary>
    public GridPoint FindNearest(GridPoint origin, int maxRadius, Func<GridPoint, bool> pred)
    {
        if (InBounds(origin) && pred(origin)) return origin;
        GridPoint best = GridPoint.Invalid;
        float bestD = float.MaxValue;
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring only
                    var p = new GridPoint(origin.X + dx, origin.Y + dy);
                    if (!InBounds(p) || !pred(p)) continue;
                    float d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = p; }
                }
            }
            if (best.IsValid) return best; // Euclidean-closest within the first ring that hits
        }
        return best;
    }
}
