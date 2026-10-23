using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Game.Rendering;

/// <summary>
/// Procedurally paints all terrain art at startup: <see cref="Variants"/> 16×16 tiles per biome,
/// packed into one atlas with 1-pixel extruded borders (prevents texture bleeding when tiles
/// are drawn at fractional zoom levels). Water biomes' variants are successive phases of a
/// wave pattern and are cycled over time to animate the sea.
/// </summary>
public sealed class TileArt
{
    public const int Size = 16, Variants = 4, Cell = Size + 2;
    public Texture2D Atlas { get; }

    public TileArt(GraphicsDevice device)
    {
        int w = Variants * Cell, h = Biomes.Count * Cell;
        var atlas = new Color[w * h];
        var tile = new Color[Size * Size];
        for (int b = 0; b < Biomes.Count; b++)
        {
            for (int v = 0; v < Variants; v++)
            {
                Paint((Biome)b, v, tile);
                Blit(tile, atlas, w, v * Cell, b * Cell);
            }
        }
        Atlas = new Texture2D(device, w, h);
        Atlas.SetData(atlas);
    }

    public Rectangle Source(Biome b, int variant) =>
        new(variant * Cell + 1, (int)b * Cell + 1, Size, Size);

    /// <summary>Copies a tile into the atlas with its edge pixels extruded by one.</summary>
    private static void Blit(Color[] tile, Color[] atlas, int atlasW, int ox, int oy)
    {
        for (int y = -1; y <= Size; y++)
        for (int x = -1; x <= Size; x++)
        {
            int sx = Math.Clamp(x, 0, Size - 1), sy = Math.Clamp(y, 0, Size - 1);
            atlas[(oy + y + 1) * atlasW + ox + x + 1] = tile[sy * Size + sx];
        }
    }

    // ---------------------------------------------------------------------------------

    private static Color Base(Biome b)
    {
        var i = Biomes.Info(b);
        return new Color(i.R, i.G, i.B);
    }

    private static Color Shade(Color c, float f) =>
        new((int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255));

    private static void Paint(Biome b, int variant, Color[] px)
    {
        var rng = new Rng(Rng.Mix((ulong)((int)b * 31 + variant + 1)));
        var baseC = Base(b);
        // Subtle per-pixel grain.
        for (int i = 0; i < px.Length; i++) px[i] = Shade(baseC, 0.94f + rng.NextFloat() * 0.1f);

        switch (b)
        {
            case Biome.DeepOcean:
            case Biome.Ocean:
            case Biome.ShallowWater:
                Waves(px, baseC, variant, b == Biome.ShallowWater ? 1.25f : b == Biome.Ocean ? 1.18f : 1.12f);
                if (b == Biome.ShallowWater)
                    for (int k = 0; k < 6; k++) Dot(px, rng.Next(16), rng.Next(16), Shade(baseC, 1.08f));
                break;
            case Biome.Beach:
                for (int k = 0; k < 14; k++) Dot(px, rng.Next(16), rng.Next(16), Shade(baseC, rng.Chance(0.5f) ? 0.86f : 1.06f));
                break;
            case Biome.Desert:
                for (int y = 2; y < 16; y += 5)
                    for (int x = 0; x < 16; x++)
                        Dot(px, x, y + (int)(MathF.Sin((x + variant * 4) * 0.6f) * 1.2f), Shade(baseC, 0.85f));
                break;
            case Biome.Grassland:
                for (int k = 0; k < 9; k++) Tuft(px, rng.Next(1, 15), rng.Next(2, 15), Shade(baseC, 0.75f), Shade(baseC, 1.18f));
                break;
            case Biome.Savanna:
                for (int k = 0; k < 5; k++) Tuft(px, rng.Next(1, 15), rng.Next(2, 15), Shade(baseC, 0.78f), Shade(baseC, 1.15f));
                if (variant % 2 == 0) Tree(px, rng.Next(5, 11), rng.Next(5, 11), 3, new Color(88, 110, 50), new Color(60, 46, 30));
                break;
            case Biome.TemperateForest:
                for (int k = 0; k < 4; k++) Tree(px, rng.Next(3, 13), rng.Next(3, 13), 3, Shade(baseC, 1.15f), new Color(60, 40, 24));
                break;
            case Biome.Jungle:
                for (int k = 0; k < 6; k++) Tree(px, rng.Next(2, 14), rng.Next(2, 14), 3, Shade(baseC, 1.3f), Shade(baseC, 0.6f));
                break;
            case Biome.Swamp:
                for (int k = 0; k < 3; k++) Blob(px, rng.Next(3, 13), rng.Next(3, 13), 2, new Color(56, 84, 80));
                for (int k = 0; k < 5; k++) Tuft(px, rng.Next(1, 15), rng.Next(2, 15), Shade(baseC, 0.7f), Shade(baseC, 1.3f));
                break;
            case Biome.Taiga:
                for (int k = 0; k < 4; k++) Pine(px, rng.Next(3, 13), rng.Next(4, 14), Shade(baseC, 0.75f), Shade(baseC, 1.15f));
                break;
            case Biome.Tundra:
                for (int k = 0; k < 6; k++) Dot(px, rng.Next(16), rng.Next(16), Shade(baseC, rng.Chance(0.5f) ? 0.8f : 1.15f));
                break;
            case Biome.Snow:
                for (int k = 0; k < 8; k++) Dot(px, rng.Next(16), rng.Next(16), new Color(206, 218, 232));
                break;
            case Biome.Hills:
                Hill(px, rng.Next(4, 12), rng.Next(8, 13), 6, baseC);
                break;
            case Biome.Mountain:
                Peak(px, rng.Next(6, 10), 3 + rng.Next(2), 12, baseC);
                break;
        }
    }

    private static void Dot(Color[] px, int x, int y, Color c)
    {
        if ((uint)x < Size && (uint)y < Size) px[y * Size + x] = c;
    }

    private static void Waves(Color[] px, Color baseC, int phase, float crest)
    {
        // Short horizontal crests drifting diagonally with phase → animates when cycled.
        for (int k = 0; k < 4; k++)
        {
            int y = (k * 4 + phase) % 16;
            int x0 = (k * 7 + phase * 3) % 16;
            for (int i = 0; i < 4; i++) Dot(px, (x0 + i) % 16, y, Shade(baseC, crest));
            Dot(px, (x0 + 4) % 16, (y + 1) % 16, Shade(baseC, crest * 0.95f));
        }
    }

    private static void Tuft(Color[] px, int x, int y, Color dark, Color light)
    {
        Dot(px, x, y, dark);
        Dot(px, x - 1, y - 1, light);
        Dot(px, x + 1, y - 1, light);
    }

    private static void Blob(Color[] px, int cx, int cy, int r, Color c)
    {
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r * r) Dot(px, cx + x, cy + y, c);
    }

    private static void Tree(Color[] px, int cx, int cy, int r, Color leaf, Color trunk)
    {
        Dot(px, cx, cy + r, trunk);
        Blob(px, cx, cy, r, Shade(leaf, 0.75f));
        Blob(px, cx - 1, cy - 1, r - 1, leaf);
        Dot(px, cx - 1, cy - 2, Shade(leaf, 1.25f));
    }

    private static void Pine(Color[] px, int cx, int baseY, Color dark, Color light)
    {
        for (int row = 0; row < 6; row++)
        {
            int half = row / 2;
            for (int x = -half; x <= half; x++) Dot(px, cx + x, baseY - 6 + row, x < 0 ? light : dark);
        }
        Dot(px, cx, baseY, new Color(70, 50, 30));
    }

    private static void Hill(Color[] px, int cx, int baseY, int r, Color c)
    {
        for (int y = 0; y < r; y++)
        for (int x = -r; x <= r; x++)
        {
            if (x * x + (y * 2) * (y * 2) > r * r * 1.2f) continue;
            Dot(px, cx + x, baseY - y, Shade(c, x < 0 ? 1.18f : 0.88f));
        }
    }

    private static void Peak(Color[] px, int cx, int top, int height, Color c)
    {
        for (int row = 0; row < height; row++)
        {
            int half = (row * 7) / height;
            for (int x = -half; x <= half; x++)
            {
                var col = Shade(c, x < 0 ? 1.25f : 0.8f);
                if (row < 3) col = x < 0 ? new Color(245, 248, 252) : new Color(200, 210, 222); // snowcap
                Dot(px, cx + x, top + row, col);
            }
        }
    }
}
