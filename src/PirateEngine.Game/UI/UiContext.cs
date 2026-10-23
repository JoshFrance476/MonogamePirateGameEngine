using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Game.Input;
using PirateEngine.Game.Rendering;

namespace PirateEngine.Game.UI;

/// <summary>
/// A small immediate-mode UI. Widgets are evaluated during Update (so clicks are known
/// before the world sees the mouse), and their visuals are recorded as draw commands that
/// <see cref="Render"/> replays during Draw.
/// </summary>
public sealed class UiContext
{
    public static class Theme
    {
        public static readonly Color Panel = new Color(14, 20, 32) * 0.92f;
        public static readonly Color PanelLight = new Color(30, 40, 58) * 0.95f;
        public static readonly Color Border = new(176, 142, 74);
        public static readonly Color Text = new(238, 228, 204);
        public static readonly Color TextDim = new(160, 152, 136);
        public static readonly Color Gold = new(246, 205, 92);
        public static readonly Color Good = new(110, 220, 120);
        public static readonly Color Warn = new(245, 195, 70);
        public static readonly Color Bad = new(235, 85, 70);
        public static readonly Color Info = new(140, 200, 245);
        public static readonly Color Button = new(48, 62, 88);
        public static readonly Color ButtonHover = new(70, 90, 126);
        public static readonly Color ButtonDisabled = new(36, 40, 50);
    }

    private enum Kind { Rect, Texture, Text, Custom }
    private readonly record struct Cmd(Kind Kind, Rectangle Dest, Rectangle? Src, Color Color, Texture2D? Tex, string? Text, int Scale, Action<SpriteBatch>? Custom = null);
    private readonly List<Cmd> _cmds = new();
    private readonly Draw _draw;
    private InputState _input = null!;
    private bool _clickConsumed;
    private string? _tooltip;

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public BitmapFont Font => _draw.Font;
    /// <summary>True once the mouse wheel has been used by a UI element this frame.</summary>
    public bool WheelConsumed { get; set; }

    public UiContext(Draw draw) => _draw = draw;

    public void Begin(InputState input, int w, int h)
    {
        _cmds.Clear();
        _insertAt = -1;
        _input = input;
        _clickConsumed = false;
        _tooltip = null;
        WheelConsumed = false;
        ScreenWidth = w;
        ScreenHeight = h;
    }

    private int _insertAt = -1;
    private void Add(Cmd c)
    {
        if (_insertAt >= 0) _cmds.Insert(_insertAt++, c);
        else _cmds.Add(c);
    }

    /// <summary>Position in the draw list; pass to <see cref="BeginBelow"/> to draw later-built UI underneath.</summary>
    public int Mark => _cmds.Count;

    /// <summary>
    /// Widgets built until <see cref="EndBelow"/> are drawn beneath everything built after
    /// <paramref name="mark"/>, while still receiving input after it (so panels on top win clicks).
    /// </summary>
    public void BeginBelow(int mark) => _insertAt = Math.Clamp(mark, 0, _cmds.Count);
    public void EndBelow() => _insertAt = -1;

    public bool MouseCaptured => _input.MouseCapturedByUi;

    public Point Mouse => _input.MousePoint;
    public bool Hover(Rectangle r) => r.Contains(_input.MousePoint);

    /// <summary>Marks a region as UI so the world ignores the mouse there.</summary>
    public void Capture(Rectangle r)
    {
        if (Hover(r)) _input.MouseCapturedByUi = true;
    }

    // ---- primitives -------------------------------------------------------------------

    public void Rect(Rectangle r, Color c) => Add(new Cmd(Kind.Rect, r, null, c, null, null, 0));

    public void Outline(Rectangle r, Color c, int t = 1)
    {
        Rect(new Rectangle(r.X, r.Y, r.Width, t), c);
        Rect(new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Rect(new Rectangle(r.X, r.Y, t, r.Height), c);
        Rect(new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    public void Image(Texture2D tex, Rectangle dest, Color c, Rectangle? src = null) =>
        Add(new Cmd(Kind.Texture, dest, src, c, tex, null, 0));

    public void Text(string text, int x, int y, Color c, int scale = 2) =>
        Add(new Cmd(Kind.Text, new Rectangle(x, y, 0, 0), null, c, null, text, scale));

    /// <summary>Arbitrary drawing (e.g. the minimap) in UI order.</summary>
    public void Custom(Action<SpriteBatch> draw) => Add(new Cmd(Kind.Custom, Rectangle.Empty, null, Color.White, null, null, 0, draw));

    public int TextWidth(string text, int scale = 2) => Font.Measure(text, scale).X;

    public void TextCentered(string text, Rectangle r, Color c, int scale = 2)
    {
        var size = Font.Measure(text, scale);
        Text(text, r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2, c, scale);
    }

    // ---- widgets ----------------------------------------------------------------------

    public void Panel(Rectangle r, string? title = null)
    {
        Capture(r);
        Rect(r, Theme.Panel);
        Outline(r, Theme.Border * 0.9f);
        if (title != null)
        {
            Rect(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, 24), Theme.PanelLight);
            Text(title, r.X + 8, r.Y + 6, Theme.Gold);
        }
    }

    public bool Button(Rectangle r, string label, bool enabled = true, string? tooltip = null, Color? textColor = null, int scale = 2)
    {
        bool hover = Hover(r);
        var bg = !enabled ? Theme.ButtonDisabled : hover ? Theme.ButtonHover : Theme.Button;
        Rect(r, bg);
        Outline(r, enabled ? (hover ? Theme.Gold : Theme.Border * 0.7f) : Theme.Border * 0.3f);
        TextCentered(label, r, enabled ? textColor ?? Theme.Text : Theme.TextDim, scale);
        if (hover && tooltip != null) _tooltip = tooltip;
        if (hover) _input.MouseCapturedByUi = true;
        if (enabled && hover && _input.LeftClicked && !_clickConsumed)
        {
            _clickConsumed = true;
            return true;
        }
        return false;
    }

    public void Bar(Rectangle r, float fraction, Color fill, string? label = null)
    {
        Rect(r, new Color(0, 0, 0) * 0.6f);
        int w = (int)((r.Width - 2) * Math.Clamp(fraction, 0f, 1f));
        if (w > 0) Rect(new Rectangle(r.X + 1, r.Y + 1, w, r.Height - 2), fill);
        Outline(r, Theme.Border * 0.5f);
        if (label != null) TextCentered(label, r, Theme.Text, 1);
    }

    public void SetTooltip(string text) => _tooltip = text;

    public bool ConsumeClick(Rectangle r)
    {
        if (_clickConsumed || !_input.LeftClicked || !Hover(r)) return false;
        _clickConsumed = true;
        return true;
    }

    // ---- rendering ----------------------------------------------------------------------

    public void Render(SpriteBatch sb)
    {
        if (_tooltip != null) EmitTooltip(_tooltip);
        foreach (var c in _cmds)
        {
            switch (c.Kind)
            {
                case Kind.Rect: sb.Draw(_draw.Pixel, c.Dest, c.Color); break;
                case Kind.Texture: sb.Draw(c.Tex!, c.Dest, c.Src, c.Color); break;
                case Kind.Text: Font.Draw(sb, c.Text!, new Vector2(c.Dest.X, c.Dest.Y), c.Color, c.Scale); break;
                case Kind.Custom: c.Custom!(sb); break;
            }
        }
    }

    private void EmitTooltip(string text)
    {
        var wrapped = Font.Wrap(text, 320, 2);
        var size = Font.Measure(wrapped, 2);
        int x = Mouse.X + 16, y = Mouse.Y + 18;
        if (x + size.X + 16 > ScreenWidth) x = Mouse.X - size.X - 20;
        if (y + size.Y + 12 > ScreenHeight) y = Mouse.Y - size.Y - 16;
        var r = new Rectangle(x, y, size.X + 14, size.Y + 12);
        Rect(r, new Color(8, 10, 16) * 0.96f);
        Outline(r, Theme.Gold * 0.8f);
        Text(wrapped, x + 7, y + 6, Theme.Text);
    }
}
