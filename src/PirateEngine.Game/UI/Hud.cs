using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.World;
using PirateEngine.Game.Input;
using PirateEngine.Game.Rendering;
using T = PirateEngine.Game.UI.UiContext.Theme;

namespace PirateEngine.Game.UI;

/// <summary>
/// All in-game UI: status bar, message log, minimap, dock panel (build / goods / ship tabs),
/// cargo panel, hover info, help and pause menu. Pure presentation over the world's public
/// state and command API.
/// </summary>
public sealed partial class Hud
{
    private enum DockTab { Build, Goods, Ship, Troops }
    private DockTab _tab = DockTab.Build;
    private int _scroll;
    private readonly UiContext _ui;
    private readonly SpriteArt _art;
    private readonly Draw _draw;

    public const int TopBarHeight = 32;
    public const int MinimapSize = 196;
    public const int PanelWidth = 340;

    public Hud(UiContext ui, SpriteArt art, Draw draw)
    {
        _ui = ui;
        _art = art;
        _draw = draw;
    }

    /// <summary>Selects a dock-panel tab by name ("build", "goods", "ship").</summary>
    public void ShowTab(string tab) => _tab = tab.ToLowerInvariant() switch
    {
        "goods" => DockTab.Goods,
        "ship" => DockTab.Ship,
        "troops" => DockTab.Troops,
        _ => DockTab.Build,
    };

    public void Build(GameWorld w, PlayerController pc, Camera2D cam, WorldRenderer worldRenderer, InputState input, float fps, Action<string> shell)
    {
        int below = _ui.Mark;   // town banners are drawn beneath every panel but lose clicks to them
        TopBar(w, pc, fps);
        Messages(w);
        Minimap(w, cam, worldRenderer, input);
        bool screen = pc.AnyScreenOpen;
        if (pc.OpenSettlement != null) SettlementScreen(w, pc, cam);
        else if (pc.ShowStates) StatesScreen(w, pc, input);
        else if (w.Player.IsDocked) DockPanel(w, pc, input);
        else if (pc.ShowCargo) CargoPanel(w);
        if (pc.SelectedUnit != null && !screen) SquadPanel(w, pc);
        if (!pc.ShowPauseMenu && !pc.ShowHelp) SettlementBanners(w, pc, cam, below);
        if (!input.MouseCapturedByUi && !screen) HoverInfo(w, pc); // world tooltips only when not over a panel
        if (pc.BuildDef != null) PlacementHint(pc);
        if (pc.ShowDebug) DebugPanel(w, pc, cam, worldRenderer, fps);
        if (pc.ShowHelp) Help();
        if (pc.ShowPauseMenu) PauseMenu(pc, shell);
    }

    // ---------------------------------------------------------------------------------

    private static string Danger(float d) => d < 0.05f ? "Home waters" : d < 0.35f ? "Contested" : d < 0.7f ? "Dangerous" : "Deadly";
    private static Color DangerColor(float d) => d < 0.05f ? T.Good : d < 0.35f ? T.Warn : T.Bad;

    private void TopBar(GameWorld w, PlayerController pc, float fps)
    {
        var bar = new Rectangle(0, 0, _ui.ScreenWidth, TopBarHeight);
        _ui.Capture(bar);
        _ui.Rect(bar, T.Panel);
        _ui.Rect(new Rectangle(0, TopBarHeight - 2, _ui.ScreenWidth, 2), T.Border * 0.8f);

        var p = w.Player;
        int x = 10, y = 9;
        void Item(string label, string value, Color c, int gap = 22)
        {
            _ui.Text(label, x, y, T.TextDim);
            x += _ui.TextWidth(label) + 8;
            _ui.Text(value, x, y, c);
            x += _ui.TextWidth(value) + gap;
        }

        Item("GOLD", w.Gold.ToString("N0"), T.Gold);
        _ui.Text("HULL", x, y, T.TextDim);
        x += _ui.TextWidth("HULL") + 8;
        var hullCol = p.HullFraction > 0.5f ? T.Good : p.HullFraction > 0.25f ? T.Warn : T.Bad;
        _ui.Bar(new Rectangle(x, 8, 110, 16), p.HullFraction, hullCol, $"{(int)p.Hull}/{(int)p.MaxHull}");
        x += 132;
        int ammo = p.Cargo[w.Defs.ResourceIndex(w.Config.AmmoResource)];
        Item("SHOT", ammo.ToString(), ammo == 0 ? T.Bad : ammo < 10 ? T.Warn : T.Text);
        Item("HOLD", $"{p.Cargo.Total}/{p.Cargo.Capacity}", p.Cargo.Free == 0 ? T.Warn : T.Text);
        int ashore = w.Units.Count(u => u.Faction == Core.Entities.Faction.Player && !u.IsDead && !u.IsMilitia);
        Item("TROOPS", $"{p.TroopsAboard}/{p.TroopCapacity}" + (ashore > 0 ? $" +{ashore} ashore" : ""), T.Text);
        Item("SHIP", p.Class.Name, T.Text);
        float danger = w.DangerAt(p.Tile);
        Item("SEA", Danger(danger), DangerColor(danger));
        var me = w.Politics.PlayerState;
        int wars = w.Politics.Diplomacy.EnemiesOf(me).Count(e => e.IsActive);
        string realm = me.Founded ? $"{me.Settlements.Count} town{(me.Settlements.Count == 1 ? "" : "s")}" : "none";
        if (wars > 0) realm += $", {wars} war{(wars == 1 ? "" : "s")}";
        if (w.Politics.PendingOffers.Count > 0) realm += $", {w.Politics.PendingOffers.Count} offer{(w.Politics.PendingOffers.Count == 1 ? "" : "s")}!";
        string label = Fit("STATES " + realm, Math.Max(60, _ui.ScreenWidth - 300 - x));
        var statesBtn = new Rectangle(x, 5, _ui.TextWidth(label) + 24, 22);
        if (_ui.Button(statesBtn, label, tooltip: $"States and diplomacy [K] - nearest town [L]\nYour realm: {realm}",
                textColor: w.Politics.PendingOffers.Count > 0 ? T.Gold : wars > 0 ? T.Bad : T.Text))
        {
            if (pc.ShowStates) pc.ShowStates = false; else pc.OpenStateScreen(pc.ViewState);
        }

        string speed = pc.Paused ? "PAUSED" : $"x{pc.TimeScale:0}";
        int mins = (int)(w.Time / 60), secs = (int)(w.Time % 60);
        string right = $"{speed}   {mins:00}:{secs:00}   ? help";
        _ui.Text(right, _ui.ScreenWidth - _ui.TextWidth(right) - 12, y, pc.Paused ? T.Warn : T.TextDim);

        // Contextual status line.
        string? status = pc.SelectedUnit is { } sel ? $"Orders go to the {sel.Name} - right-click to command the ship"
            : p.PathPending ? "Plotting a course..."
            : p.Target != null ? $"Engaging {p.Target.Name}"
            : p.PendingDock.IsValid ? "Sailing to dock..."
            : p.IsDocked ? "Docked - build, trade goods and repair from the panel on the right"
            : null;
        if (status != null)
        {
            int sw = _ui.TextWidth(status) + 20;
            var r = new Rectangle((_ui.ScreenWidth - sw) / 2, TopBarHeight + 6, sw, 22);
            _ui.Rect(r, T.Panel);
            _ui.Outline(r, T.Border * 0.5f);
            _ui.TextCentered(status, r, T.Info);
        }
    }

    private void Messages(GameWorld w)
    {
        var recent = w.Events.Log.Where(m => w.Time - m.Time < 14f).TakeLast(7).ToList();
        int lh = 20;
        int y = _ui.ScreenHeight - 12 - recent.Count * lh;
        foreach (var m in recent)
        {
            float age = w.Time - m.Time;
            float a = Math.Clamp((14f - age) / 2f, 0f, 1f);
            var c = m.Kind switch { MessageKind.Good => T.Good, MessageKind.Warning => T.Warn, MessageKind.Danger => T.Bad, _ => T.Text };
            int width = Math.Min(_ui.TextWidth(m.Text) + 14, _ui.ScreenWidth - MinimapSize - 40);
            _ui.Rect(new Rectangle(10, y - 3, width, lh - 2), new Color(0, 0, 0) * (0.55f * a));
            _ui.Text(m.Text, 17, y, c * a);
            y += lh;
        }
    }

    private void Minimap(GameWorld w, Camera2D cam, WorldRenderer wr, InputState input)
    {
        int size = MinimapSize;
        float aspect = w.Map.Width / (float)w.Map.Height;
        int mw = aspect >= 1 ? size : (int)(size * aspect), mh = aspect >= 1 ? (int)(size / aspect) : size;
        var frame = new Rectangle(_ui.ScreenWidth - mw - 16, _ui.ScreenHeight - mh - 16, mw + 8, mh + 8);
        var inner = new Rectangle(frame.X + 4, frame.Y + 4, mw, mh);
        _ui.Panel(frame);

        float sx = mw / (float)w.Map.Width, sy = mh / (float)w.Map.Height;
        Point ToMini(System.Numerics.Vector2 p) => new(inner.X + (int)(p.X * sx), inner.Y + (int)(p.Y * sy));

        _ui.Custom(sb => wr.DrawInto(sb, inner));
        foreach (var b in w.Buildings)
        {
            if (b.Settlement != null || !w.Fog.IsExplored(b.Tile)) continue;
            var mp = ToMini(b.Tile.Center);
            _ui.Rect(new Rectangle(mp.X - 1, mp.Y - 1, 3, 3), T.Gold);
        }
        foreach (var town in w.Politics.Settlements)
        {
            if (!w.Fog.IsExplored(town.Tile)) continue;
            var mp = ToMini(town.Tile.Center);
            int sz = 3 + Math.Min(town.LevelIndex, 3);
            _ui.Rect(new Rectangle(mp.X - sz / 2 - 1, mp.Y - sz / 2 - 1, sz + 2, sz + 2), town.Siege != null ? T.Bad : Color.Black);
            _ui.Rect(new Rectangle(mp.X - sz / 2, mp.Y - sz / 2, sz, sz), EntityRenderer.OwnerColor(town.Owner));
        }
        foreach (var s in w.Ships)
        {
            if (s.IsSunk || s == w.Player || !EntityRenderer.IsVisible(w, s)) continue;
            var mp = ToMini(s.Position);
            _ui.Rect(new Rectangle(mp.X - 1, mp.Y - 1, 3, 3), EntityRenderer.MapColor(s.Faction));
        }
        foreach (var u in w.Units)
        {
            if (u.IsDead || !EntityRenderer.IsVisible(w, u)) continue;
            var mp = ToMini(u.Position);
            _ui.Rect(new Rectangle(mp.X - 1, mp.Y - 1, 2, 2), EntityRenderer.UnitColor(u));
        }
        // Camera view rectangle and the player.
        var vis = cam.VisibleTiles(0);
        var a = ToMini(new System.Numerics.Vector2(vis.X, vis.Y));
        var bpt = ToMini(new System.Numerics.Vector2(vis.Right, vis.Bottom));
        var view = Rectangle.Intersect(new Rectangle(a.X, a.Y, Math.Max(2, bpt.X - a.X), Math.Max(2, bpt.Y - a.Y)), inner);
        _ui.Outline(view, Color.White * 0.8f);
        var pp = ToMini(w.Player.Position);
        _ui.Rect(new Rectangle(pp.X - 2, pp.Y - 2, 5, 5), Color.Black);
        _ui.Rect(new Rectangle(pp.X - 1, pp.Y - 1, 3, 3), Color.White);

        // Left-drag on the minimap moves the camera; right-click sails there.
        if (_ui.Hover(inner))
        {
            var m = _ui.Mouse;
            var worldPt = new Vector2((m.X - inner.X) / sx, (m.Y - inner.Y) / sy);
            if (input.LeftDown) { cam.Position = worldPt; cam.Follow = null; }
            if (input.RightClicked) w.CommandMoveTo(new Core.Util.GridPoint((int)worldPt.X, (int)worldPt.Y));
            _ui.SetTooltip("Minimap: left-drag to look, right-click to set sail");
        }
    }

    // ---- dock panel ----------------------------------------------------------------------

    private Rectangle PanelRect()
    {
        int top = TopBarHeight + 36;
        int bottom = _ui.ScreenHeight - MinimapSize - 36;
        return new Rectangle(_ui.ScreenWidth - PanelWidth - 12, top, PanelWidth, Math.Max(200, bottom - top));
    }

    // ---- troops --------------------------------------------------------------------------

    private static string KindLabel(Core.Data.UnitDef d) => d.IsRanged ? "ranged" : "melee";

    private static string UnitStats(Core.Data.UnitDef d) =>
        $"{d.Members} men, {d.HpPerMember} hp each. {(d.IsRanged ? $"Muskets: {d.Damage} per hit on troops, {d.DamageVsShips} on ships" : $"Melee: {d.Damage} per man per blow")}, " +
        $"range {d.Range:0.#}, every {d.Reload:0.#}s. Speed {d.Speed:0.#}.";

    /// <summary>Shortens text with ".." so it fits in <paramref name="maxWidth"/> pixels.</summary>
    private string Fit(string text, int maxWidth, int scale = 2)
    {
        if (_ui.TextWidth(text, scale) <= maxWidth) return text;
        while (text.Length > 1 && _ui.TextWidth(text + "..", scale) > maxWidth) text = text[..^1];
        return text + "..";
    }

    private void SquadRow(Core.Entities.LandUnit u, Rectangle row, int buttonsWidth)
    {
        _ui.Rect(row, T.PanelLight);
        _ui.Outline(row, T.Border * 0.3f);
        _ui.Image(_art.Soldier(u.Def.Kind, u.Faction, EntityRenderer.UnitCoat(u)), new Rectangle(row.X + 4, row.Y + 4, 24, 24), Color.White);
        _ui.Text(Fit(u.Name, row.Width - 40 - buttonsWidth), row.X + 32, row.Y + 4, T.Text);
        _ui.Text($"{KindLabel(u.Def)}  {u.Members}/{u.Def.Members} men", row.X + 32, row.Y + 22, T.TextDim, 1);
        _ui.Bar(new Rectangle(row.X + 32, row.Y + 33, 120, 6), u.HealthFraction, u.HealthFraction > 0.5f ? T.Good : T.Warn);
    }

    private void TroopsTab(GameWorld w, PlayerController pc, Rectangle body)
    {
        var p = w.Player;
        int y = body.Y - _scroll;
        int clipTop = body.Y, clipBottom = body.Bottom;
        bool Visible(int top, int h) => top >= clipTop && top + h <= clipBottom;

        if (Visible(y, 22)) _ui.Text($"Aboard {p.TroopsAboard}/{p.TroopCapacity}", body.X + 4, y + 4, T.Gold);
        var landAll = new Rectangle(body.Right - 132, y, 128, 22);
        if (Visible(y, 22) && _ui.Button(landAll, "Land all [U]", p.Troops.Any(t => w.CannotDisembark(t) == null), scale: 1))
            w.DisembarkAll();
        y += 28;
        if (p.Troops.Count == 0 && Visible(y, 18)) { _ui.Text("No troops aboard.", body.X + 4, y + 2, T.TextDim, 1); y += 18; }
        foreach (var u in p.Troops.ToList())
        {
            var row = new Rectangle(body.X, y, body.Width, 44);
            y += 48;
            if (!Visible(row.Y, row.Height)) continue;
            SquadRow(u, row, 66);
            string? why = w.CannotDisembark(u);
            if (_ui.Button(new Rectangle(row.Right - 64, row.Y + 10, 58, 24), "Land", why == null, why ?? "Put this squad ashore next to the dock"))
                w.Disembark(u);
        }

        // Squads ashore on this island can be recalled.
        var island = w.Map.RegionAt(p.DockTile);
        var ashore = w.Units.Where(u => u.Faction == Core.Entities.Faction.Player && !u.IsDead && !u.IsMilitia && w.Map.RegionAt(u.Tile) == island).ToList();
        y += 6;
        if (Visible(y, 20)) _ui.Text($"Ashore on this island ({ashore.Count})", body.X + 4, y + 4, T.Gold);
        y += 24;
        foreach (var u in ashore)
        {
            var row = new Rectangle(body.X, y, body.Width, 44);
            y += 48;
            if (!Visible(row.Y, row.Height)) continue;
            SquadRow(u, row, 96);
            if (_ui.Button(new Rectangle(row.Right - 96, row.Y + 10, 44, 24), "Select", tooltip: "Give this squad orders", scale: 1)) pc.SelectedUnit = u;
            string? why = w.CannotEmbark(u);
            bool boarding = u.PendingEmbark != null;
            if (_ui.Button(new Rectangle(row.Right - 48, row.Y + 10, 44, 24), boarding ? "..." : "Board", why == null && !boarding,
                    boarding ? "Marching to the ship" : why ?? "March back to the ship and board", scale: 1))
                w.CommandEmbark(u);
        }

        // Recruitment.
        y += 6;
        bool barracks = w.BarracksInReach();
        if (Visible(y, 20)) _ui.Text(barracks ? "Recruit at the Barracks" : "Recruit - needs a Barracks", body.X + 4, y + 4, barracks ? T.Gold : T.TextDim);
        y += 24;
        foreach (var def in w.Defs.Units)
        {
            var row = new Rectangle(body.X, y, body.Width, 40);
            y += 44;
            if (!Visible(row.Y, row.Height)) continue;
            string? why = w.CannotRecruit(def);
            string cost = $"{def.RecruitGold}g  " + string.Join("  ", def.RecruitCostList.Select(c => $"{w.Defs.Resources[c.res].Name} {c.amount}"));
            if (_ui.Button(row, "", why == null, $"{def.Name} ({KindLabel(def)})\n{def.Description}\n\n{UnitStats(def)}\nCost: {cost}" + (why != null ? $"\n\n{why}." : "")))
                w.RecruitUnit(def);
            _ui.Image(_art.Soldier(def.Kind, Core.Entities.Faction.Player), new Rectangle(row.X + 4, row.Y + 6, 28, 28), Color.White * (why == null ? 1f : 0.5f));
            _ui.Text($"{def.Name} ({def.Members})", row.X + 38, row.Y + 5, why == null ? T.Text : T.TextDim);
            _ui.Text(cost, row.X + 38, row.Y + 24, why == null ? T.Gold * 0.9f : T.TextDim, 1);
        }
        int content = y + _scroll - body.Y;
        _scroll = Math.Min(_scroll, Math.Max(0, content - body.Height));
        ScrollBar(body, content);
    }

    public const int SquadPanelHeight = 96;

    /// <summary>Top-left card for the selected squad: strength, current order, controls.</summary>
    private void SquadPanel(GameWorld w, PlayerController pc)
    {
        var u = pc.SelectedUnit!;
        int width = 430, height = SquadPanelHeight;
        var r = new Rectangle(12, TopBarHeight + 40, width, height);
        _ui.Panel(r);
        _ui.Image(_art.Soldier(u.Def.Kind, u.Faction, EntityRenderer.UnitCoat(u)), new Rectangle(r.X + 8, r.Y + 8, 40, 40), Color.White);
        _ui.Text(u.Name, r.X + 56, r.Y + 8, T.Gold);
        string order = u.PendingEmbark != null ? "Marching to board the ship"
            : u.Target is { } t ? $"Attacking {t.Name}"
            : u.IsMoving || u.PathPending ? "Marching"
            : "Holding position";
        _ui.Text($"{KindLabel(u.Def)} - {u.Members}/{u.Def.Members} men - {order}", r.X + 56, r.Y + 28, T.Text, 1);
        _ui.Bar(new Rectangle(r.X + 56, r.Y + 40, 200, 8), u.HealthFraction, u.HealthFraction > 0.5f ? T.Good : u.HealthFraction > 0.25f ? T.Warn : T.Bad);
        string hint = u.Def.IsRanged
            ? "Click land: march   enemy troops or ship: fire"
            : "Click land: march   enemy troops: charge";
        _ui.Text(hint, r.X + 10, r.Y + 58, T.TextDim, 1);
        _ui.Text("Your docked ship: board (E)   X: halt   Q: next squad", r.X + 10, r.Y + 74, T.TextDim, 1);
    }

    private void DockPanel(GameWorld w, PlayerController pc, InputState input)
    {
        var r = PanelRect();
        string where = Biomes.Name(w.Map.GetBiome(w.Player.DockTile));
        _ui.Panel(r, $"Docked: {where} shore");

        int tabW = (r.Width - 18) / 4;
        var tabs = new[] { (DockTab.Build, "Build"), (DockTab.Goods, "Goods"), (DockTab.Ship, "Ship"), (DockTab.Troops, "Troops") };
        for (int i = 0; i < tabs.Length; i++)
        {
            var tr = new Rectangle(r.X + 6 + i * (tabW + 2), r.Y + 30, tabW, 26);
            bool active = _tab == tabs[i].Item1;
            if (_ui.Button(tr, tabs[i].Item2, textColor: active ? T.Gold : T.TextDim)) { _tab = tabs[i].Item1; _scroll = 0; }
            if (active) _ui.Rect(new Rectangle(tr.X, tr.Bottom - 3, tr.Width, 3), T.Gold);
        }

        var body = new Rectangle(r.X + 6, r.Y + 62, r.Width - 12, r.Height - 68);
        if (_ui.Hover(r) && input.WheelDelta != 0)
        {
            _scroll = Math.Max(0, _scroll - Math.Sign(input.WheelDelta) * 40);
            _ui.WheelConsumed = true;
            input.MouseCapturedByUi = true;
        }
        switch (_tab)
        {
            case DockTab.Build: BuildTab(w, pc, body); break;
            case DockTab.Goods: GoodsTab(w, pc, body); break;
            case DockTab.Ship: ShipTab(w, body); break;
            case DockTab.Troops: TroopsTab(w, pc, body); break;
        }
    }

    /// <summary>Thin scroll indicator on the right edge of a scrollable body.</summary>
    private void ScrollBar(Rectangle body, int contentHeight)
    {
        if (contentHeight <= body.Height) return;
        var track = new Rectangle(body.Right + 1, body.Y, 3, body.Height);
        _ui.Rect(track, Color.Black * 0.5f);
        int thumbH = Math.Max(20, body.Height * body.Height / contentHeight);
        int thumbY = body.Y + (body.Height - thumbH) * _scroll / Math.Max(1, contentHeight - body.Height);
        _ui.Rect(new Rectangle(track.X, thumbY, 3, thumbH), T.Border);
    }

    private string CostText(GameWorld w, BuildingDef def)
    {
        var parts = new List<string> { $"{def.GoldCost}g" };
        parts.AddRange(def.BuildCostList.Select(c => $"{w.Defs.Resources[c.res].Name} {c.amount}"));
        return string.Join("  ", parts);
    }

    private static string RecipeText(GameWorld w, RecipeDef r)
    {
        string Side((int res, int amount)[] list) =>
            list.Length == 0 ? "" : string.Join(" + ", list.Select(x => $"{x.amount} {w.Defs.Resources[x.res].Name}"));
        string inputs = Side(r.InputList), outputs = Side(r.OutputList);
        if (r.GoldOutput > 0) outputs = (outputs.Length > 0 ? outputs + " + " : "") + $"{r.GoldOutput} gold";
        return (inputs.Length > 0 ? inputs + " -> " : "") + outputs + $"  ({r.CycleSeconds:0.#}s)";
    }

    private void BuildTab(GameWorld w, PlayerController pc, Rectangle body)
    {
        const int rowH = 46;
        int maxScroll = Math.Max(0, w.Defs.Buildings.Count * rowH - body.Height);
        _scroll = Math.Min(_scroll, maxScroll);
        ScrollBar(body, w.Defs.Buildings.Count * rowH);
        int y = body.Y - _scroll;
        foreach (var def in w.Defs.Buildings)
        {
            var row = new Rectangle(body.X, y, body.Width, rowH - 4);
            y += rowH;
            if (row.Y < body.Y || row.Bottom > body.Bottom) continue;

            bool canAfford = w.Gold >= def.GoldCost && w.Player.Cargo.HasAll(def.BuildCostList);
            bool anySpot = AnyValidSpot(w, def);
            bool selected = pc.BuildDef == def;
            string tip = $"{def.Name}\n{def.Description}\n\n" +
                         string.Join("\n", def.Recipes.Select(rc => RecipeText(w, rc))) +
                         (def.AllowedBiomes.Count > 0 ? $"\n\nBiomes: {string.Join(", ", def.AllowedBiomes.Select(b => Biomes.Name(Biomes.Parse(b))))}" : "") +
                         (def.RequiresCoast ? "\nMust be on the coast." : "") +
                         (!anySpot ? "\n\nNo suitable land within reach of this dock." : "");
            if (_ui.Button(row, "", enabled: canAfford && anySpot, tooltip: tip))
                pc.BuildDef = selected ? null : def;
            if (selected) _ui.Outline(row, T.Gold, 2);
            _ui.Image(_art.Building(def), new Rectangle(row.X + 5, row.Y + 5, 32, 32), Color.White * (canAfford && anySpot ? 1f : 0.45f));
            _ui.Text(def.Name, row.X + 44, row.Y + 6, canAfford && anySpot ? T.Text : T.TextDim);
            _ui.Text(CostText(w, def), row.X + 44, row.Y + 25, canAfford ? T.Gold * 0.9f : T.Bad, 1);
            if (!anySpot) _ui.Text("no site", row.Right - 50, row.Y + 25, T.TextDim, 1);
        }
    }

    private readonly Dictionary<BuildingDef, (float time, bool ok)> _spotCache = new();

    private bool AnyValidSpot(GameWorld w, BuildingDef def)
    {
        // Cached for a second: scanning the reach circle for every building every frame adds up.
        if (_spotCache.TryGetValue(def, out var c) && w.Time - c.time < 1f) return c.ok;
        var dock = w.Player.DockTile;
        int r = (int)MathF.Ceiling(w.Config.DockReach);
        bool ok = false;
        for (int dy = -r; dy <= r && !ok; dy++)
            for (int dx = -r; dx <= r && !ok; dx++)
                ok = w.CanPlace(def, new Core.Util.GridPoint(dock.X + dx, dock.Y + dy), checkCost: false) == PlacementError.None;
        _spotCache[def] = (w.Time, ok);
        return ok;
    }

    private void GoodsTab(GameWorld w, PlayerController pc, Rectangle body)
    {
        var buildings = w.BuildingsInReach().OrderBy(b => b.Tile.Distance(w.Player.DockTile)).ToList();
        bool towns = w.Politics.SettlementsInReach().Any();
        var auto = new Rectangle(body.X, body.Y, body.Width, 28);
        if (_ui.Button(auto, "Auto transfer all  [T]", buildings.Count > 0,
                "Collect every building's output into the hold, then deliver what each building needs from the hold. Cannonballs and planks are never unloaded."))
            w.AutoTransfer();

        if (buildings.Count == 0 && !towns)
        {
            _ui.Text(_ui.Font.Wrap("No buildings within reach of this dock. Build some from the Build tab.", body.Width - 10), body.X + 4, body.Y + 40, T.TextDim);
            return;
        }

        int y = body.Y + 36 - _scroll;
        int clipTop = body.Y + 34;
        y += TownsInReachCards(w, pc, body.X, y, body.Width, clipTop, body.Bottom);
        foreach (var b in buildings)
        {
            int h = BuildingCard(w, b, body.X, y, body.Width, clipTop, body.Bottom);
            y += h + 6;
        }
        int content = y + _scroll - (body.Y + 36);
        _scroll = Math.Min(_scroll, Math.Max(0, content - (body.Height - 36)));
        ScrollBar(new Rectangle(body.X, body.Y + 36, body.Width, body.Height - 36), content);
    }

    /// <summary>Draws one building's logistics card; returns its height. Skips drawing outside the clip band.</summary>
    private int BuildingCard(GameWorld w, Building b, int x, int y, int width, int clipTop, int clipBottom)
    {
        // Lay out chips first to know the height.
        var outs = b.Output.NonZero().ToList();
        var ins = Enumerable.Range(0, w.Defs.ResourceCount).Where(r => b.Def.AcceptsInput[r]).ToList();
        int chipRows = (outs.Count > 0 ? ChipRows(w, outs.Select(o => o.resource), width - 50) : 1) +
                       (ins.Count > 0 ? ChipRows(w, ins, width - 50) : 0);
        int h = 46 + chipRows * 22;
        bool visible = y >= clipTop && y + h <= clipBottom;
        if (!visible) return h;

        var card = new Rectangle(x, y, width, h);
        _ui.Rect(card, T.PanelLight);
        _ui.Outline(card, T.Border * 0.35f);
        _ui.Image(_art.Building(b.Def), new Rectangle(x + 4, y + 4, 28, 28), Color.White);
        _ui.Text(b.Def.Name, x + 38, y + 5, T.Text);
        var (stateText, stateCol) = b.State switch
        {
            ProductionState.Producing => ("working", T.Good),
            ProductionState.WaitingForInputs => ("needs input", T.Warn),
            ProductionState.OutputFull => ("storage full", T.Bad),
            ProductionState.NoRecipe => ("service", T.Info),
            _ => ("idle", T.TextDim),
        };
        _ui.Text(stateText, card.Right - _ui.TextWidth(stateText, 1) - 6, y + 8, stateCol, 1);
        if (b.Def.Recipes.Count > 0)
            _ui.Bar(new Rectangle(x + 38, y + 24, width - 66, 8), b.Progress01, T.Gold * 0.85f);

        int cy = y + 40;
        if (b.Def.Recipes.Count > 0 || outs.Count > 0)
        {
            _ui.Text("Take", x + 6, cy + 4, T.TextDim, 1);
            if (outs.Count == 0) { _ui.Text("-", x + 50, cy + 4, T.TextDim, 1); cy += 22; }
            else cy = Chips(w, outs.Select(o => o.resource), x + 46, cy, width - 50, r => $"{w.Defs.Resources[r].Name} {b.Output[r]}",
                r => w.Player.Cargo.Free > 0, r => w.LoadFromBuilding(b, r, b.Output[r]), "Load into the hold");
        }
        if (ins.Count > 0)
        {
            _ui.Text("Give", x + 6, cy + 4, TextDimOr(ins.Any(r => w.Player.Cargo[r] > 0)), 1);
            Chips(w, ins, x + 46, cy, width - 50, r => $"{w.Defs.Resources[r].Name} {b.Input[r]} (+{w.Player.Cargo[r]})",
                r => w.Player.Cargo[r] > 0 && b.Input.Free > 0, r => w.UnloadToBuilding(b, r, w.Player.Cargo[r]),
                "Unload from the hold: stored here (+ carried)");
        }

        // Demolish (shift-click) in the corner.
        var del = new Rectangle(card.Right - 20, y + 22, 16, 14);
        if (_ui.Button(del, "x", tooltip: "Shift+click to demolish (refunds half)", scale: 1) && _shiftHeld) w.Demolish(b);
        return h;
    }

    private static Color TextDimOr(bool active) => active ? T.Text : T.TextDim;
    private bool _shiftHeld;
    public void SetModifiers(bool shift) => _shiftHeld = shift;

    private int ChipWidth(GameWorld w, string text) => _ui.TextWidth(text, 1) + 14;

    private int ChipRows(GameWorld w, IEnumerable<int> resources, int width)
    {
        int rows = 1, x = 0;
        foreach (var r in resources)
        {
            int cw = ChipWidth(w, w.Defs.Resources[r].Name + " 000 (+000)");
            if (x + cw > width && x > 0) { rows++; x = 0; }
            x += cw + 4;
        }
        return rows;
    }

    private int Chips(GameWorld w, IEnumerable<int> resources, int x0, int y, int width, Func<int, string> label,
        Func<int, bool> enabled, Action<int> onClick, string tooltip)
    {
        int x = x0;
        foreach (var r in resources)
        {
            string text = label(r);
            int cw = ChipWidth(w, w.Defs.Resources[r].Name + " 000 (+000)");
            if (x + cw > x0 + width && x > x0) { x = x0; y += 22; }
            var chip = new Rectangle(x, y, cw, 19);
            var col = Draw.ParseHex(w.Defs.Resources[r].Color, Color.White);
            if (_ui.Button(chip, "", enabled(r), tooltip)) onClick(r);
            _ui.Rect(new Rectangle(chip.X + 3, chip.Y + 5, 8, 9), col);
            _ui.Text(text, chip.X + 14, chip.Y + 6, enabled(r) ? T.Text : T.TextDim, 1);
            x += cw + 4;
        }
        return y + 22;
    }

    private void ShipTab(GameWorld w, Rectangle body)
    {
        var p = w.Player;
        int y = body.Y;
        _ui.Text($"{p.Name}  -  {p.Class.Name}", body.X + 4, y, T.Gold);
        y += 24;
        _ui.Text(StatsLine(p.Class), body.X + 4, y, T.TextDim, 1);
        y += 18;
        _ui.Bar(new Rectangle(body.X + 4, y, body.Width - 8, 16), p.HullFraction, T.Good, $"Hull {(int)p.Hull}/{(int)p.MaxHull}");
        y += 24;

        int planks = p.Cargo[w.Defs.ResourceIndex(w.Config.RepairResource)];
        int need = (int)MathF.Ceiling((p.MaxHull - p.Hull) / w.Config.HullPerPlank);
        string repairName = w.Defs.Resource(w.Config.RepairResource).Name;
        if (_ui.Button(new Rectangle(body.X + 4, y, body.Width - 8, 28), need > 0 ? $"Repair [R]  ({Math.Min(need, planks)}/{need} {repairName})" : "Hull is sound",
                need > 0 && planks > 0, $"Each {repairName} restores {w.Config.HullPerPlank} hull."))
            w.Repair();
        y += 34;

        var next = p.Class.UpgradeTarget;
        if (next != null)
        {
            bool can = w.CanUpgradeShip(out var reason);
            string cost = $"{p.Class.UpgradeGold}g  " + string.Join("  ", p.Class.UpgradeCostList.Select(c => $"{w.Defs.Resources[c.res].Name} {c.amount}"));
            if (_ui.Button(new Rectangle(body.X + 4, y, body.Width - 8, 28), $"Refit as {next.Name}", can,
                    $"Refit at a Shipyard in reach.\nCost: {cost}\n{StatsLine(next)}" + (can ? "" : $"\n\n{reason}.")))
                w.UpgradeShip();
            y += 32;
            _ui.Text(_ui.Font.Wrap(cost, body.Width - 8, 1), body.X + 4, y, can ? T.Gold : T.TextDim, 1);
            y += 22;
        }

        y += 6;
        _ui.Text($"Hold {p.Cargo.Total}/{p.Cargo.Capacity}", body.X + 4, y, T.Text);
        y += 22;
        CargoList(w, body.X + 4, y, body.Width - 8, body.Bottom);
    }

    private static string StatsLine(ShipClassDef c) =>
        $"Speed {c.Speed:0.#}  Guns {c.Guns}x{c.Damage}  Range {c.Range:0.#}  Hold {c.Cargo}";

    private void CargoList(GameWorld w, int x, int y, int width, int bottom)
    {
        var items = w.Player.Cargo.NonZero().ToList();
        if (items.Count == 0) { _ui.Text("(empty)", x, y, T.TextDim, 1); return; }
        int colW = width / 2;
        for (int i = 0; i < items.Count; i++)
        {
            int cx = x + (i % 2) * colW, cy = y + (i / 2) * 18;
            if (cy + 16 > bottom) break;
            var (res, amount) = items[i];
            _ui.Rect(new Rectangle(cx, cy + 3, 9, 9), Draw.ParseHex(w.Defs.Resources[res].Color, Color.White));
            _ui.Text($"{w.Defs.Resources[res].Name} {amount}", cx + 14, cy + 4, T.Text, 1);
        }
    }

    private void CargoPanel(GameWorld w)
    {
        int rows = Math.Max(1, (w.Player.Cargo.NonZero().Count() + 1) / 2);
        var troops = w.Player.Troops;
        var r = new Rectangle(_ui.ScreenWidth - 252, TopBarHeight + 36, 240, 64 + rows * 18 + troops.Count * 16);
        _ui.Panel(r, $"Hold {w.Player.Cargo.Total}/{w.Player.Cargo.Capacity}  [Tab]");
        CargoList(w, r.X + 8, r.Y + 32, r.Width - 16, r.Y + 32 + rows * 18);
        int ty = r.Y + 38 + rows * 18;
        _ui.Text($"Troops {w.Player.TroopsAboard}/{w.Player.TroopCapacity}", r.X + 8, ty, T.Gold, 1);
        foreach (var t in troops)
        {
            ty += 16;
            _ui.Text($"{t.Name}  {t.Members}/{t.Def.Members}", r.X + 16, ty, T.Text, 1);
        }
    }

    // ---- hover, hints, overlays ----------------------------------------------------------

    private void HoverInfo(GameWorld w, PlayerController pc)
    {
        if (pc.BuildDef != null || pc.ShowPauseMenu) return;
        if (_ui.Hover(new Rectangle(0, 0, _ui.ScreenWidth, TopBarHeight))) return;
        if (pc.HoverUnit is { } hu)
        {
            bool hostile = GameWorld.IsHostile(w.Player, hu);
            string side = hu.IsMilitia && hu.Faction == Core.Entities.Faction.Player ? "your town's militia"
                : hu.Faction == Core.Entities.Faction.Player ? "your squad - click to select"
                : hu.Faction == Core.Entities.Faction.Navy ? "Navy garrison - hostile"
                : (hu.Allegiance?.Name ?? "independent militia") + (hostile ? " - hostile" : " - not at war with you");
            string order = hu.Faction == Core.Entities.Faction.Player || !hostile
                ? ""
                : pc.SelectedUnit != null ? "\nClick to attack with your squad" : "\nClick to bombard from your ship";
            _ui.SetTooltip($"{hu.Name} ({side})\n{KindLabel(hu.Def)}, {hu.Members}/{hu.Def.Members} men{order}");
            return;
        }
        if (pc.HoverOwnShip && pc.SelectedUnit != null)
        {
            _ui.SetTooltip(w.CannotEmbark(pc.SelectedUnit) is { } why ? $"Can't board: {why}" : "Click to march back and board");
            return;
        }
        if (pc.HoverShip is { } s)
        {
            string stance = s.Faction == Faction.Navy ? "Navy - hostile" : s.Provoked ? "Merchant - hostile" : "Merchant - neutral";
            string state = s.Brain?.State ?? "";
            string attack = pc.SelectedUnit == null ? "Click to attack"
                : pc.SelectedUnit.Def.IsRanged ? "Click to fire on it with your musketeers" : "Melee squads can't attack ships";
            _ui.SetTooltip($"{s.Name}\n{s.Class.Name} ({stance})\nHull {(int)s.Hull}/{(int)s.MaxHull}  Guns {s.Class.Guns}  {state}\n{attack}");
            return;
        }
        if (pc.HoverBuilding is { } b)
        {
            string recipe = b.CurrentRecipe != null ? RecipeText(w, b.CurrentRecipe) : b.Def.Recipes.Count > 0 ? RecipeText(w, b.Def.Recipes[0]) : "Service building";
            string town = b.Settlement is { } st ? $"\n{st.Name} - {OwnerName(st)}" + (b.IsTownHall ? "\nClick for the settlement screen" : "") : "";
            _ui.SetTooltip($"{b.Def.Name} ({b.State}){town}\n{recipe}\nIn: {Inv(w, b.Input)}\nOut: {Inv(w, b.Output)}");
            return;
        }
        var t = pc.HoverTile;
        if (!t.IsValid) return;
        if (!w.Fog.IsExplored(t)) { _ui.SetTooltip("Uncharted"); return; }
        int i = w.Map.Index(t);
        var biome = w.Map.Biome[i];
        float tempC = -25f + w.Map.Temperature[i] * 60f;
        string info = $"{Biomes.Name(biome)}  ({t.X}, {t.Y})\nTemperature {tempC:0}C, moisture {w.Map.Moisture[i] * 100:0}%";
        if (pc.SelectedUnit != null)
            info += Biomes.IsWater(biome) ? "\nTroops can't cross water" : $"\nClick to march here (terrain cost {Biomes.LandCost(biome):0.0#}x)";
        else
            info += Biomes.IsWater(biome) ? "\nClick to set sail" : "\nClick to dock here";
        _ui.SetTooltip(info);
    }

    private static string Inv(GameWorld w, Inventory inv)
    {
        var items = inv.NonZero().Select(x => $"{w.Defs.Resources[x.resource].Name} {x.amount}").ToList();
        return items.Count == 0 ? "-" : string.Join(", ", items);
    }

    private void PlacementHint(PlayerController pc)
    {
        var def = pc.BuildDef!;
        string msg = pc.HoverPlacement == PlacementError.None
            ? $"Place {def.Name}: left-click (Shift: keep placing), right-click to cancel"
            : $"{def.Name}: {GameWorld.Describe(pc.HoverPlacement)}";
        int w = _ui.TextWidth(msg) + 20;
        var r = new Rectangle((_ui.ScreenWidth - w) / 2, _ui.ScreenHeight - 64, w, 26);
        _ui.Rect(r, T.Panel);
        _ui.Outline(r, pc.HoverPlacement == PlacementError.None ? T.Good : T.Bad);
        _ui.TextCentered(msg, r, pc.HoverPlacement == PlacementError.None ? T.Text : T.Bad);
    }

    private void DebugPanel(GameWorld w, PlayerController pc, Camera2D cam, WorldRenderer wr, float fps)
    {
        int top = TopBarHeight + 40 + (pc.SelectedUnit != null ? SquadPanelHeight + 8 : 0);
        var r = new Rectangle(12, top, 330, 208);
        _ui.Panel(r, "Debug [F3]");
        var lines = new[]
        {
            $"FPS {fps:0}   zoom {cam.PixelsPerTile:0.0} px/tile",
            $"Map {w.Map.Width}x{w.Map.Height} seed {w.Map.Seed} setup {w.Politics.SetupId ?? "-"}",
            $"Ships {w.Ships.Count}  squads ashore {w.Units.Count}  balls {w.Projectiles.Count}",
            $"Buildings {w.Buildings.Count}  explored {w.Fog.CountExplored() * 100.0 / w.Map.Count:0.0}%",
            $"Tiles drawn {wr.TilesDrawnLastFrame}  chunk rebuilds {wr.ChunksRebuiltTotal}",
            $"Last A* expansions {w.Pathfinder.LastExpansions}",
            $"Player {w.Player.Position.X:0.0},{w.Player.Position.Y:0.0}  danger {w.DangerAt(w.Player.Tile):0.00}",
            $"Sunk {w.ShipsSunkByPlayer}  gold earned {w.TotalGoldEarned}",
        };
        for (int i = 0; i < lines.Length; i++) _ui.Text(lines[i], r.X + 8, r.Y + 32 + i * 18, T.Text, 1);
    }

    private void Help()
    {
        var lines = new[]
        {
            ("Left-click water", "Set sail (click-to-move)"),
            ("Left-click land", "Sail to the nearest coast and dock"),
            ("Left-click a ship", "Attack: your ship manoeuvres for broadsides"),
            ("Left-click troops", "Bombard enemy troops ashore from the ship"),
            ("Right-click", "Cancel placement / drop anchor"),
            ("Wheel, +/-", "Zoom (zoom out to see the whole map)"),
            ("WASD / arrows", "Pan camera (Shift = faster), middle-drag"),
            ("F / Home", "Follow your ship"),
            ("Space, 1-4", "Pause, game speed x1 x2 x4 x8"),
            ("T / R", "Auto transfer goods / repair (when docked)"),
            ("Click your squad", "Select it; clicks then order the squad"),
            ("  ...land / enemy", "March / attack (musketeers can fire on ships)"),
            ("  ...your ship", "Board the docked ship (or press E)"),
            ("U / Q / X", "Land all troops / next squad / halt"),
            ("Tab", "Toggle hold panel"),
            ("K / L", "States & diplomacy / nearest settlement"),
            ("Click a town name", "Settlement screen: production, stores, trade, attack"),
            ("F5 / F9", "Quick save / quick load"),
            ("F3 / F4 / G", "Debug info / toggle fog / grid"),
            ("F12", "Screenshot"),
        };
        int w = Math.Min(_ui.ScreenWidth - 40, 790), h = 92 + lines.Length * 22;
        var r = new Rectangle((_ui.ScreenWidth - w) / 2, (_ui.ScreenHeight - h) / 2, w, h);
        _ui.Panel(r, "How to play  [H]");
        int y = r.Y + 36;
        _ui.Text(_ui.Font.Wrap("Dock at islands, build producers, ferry goods to complete production chains, sink Navy ships for loot. Build a Town Hall to found your own settlement and state; trade with, ally with or conquer the computer-run states and independent towns.", w - 24, 1), r.X + 12, y, T.TextDim, 1);
        y += 34;
        foreach (var (key, what) in lines)
        {
            _ui.Text(key, r.X + 16, y, T.Gold);
            _ui.Text(what, r.X + 240, y, T.Text);
            y += 22;
        }
    }

    private void PauseMenu(PlayerController pc, Action<string> shell)
    {
        _ui.Rect(new Rectangle(0, 0, _ui.ScreenWidth, _ui.ScreenHeight), Color.Black * 0.45f);
        _ui.Capture(new Rectangle(0, 0, _ui.ScreenWidth, _ui.ScreenHeight));
        int w = 300, h = 220;
        var r = new Rectangle((_ui.ScreenWidth - w) / 2, (_ui.ScreenHeight - h) / 2, w, h);
        _ui.Panel(r, "Paused");
        int y = r.Y + 40;
        if (_ui.Button(new Rectangle(r.X + 20, y, w - 40, 34), "Resume")) pc.ShowPauseMenu = false;
        y += 42;
        if (_ui.Button(new Rectangle(r.X + 20, y, w - 40, 34), "Quick save [F5]")) { shell("save"); pc.ShowPauseMenu = false; }
        y += 42;
        if (_ui.Button(new Rectangle(r.X + 20, y, w - 40, 34), "Quick load [F9]")) { shell("load"); pc.ShowPauseMenu = false; }
        y += 42;
        if (_ui.Button(new Rectangle(r.X + 20, y, w - 40, 34), "Quit to desktop", textColor: T.Bad)) shell("quit");
    }
}
