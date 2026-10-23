using Microsoft.Xna.Framework;

namespace PirateEngine.Game.Rendering;

/// <summary>
/// Top-down camera in world (tile) units. <see cref="PixelsPerTile"/> is the effective zoom.
/// Zooming is exponential and anchored on a screen point, so the tile under the cursor stays put.
/// </summary>
public sealed class Camera2D
{
    public Vector2 Position;              // world point at screen centre
    public float PixelsPerTile = 24f;
    public float MinPixelsPerTile = 0.6f;
    public float MaxPixelsPerTile = 64f;
    public int ViewportWidth, ViewportHeight;
    public Vector2 WorldSize;

    /// <summary>Smooth-follow target (e.g. the player's ship). Null = free camera.</summary>
    public Func<Vector2>? Follow;
    private float _targetPpt = 24f;

    public Matrix Transform =>
        Matrix.CreateTranslation(-Position.X, -Position.Y, 0) *
        Matrix.CreateScale(PixelsPerTile, PixelsPerTile, 1) *
        Matrix.CreateTranslation(MathF.Round(ViewportWidth / 2f), MathF.Round(ViewportHeight / 2f), 0);

    public Vector2 ScreenToWorld(Vector2 screen) =>
        Position + (screen - new Vector2(ViewportWidth / 2f, ViewportHeight / 2f)) / PixelsPerTile;

    public Vector2 WorldToScreen(Vector2 world) =>
        (world - Position) * PixelsPerTile + new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);

    /// <summary>Visible world rectangle (in tiles), expanded by <paramref name="margin"/>.</summary>
    public Rectangle VisibleTiles(int margin = 1)
    {
        var tl = ScreenToWorld(Vector2.Zero);
        var br = ScreenToWorld(new Vector2(ViewportWidth, ViewportHeight));
        int x0 = (int)MathF.Floor(tl.X) - margin, y0 = (int)MathF.Floor(tl.Y) - margin;
        int x1 = (int)MathF.Ceiling(br.X) + margin, y1 = (int)MathF.Ceiling(br.Y) + margin;
        return new Rectangle(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Zoom by a factor around a screen point (mouse wheel).</summary>
    public void ZoomAt(Vector2 screenAnchor, float factor)
    {
        _targetPpt = Math.Clamp(_targetPpt * factor, MinPixelsPerTile, MaxPixelsPerTile);
        _zoomAnchor = screenAnchor;
    }

    public void SetZoom(float ppt)
    {
        PixelsPerTile = _targetPpt = Math.Clamp(ppt, MinPixelsPerTile, MaxPixelsPerTile);
    }

    private Vector2? _zoomAnchor;

    public void Pan(Vector2 worldDelta)
    {
        Position += worldDelta;
        Follow = null;
    }

    public void Update(float dt)
    {
        // Smooth exponential zoom, keeping the anchor's world point fixed under the cursor.
        if (MathF.Abs(PixelsPerTile - _targetPpt) > 0.001f)
        {
            var anchor = _zoomAnchor ?? new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);
            var before = ScreenToWorld(anchor);
            float k = 1f - MathF.Exp(-dt * 14f);
            PixelsPerTile = MathF.Exp(MathHelper.Lerp(MathF.Log(PixelsPerTile), MathF.Log(_targetPpt), k));
            if (MathF.Abs(PixelsPerTile - _targetPpt) < 0.001f) PixelsPerTile = _targetPpt;
            if (Follow == null) Position += before - ScreenToWorld(anchor);
        }

        if (Follow != null)
        {
            var target = Follow();
            float k = 1f - MathF.Exp(-dt * 6f);
            Position = Vector2.Lerp(Position, target, k);
        }

        Position.X = Math.Clamp(Position.X, 0, WorldSize.X);
        Position.Y = Math.Clamp(Position.Y, 0, WorldSize.Y);
    }
}
