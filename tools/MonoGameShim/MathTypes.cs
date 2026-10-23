// Subset of Microsoft.Xna.Framework math types with MonoGame-compatible signatures.
namespace Microsoft.Xna.Framework;

public struct Vector2 : IEquatable<Vector2>
{
    public float X, Y;
    public Vector2(float x, float y) { X = x; Y = y; }
    public Vector2(float value) { X = value; Y = value; }

    public static Vector2 Zero => new(0, 0);
    public static Vector2 One => new(1, 1);

    public float Length() => MathF.Sqrt(X * X + Y * Y);
    public float LengthSquared() => X * X + Y * Y;
    public void Normalize() { float l = Length(); if (l > 0) { X /= l; Y /= l; } }
    public static Vector2 Normalize(Vector2 v) { v.Normalize(); return v; }
    public static float Distance(Vector2 a, Vector2 b) => (a - b).Length();
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;
    public static Vector2 Transform(Vector2 p, Matrix m) =>
        new(p.X * m.M11 + p.Y * m.M21 + m.M41, p.X * m.M12 + p.Y * m.M22 + m.M42);

    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2 operator -(Vector2 a) => new(-a.X, -a.Y);
    public static Vector2 operator *(Vector2 a, float s) => new(a.X * s, a.Y * s);
    public static Vector2 operator *(float s, Vector2 a) => new(a.X * s, a.Y * s);
    public static Vector2 operator *(Vector2 a, Vector2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2 operator /(Vector2 a, float s) => new(a.X / s, a.Y / s);
    public static Vector2 operator /(Vector2 a, Vector2 b) => new(a.X / b.X, a.Y / b.Y);
    public static bool operator ==(Vector2 a, Vector2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
    public bool Equals(Vector2 o) => this == o;
    public override bool Equals(object obj) => obj is Vector2 v && this == v;
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"{{X:{X} Y:{Y}}}";
}

public struct Point : IEquatable<Point>
{
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public static Point Zero => new(0, 0);
    public bool Equals(Point o) => X == o.X && Y == o.Y;
    public override bool Equals(object obj) => obj is Point p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Point a, Point b) => a.Equals(b);
    public static bool operator !=(Point a, Point b) => !a.Equals(b);
}

public struct Rectangle : IEquatable<Rectangle>
{
    public int X, Y, Width, Height;
    public Rectangle(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    public static Rectangle Empty => new();
    public int Left => X;
    public int Right => X + Width;
    public int Top => Y;
    public int Bottom => Y + Height;
    public Point Location => new(X, Y);
    public Point Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width == 0 && Height == 0 && X == 0 && Y == 0;
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
    public bool Contains(Point p) => Contains(p.X, p.Y);
    public bool Contains(Vector2 v) => v.X >= X && v.X < X + Width && v.Y >= Y && v.Y < Y + Height;
    public bool Intersects(Rectangle r) => r.Left < Right && Left < r.Right && r.Top < Bottom && Top < r.Bottom;
    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        if (!a.Intersects(b)) return Empty;
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new Rectangle(x, y, Math.Min(a.Right, b.Right) - x, Math.Min(a.Bottom, b.Bottom) - y);
    }
    public bool Equals(Rectangle o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
    public override bool Equals(object obj) => obj is Rectangle r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(Rectangle a, Rectangle b) => a.Equals(b);
    public static bool operator !=(Rectangle a, Rectangle b) => !a.Equals(b);
}

public struct Color : IEquatable<Color>
{
    private uint _packed; // ABGR like MonoGame
    public byte R { get => (byte)_packed; set => _packed = (_packed & 0xFFFFFF00) | value; }
    public byte G { get => (byte)(_packed >> 8); set => _packed = (_packed & 0xFFFF00FF) | ((uint)value << 8); }
    public byte B { get => (byte)(_packed >> 16); set => _packed = (_packed & 0xFF00FFFF) | ((uint)value << 16); }
    public byte A { get => (byte)(_packed >> 24); set => _packed = (_packed & 0x00FFFFFF) | ((uint)value << 24); }
    public uint PackedValue { get => _packed; set => _packed = value; }

    private static byte C(int v) => (byte)Math.Clamp(v, 0, 255);
    private static byte F(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    public Color(int r, int g, int b) : this(r, g, b, 255) { }
    public Color(int r, int g, int b, int a) { _packed = 0; R = C(r); G = C(g); B = C(b); A = C(a); }
    public Color(float r, float g, float b) : this(r, g, b, 1f) { }
    public Color(float r, float g, float b, float a) { _packed = 0; R = F(r); G = F(g); B = F(b); A = F(a); }
    public Color(Color c, float alpha) : this(c.R, c.G, c.B, (int)(alpha * 255)) { }

    public static Color operator *(Color c, float s) => new(
        (int)(c.R * s), (int)(c.G * s), (int)(c.B * s), (int)(c.A * s));
    public static bool operator ==(Color a, Color b) => a._packed == b._packed;
    public static bool operator !=(Color a, Color b) => a._packed != b._packed;
    public bool Equals(Color o) => _packed == o._packed;
    public override bool Equals(object obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => (int)_packed;
    public override string ToString() => $"{{R:{R} G:{G} B:{B} A:{A}}}";

    public static Color Transparent => new(0, 0, 0, 0);
    public static Color White => new(255, 255, 255);
    public static Color Black => new(0, 0, 0);
    public static Color Gray => new(128, 128, 128);
    public static Color Wheat => new(245, 222, 179);
    public static Color Red => new(255, 0, 0);
    public static Color CornflowerBlue => new(100, 149, 237);
}

public struct Matrix
{
    public float M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44;

    public static Matrix Identity => new() { M11 = 1, M22 = 1, M33 = 1, M44 = 1 };
    public static Matrix CreateTranslation(float x, float y, float z) { var m = Identity; m.M41 = x; m.M42 = y; m.M43 = z; return m; }
    public static Matrix CreateScale(float x, float y, float z) { var m = Identity; m.M11 = x; m.M22 = y; m.M33 = z; return m; }
    public static Matrix CreateScale(float s) => CreateScale(s, s, s);

    public static Matrix operator *(Matrix a, Matrix b)
    {
        var r = new Matrix();
        r.M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41;
        r.M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42;
        r.M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43;
        r.M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44;
        r.M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41;
        r.M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42;
        r.M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43;
        r.M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44;
        r.M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41;
        r.M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42;
        r.M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43;
        r.M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44;
        r.M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41;
        r.M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42;
        r.M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43;
        r.M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44;
        return r;
    }
}

public static class MathHelper
{
    public const float Pi = MathF.PI;
    public const float TwoPi = MathF.PI * 2;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
    public static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
    public static float ToRadians(float deg) => deg * MathF.PI / 180f;
}
