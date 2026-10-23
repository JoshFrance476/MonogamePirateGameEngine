using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.World;
using PirateEngine.Game.Input;
using PirateEngine.Game.Rendering;
using PirateEngine.Game.UI;

namespace PirateEngine.Game;

public sealed class GameOptions
{
    public int Seed = Environment.TickCount & 0xFFFF;
    public int Size = 1024;
    public string? LoadPath;
    public string? ScreenshotPath;
    public int ScreenshotFrames = 90;
    public string? Scenario;
    /// <summary>World-setup preset id from setup.json (null = its default).</summary>
    public string? Setup;
    public int WindowWidth = 1600, WindowHeight = 900;
    public string DataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
}

/// <summary>
/// The MonoGame shell: owns the window, the per-frame loop and all presentation objects, and
/// drives the headless <see cref="GameWorld"/>. World generation and save loading run on a
/// background task behind a loading screen.
/// </summary>
public sealed class PirateGame : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly GameOptions _opt;
    private SpriteBatch _sb = null!;
    private Draw _draw = null!;
    private TileArt _tileArt = null!;
    private SpriteArt _spriteArt = null!;
    private UiContext _ui = null!;
    private Hud _hud = null!;
    private readonly InputState _input = new();
    private readonly PlayerController _controller = new();
    private readonly Camera2D _camera = new();
    private DefinitionDatabase _defs = null!;

    private GameWorld? _world;
    private WorldRenderer? _worldRenderer;
    private EntityRenderer? _entityRenderer;

    private Task<GameWorld>? _loading;
    private volatile string _loadStage = "Preparing";
    private volatile float _loadProgress;
    private string? _fatalError;

    private float _fps = 60, _renderTime;
    private int _framesSinceLoad;
    private bool _scenarioApplied;

    public PirateGame(GameOptions options)
    {
        _opt = options;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = options.WindowWidth,
            PreferredBackBufferHeight = options.WindowHeight,
            SynchronizeWithVerticalRetrace = true,
        };
        IsMouseVisible = true;
        IsFixedTimeStep = false; // the simulation runs its own fixed steps; render as fast as vsync allows
        Window.AllowUserResizing = true;
        Window.Title = "Pirate Engine";
    }

    public static string SaveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PirateEngine");
    public static string QuickSavePath => Path.Combine(SaveDirectory, "quicksave.json");

    protected override void Initialize()
    {
        Window.ClientSizeChanged += (_, _) =>
        {
            var b = Window.ClientBounds;
            if (b.Width <= 0 || b.Height <= 0) return;
            if (_graphics.PreferredBackBufferWidth == b.Width && _graphics.PreferredBackBufferHeight == b.Height) return;
            _graphics.PreferredBackBufferWidth = b.Width;
            _graphics.PreferredBackBufferHeight = b.Height;
            _graphics.ApplyChanges();
        };
        _controller.ShellAction += OnShellAction;
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _draw = new Draw(GraphicsDevice);
        _tileArt = new TileArt(GraphicsDevice);
        _spriteArt = new SpriteArt(GraphicsDevice);
        _ui = new UiContext(_draw);
        _hud = new Hud(_ui, _spriteArt, _draw);

        try
        {
            _defs = DefinitionDatabase.Load(_opt.DataDirectory);
        }
        catch (Exception ex)
        {
            _fatalError = "Content error:\n" + ex.Message;
            return;
        }

        if (_opt.LoadPath != null) BeginLoad(_opt.LoadPath);
        else BeginNewWorld(_opt.Seed, _opt.Size);
    }

    // ---- world lifecycle --------------------------------------------------------------

    private IProgress<(string, float)> LoadProgress() =>
        new InlineProgress(v => { _loadStage = v.Item1; _loadProgress = v.Item2; });

    private void BeginNewWorld(int seed, int size)
    {
        var gen = new WorldGenSettings { Seed = seed, Width = size, Height = size, Setup = _opt.Setup };
        var progress = LoadProgress();
        _loading = Task.Run(() => GameWorld.CreateNew(gen, _defs, progress));
    }

    private void BeginLoad(string path)
    {
        var progress = LoadProgress();
        _loading = Task.Run(() => GameWorld.LoadFromFile(path, _defs, progress));
    }

    private void OnWorldReady(GameWorld world)
    {
        _world?.Dispose();
        _world = world;
        world.AsyncPathfinding = true;
        _worldRenderer = new WorldRenderer(GraphicsDevice, world.Map, world.Fog, _tileArt, _draw);
        _worldRenderer.BuildAll();
        _entityRenderer = new EntityRenderer(_draw, _spriteArt);
        _camera.WorldSize = new Vector2(world.Map.Width, world.Map.Height);
        _camera.Position = EntityRenderer.V(world.Player.Position);
        _camera.SetZoom(28f);
        _camera.Follow = () => EntityRenderer.V(_world!.Player.Position);
        _controller.BuildDef = null;
        _framesSinceLoad = 0;
        _scenarioApplied = false;
        Window.Title = $"Pirate Engine - seed {world.Map.Seed} ({world.Map.Width}x{world.Map.Height})";
    }

    private void OnShellAction(string action)
    {
        if (_world == null) return;
        switch (action)
        {
            case "save":
                try
                {
                    _world.SaveToFile(QuickSavePath);
                    _world.Events.Post($"Game saved to {QuickSavePath}", Core.Simulation.MessageKind.Good);
                }
                catch (Exception ex) { _world.Events.Post("Save failed: " + ex.Message, Core.Simulation.MessageKind.Danger); }
                break;
            case "load":
                if (File.Exists(QuickSavePath)) BeginLoad(QuickSavePath);
                else _world.Events.Post("No quicksave found.", Core.Simulation.MessageKind.Warning);
                break;
            case "screenshot": _pendingScreenshot = Path.Combine(SaveDirectory, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"); break;
            case "quit": Exit(); break;
            case "grid": if (_worldRenderer != null) _worldRenderer.ShowGrid = !_worldRenderer.ShowGrid; break;
            case "toggle-fog": _world.Fog.Enabled = !_world.Fog.Enabled; break;
        }
    }

    private string? _pendingScreenshot;

    // ---- loop -------------------------------------------------------------------------

    protected override void Update(GameTime gameTime)
    {
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.1f);
        _input.Update(IsActive);
        _camera.ViewportWidth = GraphicsDevice.Viewport.Width;
        _camera.ViewportHeight = GraphicsDevice.Viewport.Height;

        if (_loading != null)
        {
            if (_loading.IsCompleted)
            {
                if (_loading.IsFaulted) _fatalError = "Failed to create world:\n" + _loading.Exception!.GetBaseException().Message;
                else OnWorldReady(_loading.Result);
                _loading = null;
            }
            base.Update(gameTime);
            return;
        }
        if (_world == null) { base.Update(gameTime); return; }

        if (!_scenarioApplied && _opt.Scenario != null)
        {
            DemoScenarios.Apply(_opt.Scenario, _world, _camera, _controller, _hud);
            _scenarioApplied = true;
        }

        _ui.Begin(_input, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        _hud.SetModifiers(_input.Shift);
        _hud.Build(_world, _controller, _camera, _worldRenderer!, _input, _fps, OnShellAction);
        _controller.Update(_world, _camera, _input, dt);

        bool paused = _controller.Paused || _controller.ShowPauseMenu;
        if (!paused) _world.Update(dt * _controller.TimeScale);
        _camera.Update(dt);
        _entityRenderer!.Update(_world, dt, paused);
        _worldRenderer!.UpdateChunks(budget: 2);
        _renderTime += paused ? 0 : dt;

        if (dt > 0) _fps = MathHelper.Lerp(_fps, 1f / dt, 0.05f);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(WorldRenderer.FogColor);

        if (_fatalError != null) DrawMessageScreen(_fatalError, null);
        else if (_loading != null || _world == null) DrawMessageScreen(_loadStage + "...", _loadProgress);
        else DrawWorld();

        base.Draw(gameTime);
        HandleScreenshots();
    }

    private void DrawWorld()
    {
        var w = _world!;
        float ppt = _camera.PixelsPerTile;
        var visible = _camera.VisibleTiles(1);
        float alpha = _controller.Paused ? 1f : w.Alpha;
        bool detail = ppt >= _worldRenderer!.DetailThreshold;

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            detail || ppt >= 2f ? SamplerState.PointClamp : SamplerState.LinearClamp,
            null, null, null, _camera.Transform);
        if (detail) _worldRenderer.DrawDetail(_sb, visible, _renderTime);
        else _worldRenderer.DrawOverview(_sb, visible);
        _entityRenderer!.Draw(_sb, w, visible, ppt, alpha);
        _entityRenderer.DrawOverlays(_sb, w, ppt, alpha, _controller.Hovered, _controller.SelectedUnit);
        _controller.DrawWorldOverlay(_sb, w, _draw, _spriteArt, ppt);
        _sb.End();

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, null);
        _ui.Render(_sb);
        _sb.End();
    }

    private void DrawMessageScreen(string text, float? progress)
    {
        int w = GraphicsDevice.Viewport.Width, h = GraphicsDevice.Viewport.Height;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, null);
        var title = "PIRATE ENGINE";
        var ts = _draw.Font.Measure(title, 6);
        _draw.Font.Draw(_sb, title, new Vector2((w - ts.X) / 2, h / 2 - 120), UiContext.Theme.Gold, 6, shadow: true);
        var wrapped = _draw.Font.Wrap(text, w - 200, 2);
        var size = _draw.Font.Measure(wrapped, 2);
        _draw.Font.Draw(_sb, wrapped, new Vector2((w - size.X) / 2, h / 2 - 20), UiContext.Theme.Text, 2);
        if (progress is float p)
        {
            var bar = new Rectangle(w / 2 - 200, h / 2 + 20, 400, 14);
            _draw.Bar(_sb, bar, p, UiContext.Theme.Gold, new Color(30, 36, 50));
            _draw.Outline(_sb, bar, UiContext.Theme.Border);
        }
        _sb.End();
    }

    private void HandleScreenshots()
    {
        if (_world != null && _loading == null) _framesSinceLoad++;
        string? path = _pendingScreenshot;
        bool automated = _opt.ScreenshotPath != null && _world != null && _framesSinceLoad == _opt.ScreenshotFrames;
        if (automated) path = _opt.ScreenshotPath;
        if (path == null) return;
        _pendingScreenshot = null;
        try
        {
            SaveBackBuffer(path);
            _world?.Events.Post($"Screenshot saved: {path}", Core.Simulation.MessageKind.Info);
        }
        catch (Exception ex) { _world?.Events.Post("Screenshot failed: " + ex.Message, Core.Simulation.MessageKind.Warning); }
        if (automated) Exit();
    }

    private void SaveBackBuffer(string path)
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth, h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var data = new Color[w * h];
        GraphicsDevice.GetBackBufferData(data);
        using var tex = new Texture2D(GraphicsDevice, w, h);
        tex.SetData(data);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, w, h);
    }

    protected override void UnloadContent()
    {
        _world?.Dispose();
        base.UnloadContent();
    }

    /// <summary>IProgress that reports on the calling thread (no SynchronizationContext needed).</summary>
    private sealed class InlineProgress(Action<(string, float)> report) : IProgress<(string, float)>
    {
        public void Report((string, float) value) => report(value);
    }
}
