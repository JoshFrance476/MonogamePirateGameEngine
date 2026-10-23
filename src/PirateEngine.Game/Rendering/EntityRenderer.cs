using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Politics;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using NVec = System.Numerics.Vector2;

namespace PirateEngine.Game.Rendering;

/// <summary>
/// Draws everything that lives on top of the terrain: buildings, wrecks, squads of troops,
/// ships (interpolated between simulation steps), cannon and musket balls, wakes and combat
/// effects, plus order overlays.
/// All drawing happens in world units inside the camera-transformed SpriteBatch.
/// </summary>
public sealed class EntityRenderer
{
    private readonly Draw _draw;
    private readonly SpriteArt _art;

    private struct WakeParticle { public Vector2 Pos; public float Born; public float Size; }
    private readonly List<WakeParticle> _wake = new();
    private float _wakeTimer;
    private float _clock;

    public EntityRenderer(Draw draw, SpriteArt art)
    {
        _draw = draw;
        _art = art;
    }

    public static Vector2 V(NVec v) => new(v.X, v.Y);

    /// <summary>Marker colour for a faction on the overview map and minimap.</summary>
    public static Color MapColor(Faction f) => f switch
    {
        Faction.Player => Color.White,
        Faction.Navy => new Color(235, 70, 60),
        _ => new Color(240, 200, 70),
    };

    // ---- political colours ------------------------------------------------------------

    /// <summary>Colour used for independent towns and their militia.</summary>
    public static readonly Color IndependentColor = new(214, 200, 168);
    private static readonly Dictionary<string, Color> _hexCache = new();

    public static Color Hex(string hex)
    {
        if (!_hexCache.TryGetValue(hex, out var c)) _hexCache[hex] = c = Rendering.Draw.ParseHex(hex, Color.Gray);
        return c;
    }

    /// <summary>A settlement owner's colour (independent towns are parchment).</summary>
    public static Color OwnerColor(State? s) => s == null ? IndependentColor : Hex(s.Color);

    /// <summary>Marker colour for a squad: state troops wear their realm's colour.</summary>
    public static Color UnitColor(LandUnit u) => u.Faction == Faction.State ? OwnerColor(u.Allegiance) : MapColor(u.Faction);

    /// <summary>Uniform colour for state troops; null for pirates and the Navy.</summary>
    public static Color? UnitCoat(LandUnit u) =>
        u.Faction != Faction.State ? null : u.Allegiance == null ? new Color(122, 98, 66) : OwnerColor(u.Allegiance);

    public static Vector2 RenderPos(MobileEntity s, float alpha) =>
        V(NVec.Lerp(s.PreviousPosition, s.Position, alpha));

    public static float RenderHeading(MobileEntity s, float alpha) =>
        s.PreviousHeading + MathUtil.AngleDelta(s.PreviousHeading, s.Heading) * alpha;

    /// <summary>
    /// Whether the player can currently see this ship or squad: their own always; others within
    /// the ship's spyglass range or the sight of a squad ashore (or with fog disabled).
    /// </summary>
    public static bool IsVisible(GameWorld w, MobileEntity s)
    {
        if (s.Faction == Faction.Player || !w.Fog.Enabled || s == w.Player.Target) return true;
        if (NVec.Distance(s.Position, w.Player.Position) <= w.Player.Class.SightRadius * 1.6f) return true;
        foreach (var u in w.Units)
            if (u.Faction == Faction.Player && !u.IsDead && NVec.Distance(s.Position, u.Position) <= u.Def.SightRadius * 1.3f)
                return true;
        return false;
    }

    public void Update(GameWorld w, float realDt, bool paused)
    {
        _clock += realDt;
        if (paused) return;
        _wakeTimer -= realDt;
        if (_wakeTimer <= 0)
        {
            _wakeTimer = 0.07f;
            foreach (var s in w.Ships)
            {
                if (s.IsSunk || s.Speed < 0.6f || !IsVisible(w, s)) continue; // no wakes through the fog
                var back = V(s.Position) - new Vector2(MathF.Cos(s.Heading), MathF.Sin(s.Heading)) * 0.6f;
                _wake.Add(new WakeParticle { Pos = back, Born = _clock, Size = 0.1f + s.Speed * 0.012f });
            }
        }
        _wake.RemoveAll(p => _clock - p.Born > 1.6f);
    }

    public void Draw(SpriteBatch sb, GameWorld w, Rectangle visible, float ppt, float alpha)
    {
        bool detail = ppt >= 9f;
        var vis = new Rectangle(visible.X - 2, visible.Y - 2, visible.Width + 4, visible.Height + 4);

        DrawSettlements(sb, w, vis, ppt);

        // ---- buildings --------------------------------------------------------------
        foreach (var b in w.Buildings)
        {
            if (!vis.Contains(b.Tile.X, b.Tile.Y)) continue;
            if (!w.Fog.IsExplored(b.Tile)) continue;
            if (!detail)
            {
                var c = b.Settlement != null ? OwnerColor(b.Settlement.Owner) : Hex(b.Def.Color);
                _draw.Rect(sb, new Rectangle(b.Tile.X, b.Tile.Y, 1, 1), b.IsTownHall ? Color.White : c);
                continue;
            }
            sb.Draw(_art.Building(b.Def), new Rectangle(b.Tile.X, b.Tile.Y, 1, 1), Color.White);
            if (b.Settlement is { } town)
            {
                // Owner pennant: a small flag in the realm's colour (town halls fly a big one).
                var oc = OwnerColor(town.Owner);
                float wave = MathF.Sin(_clock * 3f + b.Id) * 0.02f;
                if (b.IsTownHall)
                {
                    var pole = new Vector2(b.Tile.X + 0.5f, b.Tile.Y + 0.08f);
                    _draw.Line(sb, pole, pole - new Vector2(0, 0.5f), new Color(60, 44, 28), 0.05f);
                    _draw.RectF(sb, pole - new Vector2(0, 0.5f), new Vector2(0.36f, 0.22f + wave), oc);
                    _draw.OutlineF(sb, pole - new Vector2(0, 0.5f), new Vector2(0.36f, 0.22f + wave), Color.Black * 0.6f, 0.025f);
                }
                else
                {
                    var pole = new Vector2(b.Tile.X + 0.1f, b.Tile.Y + 0.3f);
                    _draw.Line(sb, pole, pole - new Vector2(0, 0.26f), new Color(60, 44, 28), 0.03f);
                    _draw.RectF(sb, pole - new Vector2(0, 0.26f), new Vector2(0.16f, 0.1f + wave), oc);
                }
            }
            var dot = b.State switch
            {
                ProductionState.Producing => new Color(90, 220, 110),
                ProductionState.WaitingForInputs => new Color(240, 200, 60),
                ProductionState.OutputFull => new Color(230, 70, 60),
                _ => new Color(160, 160, 160),
            };
            _draw.FilledCircle(sb, new Vector2(b.Tile.X + 0.86f, b.Tile.Y + 0.14f), 0.11f, Color.Black * 0.7f);
            _draw.FilledCircle(sb, new Vector2(b.Tile.X + 0.86f, b.Tile.Y + 0.14f), 0.08f, dot);
            if (b.State == ProductionState.Producing)
                _draw.RectF(sb, new Vector2(b.Tile.X + 0.12f, b.Tile.Y + 0.9f), new Vector2(0.76f * b.Progress01, 0.07f), new Color(255, 230, 140));
        }

        // ---- wrecks -----------------------------------------------------------------
        foreach (var wr in w.Wrecks)
        {
            var p = V(wr.Position);
            if (!vis.Contains((int)p.X, (int)p.Y)) continue;
            float bob = MathF.Sin(_clock * 2.4f + wr.Id) * 0.04f;
            float fade = Math.Clamp(wr.TimeLeft / 10f, 0.3f, 1f);
            _draw.FilledCircle(sb, p, 0.42f, new Color(255, 220, 120) * (0.15f + 0.1f * MathF.Sin(_clock * 3)));
            _draw.RectF(sb, p + new Vector2(-0.3f, -0.08f + bob), new Vector2(0.6f, 0.14f), new Color(110, 72, 40) * fade);
            _draw.RectF(sb, p + new Vector2(-0.12f, -0.3f - bob), new Vector2(0.14f, 0.5f), new Color(92, 60, 34) * fade);
            _draw.FilledCircle(sb, p + new Vector2(0.22f, 0.18f + bob), 0.12f, new Color(140, 96, 52) * fade);
        }

        // ---- wakes ------------------------------------------------------------------
        foreach (var wp in _wake)
        {
            float age = _clock - wp.Born;
            float a = Math.Clamp(1f - age / 1.6f, 0f, 1f);
            _draw.FilledCircle(sb, wp.Pos, wp.Size + age * 0.18f, Color.White * (a * 0.35f));
        }

        // ---- squads ashore -------------------------------------------------------------
        foreach (var u in w.Units)
        {
            if (u.IsDead || !IsVisible(w, u)) continue;
            var p = RenderPos(u, alpha);
            if (!vis.Contains((int)p.X, (int)p.Y)) continue;
            if (!detail)
            {
                float r = Math.Max(0.45f, 2.5f / ppt);
                _draw.Rect(sb, new Rectangle((int)p.X, (int)p.Y, 1, 1), UnitColor(u) * 0.85f);
                _draw.FilledCircle(sb, p, r, UnitColor(u));
                continue;
            }
            DrawSquad(sb, w, u, p, RenderHeading(u, alpha));
        }

        // ---- ships ------------------------------------------------------------------
        foreach (var s in w.Ships)
        {
            if (s.IsSunk || !IsVisible(w, s)) continue;
            var p = RenderPos(s, alpha);
            if (!vis.Contains((int)p.X, (int)p.Y)) continue;
            float heading = RenderHeading(s, alpha);
            if (!detail)
            {
                float r = Math.Max(0.6f, 3.5f / ppt);
                _draw.FilledCircle(sb, p, r, MapColor(s.Faction));
                continue;
            }
            var tex = _art.Ship(s.Class, s.Faction);
            // Logically a ship is one tile; it's drawn a little larger so hull and sails read clearly.
            float scale = 1.45f / SpriteArt.ShipSize;
            // Shadow, then the ship. Gentle roll makes it feel afloat.
            float roll = 1f + MathF.Sin(_clock * 2f + s.Id) * 0.025f;
            sb.Draw(tex, p + new Vector2(0.07f, 0.09f), null, Color.Black * 0.3f, heading,
                new Vector2(SpriteArt.ShipSize / 2f), new Vector2(scale, scale * roll), SpriteEffects.None, 0f);
            sb.Draw(tex, p, null, Color.White, heading, new Vector2(SpriteArt.ShipSize / 2f),
                new Vector2(scale, scale * roll), SpriteEffects.None, 0f);

            bool showBar = s.Hull < s.MaxHull || s == w.Player.Target || GameWorld.IsHostile(w.Player, s);
            if (showBar)
            {
                var barPos = p + new Vector2(-0.45f, -0.95f);
                _draw.RectF(sb, barPos - new Vector2(0.03f), new Vector2(0.96f, 0.16f), Color.Black * 0.7f);
                var col = s.HullFraction > 0.5f ? new Color(90, 210, 100) : s.HullFraction > 0.25f ? new Color(240, 190, 60) : new Color(230, 60, 50);
                _draw.RectF(sb, barPos, new Vector2(0.9f * s.HullFraction, 0.1f), col);
                if (s.Faction != Faction.Player && s.ReloadTimer > 0)
                    _draw.RectF(sb, barPos + new Vector2(0, 0.12f), new Vector2(0.9f * Math.Clamp(1 - s.ReloadTimer / s.Class.Reload, 0, 1), 0.03f), Color.White * 0.6f);
            }
        }

        // ---- cannonballs ------------------------------------------------------------
        foreach (var pr in w.Projectiles)
        {
            if (pr.Dead) continue;
            var p = V(NVec.Lerp(pr.PreviousPosition, pr.Position, alpha));
            if (pr.Kind == ProjectileKind.Musket)
            {
                if (!detail) continue;
                var dir = Vector2.Normalize(V(pr.Velocity));
                _draw.Line(sb, p - dir * 0.18f, p, new Color(255, 236, 170) * 0.85f, 0.03f);
                continue;
            }
            if (!detail) { _draw.FilledCircle(sb, p, 2f / ppt, Color.Black); continue; }
            // Fake ballistic arc: the ball's shadow stays on the water, the ball rises.
            float total = pr.RemainingDistance + 1f;
            float lift = MathF.Sin(Math.Clamp(1f - pr.RemainingDistance / Math.Max(total, 1f), 0f, 1f) * MathF.PI) * 0.25f;
            _draw.FilledCircle(sb, p, 0.07f, Color.Black * 0.35f);
            _draw.FilledCircle(sb, p - new Vector2(0, lift), 0.08f, new Color(30, 30, 30));
        }

        // ---- effects ----------------------------------------------------------------
        foreach (var e in w.Effects)
        {
            float age = w.Time - e.Time;
            var p = V(e.Position);
            switch (e.Kind)
            {
                case EffectKind.Splash when age < 0.7f:
                    _draw.RingSprite(sb, p, 0.15f + age * 0.7f, Color.White * (1 - age / 0.7f) * 0.8f);
                    _draw.FilledCircle(sb, p, 0.1f * (1 - age / 0.7f), Color.White * 0.9f);
                    break;
                case EffectKind.Hit when age < 0.6f:
                    _draw.FilledCircle(sb, p, 0.15f + age * 0.6f, new Color(255, 170, 60) * (1 - age / 0.6f));
                    _draw.FilledCircle(sb, p + new Vector2(0.1f, -age * 0.5f), 0.1f + age * 0.4f, new Color(80, 80, 80) * (0.7f - age));
                    break;
                case EffectKind.Muzzle when age < 1.0f:
                    for (int k = 0; k < 3; k++)
                        _draw.FilledCircle(sb, p + new Vector2(MathF.Cos(k * 2.1f), MathF.Sin(k * 2.1f)) * (0.3f + age * 0.3f),
                            0.12f + age * 0.25f, new Color(210, 210, 210) * ((1 - age) * 0.45f));
                    break;
                case EffectKind.MusketSmoke when age < 1.4f:
                    for (int k = 0; k < 4; k++)
                        _draw.FilledCircle(sb, p + new Vector2((k - 1.5f) * 0.18f, -age * 0.25f),
                            0.09f + age * 0.18f, new Color(225, 225, 225) * ((1.4f - age) / 1.4f * 0.5f));
                    break;
                case EffectKind.Clash when age < 0.35f:
                    for (int k = 0; k < 5; k++)
                    {
                        float a = k * 1.256f + e.Time * 7f;
                        var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                        _draw.Line(sb, p + d * age * 0.4f, p + d * (0.12f + age * 0.9f), new Color(255, 245, 200) * (1 - age / 0.35f), 0.03f);
                    }
                    break;
                case EffectKind.UnitHit when age < 0.4f:
                    _draw.FilledCircle(sb, p, 0.06f + age * 0.2f, new Color(200, 40, 30) * (1 - age / 0.4f));
                    break;
                case EffectKind.UnitDeath when age < 2f:
                    _draw.RingSprite(sb, p, 0.2f + age * 0.5f, new Color(60, 40, 30) * (1 - age / 2f));
                    break;
                case EffectKind.Dust when age < 0.9f:
                    _draw.FilledCircle(sb, p, 0.12f + age * 0.45f, new Color(170, 150, 110) * ((0.9f - age) / 0.9f * 0.6f));
                    break;
                case EffectKind.Sink when age < 2f:
                    _draw.RingSprite(sb, p, 0.3f + age * 1.1f, new Color(200, 230, 255) * (1 - age / 2f));
                    _draw.FilledCircle(sb, p, 0.5f * (1 - age / 2f), new Color(20, 40, 60) * 0.6f);
                    break;
            }
        }
    }

    /// <summary>
    /// Settlement territory (a ring in the owner's colour), siege rings and occupation progress.
    /// </summary>
    private void DrawSettlements(SpriteBatch sb, GameWorld w, Rectangle vis, float ppt)
    {
        var cfg = w.Config.Politics;
        float lw = Math.Max(0.05f, 1.6f / ppt);
        foreach (var s in w.Politics.Settlements)
        {
            float r = s.Radius(cfg);
            if (s.Tile.X + r < vis.X || s.Tile.X - r > vis.Right || s.Tile.Y + r < vis.Y || s.Tile.Y - r > vis.Bottom) continue;
            if (!w.Fog.IsExplored(s.Tile)) continue;
            var c = V(s.Tile.Center);
            var col = OwnerColor(s.Owner);
            _draw.FilledCircle(sb, c, r, col * (ppt < 2 ? 0.5f : ppt < 9 ? 0.16f : 0.07f)); // a political map when zoomed out
            _draw.Circle(sb, c, r, col * 0.75f, lw, 96, dashed: s.Owner == null);
            if (s.Siege != null)
            {
                float pulse = 0.5f + 0.5f * MathF.Sin(_clock * 4f);
                _draw.Circle(sb, c, 1.4f + pulse * 0.25f, new Color(255, 70, 50) * (0.6f + pulse * 0.4f), lw * 2, 32);
                // Siege progress arc as a bar under the hall.
                _draw.RectF(sb, c + new Vector2(-0.6f, 0.7f), new Vector2(1.2f, 0.12f), Color.Black * 0.7f);
                _draw.RectF(sb, c + new Vector2(-0.58f, 0.72f), new Vector2(1.16f * Math.Clamp(s.Siege.Progress / 100f, 0, 1), 0.08f), new Color(255, 80, 60));
            }
            if (s.OccupationProgress > 0 && s.Occupier != null)
            {
                float f = Math.Clamp(s.OccupationProgress / w.Config.Politics.CaptureSeconds, 0, 1);
                var oc = s.Occupier.IsPlayer ? Color.White : OwnerColor(s.Occupier);
                _draw.RectF(sb, c + new Vector2(-0.6f, 0.86f), new Vector2(1.2f, 0.12f), Color.Black * 0.7f);
                _draw.RectF(sb, c + new Vector2(-0.58f, 0.88f), new Vector2(1.16f * f, 0.08f), oc);
            }
        }
    }

    /// <summary>
    /// Player order overlays: planned course, dock target, reach circle, combat range and target;
    /// and for a selected squad its march route, weapon range and target.
    /// </summary>
    public void DrawOverlays(SpriteBatch sb, GameWorld w, float ppt, float alpha, MobileEntity? hovered, LandUnit? selected)
    {
        var player = w.Player;
        var pp = RenderPos(player, alpha);
        float lw = Math.Max(0.04f, 1.5f / ppt);

        // Planned course.
        if (player.IsMoving)
        {
            var prev = pp;
            var gold = new Color(255, 214, 120) * 0.85f;
            for (int i = player.PathIndex; i < player.Path.Count; i++)
            {
                var next = V(player.Path[i].Center);
                _draw.DashedLine(sb, prev, next, gold, lw, 0.45f, 0.3f, -_clock * 1.2f);
                prev = next;
            }
            float pulse = 0.25f + 0.05f * MathF.Sin(_clock * 5);
            _draw.Circle(sb, prev, pulse, gold, lw, 16);
        }
        if (player.PendingDock.IsValid)
        {
            var c = V(player.PendingDock.Center);
            _draw.OutlineF(sb, new Vector2(player.PendingDock.X, player.PendingDock.Y), Vector2.One, new Color(120, 220, 255), lw * 1.5f);
            _draw.Line(sb, c - new Vector2(0, 0.3f), c + new Vector2(0, 0.3f), new Color(120, 220, 255), lw * 1.5f);
        }

        // Dock reach.
        if (player.IsDocked)
        {
            var c = V(player.DockTile.Center);
            _draw.Circle(sb, c, w.Config.DockReach, new Color(120, 220, 255) * 0.7f, lw, 96, dashed: true);
            _draw.OutlineF(sb, new Vector2(player.DockTile.X, player.DockTile.Y), Vector2.One, new Color(120, 220, 255), lw * 2);
        }

        // Combat: range ring and target markers.
        var target = player.Target;
        bool hostileHover = hovered != null && hovered != player;
        if (target != null || hostileHover)
            _draw.Circle(sb, pp, player.Class.Range, new Color(255, 120, 90) * 0.45f, lw, 64, dashed: true);
        if (target != null && !target.IsDead)
        {
            var tp = RenderPos(target, alpha);
            float r = 0.75f + 0.06f * MathF.Sin(_clock * 6);
            _draw.Circle(sb, tp, r, new Color(255, 70, 60), lw * 1.6f, 24);
            for (int k = 0; k < 4; k++)
            {
                float a = k * MathF.PI / 2 + _clock;
                var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                _draw.Line(sb, tp + d * (r + 0.05f), tp + d * (r + 0.3f), new Color(255, 70, 60), lw * 1.6f);
            }
        }
        if (hovered != null && hovered != target && hovered != player)
            _draw.Circle(sb, RenderPos(hovered, alpha), 0.7f, Color.White * 0.8f, lw, 24);

        if (selected != null && !selected.IsDead && !selected.IsEmbarked) DrawSquadOrders(sb, w, selected, ppt, alpha);
    }

    private void DrawSquadOrders(SpriteBatch sb, GameWorld w, LandUnit u, float ppt, float alpha)
    {
        float lw = Math.Max(0.04f, 1.5f / ppt);
        var up = RenderPos(u, alpha);
        var green = new Color(120, 255, 140);
        _draw.Circle(sb, up, 0.62f + 0.03f * MathF.Sin(_clock * 4), green, lw * 1.5f, 28);
        if (u.Def.IsRanged)
            _draw.Circle(sb, up, u.Def.Range, green * 0.4f, lw, 64, dashed: true);
        if (u.IsMoving)
        {
            var prev = up;
            for (int i = u.PathIndex; i < u.Path.Count; i++)
            {
                var next = V(u.Path[i].Center);
                _draw.DashedLine(sb, prev, next, green * 0.85f, lw, 0.3f, 0.22f, -_clock * 1.5f);
                prev = next;
            }
            _draw.Circle(sb, prev, 0.22f, green, lw, 12);
        }
        if (u.PendingEmbark is { } ship)
            _draw.DashedLine(sb, up, RenderPos(ship, alpha), new Color(120, 220, 255), lw, 0.3f, 0.22f, -_clock * 1.5f);
        if (u.Target is { IsDead: false } t)
        {
            var tp = RenderPos(t, alpha);
            _draw.DashedLine(sb, up, tp, new Color(255, 90, 70) * 0.8f, lw, 0.25f, 0.2f, -_clock * 2f);
            _draw.Circle(sb, tp, 0.7f, new Color(255, 70, 60), lw * 1.6f, 24);
        }
    }

    /// <summary>
    /// A squad is drawn as its actual head-count of soldiers in formation (melee in a clump,
    /// musketeers in a two-deep firing line) facing its heading, with a faction banner at the rear.
    /// Soldiers bob while marching and jostle while fighting hand to hand.
    /// </summary>
    private void DrawSquad(SpriteBatch sb, GameWorld w, LandUnit u, Vector2 p, float heading)
    {
        int n = u.Members;
        var tex = _art.Soldier(u.Def.Kind, u.Faction, UnitCoat(u));
        var fwd = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
        var side = new Vector2(-fwd.Y, fwd.X);
        bool marching = u.Speed > 0.2f;
        bool brawling = !u.Def.IsRanged && u.Target is LandUnit t && u.DistanceTo(t) <= u.Def.Range;
        int perRank = u.Def.IsRanged ? 5 : 4;
        int ranks = (n + perRank - 1) / perRank;
        const float spacing = 0.22f;
        float scale = 0.32f / SpriteArt.SoldierSize;

        _draw.FilledCircle(sb, p + new Vector2(0.03f, 0.05f), 0.5f, Color.Black * 0.18f); // ground shadow
        for (int i = 0; i < n; i++)
        {
            int rank = i / perRank, file = i % perRank;
            int inThisRank = Math.Min(perRank, n - rank * perRank);
            float sx = (file - (inThisRank - 1) / 2f) * spacing;
            float fx = ((ranks - 1) / 2f - rank) * spacing;
            uint h = Rng.Hash(u.Id, i, 3);
            float jx = ((h & 255) / 255f - 0.5f) * 0.05f, jy = (((h >> 8) & 255) / 255f - 0.5f) * 0.05f;
            float bob = marching ? MathF.Sin(_clock * 11f + i * 1.7f) * 0.02f : 0f;
            if (brawling) { jx += MathF.Sin(_clock * 17f + i * 2.3f) * 0.05f; jy += MathF.Cos(_clock * 13f + i) * 0.04f; }
            var pos = p + fwd * (fx + jx + bob) + side * (sx + jy);
            sb.Draw(tex, pos, null, Color.White, heading, new Vector2(SpriteArt.SoldierSize / 2f), scale, SpriteEffects.None, 0f);
        }

        // Banner at the rear left of the formation.
        float rear = ranks / 2f * spacing + 0.12f;
        var pole = p - fwd * rear - side * (perRank / 2f * spacing);
        var flagCol = u.Faction == Faction.State ? UnitColor(u) : SpriteArt.FlagColor(u.Faction);
        _draw.Line(sb, pole, pole - new Vector2(0, 0.42f), new Color(70, 50, 30), 0.035f);
        float wave = MathF.Sin(_clock * 3f + u.Id) * 0.03f;
        _draw.RectF(sb, pole - new Vector2(0, 0.42f), new Vector2(0.2f, 0.13f + wave), flagCol);
        if (u.Faction == Faction.Player) _draw.FilledCircle(sb, pole + new Vector2(0.1f, -0.36f), 0.035f, Color.White);
        _draw.RectF(sb, pole + new Vector2(0.02f, -0.27f), new Vector2(0.1f, 0.06f),
            u.Def.IsRanged ? new Color(255, 230, 140) : new Color(210, 210, 220)); // kind pennant

        // Strength bar.
        bool hostile = GameWorld.IsHostile(w.Player, u);
        if (u.Hp < u.MaxHp || hostile)
        {
            var barPos = p + new Vector2(-0.42f, -0.78f);
            _draw.RectF(sb, barPos - new Vector2(0.03f), new Vector2(0.9f, 0.14f), Color.Black * 0.7f);
            var col = u.HealthFraction > 0.5f ? new Color(90, 210, 100) : u.HealthFraction > 0.25f ? new Color(240, 190, 60) : new Color(230, 60, 50);
            _draw.RectF(sb, barPos, new Vector2(0.84f * u.HealthFraction, 0.08f), col);
        }
    }
}
