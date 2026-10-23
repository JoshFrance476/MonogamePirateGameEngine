using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PirateEngine.Game.Input;

/// <summary>Per-frame snapshot of mouse and keyboard with edge detection (pressed / released this frame).</summary>
public sealed class InputState
{
    private KeyboardState _kb, _prevKb;
    private MouseState _mouse, _prevMouse;

    /// <summary>Set by the UI when the cursor is over a panel, so clicks don't fall through to the world.</summary>
    public bool MouseCapturedByUi { get; set; }

    public void Update(bool windowActive)
    {
        _prevKb = _kb;
        _prevMouse = _mouse;
        _kb = Keyboard.GetState();
        _mouse = windowActive ? Mouse.GetState() : _prevMouse;
        MouseCapturedByUi = false;
    }

    public Vector2 MousePosition => new(_mouse.X, _mouse.Y);
    public Point MousePoint => new(_mouse.X, _mouse.Y);
    public Vector2 MouseDelta => new(_mouse.X - _prevMouse.X, _mouse.Y - _prevMouse.Y);
    public int WheelDelta => _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;

    public bool LeftDown => _mouse.LeftButton == ButtonState.Pressed;
    public bool LeftClicked => _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
    public bool LeftReleased => _mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed;
    public bool RightClicked => _mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;
    public bool MiddleDown => _mouse.MiddleButton == ButtonState.Pressed;

    public bool KeyDown(Keys k) => _kb.IsKeyDown(k);
    public bool KeyPressed(Keys k) => _kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
    public bool Shift => _kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift);
    public bool Ctrl => _kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl);
}
