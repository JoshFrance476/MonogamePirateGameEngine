using PirateEngine.Core.Util;

namespace PirateEngine.Core.World;

/// <summary>
/// Explored-tile mask, packed 1 bit per tile (≈128 KB for 1024²). Tracks a per-chunk
/// revision number so renderers can re-upload only the chunks that changed.
/// </summary>
public sealed class FogOfWar
{
    public const int ChunkSize = 64;

    private readonly ulong[] _bits;
    private readonly int[] _chunkRevision;
    public int Width { get; }
    public int Height { get; }
    public int ChunksX { get; }
    public int ChunksY { get; }
    public bool Enabled { get; set; } = false;

    public FogOfWar(int width, int height)
    {
        Width = width;
        Height = height;
        _bits = new ulong[(width * height + 63) / 64];
        ChunksX = (width + ChunkSize - 1) / ChunkSize;
        ChunksY = (height + ChunkSize - 1) / ChunkSize;
        _chunkRevision = new int[ChunksX * ChunksY];
    }

    public bool IsExplored(int x, int y)
    {
        if (!Enabled) return true;
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return false;
        int i = y * Width + x;
        return (_bits[i >> 6] & (1UL << (i & 63))) != 0;
    }

    public bool IsExplored(GridPoint p) => IsExplored(p.X, p.Y);

    public int ChunkRevision(int cx, int cy) => _chunkRevision[cy * ChunksX + cx];

    /// <summary>Reveals a filled circle. Returns the number of newly explored tiles.</summary>
    public int Reveal(GridPoint centre, int radius)
    {
        int newly = 0, r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = centre.Y + dy;
            if ((uint)y >= (uint)Height) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int x = centre.X + dx;
                if ((uint)x >= (uint)Width) continue;
                int i = y * Width + x;
                ulong mask = 1UL << (i & 63);
                if ((_bits[i >> 6] & mask) != 0) continue;
                _bits[i >> 6] |= mask;
                _chunkRevision[(y / ChunkSize) * ChunksX + x / ChunkSize]++;
                newly++;
            }
        }
        return newly;
    }

    public void RevealAll()
    {
        Array.Fill(_bits, ulong.MaxValue);
        for (int i = 0; i < _chunkRevision.Length; i++) _chunkRevision[i]++;
    }

    public string Serialize() => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_bits.AsSpan()).ToArray());

    public void Deserialize(string data)
    {
        var bytes = Convert.FromBase64String(data);
        if (bytes.Length != _bits.Length * 8) throw new InvalidDataException("Fog data size mismatch.");
        Buffer.BlockCopy(bytes, 0, _bits, 0, bytes.Length);
        for (int i = 0; i < _chunkRevision.Length; i++) _chunkRevision[i]++;
    }

    public int CountExplored()
    {
        int c = 0;
        foreach (var b in _bits) c += System.Numerics.BitOperations.PopCount(b);
        return Math.Min(c, Width * Height);
    }
}
