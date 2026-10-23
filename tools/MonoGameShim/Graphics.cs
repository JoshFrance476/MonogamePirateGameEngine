// Software implementation of the SpriteBatch-based subset of Microsoft.Xna.Framework.Graphics.
using System.IO.Compression;

namespace Microsoft.Xna.Framework.Graphics;

public enum SpriteSortMode { Deferred, Immediate, Texture, BackToFront, FrontToBack }
[Flags] public enum SpriteEffects { None = 0, FlipHorizontally = 1, FlipVertically = 2 }

public sealed class BlendState { public static readonly BlendState AlphaBlend = new(), Opaque = new(), Additive = new(), NonPremultiplied = new(); }
public sealed class SamplerState
{
    public bool Linear { get; private init; }
    public static readonly SamplerState PointClamp = new(), PointWrap = new();
    public static readonly SamplerState LinearClamp = new() { Linear = true }, LinearWrap = new() { Linear = true };
}
public sealed class DepthStencilState { public static readonly DepthStencilState None = new(), Default = new(); }
public sealed class RasterizerState { public static readonly RasterizerState CullNone = new(), CullCounterClockwise = new(); }
public class Effect { }

public struct Viewport
{
    public int X, Y, Width, Height;
    public Viewport(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }
    public Rectangle Bounds => new(X, Y, Width, Height);
}

public sealed class PresentationParameters
{
    public int BackBufferWidth { get; set; }
    public int BackBufferHeight { get; set; }
}

public sealed class GraphicsDevice
{
    public PresentationParameters PresentationParameters { get; } = new();
    public Viewport Viewport => new(0, 0, PresentationParameters.BackBufferWidth, PresentationParameters.BackBufferHeight);

    /// <summary>The software framebuffer (premultiplied RGBA, row-major).</summary>
    internal Color[] Framebuffer = Array.Empty<Color>();

    internal void Resize(int w, int h)
    {
        PresentationParameters.BackBufferWidth = w;
        PresentationParameters.BackBufferHeight = h;
        Framebuffer = new Color[w * h];
    }

    public void Clear(Color c) => Array.Fill(Framebuffer, c);

    public void GetBackBufferData<T>(T[] data) where T : struct
    {
        if (data is not Color[] dst) throw new NotSupportedException("Shim supports Color only.");
        Array.Copy(Framebuffer, dst, Math.Min(dst.Length, Framebuffer.Length));
    }
}

public abstract class GraphicsResource : IDisposable
{
    public bool IsDisposed { get; private set; }
    public virtual void Dispose() => IsDisposed = true;
}

public class Texture2D : GraphicsResource
{
    public int Width { get; }
    public int Height { get; }
    internal Color[] Data;
    public Rectangle Bounds => new(0, 0, Width, Height);

    public Texture2D(GraphicsDevice device, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Data = new Color[width * height];
    }

    public void SetData<T>(T[] data) where T : struct => SetData(data, 0, data.Length);

    public void SetData<T>(T[] data, int startIndex, int elementCount) where T : struct
    {
        if (data is not Color[] src) throw new NotSupportedException("Shim supports Color only.");
        if (elementCount != Width * Height) throw new ArgumentException($"Expected {Width * Height} elements, got {elementCount}.");
        Array.Copy(src, startIndex, Data, 0, elementCount);
    }

    public void GetData<T>(T[] data) where T : struct
    {
        if (data is not Color[] dst) throw new NotSupportedException();
        Array.Copy(Data, dst, Math.Min(dst.Length, Data.Length));
    }

    public void SaveAsPng(Stream stream, int width, int height) => Png.Write(stream, Data, Width, Height);
}

/// <summary>
/// Draws textured quads into the device's software framebuffer with the same semantics the
/// game relies on: sprite transform (position/origin/rotation/scale), a 2D transform matrix,
/// colour tinting, premultiplied alpha blending, and point sampling.
/// </summary>
public sealed class SpriteBatch : GraphicsResource
{
    private readonly GraphicsDevice _device;
    private Matrix _m = Matrix.Identity;
    private bool _begun;
    public static long PixelsShaded;

    public SpriteBatch(GraphicsDevice device) => _device = device;
    public GraphicsDevice GraphicsDevice => _device;

    public void Begin(SpriteSortMode sortMode = SpriteSortMode.Deferred, BlendState blendState = null,
        SamplerState samplerState = null, DepthStencilState depthStencilState = null,
        RasterizerState rasterizerState = null, Effect effect = null, Matrix? transformMatrix = null)
    {
        if (_begun) throw new InvalidOperationException("Begin called twice without End.");
        _begun = true;
        _m = transformMatrix ?? Matrix.Identity;
    }

    public void End()
    {
        if (!_begun) throw new InvalidOperationException("End called without Begin.");
        _begun = false;
    }

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Color color) =>
        Draw(texture, destinationRectangle, null, color);

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color)
    {
        var src = sourceRectangle ?? texture.Bounds;
        Quad(texture, src, color, new Vector2(destinationRectangle.X, destinationRectangle.Y), Vector2.Zero,
            new Vector2(destinationRectangle.Width / (float)src.Width, destinationRectangle.Height / (float)src.Height), 0f);
    }

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, SpriteEffects effects, float layerDepth)
    {
        var src = sourceRectangle ?? texture.Bounds;
        Quad(texture, src, color, new Vector2(destinationRectangle.X, destinationRectangle.Y), origin,
            new Vector2(destinationRectangle.Width / (float)src.Width, destinationRectangle.Height / (float)src.Height), rotation);
    }

    public void Draw(Texture2D texture, Vector2 position, Color color) =>
        Quad(texture, texture.Bounds, color, position, Vector2.Zero, Vector2.One, 0f);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color) =>
        Quad(texture, sourceRectangle ?? texture.Bounds, color, position, Vector2.Zero, Vector2.One, 0f);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color, float rotation,
        Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth) =>
        Quad(texture, sourceRectangle ?? texture.Bounds, color, position, origin, scale, rotation);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color, float rotation,
        Vector2 origin, float scale, SpriteEffects effects, float layerDepth) =>
        Quad(texture, sourceRectangle ?? texture.Bounds, color, position, origin, new Vector2(scale, scale), rotation);

    private void Quad(Texture2D tex, Rectangle src, Color tint, Vector2 position, Vector2 origin, Vector2 scale, float rotation)
    {
        if (!_begun) throw new InvalidOperationException("Draw called outside Begin/End.");
        if (src.Width <= 0 || src.Height <= 0 || tint.A == 0 && tint.R == 0 && tint.G == 0 && tint.B == 0) return;

        // texel (u,v) → world: position + R(rot) * ((u,v) - origin) * scale ; world → screen via matrix.
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
        // Linear part L (2x2) and offset t mapping (u,v) to world.
        float a = cos * scale.X, b = -sin * scale.Y, c = sin * scale.X, d = cos * scale.Y;
        float tx = position.X - (a * origin.X + b * origin.Y), ty = position.Y - (c * origin.X + d * origin.Y);
        // Compose with matrix (row-vector convention): screen = (x*M11 + y*M21 + M41, x*M12 + y*M22 + M42)
        float A = a * _m.M11 + c * _m.M21, B = b * _m.M11 + d * _m.M21;
        float C = a * _m.M12 + c * _m.M22, D = b * _m.M12 + d * _m.M22;
        float TX = tx * _m.M11 + ty * _m.M21 + _m.M41, TY = tx * _m.M12 + ty * _m.M22 + _m.M42;

        float det = A * D - B * C;
        if (MathF.Abs(det) < 1e-12f) return;

        // Screen-space bounding box of the quad.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (u, v) in new[] { (0f, 0f), (src.Width, 0f), (0f, (float)src.Height), ((float)src.Width, (float)src.Height) })
        {
            float sx = A * u + B * v + TX, sy = C * u + D * v + TY;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
        }
        int w = _device.PresentationParameters.BackBufferWidth, h = _device.PresentationParameters.BackBufferHeight;
        int x0 = Math.Max(0, (int)MathF.Floor(minX)), x1 = Math.Min(w - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY)), y1 = Math.Min(h - 1, (int)MathF.Ceiling(maxY));
        if (x0 > x1 || y0 > y1) return;

        float ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        var fb = _device.Framebuffer;
        var texData = tex.Data;
        int tw = tex.Width;
        int tr = tint.R, tg = tint.G, tb = tint.B, ta = tint.A;

        for (int py = y0; py <= y1; py++)
        {
            float sy = py + 0.5f - TY;
            for (int px = x0; px <= x1; px++)
            {
                float sx = px + 0.5f - TX;
                float u = ia * sx + ib * sy, v = ic * sx + id * sy;
                if (u < 0 || v < 0 || u >= src.Width || v >= src.Height) continue;
                var t = texData[(src.Y + (int)v) * tw + src.X + (int)u];
                if (t.A == 0 && t.R == 0 && t.G == 0 && t.B == 0) continue;
                // Tint (premultiplied) then blend: dst = src + dst * (1 - srcA).
                int sr = t.R * tr / 255, sg = t.G * tg / 255, sb2 = t.B * tb / 255, sa = t.A * ta / 255;
                ref var dst = ref fb[py * w + px];
                int inv = 255 - sa;
                dst = new Color(sr + dst.R * inv / 255, sg + dst.G * inv / 255, sb2 + dst.B * inv / 255, sa + dst.A * inv / 255);
                PixelsShaded++;
            }
        }
    }
}

/// <summary>Minimal PNG encoder (RGBA8, zlib via System.IO.Compression).</summary>
internal static class Png
{
    public static void Write(Stream output, Color[] pixels, int width, int height)
    {
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(output, "IHDR", ihdr);

        var raw = new byte[(width * 4 + 1) * height];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                // Un-premultiply for a normal PNG; screenshots are opaque anyway.
                int a = c.A;
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, c.R * 255 / a);
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, c.G * 255 / a);
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, c.B * 255 / a);
                raw[o++] = (byte)a;
            }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        Chunk(output, "IDAT", ms.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteBE(byte[] b, int i, int v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(typeBytes, 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4]; WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data, uint crc)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
