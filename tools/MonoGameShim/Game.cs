// Headless game loop. Frames advance with a fixed 1/60 s GameTime as fast as the CPU allows.
// Environment variables:
//   SHIM_MAX_FRAMES   stop after this many frames (default 600) unless the game exits first
//   SHIM_MOUSE        "x,y" cursor position reported by Mouse.GetState()
using System.Diagnostics;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework;

public class GameTime
{
    public TimeSpan TotalGameTime { get; set; }
    public TimeSpan ElapsedGameTime { get; set; }
    public bool IsRunningSlowly { get; set; }
    public GameTime() { }
    public GameTime(TimeSpan total, TimeSpan elapsed) { TotalGameTime = total; ElapsedGameTime = elapsed; }
}

public sealed class GameWindow
{
    private readonly Game _game;
    internal GameWindow(Game game) => _game = game;
    public string Title { get; set; } = "";
    public bool AllowUserResizing { get; set; }
    public Rectangle ClientBounds =>
        new(0, 0, _game.GraphicsDevice?.PresentationParameters.BackBufferWidth ?? 0, _game.GraphicsDevice?.PresentationParameters.BackBufferHeight ?? 0);
    public event EventHandler<EventArgs> ClientSizeChanged;
}

public sealed class GraphicsDeviceManager
{
    private readonly Game _game;
    public int PreferredBackBufferWidth { get; set; } = 800;
    public int PreferredBackBufferHeight { get; set; } = 480;
    public bool SynchronizeWithVerticalRetrace { get; set; }
    public bool IsFullScreen { get; set; }
    public GraphicsDevice GraphicsDevice => _game.GraphicsDevice;

    public GraphicsDeviceManager(Game game)
    {
        _game = game;
        game.GraphicsManager = this;
    }

    public void ApplyChanges() => _game.GraphicsDevice?.Resize(PreferredBackBufferWidth, PreferredBackBufferHeight);
}

public class Game : IDisposable
{
    internal GraphicsDeviceManager GraphicsManager;
    private bool _exit;

    public GraphicsDevice GraphicsDevice { get; private set; }
    public GameWindow Window { get; }
    public bool IsMouseVisible { get; set; }
    public bool IsFixedTimeStep { get; set; } = true;
    public bool IsActive => true;
    public TimeSpan TargetElapsedTime { get; set; } = TimeSpan.FromSeconds(1 / 60.0);

    public Game() => Window = new GameWindow(this);

    protected virtual void Initialize() => LoadContent();
    protected virtual void LoadContent() { }
    protected virtual void UnloadContent() { }
    protected virtual void Update(GameTime gameTime) { }
    protected virtual void Draw(GameTime gameTime) { }

    public void Exit() => _exit = true;

    public void Run()
    {
        GraphicsDevice = new GraphicsDevice();
        GraphicsDevice.Resize(GraphicsManager?.PreferredBackBufferWidth ?? 800, GraphicsManager?.PreferredBackBufferHeight ?? 480);
        Initialize();

        int maxFrames = int.TryParse(Environment.GetEnvironmentVariable("SHIM_MAX_FRAMES"), out var mf) ? mf : 600;
        var step = TimeSpan.FromSeconds(1 / 60.0);
        var total = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        int frame = 0;
        while (!_exit && frame < maxFrames)
        {
            total += step;
            var gt = new GameTime(total, step);
            Input.Keyboard.Frame = frame;
            Update(gt);
            Draw(gt);
            frame++;
            Thread.Sleep(1); // let background loading threads breathe
        }
        UnloadContent();
        Console.WriteLine($"[shim] {frame} frames in {sw.Elapsed.TotalSeconds:0.0}s ({sw.Elapsed.TotalMilliseconds / Math.Max(1, frame):0.0} ms/frame, " +
                          $"{SpriteBatch.PixelsShaded / Math.Max(1, frame) / 1000}k px/frame){(_exit ? "" : " - frame limit reached")}");
    }

    public void Dispose() { }
}
