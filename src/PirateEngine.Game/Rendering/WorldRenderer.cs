using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Game.Rendering;

/// <summary>
/// Terrain renderer built for very large maps. Two levels of detail:
/// <list type="bullet">
/// <item><b>Overview</b> (zoomed out): the map is baked into 256×256-tile chunk textures at one
/// texel per tile (with hill-shading, depth tint and fog baked in). Drawing the whole world is
/// a handful of quads. Chunks are rebuilt lazily when exploration changes them.</item>
/// <item><b>Detail</b> (zoomed in): only the visible tiles are drawn, each with procedural tile
/// art, elevation shading, animated water and shoreline foam.</item>
/// </list>
/// The same chunk textures feed the minimap, so it costs nothing extra.
/// </summary>
public sealed class WorldRenderer
{
    public const int ChunkTiles = 256;
    public float DetailThreshold = 9f; // pixels per tile at which detail mode kicks in
    public bool ShowGrid;

    private readonly TileMap _map;
    private readonly FogOfWar _fog;
    private readonly TileArt _art;
    private readonly Draw _draw;
    private readonly GraphicsDevice _device;
    private readonly int _chunksX, _chunksY;
    private readonly Texture2D?[] _chunks;
    private readonly long[] _chunkStamp;
    private readonly Color[] _buffer = new Color[ChunkTiles * ChunkTiles];
    private readonly float[] _shade;   // per-tile hill-shade factor, computed once
    private readonly Color[] _overviewColor; // per-tile explored colour, computed once

    public static readonly Color FogColor = new(10, 14, 22);

    public int ChunksX => _chunksX;
    public int ChunksY => _chunksY;
    public int TilesDrawnLastFrame { get; private set; }
    public int ChunksRebuiltTotal { get; private set; }

    public WorldRenderer(GraphicsDevice device, TileMap map, FogOfWar fog, TileArt art, Draw draw)
    {
        _device = device;
        _map = map;
        _fog = fog;
        _art = art;
        _draw = draw;
        _chunksX = (map.Width + ChunkTiles - 1) / ChunkTiles;
        _chunksY = (map.Height + ChunkTiles - 1) / ChunkTiles;
        _chunks = new Texture2D?[_chunksX * _chunksY];
        _chunkStamp = new long[_chunks.Length];
        Array.Fill(_chunkStamp, -1);
        _shade = new float[map.Count];
        _overviewColor = new Color[map.Count];
        PrecomputeShading();
    }

    private void PrecomputeShading()
    {
        int w = _map.Width, h = _map.Height;
        var e = _map.Elevation;
        float sea = _map.SeaLevel;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var biome = _map.Biome[i];
                var info = Biomes.Info(biome);
                float shade;
                if (info.IsWater)
                {
                    // Deeper water darker.
                    float depth = Math.Clamp((sea - e[i]) / Math.Max(sea, 1e-3f), 0f, 1f);
                    shade = 1.08f - depth * 0.35f;
                }
                else
                {
                    // Lambert-ish hill shading from the north-west.
                    float ex = e[Math.Max(0, x - 1) + y * w] - e[Math.Min(w - 1, x + 1) + y * w];
                    float ey = e[x + Math.Max(0, y - 1) * w] - e[x + Math.Min(h - 1, y + 1) * w];
                    shade = Math.Clamp(1f + (ex + ey) * 9f, 0.72f, 1.18f);
                }
                _shade[i] = shade;
                uint n = Rng.Hash(x, y, 77) & 15;
                float grain = 0.97f + n * 0.004f;
                _overviewColor[i] = new Color(
                    (int)Math.Clamp(info.R * shade * grain, 0, 255),
                    (int)Math.Clamp(info.G * shade * grain, 0, 255),
                    (int)Math.Clamp(info.B * shade * grain, 0, 255));
            }
        });
    }

    // ---- overview chunks -------------------------------------------------------------

    private long FogStamp(int cx, int cy)
    {
        if (!_fog.Enabled) return long.MaxValue;
        const int per = ChunkTiles / FogOfWar.ChunkSize;
        long sum = 0;
        for (int fy = cy * per; fy < Math.Min(_fog.ChunksY, (cy + 1) * per); fy++)
            for (int fx = cx * per; fx < Math.Min(_fog.ChunksX, (cx + 1) * per); fx++)
                sum = sum * 31 + _fog.ChunkRevision(fx, fy);
        return sum;
    }

    private void RebuildChunk(int cx, int cy)
    {
        int x0 = cx * ChunkTiles, y0 = cy * ChunkTiles;
        int cw = Math.Min(ChunkTiles, _map.Width - x0), ch = Math.Min(ChunkTiles, _map.Height - y0);
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                int wx = x0 + x, wy = y0 + y;
                _buffer[y * cw + x] = _fog.IsExplored(wx, wy) ? _overviewColor[_map.Index(wx, wy)] : FogColor;
            }
        }
        ref var tex = ref _chunks[cy * _chunksX + cx];
        tex ??= new Texture2D(_device, cw, ch);
        if (cw * ch == _buffer.Length) tex.SetData(_buffer);
        else tex.SetData(_buffer[..(cw * ch)]);
        ChunksRebuiltTotal++;
    }

    /// <summary>Rebuilds chunks whose fog changed (at most <paramref name="budget"/> per call).</summary>
    public void UpdateChunks(int budget = 2)
    {
        for (int cy = 0; cy < _chunksY && budget > 0; cy++)
        for (int cx = 0; cx < _chunksX && budget > 0; cx++)
        {
            int i = cy * _chunksX + cx;
            long stamp = FogStamp(cx, cy);
            if (_chunks[i] != null && stamp == _chunkStamp[i]) continue;
            RebuildChunk(cx, cy);
            _chunkStamp[i] = stamp;
            budget--;
        }
    }

    /// <summary>Forces every chunk to exist (call once after loading).</summary>
    public void BuildAll() => UpdateChunks(int.MaxValue);

    /// <summary>Draws the baked overview. Call inside a SpriteBatch using the camera transform.</summary>
    public void DrawOverview(SpriteBatch sb, Rectangle visibleTiles)
    {
        for (int cy = 0; cy < _chunksY; cy++)
        for (int cx = 0; cx < _chunksX; cx++)
        {
            var tex = _chunks[cy * _chunksX + cx];
            if (tex == null) continue;
            var dest = new Rectangle(cx * ChunkTiles, cy * ChunkTiles, tex.Width, tex.Height);
            if (!dest.Intersects(visibleTiles)) continue;
            sb.Draw(tex, dest, Color.White);
        }
    }

    /// <summary>Draws the overview scaled into a screen rectangle (for the minimap).</summary>
    public void DrawInto(SpriteBatch sb, Rectangle screen)
    {
        float sx = screen.Width / (float)_map.Width, sy = screen.Height / (float)_map.Height;
        for (int cy = 0; cy < _chunksY; cy++)
        for (int cx = 0; cx < _chunksX; cx++)
        {
            var tex = _chunks[cy * _chunksX + cx];
            if (tex == null) continue;
            int x0 = screen.X + (int)MathF.Round(cx * ChunkTiles * sx), y0 = screen.Y + (int)MathF.Round(cy * ChunkTiles * sy);
            int x1 = screen.X + (int)MathF.Round((cx * ChunkTiles + tex.Width) * sx), y1 = screen.Y + (int)MathF.Round((cy * ChunkTiles + tex.Height) * sy);
            sb.Draw(tex, new Rectangle(x0, y0, x1 - x0, y1 - y0), Color.White);
        }
    }

    // ---- detail ---------------------------------------------------------------------

    public void DrawDetail(SpriteBatch sb, Rectangle visible, float time)
    {
        int x0 = Math.Max(0, visible.X), y0 = Math.Max(0, visible.Y);
        int x1 = Math.Min(_map.Width, visible.Right), y1 = Math.Min(_map.Height, visible.Bottom);
        int frame = (int)(time * 1.6f);
        int count = 0;
        var atlas = _art.Atlas;

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                var dest = new Rectangle(x, y, 1, 1);
                if (!_fog.IsExplored(x, y))
                {
                    sb.Draw(_draw.Pixel, dest, FogColor);
                    continue;
                }
                int i = y * _map.Width + x;
                var biome = _map.Biome[i];
                uint h = Rng.Hash(x, y, 1);
                bool water = Biomes.IsWater(biome);
                int variant = water ? (int)((h + (uint)frame + (uint)((x + y) >> 2)) & 3) : (int)(h & 3);
                float s = Math.Min(1f, _shade[i]);
                sb.Draw(atlas, dest, _art.Source(biome, variant), new Color(s, s, s));
                count++;
            }
        }

        // Shoreline foam: a thin pulsing strip on water edges that touch land.
        float pulse = 0.35f + 0.2f * MathF.Sin(time * 2.2f);
        var foam = Color.White * pulse;
        const float t = 0.12f;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (!_map.IsWater(x, y) || !_fog.IsExplored(x, y)) continue;
                if (_map.IsLand(x - 1, y)) _draw.RectF(sb, new Vector2(x, y), new Vector2(t, 1), foam);
                if (_map.IsLand(x + 1, y)) _draw.RectF(sb, new Vector2(x + 1 - t, y), new Vector2(t, 1), foam);
                if (_map.IsLand(x, y - 1)) _draw.RectF(sb, new Vector2(x, y), new Vector2(1, t), foam);
                if (_map.IsLand(x, y + 1)) _draw.RectF(sb, new Vector2(x, y + 1 - t), new Vector2(1, t), foam);
            }
        }

        if (ShowGrid)
        {
            var gc = Color.Black * 0.18f;
            for (int x = x0; x <= x1; x++) _draw.RectF(sb, new Vector2(x, y0), new Vector2(0.04f, y1 - y0), gc);
            for (int y = y0; y <= y1; y++) _draw.RectF(sb, new Vector2(x0, y), new Vector2(x1 - x0, 0.04f), gc);
        }
        TilesDrawnLastFrame = count;
    }
}
