namespace Microsoft.Xna.Framework.Input;

public enum ButtonState { Released, Pressed }

public enum Keys
{
    None = 0, Back = 8, Tab = 9, Enter = 13, Escape = 27, Space = 32,
    PageUp = 33, PageDown = 34, End = 35, Home = 36, Left = 37, Up = 38, Right = 39, Down = 40, Delete = 46,
    D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Add = 107, Subtract = 109,
    F1 = 112, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    LeftShift = 160, RightShift = 161, LeftControl = 162, RightControl = 163, LeftAlt = 164, RightAlt = 165,
    OemPlus = 187, OemComma = 188, OemMinus = 189, OemPeriod = 190,
}

public struct MouseState
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public Point Position => new(X, Y);
    public int ScrollWheelValue { get; internal set; }
    public ButtonState LeftButton { get; internal set; }
    public ButtonState RightButton { get; internal set; }
    public ButtonState MiddleButton { get; internal set; }
}

public static class Mouse
{
    public static MouseState GetState()
    {
        var s = new MouseState();
        var env = Environment.GetEnvironmentVariable("SHIM_MOUSE");
        if (env != null && env.Split(',') is [var x, var y] && int.TryParse(x, out var mx) && int.TryParse(y, out var my))
        {
            s.X = mx;
            s.Y = my;
        }
        return s;
    }
}

public struct KeyboardState
{
    private readonly HashSet<Keys> _down;
    internal KeyboardState(HashSet<Keys> down) => _down = down;
    public bool IsKeyDown(Keys key) => _down != null && _down.Contains(key);
    public bool IsKeyUp(Keys key) => !IsKeyDown(key);
    public Keys[] GetPressedKeys() => _down?.ToArray() ?? Array.Empty<Keys>();
}

/// <summary>
/// Scripted keyboard: SHIM_KEYS="H:120,Escape:200" holds each key down for exactly one frame
/// at the given frame number, which the game sees as a key press.
/// </summary>
public static class Keyboard
{
    internal static int Frame;
    private static List<(Keys key, int frame)> _script;

    public static KeyboardState GetState()
    {
        _script ??= (Environment.GetEnvironmentVariable("SHIM_KEYS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split(':'))
            .Where(p => p.Length == 2 && Enum.TryParse<Keys>(p[0], out _) && int.TryParse(p[1], out _))
            .Select(p => (Enum.Parse<Keys>(p[0]), int.Parse(p[1])))
            .ToList();
        return new KeyboardState(_script.Where(k => k.frame == Frame).Select(k => k.key).ToHashSet());
    }
}
