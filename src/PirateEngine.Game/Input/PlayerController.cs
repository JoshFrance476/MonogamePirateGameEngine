using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Game.Rendering;

namespace PirateEngine.Game.Input;

/// <summary>
/// Translates mouse/keyboard into camera movement and <see cref="GameWorld"/> commands.
/// <para>With no squad selected, orders go to the ship. Left-click: sail to water / dock at land /
/// attack a ship or bombard troops / place the selected building.</para>
/// <para>Left-click one of your squads ashore to select it; orders then go to the squad. Left-click:
/// march / attack troops (or a ship, for musketeers) / board your docked ship.</para>
/// Right-click: cancel placement, else deselect the squad, else drop anchor.
/// </summary>
public sealed class PlayerController
{
    public BuildingDef? BuildDef;      // non-null = placement mode
    /// <summary>Squad ashore receiving orders; null = orders go to the ship.</summary>
    public LandUnit? SelectedUnit;
    public bool Paused;
    public float TimeScale = 1f;
    public bool ShowHelp;
    public bool ShowCargo = true;
    public bool ShowDebug;
    public bool ShowPauseMenu;
    /// <summary>Settlement whose screen is open (null = closed).</summary>
    public Core.Politics.Settlement? OpenSettlement;
    /// <summary>States screen open, and which state it shows (null = the player's or the first).</summary>
    public bool ShowStates;
    public Core.Politics.State? ViewState;
    public bool AnyScreenOpen => OpenSettlement != null || ShowStates;

    public void OpenSettlementScreen(Core.Politics.Settlement s) { OpenSettlement = s; ShowStates = false; }
    public void OpenStateScreen(Core.Politics.State? s) { ShowStates = true; ViewState = s; OpenSettlement = null; }

    public GridPoint HoverTile { get; private set; } = GridPoint.Invalid;
    public Ship? HoverShip { get; private set; }
    public LandUnit? HoverUnit { get; private set; }
    /// <summary>The ship or squad under the cursor (for highlight rings).</summary>
    public MobileEntity? Hovered => (MobileEntity?)HoverUnit ?? HoverShip;
    /// <summary>True when the cursor is over the player's own ship.</summary>
    public bool HoverOwnShip { get; private set; }
    public Building? HoverBuilding { get; private set; }
    public PlacementError HoverPlacement { get; private set; }

    /// <summary>Raised for actions the game shell handles (save, load, screenshot, quit).</summary>
    public event Action<string>? ShellAction;

    public void Update(GameWorld w, Camera2D cam, InputState input, float realDt)
    {
        HandleKeys(w, cam, input);
        HandleCamera(cam, input, realDt);
        if (ShowPauseMenu) return;

        // What's under the cursor?
        var world = cam.ScreenToWorld(input.MousePosition);
        HoverTile = GridPoint.FromWorld(new System.Numerics.Vector2(world.X, world.Y));
        if (!w.Map.InBounds(HoverTile)) HoverTile = GridPoint.Invalid;
        var nworld = new System.Numerics.Vector2(world.X, world.Y);
        var ship = w.ShipAt(nworld, Math.Max(0.6f, 6f / cam.PixelsPerTile));
        HoverShip = ship != null && EntityRenderer.IsVisible(w, ship) && ship != w.Player ? ship : null;
        HoverOwnShip = ship == w.Player;
        var unit = w.UnitAt(nworld, Math.Max(0.55f, 6f / cam.PixelsPerTile));
        HoverUnit = unit != null && EntityRenderer.IsVisible(w, unit) ? unit : null;
        if (SelectedUnit is { IsDead: true } or { IsEmbarked: true }) SelectedUnit = null;
        HoverBuilding = HoverTile.IsValid ? w.BuildingAt(HoverTile) : null;
        if (BuildDef != null && HoverTile.IsValid) HoverPlacement = w.CanPlace(BuildDef, HoverTile);
        if (BuildDef != null && !w.Player.IsDocked) BuildDef = null; // left the dock

        if (input.MouseCapturedByUi) return;

        if (input.LeftClicked && HoverTile.IsValid)
        {
            if (BuildDef != null)
            {
                if (w.PlaceBuilding(BuildDef, HoverTile) != null && !input.Shift) BuildDef = null;
            }
            else if (HoverUnit is { Faction: Faction.Player, IsMilitia: false } own) SelectedUnit = own;
            else if (SelectedUnit != null) OrderSquad(w, SelectedUnit);
            else if (HoverUnit == null && HoverShip == null && HoverBuilding is { IsTownHall: true, Settlement: { } town }) OpenSettlementScreen(town);
            else if (HoverShip != null) w.CommandAttack(HoverShip);
            else if (HoverUnit != null) w.CommandAttack(HoverUnit); // bombard troops ashore
            else w.CommandMoveTo(HoverTile);
        }
        if (input.RightClicked)
        {
            if (BuildDef != null) BuildDef = null;
            else if (SelectedUnit != null) SelectedUnit = null;
            else w.CommandStop();
        }
    }

    private void OrderSquad(GameWorld w, LandUnit u)
    {
        if (HoverOwnShip) w.CommandEmbark(u);
        else if (HoverUnit != null && HoverUnit.Faction != Faction.Player) w.CommandUnitAttack(u, HoverUnit);
        else if (HoverShip != null) w.CommandUnitAttack(u, HoverShip);
        else w.CommandUnitMove(u, HoverTile);
    }

    /// <summary>Selects the next squad ashore (cycling) and points the camera at it.</summary>
    public void SelectNextSquad(GameWorld w, Camera2D cam)
    {
        var squads = w.Units.Where(u => u.Faction == Faction.Player && !u.IsDead && !u.IsMilitia).ToList();
        if (squads.Count == 0) { SelectedUnit = null; return; }
        int i = SelectedUnit == null ? 0 : (squads.IndexOf(SelectedUnit) + 1) % squads.Count;
        SelectedUnit = squads[i];
        cam.Follow = null;
        cam.Position = EntityRenderer.V(SelectedUnit.Position);
    }

    private void HandleKeys(GameWorld w, Camera2D cam, InputState input)
    {
        if (input.KeyPressed(Keys.Escape))
        {
            if (BuildDef != null) BuildDef = null;
            else if (OpenSettlement != null) OpenSettlement = null;
            else if (ShowStates) ShowStates = false;
            else if (SelectedUnit != null) SelectedUnit = null;
            else if (ShowHelp) ShowHelp = false;
            else ShowPauseMenu = !ShowPauseMenu;
        }
        if (input.KeyPressed(Keys.F1) || input.KeyPressed(Keys.H)) ShowHelp = !ShowHelp;
        if (ShowPauseMenu) return;

        if (input.KeyPressed(Keys.Space)) Paused = !Paused;
        if (input.KeyPressed(Keys.D1)) { TimeScale = 1; Paused = false; }
        if (input.KeyPressed(Keys.D2)) { TimeScale = 2; Paused = false; }
        if (input.KeyPressed(Keys.D3)) { TimeScale = 4; Paused = false; }
        if (input.KeyPressed(Keys.D4)) { TimeScale = 8; Paused = false; }
        if (input.KeyPressed(Keys.Tab)) ShowCargo = !ShowCargo;
        if (input.KeyPressed(Keys.K)) { if (ShowStates) ShowStates = false; else OpenStateScreen(ViewState); }
        if (input.KeyPressed(Keys.L))
        {
            // Nearest settlement to the camera / ship.
            var near = w.Politics.Settlements.Where(x => w.Fog.IsExplored(x.Tile))
                .OrderBy(x => x.Tile.Distance(w.Player.Tile)).FirstOrDefault();
            if (OpenSettlement != null) OpenSettlement = null;
            else if (near != null) OpenSettlementScreen(near);
        }
        if (OpenSettlement != null && !w.Politics.Settlements.Contains(OpenSettlement)) OpenSettlement = null;
        if (input.KeyPressed(Keys.G)) ShellAction?.Invoke("grid");
        if (input.KeyPressed(Keys.F3)) ShowDebug = !ShowDebug;
        if (input.KeyPressed(Keys.F4)) ShellAction?.Invoke("toggle-fog");
        if (input.KeyPressed(Keys.F5)) ShellAction?.Invoke("save");
        if (input.KeyPressed(Keys.F9)) ShellAction?.Invoke("load");
        if (input.KeyPressed(Keys.F12)) ShellAction?.Invoke("screenshot");
        if (input.KeyPressed(Keys.F)) cam.Follow = cam.Follow == null ? () => EntityRenderer.V(w.Player.Position) : null;
        if (input.KeyPressed(Keys.T) && w.Player.IsDocked) w.AutoTransfer();
        if (input.KeyPressed(Keys.R) && w.Player.IsDocked) w.Repair();
        if (input.KeyPressed(Keys.X))
        {
            if (SelectedUnit != null) w.CommandUnitStop(SelectedUnit);
            else w.CommandStop();
        }
        if (input.KeyPressed(Keys.E) && SelectedUnit != null) w.CommandEmbark(SelectedUnit);
        if (input.KeyPressed(Keys.U) && w.Player.IsDocked) w.DisembarkAll();
        if (input.KeyPressed(Keys.Q)) SelectNextSquad(w, cam);
        if (input.KeyPressed(Keys.Home))
        {
            cam.Follow = () => EntityRenderer.V(w.Player.Position);
        }
    }

    private void HandleCamera(Camera2D cam, InputState input, float dt)
    {
        float speed = 900f / cam.PixelsPerTile * dt * (input.Shift ? 2.5f : 1f);
        var pan = Vector2.Zero;
        if (input.KeyDown(Keys.W) || input.KeyDown(Keys.Up)) pan.Y -= 1;
        if (input.KeyDown(Keys.S) || input.KeyDown(Keys.Down)) pan.Y += 1;
        if (input.KeyDown(Keys.A) || input.KeyDown(Keys.Left)) pan.X -= 1;
        if (input.KeyDown(Keys.D) || input.KeyDown(Keys.Right)) pan.X += 1;
        if (pan != Vector2.Zero) cam.Pan(Vector2.Normalize(pan) * speed);
        if (input.MiddleDown && input.MouseDelta != Vector2.Zero) cam.Pan(-input.MouseDelta / cam.PixelsPerTile);
        if (input.WheelDelta != 0 && !input.MouseCapturedByUi)
            cam.ZoomAt(input.MousePosition, MathF.Pow(1.0018f, input.WheelDelta));
        if (input.KeyPressed(Keys.OemPlus) || input.KeyPressed(Keys.Add)) cam.ZoomAt(new Vector2(cam.ViewportWidth, cam.ViewportHeight) / 2, 1.5f);
        if (input.KeyPressed(Keys.OemMinus) || input.KeyPressed(Keys.Subtract)) cam.ZoomAt(new Vector2(cam.ViewportWidth, cam.ViewportHeight) / 2, 1 / 1.5f);
    }

    /// <summary>World-space overlay for building placement: valid tiles in reach and the ghost under the cursor.</summary>
    public void DrawWorldOverlay(SpriteBatch sb, GameWorld w, Draw draw, SpriteArt art, float ppt)
    {
        if (BuildDef == null || !w.Player.IsDocked) return;
        var dock = w.Player.DockTile;
        int r = (int)MathF.Ceiling(w.Config.DockReach);
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = new GridPoint(dock.X + dx, dock.Y + dy);
            if (w.CanPlace(BuildDef, p, checkCost: false) == PlacementError.None)
            {
                draw.Rect(sb, new Rectangle(p.X, p.Y, 1, 1), new Color(150, 255, 170) * 0.18f);
                draw.OutlineF(sb, new Vector2(p.X + 0.06f, p.Y + 0.06f), new Vector2(0.88f), Color.White * 0.45f, Math.Max(0.03f, 1f / ppt));
            }
        }
        if (!HoverTile.IsValid) return;
        bool ok = HoverPlacement == PlacementError.None;
        sb.Draw(art.Building(BuildDef), new Rectangle(HoverTile.X, HoverTile.Y, 1, 1), (ok ? Color.White : new Color(255, 120, 120)) * 0.75f);
        draw.OutlineF(sb, new Vector2(HoverTile.X, HoverTile.Y), Vector2.One, ok ? new Color(90, 255, 120) : new Color(255, 80, 70), Math.Max(0.05f, 2f / ppt));
    }
}
