using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace PirateEngine.Game.Rendering;

/// <summary>Shared textures and primitive drawing helpers (rects, lines, circles) on top of SpriteBatch.</summary>
public sealed class Draw
{
    public Texture2D Pixel { get; }
    /// <summary>64×64 anti-aliased filled disc (white).</summary>
    public Texture2D Disc { get; }
    /// <summary>64×64 anti-aliased ring (white).</summary>
    public Texture2D Ring { get; }
    public BitmapFont Font { get; }

    public Draw(GraphicsDevice device)
    {
        Pixel = new Texture2D(device, 1, 1);
        Pixel.SetData(new[] { Color.White });
        Disc = MakeCircle(device, 64, filled: true);
        Ring = MakeCircle(device, 64, filled: false);
        Font = new BitmapFont(device);
    }

    private static Texture2D MakeCircle(GraphicsDevice device, int size, bool filled)
    {
        var data = new Color[size * size];
        float r = size / 2f - 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - r, dy = y - r;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float a = filled ? Math.Clamp(r - d, 0f, 1f) : Math.Clamp(1.5f - MathF.Abs(d - (r - 2.5f)), 0f, 1f);
            data[y * size + x] = Color.White * a;
        }
        var t = new Texture2D(device, size, size);
        t.SetData(data);
        return t;
    }

    public void Rect(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Pixel, r, c);

    public void RectF(SpriteBatch sb, Vector2 pos, Vector2 size, Color c) =>
        sb.Draw(Pixel, pos, null, c, 0f, Vector2.Zero, size, SpriteEffects.None, 0f);

    public void Outline(SpriteBatch sb, Rectangle r, Color c, int t = 1)
    {
        sb.Draw(Pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        sb.Draw(Pixel, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        sb.Draw(Pixel, new Rectangle(r.X, r.Y + t, t, r.Height - 2 * t), c);
        sb.Draw(Pixel, new Rectangle(r.Right - t, r.Y + t, t, r.Height - 2 * t), c);
    }

    public void OutlineF(SpriteBatch sb, Vector2 pos, Vector2 size, Color c, float t)
    {
        RectF(sb, pos, new Vector2(size.X, t), c);
        RectF(sb, pos + new Vector2(0, size.Y - t), new Vector2(size.X, t), c);
        RectF(sb, pos, new Vector2(t, size.Y), c);
        RectF(sb, pos + new Vector2(size.X - t, 0), new Vector2(t, size.Y), c);
    }

    public void Line(SpriteBatch sb, Vector2 a, Vector2 b, Color c, float thickness = 1f)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        float angle = MathF.Atan2(d.Y, d.X);
        sb.Draw(Pixel, a, null, c, angle, new Vector2(0, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    public void DashedLine(SpriteBatch sb, Vector2 a, Vector2 b, Color c, float thickness, float dash, float gap, float phase = 0)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var dir = d / len;
        float period = dash + gap;
        float t = -(phase % period);
        while (t < len)
        {
            float s0 = Math.Max(0, t), s1 = Math.Min(len, t + dash);
            if (s1 > s0) Line(sb, a + dir * s0, a + dir * s1, c, thickness);
            t += period;
        }
    }

    public void FilledCircle(SpriteBatch sb, Vector2 centre, float radius, Color c) =>
        sb.Draw(Disc, centre, null, c, 0f, new Vector2(32, 32), radius / 32f, SpriteEffects.None, 0f);

    public void RingSprite(SpriteBatch sb, Vector2 centre, float radius, Color c) =>
        sb.Draw(Ring, centre, null, c, 0f, new Vector2(32, 32), radius / 30f, SpriteEffects.None, 0f);

    /// <summary>Circle outline from line segments (constant thickness at any radius).</summary>
    public void Circle(SpriteBatch sb, Vector2 centre, float radius, Color c, float thickness, int segments = 48, bool dashed = false)
    {
        var prev = centre + new Vector2(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var p = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            if (!dashed || i % 2 == 0) Line(sb, prev, p, c, thickness);
            prev = p;
        }
    }

    public void Bar(SpriteBatch sb, Rectangle r, float fraction, Color fill, Color back)
    {
        Rect(sb, r, back);
        int w = (int)(r.Width * Math.Clamp(fraction, 0f, 1f));
        if (w > 0) Rect(sb, new Rectangle(r.X, r.Y, w, r.Height), fill);
    }

    public static Color ParseHex(string hex, Color fallback)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int v)) return fallback;
        return new Color((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
}
