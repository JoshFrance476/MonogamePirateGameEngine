using Microsoft.Xna.Framework;
using PirateEngine.Core.Politics;
using PirateEngine.Core.Simulation;
using PirateEngine.Game.Input;
using PirateEngine.Game.Rendering;
using T = PirateEngine.Game.UI.UiContext.Theme;

namespace PirateEngine.Game.UI;

// Political UI: settlement banners in the world, the settlement screen and the states screen.
public sealed partial class Hud
{
    private bool _independentView;   // states screen showing the independent towns
    private int _stateScroll, _townScroll;

    private static string Stance(Stance s) => s switch { Core.Politics.Stance.War => "WAR", Core.Politics.Stance.Alliance => "Allied", _ => "Peace" };
    private static Color StanceColor(Stance s) => s switch { Core.Politics.Stance.War => T.Bad, Core.Politics.Stance.Alliance => T.Good, _ => T.TextDim };
    private static Color OpinionColor(float o) => o >= 30 ? T.Good : o <= -30 ? T.Bad : T.Warn;
    private static string OwnerName(Settlement s) => s.Owner?.Name ?? "Independent";

    /// <summary>Stance, or for pairs at peace their long-standing feeling (rivals / friends).</summary>
    private static (string, Color) RelationLabel(Relation r) => r.Stance switch
    {
        Core.Politics.Stance.Peace when r.IsRivalry => ("Rivals", T.Warn),
        Core.Politics.Stance.Peace when r.Baseline >= 35 => ("Friends", T.Good * 0.8f),
        _ => (Stance(r.Stance), StanceColor(r.Stance)),
    };

    private void Swatch(Rectangle r, Color c)
    {
        _ui.Rect(r, Color.Black * 0.8f);
        _ui.Rect(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), c);
    }

    /// <summary>How the player stands with a settlement, as a short label and colour.</summary>
    private static (string, Color) Standing(GameWorld w, Settlement s)
    {
        var p = w.Politics;
        if (s.Owner == p.PlayerState) return ("Yours", T.Gold);
        if (p.IsHostileTo(s, p.PlayerState)) return ("Hostile", T.Bad);
        if (s.Owner != null && p.Diplomacy.Allied(s.Owner, p.PlayerState)) return ("Allied", T.Good);
        return ("Neutral", T.TextDim);
    }

    // ---------------------------------------------------------------------------------
    // Banners: a clickable name plate over every known town, drawn beneath the panels.
    // ---------------------------------------------------------------------------------

    private void SettlementBanners(GameWorld w, PlayerController pc, Camera2D cam, int belowMark)
    {
        float ppt = cam.PixelsPerTile;
        if (ppt < 1.2f) return;
        var cfg = w.Config.Politics;
        _ui.BeginBelow(belowMark);
        foreach (var s in w.Politics.Settlements)
        {
            if (!w.Fog.IsExplored(s.Tile)) continue;
            var sp = cam.WorldToScreen(EntityRenderer.V(s.Tile.Center));
            if (sp.X < -150 || sp.X > _ui.ScreenWidth + 150 || sp.Y < -200 || sp.Y > _ui.ScreenHeight + 200) continue;
            bool big = ppt >= 6;
            // The name plate sits on top of the territory ring, clear of the buildings.
            float ring = s.Radius(cfg) * ppt;
            int y = (int)(sp.Y - Math.Max(ring, 16f)) - (big ? 11 : 7);
            if (y < TopBarHeight + 32 && sp.Y > TopBarHeight + 60) y = TopBarHeight + 32;
            if (y < TopBarHeight || y > _ui.ScreenHeight - 20) continue;

            string name = s.Name + (big ? $"  {s.Population}" : "");
            int scale = big ? 2 : 1;
            int w0 = _ui.TextWidth(name, scale) + 20;
            int h = big ? 22 : 14;
            var r = new Rectangle((int)sp.X - w0 / 2, y, w0, h);
            var oc = EntityRenderer.OwnerColor(s.Owner);
            bool hover = _ui.Hover(r) && !_ui.MouseCaptured;
            _ui.Rect(r, (hover ? T.PanelLight : T.Panel) * 0.9f);
            _ui.Rect(new Rectangle(r.X, r.Y, 5, r.Height), oc);
            _ui.Outline(r, s.Siege != null ? T.Bad : hover ? T.Gold : oc * 0.6f);
            _ui.Text(name, r.X + 12, r.Y + (big ? 4 : 3), s.Owner == w.Politics.PlayerState ? T.Gold : T.Text, scale);
            if (big)
            {
                string sub = $"{s.Level(cfg).Name} - {OwnerName(s)}";
                var (standing, sc) = Standing(w, s);
                if (standing != "Neutral") sub += $" ({standing})";
                int sw = _ui.TextWidth(sub, 1) + 8;
                var sr = new Rectangle((int)sp.X - sw / 2, r.Bottom, sw, 12);
                _ui.Rect(sr, Color.Black * 0.55f);
                _ui.Text(sub, sr.X + 4, sr.Y + 2, standing == "Neutral" ? T.TextDim : sc, 1);
            }
            if (hover)
            {
                _ui.SetTooltip($"{s.Name} ({s.Level(cfg).Name})\n{OwnerName(s)}\nPopulation {s.Population}, garrison {(int)s.Garrison}" +
                               (s.Siege != null ? $"\nUnder siege by the {s.Siege.Attacker.Name}!" : "") + "\n\nClick for the settlement screen");
                if (_ui.ConsumeClick(r)) pc.OpenSettlementScreen(s);
                _ui.Capture(r);
            }
        }
        _ui.EndBelow();
    }

    // ---------------------------------------------------------------------------------
    // Settlement screen
    // ---------------------------------------------------------------------------------

    private Rectangle ScreenRect(int maxW, int maxH)
    {
        int w = Math.Min(maxW, _ui.ScreenWidth - 24);
        int h = Math.Min(maxH, _ui.ScreenHeight - TopBarHeight - 48);
        return new Rectangle((_ui.ScreenWidth - w) / 2, TopBarHeight + 40, w, h);
    }

    private void SettlementScreen(GameWorld w, PlayerController pc, Camera2D cam)
    {
        var s = pc.OpenSettlement!;
        var p = w.Politics;
        var cfg = w.Config.Politics;
        var level = s.Level(cfg);
        var r = ScreenRect(880, 620);
        _ui.Panel(r, $"{s.Name}  -  {level.Name}");
        if (_ui.Button(new Rectangle(r.Right - 92, r.Y + 3, 88, 20), "Close [Esc]", scale: 1)) pc.OpenSettlement = null;

        int x = r.X + 12, y = r.Y + 34;
        int colW = (r.Width - 36) / 2;
        int rx = x + colW + 12;

        // ---- owner line --------------------------------------------------------------
        Swatch(new Rectangle(x, y, 18, 18), EntityRenderer.OwnerColor(s.Owner));
        string owner = s.Owner == null ? "Independent town (self-governing)"
            : s.Owner == p.PlayerState ? $"Yours - {s.Owner.Name}"
            : $"{s.Owner.Name}  [{s.Owner.Behaviour.Name}]";
        _ui.Text(Fit(owner, colW * 2 - 160), x + 26, y + 2, s.Owner == p.PlayerState ? T.Gold : T.Text);
        if (s.Owner != null && _ui.Button(new Rectangle(r.Right - 140, y - 2, 128, 22), "View state", scale: 1))
            pc.OpenStateScreen(s.Owner);
        y += 28;

        var (standing, sc) = Standing(w, s);
        string rel = s.Owner == p.PlayerState ? "Your town: its workshops share the stores; its taxes are yours" : standing;
        if (s.Owner != null && !s.Owner.IsPlayer) rel += $"  -  opinion of you {p.Diplomacy.Opinion(p.PlayerState, s.Owner):0}";
        if (s.Owner == null && s.AttackedBy.Count > 0) rel += $"  -  fighting {string.Join(", ", s.AttackedBy.Select(a => a.Name))}";
        _ui.Text(Fit(rel, colW * 2), x, y, sc, 1);
        y += 18;
        _ui.Rect(new Rectangle(x, y, r.Width - 24, 1), T.Border * 0.4f);
        y += 8;

        // ---- left column: the town ---------------------------------------------------------
        int ly = y;
        void Stat(string label, string value, Color c, string? tip = null)
        {
            _ui.Text(label, x, ly, T.TextDim, 1);
            _ui.Text(value, x + 112, ly, c, 1);
            if (tip != null && _ui.Hover(new Rectangle(x, ly - 2, colW, 14))) _ui.SetTooltip(tip);
            ly += 16;
        }

        _ui.Text("Population", x, ly + 3, T.TextDim, 1);
        _ui.Bar(new Rectangle(x + 112, ly, colW - 112, 14), s.Population / (float)Math.Max(1, s.PopulationCap), T.Good, $"{s.Population} / {s.PopulationCap}");
        ly += 20;
        int need = p.FoodNeed(s);
        float forage = p.Forage(s);
        Stat("Food", $"{s.FedRatio * 100:0}% fed  (eats {need * p.TicksPerMinute:0}/min, forages {forage * p.TicksPerMinute:0.#}/min)",
            s.FedRatio >= 0.99f ? T.Good : s.FedRatio > 0.6f ? T.Warn : T.Bad,
            "Towns eat food from their stores. Fed towns grow toward their cap (more buildings, higher level = bigger cap); hungry towns shrink.");
        Stat("Garrison", $"{(int)s.Garrison} soldiers" + (s.MusteredMilitia.Count > 0 ? $"  +{s.MusteredMilitia.Sum(m => m.Members)} mustered" : ""),
            T.Text, "Defenders. They muster as militia squads when hostile troops come near, and fight sieges far away.");
        Stat("Defences", $"x{level.Fortification:0.##} fortification" + (s.Owner?.Capital == s ? ", capital x1.5" : ""), T.Text);
        float treasury = s.Owner == null ? s.Treasury : p.TreasuryOf(s.Owner);
        Stat(s.Owner == null ? "Treasury" : "State treasury", $"{treasury:N0} gold", T.Gold);
        Stat("Taxes", $"{s.LastIncome * p.TicksPerMinute:0} gold/min" + (s.Owner == p.PlayerState ? " to you" : ""), T.Gold * 0.9f);
        Stat("Buildings", (s.Owner == p.PlayerState ? $"{s.Buildings.Count}" : $"{s.Buildings.Count} (builds up to {level.MaxBuildings})") +
                          $"   territory radius {level.Radius:0}", T.Text);
        Stat("Age", s.FoundedAt <= 0 ? "an old town" : $"founded {(w.Time - s.FoundedAt) / 60:0} min ago", T.TextDim);

        if (s.Siege is { } siege)
        {
            ly += 4;
            _ui.Text($"Besieged by the {siege.Attacker.Name}", x, ly, T.Bad, 1);
            ly += 14;
            _ui.Bar(new Rectangle(x, ly, colW, 12), siege.Progress / 100f, T.Bad, $"{siege.SoldiersTotal} attackers - breach {siege.Progress:0}%");
            ly += 18;
        }
        if (s.Occupier != null && s.OccupationProgress > 0)
        {
            _ui.Text($"Town hall held by {s.Occupier.Name}", x, ly, T.Warn, 1);
            ly += 14;
            _ui.Bar(new Rectangle(x, ly, colW, 12), s.OccupationProgress / cfg.CaptureSeconds, T.Warn);
            ly += 18;
        }

        // Production.
        ly += 6;
        _ui.Text("Produces per minute", x, ly, T.Gold);
        var capacity = p.ProductionPerMinute(s);
        if (_ui.Hover(new Rectangle(x, ly, colW, 18)))
            _ui.SetTooltip("What the workshops delivered to the stores over the last economy tick (every " +
                           $"{w.Config.Politics.EconomyInterval:0}s), per minute.\n\nFull capacity if every workshop had its inputs:\n" +
                           (capacity.Count == 0 ? "-" : string.Join(", ", capacity.Select(c => $"{(c.resource < 0 ? "Gold" : w.Defs.Resources[c.resource].Name)} {c.perMinute:0.#}"))));
        ly += 20;
        var prod = Enumerable.Range(0, w.Defs.ResourceCount).Where(res => s.LastProduced[res] > 0)
            .Select(res => (res, s.LastProduced[res] * p.TicksPerMinute)).ToList();
        if (s.LastBuildingGold > 0) prod.Add((-1, s.LastBuildingGold * p.TicksPerMinute));
        if (prod.Count == 0) { _ui.Text(s.Buildings.Count == 0 ? "Nothing yet - the town lives by foraging." : "Nothing this tick - workshops lack inputs.", x, ly, T.TextDim, 1); ly += 16; }
        else ly = RateChips(w, prod.OrderByDescending(x => x.Item2).ToList(), x, ly, colW);

        ly += 6;
        _ui.Text("Buildings", x, ly, T.Gold);
        ly += 20;
        int half = colW / 2;
        int i = 0;
        foreach (var b in s.Buildings.OrderBy(b => b.Def.Name))
        {
            int bx = x + (i % 2) * half, by = ly + (i / 2) * 18;
            if (by + 16 > r.Bottom - 8) { _ui.Text($"... and {s.Buildings.Count - i} more", bx, by, T.TextDim, 1); break; }
            var col = b.State switch
            {
                Core.Economy.ProductionState.Producing => T.Good,
                Core.Economy.ProductionState.WaitingForInputs => T.Warn,
                Core.Economy.ProductionState.OutputFull => T.Bad,
                _ => T.TextDim,
            };
            _ui.Image(_art.Building(b.Def), new Rectangle(bx, by, 14, 14), Color.White);
            _ui.Rect(new Rectangle(bx + 16, by + 5, 4, 4), col);
            _ui.Text(Fit(b.Def.Name, half - 28, 1), bx + 24, by + 4, T.Text, 1);
            i++;
        }

        // ---- right column: stores, market, actions -----------------------------------------
        int ry = y;
        _ui.Text($"Stores  {s.Stores.Total}/{s.Stores.Capacity}", rx, ry, T.Gold);
        ry += 20;
        var stock = s.Stores.NonZero().ToList();
        if (stock.Count == 0) { _ui.Text("(empty)", rx, ry, T.TextDim, 1); ry += 16; }
        else ry = RateChips(w, stock.Select(sx => (sx.resource, (float)sx.amount)).ToList(), rx, ry, colW, integer: true);
        ry += 8;

        if (s.Owner == p.PlayerState) ry = OwnStores(w, s, rx, ry, colW);
        else ry = Market(w, s, rx, ry, colW, r.Bottom - 70);

        // Actions along the bottom of the right column.
        int by0 = r.Bottom - 34;
        if (_ui.Button(new Rectangle(rx, by0, 70, 26), "Look", tooltip: "Centre the camera on this town"))
        {
            cam.Follow = null;
            cam.Position = EntityRenderer.V(s.Tile.Center);
        }
        var port = p.DockingSpot(s);
        bool here = p.InReach(s);
        if (_ui.Button(new Rectangle(rx + 74, by0, 92, 26), here ? "Docked" : "Sail here", port.IsValid && !here,
                port.IsValid ? "Sail to the town's harbour and dock within reach of its town hall" : "No harbour on the open sea"))
        {
            w.CommandDock(port);
            pc.OpenSettlement = null;
        }
        if (s.Owner != p.PlayerState)
        {
            string? why = p.PlayerCannotAttack(s);
            string what = s.Owner == null
                ? "The town will treat you as an enemy and muster its militia."
                : p.Diplomacy.AtWar(p.PlayerState, s.Owner) ? "You are already at war with its owner." : $"This declares war on the {s.Owner.Name}.";
            if (_ui.Button(new Rectangle(rx + 170, by0, colW - 170, 26), "Attack this town", why == null,
                    $"{what}\nTo capture it, land troops, defeat its militia and hold the town hall for {cfg.CaptureSeconds:0}s with no defenders left." +
                    (why != null ? $"\n\n{why}." : ""), textColor: T.Bad))
                p.PlayerAttackSettlement(s);
        }
    }

    /// <summary>Resource chips with an amount or rate; returns the next y.</summary>
    private int RateChips(GameWorld w, List<(int resource, float value)> items, int x0, int y, int width, bool integer = false)
    {
        int x = x0;
        foreach (var (res, v) in items)
        {
            string name = res < 0 ? "Gold" : w.Defs.Resources[res].Name;
            string text = integer ? $"{name} {v:0}" : $"{name} {v:0.#}";
            int cw = _ui.TextWidth(text, 1) + 20;
            if (x + cw > x0 + width && x > x0) { x = x0; y += 20; }
            var chip = new Rectangle(x, y, cw, 17);
            _ui.Rect(chip, T.PanelLight);
            _ui.Rect(new Rectangle(chip.X + 3, chip.Y + 4, 8, 9), res < 0 ? T.Gold : Draw.ParseHex(w.Defs.Resources[res].Color, Color.White));
            _ui.Text(text, chip.X + 14, chip.Y + 5, T.Text, 1);
            x += cw + 4;
        }
        return y + 22;
    }

    private int OwnStores(GameWorld w, Settlement s, int x, int y, int width)
    {
        var p = w.Politics;
        _ui.Text("Your town's stores", x, y, T.Gold);
        y += 20;
        if (!p.InReach(s))
        {
            _ui.Text(_ui.Font.Wrap("Dock within reach of the town hall to load from or add to the stores. " +
                                   "Your workshops here feed each other and the stores automatically.", width, 1), x, y, T.TextDim, 1);
            return y + 40;
        }
        _ui.Text("Take into the hold", x, y, T.TextDim, 1);
        y = Chips(w, s.Stores.NonZero().Select(sx => sx.resource).ToList(), x, y + 14, width,
            r => $"{w.Defs.Resources[r].Name} {s.Stores[r]}", r => w.Player.Cargo.Free > 0,
            r => p.LoadFromStores(s, r, _shiftHeld ? s.Stores[r] : 10), "Load 10 (Shift: all)");
        _ui.Text("Store from the hold", x, y, T.TextDim, 1);
        y = Chips(w, w.Player.Cargo.NonZero().Select(c => c.resource).ToList(), x, y + 14, width,
            r => $"{w.Defs.Resources[r].Name} {w.Player.Cargo[r]}", r => s.Stores.Free > 0,
            r => p.UnloadToStores(s, r, _shiftHeld ? w.Player.Cargo[r] : 10), "Store 10 (Shift: all). Food feeds the town; other goods supply its workshops.");
        return y;
    }

    private int Market(GameWorld w, Settlement s, int x, int y, int width, int bottom)
    {
        var p = w.Politics;
        _ui.Text("Market", x, y, T.Gold);
        string? why = p.CannotTrade(s);
        if (why != null)
        {
            _ui.Text(why, x + 80, y + 4, T.TextDim, 1);
            return y + 22;
        }
        string purse = $"merchants have {p.MerchantFunds(s):N0}g";
        _ui.Text(purse, x + width - _ui.TextWidth(purse, 1) - 2, y + 4, T.TextDim, 1);
        y += 20;
        _ui.Text("Goods", x, y, T.TextDim, 1);
        _ui.Text("Stock", x + 120, y, T.TextDim, 1);
        _ui.Text("Buy", x + 168, y, T.TextDim, 1);
        _ui.Text("Sell", x + width - 128, y, T.TextDim, 1);
        y += 14;
        var rows = Enumerable.Range(0, w.Defs.ResourceCount)
            .Where(res => s.Stores[res] > 0 || w.Player.Cargo[res] > 0)
            .OrderByDescending(res => w.Player.Cargo[res]).ThenByDescending(res => s.Stores[res]).ToList();
        foreach (var res in rows)
        {
            if (y + 20 > bottom) { _ui.Text("...", x, y, T.TextDim, 1); break; }
            var def = w.Defs.Resources[res];
            _ui.Rect(new Rectangle(x, y + 5, 8, 9), Draw.ParseHex(def.Color, Color.White));
            _ui.Text(Fit(def.Name, 100, 1), x + 12, y + 6, T.Text, 1);
            _ui.Text(s.Stores[res].ToString(), x + 120, y + 6, T.Text, 1);
            int buy = p.BuyPrice(s, res), sell = p.SellPrice(s, res);
            bool canBuy = s.Stores[res] > 0 && w.Gold >= buy && w.Player.Cargo.Free > 0;
            if (_ui.Button(new Rectangle(x + 166, y + 1, 70, 18), $"{buy}g", canBuy,
                    $"Buy {def.Name} for {buy}g each (click: 1, Shift: 10). Prices rise as their stock runs low.", scale: 1))
                p.BuyFrom(s, res, _shiftHeld ? 10 : 1);
            int have = w.Player.Cargo[res];
            bool canSell = have > 0 && s.Stores.Free > 0 && p.MerchantFunds(s) >= sell;
            if (_ui.Button(new Rectangle(x + width - 130, y + 1, 128, 18), have > 0 ? $"{sell}g  (have {have})" : $"{sell}g", canSell,
                    $"Sell {def.Name} for {sell}g each (click: 1, Shift: all). Scarce goods fetch more; mercantile states pay best.", scale: 1))
                p.SellTo(s, res, _shiftHeld ? have : 1);
            y += 20;
        }
        return y + 4;
    }

    // ---------------------------------------------------------------------------------
    // States screen
    // ---------------------------------------------------------------------------------

    private void StatesScreen(GameWorld w, PlayerController pc, InputState input)
    {
        var p = w.Politics;
        var r = ScreenRect(980, 640);
        _ui.Panel(r, "States and diplomacy  [K]");
        if (_ui.Button(new Rectangle(r.Right - 92, r.Y + 3, 88, 20), "Close [Esc]", scale: 1)) pc.ShowStates = false;

        // ---- left: list of realms ----------------------------------------------------------
        var list = new Rectangle(r.X + 8, r.Y + 32, 270, r.Height - 40);
        _ui.Rect(list, Color.Black * 0.25f);
        var entries = new List<State?> { p.PlayerState };
        entries.AddRange(p.States.Where(s => !s.IsPlayer && s.IsActive).OrderByDescending(s => s.Settlements.Count));
        entries.Add(null); // independents
        entries.AddRange(p.States.Where(s => !s.IsPlayer && s.Collapsed));
        if (pc.ViewState == null && !_independentView) pc.ViewState = p.PlayerState;
        if (pc.ViewState is { Collapsed: true } && !p.States.Contains(pc.ViewState)) pc.ViewState = p.PlayerState;

        int y = list.Y + 4 - _stateScroll;
        if (_ui.Hover(list) && input.WheelDelta != 0)
        {
            _stateScroll = Math.Max(0, _stateScroll - Math.Sign(input.WheelDelta) * 36);
            _ui.WheelConsumed = true;
            input.MouseCapturedByUi = true;
        }
        foreach (var st in entries)
        {
            var row = new Rectangle(list.X + 4, y, list.Width - 8, 38);
            y += 42;
            if (row.Y < list.Y || row.Bottom > list.Bottom) continue;
            bool selected = st == null ? _independentView : !_independentView && pc.ViewState == st;
            string title, sub;
            Color col;
            if (st == null)
            {
                int n = p.Settlements.Count(s => s.IsIndependent);
                title = "Independent towns";
                sub = $"{n} self-governing towns";
                col = EntityRenderer.IndependentColor;
            }
            else
            {
                title = st.IsPlayer && !st.Founded ? "You (no state yet)" : st.Name;
                sub = st.Collapsed ? "Fallen" : $"{st.Behaviour.Name} - {st.Settlements.Count} towns";
                if (!st.IsPlayer && st.IsActive)
                {
                    var stance = p.Diplomacy.StanceOf(p.PlayerState, st);
                    if (stance != Core.Politics.Stance.Peace) sub += $" - {Stance(stance)}";
                }
                col = EntityRenderer.OwnerColor(st);
            }
            if (_ui.Button(row, "", tooltip: null))
            {
                _independentView = st == null;
                if (st != null) pc.ViewState = st;
                _townScroll = 0;
            }
            if (selected) _ui.Outline(row, T.Gold, 2);
            Swatch(new Rectangle(row.X + 6, row.Y + 9, 18, 18), st?.Collapsed == true ? col * 0.4f : col);
            _ui.Text(Fit(title, row.Width - 40), row.X + 32, row.Y + 5, st?.Collapsed == true ? T.TextDim : st?.IsPlayer == true ? T.Gold : T.Text);
            _ui.Text(Fit(sub, row.Width - 40, 1), row.X + 32, row.Y + 24, st != null && sub.Contains("WAR") ? T.Bad : T.TextDim, 1);
        }
        int content = entries.Count * 42 + 8;
        _stateScroll = Math.Min(_stateScroll, Math.Max(0, content - list.Height));

        // ---- right: details ----------------------------------------------------------------
        var body = new Rectangle(list.Right + 12, r.Y + 32, r.Right - list.Right - 22, r.Height - 40);
        int by = body.Y;
        by = Offers(w, body, by);
        if (_independentView) IndependentDetails(w, pc, body, by, input);
        else StateDetails(w, pc, pc.ViewState ?? p.PlayerState, body, by, input);
    }

    private int Offers(GameWorld w, Rectangle body, int y)
    {
        var p = w.Politics;
        foreach (var o in p.PendingOffers.ToList())
        {
            var row = new Rectangle(body.X, y, body.Width, 30);
            _ui.Rect(row, T.PanelLight);
            _ui.Outline(row, o.Kind == OfferKind.Peace ? T.Good : T.Info);
            Swatch(new Rectangle(row.X + 6, row.Y + 7, 16, 16), EntityRenderer.OwnerColor(o.From));
            string text = o.Kind == OfferKind.Peace ? $"The {o.From.Name} offers you peace" : $"The {o.From.Name} proposes an alliance";
            _ui.Text(Fit(text, row.Width - 226, 1), row.X + 30, row.Y + 6, T.Text, 1);
            _ui.Text($"answer within {Math.Max(0, o.Expires - w.Time):0}s", row.X + 30, row.Y + 18, T.TextDim, 1);
            if (_ui.Button(new Rectangle(row.Right - 184, row.Y + 4, 88, 22), "Accept", textColor: T.Good, scale: 1)) p.AnswerOffer(o, true);
            if (_ui.Button(new Rectangle(row.Right - 92, row.Y + 4, 88, 22), "Decline", textColor: T.Bad, scale: 1)) p.AnswerOffer(o, false);
            y += 36;
        }
        return y;
    }

    private void StateDetails(GameWorld w, PlayerController pc, State st, Rectangle body, int y, InputState input)
    {
        var p = w.Politics;
        int x = body.X;
        var col = EntityRenderer.OwnerColor(st);
        Swatch(new Rectangle(x, y, 22, 22), col);
        _ui.Text(st.IsPlayer && !st.Founded ? "Your company" : st.Name, x + 30, y + 4, st.IsPlayer ? T.Gold : T.Text);
        y += 28;
        if (st.IsPlayer && !st.Founded)
        {
            _ui.Text(_ui.Font.Wrap("You have no state yet. Build a Town Hall on unclaimed land (Build tab when docked) to found a settlement " +
                                   "and declare your own state, or capture a town by force: land troops, beat its militia and hold its town hall.",
                body.Width, 1), x, y, T.TextDim, 1);
            y += 44;
        }
        else
        {
            _ui.Text(_ui.Font.Wrap($"{st.Behaviour.Name}: {st.Behaviour.Description}", body.Width, 1), x, y, T.TextDim, 1);
            y += 30;
        }

        // Key figures.
        int colW = body.Width / 3;
        void Fig(int i, string label, string value, Color c)
        {
            int fx = x + (i % 3) * colW, fy = y + (i / 3) * 32;
            _ui.Text(label, fx, fy, T.TextDim, 1);
            _ui.Text(value, fx, fy + 12, c);
        }
        Fig(0, "Treasury", $"{p.TreasuryOf(st):N0}", T.Gold);
        Fig(1, "Field army", st.IsPlayer ? $"{w.PlayerSquads.Sum(u => u.Members)} ashore" : $"{st.FieldArmy:0}", T.Text);
        Fig(2, "Garrisons", $"{st.GarrisonTotal:0}", T.Text);
        Fig(3, "Population", $"{st.Population}", T.Text);
        Fig(4, "Settlements", $"{st.Settlements.Count}", T.Text);
        Fig(5, "Capital", st.Capital?.Name ?? "-", T.Text);
        y += 70;

        // Relationship with the player and actions.
        if (!st.IsPlayer && st.IsActive)
        {
            var rel = p.Diplomacy.Get(p.PlayerState, st);
            _ui.Rect(new Rectangle(x, y, body.Width, 1), T.Border * 0.4f);
            y += 6;
            _ui.Text("Relations with you:", x, y, T.Gold);
            int sx = x + _ui.TextWidth("Relations with you:") + 12;
            _ui.Text(Stance(rel.Stance), sx, y, StanceColor(rel.Stance));
            sx += _ui.TextWidth(Stance(rel.Stance)) + 12;
            if (rel.TruceUntil > w.Time) _ui.Text($"no war for {(rel.TruceUntil - w.Time) / 60:0.#} min (truce)", sx, y + 4, T.Info, 1);
            y += 20;
            _ui.Text("Opinion", x, y + 2, T.TextDim, 1);
            var bar = new Rectangle(x + 70, y, 200, 12);
            _ui.Rect(bar, Color.Black * 0.6f);
            int mid = bar.X + bar.Width / 2;
            int len = (int)(rel.Opinion / 100f * (bar.Width / 2));
            _ui.Rect(len >= 0 ? new Rectangle(mid, bar.Y + 1, len, 10) : new Rectangle(mid + len, bar.Y + 1, -len, 10), OpinionColor(rel.Opinion));
            _ui.Rect(new Rectangle(mid, bar.Y - 2, 1, 16), T.Text * 0.5f);
            // Where their opinion of you settles when nothing disturbs it.
            int natural = mid + (int)(Math.Clamp(rel.Baseline, -100, 100) / 100f * (bar.Width / 2));
            _ui.Rect(new Rectangle(natural - 1, bar.Y - 3, 3, 18), T.Info);
            if (_ui.Hover(new Rectangle(x, y - 2, bar.Right - x + 40, 18)))
                _ui.SetTooltip($"Opinion of you: {rel.Opinion:0}\nNatural level: {rel.Baseline:0} (blue mark)\n\n" +
                               "Gifts, trade, shared enemies and your conquests move it; over time it drifts back to its natural level.");
            _ui.Text($"{rel.Opinion:0}", bar.Right + 8, y + 2, OpinionColor(rel.Opinion), 1);
            if (rel.Stance == Core.Politics.Stance.War) _ui.Text($"war-weariness {rel.WarWeariness:0}", bar.Right + 50, y + 2, T.TextDim, 1);
            y += 20;

            int bw = (body.Width - 16) / 5;
            void Act(int i, string label, string action, string tip, Func<bool> run, Color? c = null)
            {
                string? why = p.PlayerCannot(st, action);
                if (_ui.Button(new Rectangle(x + i * (bw + 4), y, bw, 26), label, why == null, tip + (why != null ? $"\n\n{why}." : ""), c, scale: 1))
                    run();
            }
            Act(0, "Declare war", "war", "Their towns, troops and ships become hostile. Their allies may join them.", () => p.PlayerDeclareWar(st), T.Bad);
            Act(1, "Propose peace", "peace", "Accepted if they are war-weary, losing, or peaceable by nature.", () => p.PlayerProposePeace(st), T.Good);
            Act(2, "Alliance", "alliance", "Needs their opinion of you at 40+; warlike rulers refuse. Allies may be dragged into each other's wars.", () => p.PlayerProposeAlliance(st));
            Act(3, "Break alliance", "break", "Ends the alliance (opinion -15).", () => p.PlayerBreakAlliance(st));
            Act(4, $"Gift {w.Config.Politics.GiftGold}g", "gift", "Improves their opinion of you; mercantile states value gold most.", () => p.PlayerGift(st), T.Gold);
            y += 34;
        }

        // Settlements and diplomacy side by side.
        _ui.Rect(new Rectangle(x, y, body.Width, 1), T.Border * 0.4f);
        y += 6;
        int half = (body.Width - 12) / 2;
        int listTop = y + 20;
        _ui.Text("Settlements", x, y, T.Gold);
        _ui.Text("Diplomacy", x + half + 12, y, T.Gold);

        var towns = st.Settlements.OrderByDescending(s => s == st.Capital).ThenByDescending(s => s.Population).ToList();
        var area = new Rectangle(x, listTop, half, body.Bottom - listTop);
        if (_ui.Hover(area) && input.WheelDelta != 0)
        {
            _townScroll = Math.Max(0, _townScroll - Math.Sign(input.WheelDelta) * 30);
            _ui.WheelConsumed = true;
            input.MouseCapturedByUi = true;
        }
        TownList(w, pc, towns, area, st.Capital);

        // Every other active realm and how this one stands with it.
        int dy = listTop;
        var others = p.States.Where(o => o != st && o.IsActive).OrderBy(o => p.Diplomacy.StanceOf(st, o) == Core.Politics.Stance.Peace).ThenBy(o => o.Name);
        foreach (var o in others)
        {
            if (dy + 18 > body.Bottom) break;
            var rel = p.Diplomacy.Get(st, o);
            int ox = x + half + 12;
            Swatch(new Rectangle(ox, dy + 2, 12, 12), EntityRenderer.OwnerColor(o));
            _ui.Text(Fit(o.IsPlayer ? "You" : o.Name, half - 130, 1), ox + 18, dy + 4, o.IsPlayer ? T.Gold : T.Text, 1);
            var (label, labelColor) = RelationLabel(rel);
            _ui.Text(label, ox + half - 106, dy + 4, labelColor, 1);
            _ui.Text($"{rel.Opinion:+0;-0;0}", ox + half - 40, dy + 4, OpinionColor(rel.Opinion), 1);
            var hit = new Rectangle(ox, dy, half - 12, 16);
            if (_ui.Hover(hit))
                _ui.SetTooltip($"{st.Name} - {o.Name}\n{Stance(rel.Stance)}, opinion {rel.Opinion:0} (natural level {rel.Baseline:0})" +
                               (rel.IsRivalry ? "\nOld rivals: quick to take offence" : rel.Baseline >= 35 ? "\nOld friends" : "") +
                               (rel.Stance == Core.Politics.Stance.War ? $"\nAt war for {(w.Time - rel.Since) / 60:0} min" : ""));
            dy += 18;
        }
        int hostileIndependents = p.Settlements.Count(s => s.IsIndependent && s.AttackedBy.Contains(st));
        if (hostileIndependents > 0 && dy + 18 <= body.Bottom)
            _ui.Text($"Fighting {hostileIndependents} independent town{(hostileIndependents == 1 ? "" : "s")}", x + half + 12, dy + 4, T.Warn, 1);
    }

    private void IndependentDetails(GameWorld w, PlayerController pc, Rectangle body, int y, InputState input)
    {
        int x = body.X;
        Swatch(new Rectangle(x, y, 22, 22), EntityRenderer.IndependentColor);
        _ui.Text("Independent towns", x + 30, y + 4, T.Text);
        y += 28;
        _ui.Text(_ui.Font.Wrap("Self-governing towns owing allegiance to no state. They trade with anyone, grow on their own, " +
                               "and defend themselves against whoever attacks them. States - and you - can conquer them.", body.Width, 1),
            x, y, T.TextDim, 1);
        y += 34;
        var area = new Rectangle(x, y, body.Width, body.Bottom - y);
        if (_ui.Hover(area) && input.WheelDelta != 0)
        {
            _townScroll = Math.Max(0, _townScroll - Math.Sign(input.WheelDelta) * 30);
            _ui.WheelConsumed = true;
            input.MouseCapturedByUi = true;
        }
        TownList(w, pc, w.Politics.Settlements.Where(s => s.IsIndependent).OrderByDescending(s => s.Population).ToList(), area, null);
    }

    private void TownList(GameWorld w, PlayerController pc, List<Settlement> towns, Rectangle area, Settlement? capital)
    {
        var cfg = w.Config.Politics;
        if (towns.Count == 0) { _ui.Text("None.", area.X, area.Y + 2, T.TextDim, 1); return; }
        const int rowH = 40;
        int content = towns.Count * rowH;
        _townScroll = Math.Min(_townScroll, Math.Max(0, content - area.Height));
        int y = area.Y - _townScroll;
        foreach (var s in towns)
        {
            var row = new Rectangle(area.X, y, area.Width, rowH - 4);
            y += rowH;
            if (row.Y < area.Y || row.Bottom > area.Bottom) continue;
            bool known = w.Fog.IsExplored(s.Tile);
            if (_ui.Button(row, "", tooltip: known ? "Open the settlement screen" : "Uncharted - you've only heard of it"))
                pc.OpenSettlementScreen(s);
            string name = (s == capital ? "* " : "") + s.Name;
            _ui.Text(Fit(name, row.Width - 80), row.X + 6, row.Y + 4, s.Siege != null ? T.Bad : T.Text);
            _ui.Text($"{s.Level(cfg).Name}, pop {s.Population}, garrison {(int)s.Garrison}" + (s.Siege != null ? " - BESIEGED" : ""),
                row.X + 6, row.Y + 23, s.Siege != null ? T.Bad : T.TextDim, 1);
            var (standing, sc) = Standing(w, s);
            _ui.Text(standing, row.Right - _ui.TextWidth(standing, 1) - 8, row.Y + 7, sc, 1);
        }
        if (content > area.Height)
        {
            var track = new Rectangle(area.Right - 3, area.Y, 3, area.Height);
            _ui.Rect(track, Color.Black * 0.5f);
            int thumbH = Math.Max(20, area.Height * area.Height / content);
            int thumbY = area.Y + (area.Height - thumbH) * _townScroll / Math.Max(1, content - area.Height);
            _ui.Rect(new Rectangle(track.X, thumbY, 3, thumbH), T.Border);
        }
    }

    // ---------------------------------------------------------------------------------
    // Dock panel: towns in reach (Goods tab)
    // ---------------------------------------------------------------------------------

    /// <summary>Cards for towns whose town hall is within reach of the dock; returns the height used.</summary>
    private int TownsInReachCards(GameWorld w, PlayerController pc, int x, int y, int width, int clipTop, int clipBottom)
    {
        int y0 = y;
        foreach (var s in w.Politics.SettlementsInReach())
        {
            int h = 46;
            if (y >= clipTop && y + h <= clipBottom)
            {
                var card = new Rectangle(x, y, width, h);
                _ui.Rect(card, T.PanelLight);
                _ui.Outline(card, EntityRenderer.OwnerColor(s.Owner) * 0.8f);
                _ui.Rect(new Rectangle(x, y, 5, h), EntityRenderer.OwnerColor(s.Owner));
                _ui.Text(Fit(s.Name, width - 120), x + 12, y + 6, T.Text);
                bool own = s.Owner == w.Politics.PlayerState;
                string sub = own ? $"Your town - stores {s.Stores.Total}/{s.Stores.Capacity}" : $"{OwnerName(s)} - " + (w.Politics.CannotTrade(s) ?? "open for trade");
                _ui.Text(Fit(sub, width - 120, 1), x + 12, y + 27, T.TextDim, 1);
                if (_ui.Button(new Rectangle(card.Right - 100, y + 10, 92, 26), own ? "Stores" : "Market", tooltip: "Open the settlement screen"))
                    pc.OpenSettlementScreen(s);
            }
            y += h + 6;
        }
        return y - y0;
    }
}
