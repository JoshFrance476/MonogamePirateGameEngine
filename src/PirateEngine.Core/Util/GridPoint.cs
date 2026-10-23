using System.Numerics;

namespace PirateEngine.Core.Util;

/// <summary>Integer tile coordinate.</summary>
public readonly record struct GridPoint(int X, int Y)
{
    public static readonly GridPoint Invalid = new(int.MinValue, int.MinValue);

    public bool IsValid => X != int.MinValue;

    /// <summary>World-space centre of the tile (tiles are 1 world unit wide).</summary>
    public Vector2 Center => new(X + 0.5f, Y + 0.5f);

    public static GridPoint FromWorld(Vector2 world) => new((int)MathF.Floor(world.X), (int)MathF.Floor(world.Y));

    public int ChebyshevDistance(GridPoint o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Y - o.Y));

    public float Distance(GridPoint o)
    {
        float dx = X - o.X, dy = Y - o.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static GridPoint operator +(GridPoint a, GridPoint b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>The 8 neighbour offsets; the first 4 are orthogonal.</summary>
    public static readonly GridPoint[] Neighbours8 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
    };

    public override string ToString() => $"({X},{Y})";
}

public static class MathUtil
{
    public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    public static float SmoothStep(float a, float b, float t)
    {
        t = Clamp01((t - a) / (b - a));
        return t * t * (3 - 2 * t);
    }

    /// <summary>Shortest signed difference between two angles (radians), in [-π, π].</summary>
    public static float AngleDelta(float from, float to)
    {
        float d = (to - from) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }

    public static float MoveTowardsAngle(float current, float target, float maxDelta)
    {
        float d = AngleDelta(current, target);
        if (MathF.Abs(d) <= maxDelta) return target;
        return current + MathF.Sign(d) * maxDelta;
    }
}
