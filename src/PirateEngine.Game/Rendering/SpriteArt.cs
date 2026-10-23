using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using PirateEngine.Core.Data;
using UnitKind = PirateEngine.Core.Data.UnitKind;
using PirateEngine.Core.Entities;

namespace PirateEngine.Game.Rendering;

/// <summary>
/// Procedural ship and building sprites, generated per ship class × faction and per building
/// type on first use and cached. Ships face +X (east) in the texture and are rotated by heading.
/// </summary>
public sealed class SpriteArt
{
    public const int ShipSize = 32, BuildingSize = 32, SoldierSize = 16;
    private readonly GraphicsDevice _device;
    private readonly Dictionary<(string, Faction), Texture2D> _ships = new();
    private readonly Dictionary<string, Texture2D> _buildings = new();
    private readonly Dictionary<(UnitKind, Faction, uint), Texture2D> _soldiers = new();

    public SpriteArt(GraphicsDevice device) => _device = device;

    public static Color FlagColor(Faction f) => f switch
    {
        Faction.Player => new Color(20, 20, 20),
        Faction.Navy => new Color(200, 40, 40),
        _ => new Color(230, 190, 60),
    };

    public Texture2D Ship(ShipClassDef cls, Faction faction)
    {
        if (_ships.TryGetValue((cls.Id, faction), out var tex)) return tex;
        const int S = ShipSize;
        const float mid = S / 2f;
        var px = new Color[S * S];
        var hull = Draw.ParseHex(cls.HullColor, new Color(110, 70, 40));
        var sail = Draw.ParseHex(cls.SailColor, Color.Wheat);
        var deck = Lighten(hull, 1.55f);
        var dark = Lighten(hull, 0.5f);
        int masts = cls.Guns >= 6 ? 3 : cls.Guns >= 3 ? 2 : 1;

        // Hull outline: blunt stern (left), widest just aft of midships, sharp bow (right).
        float HalfWidth(float x)
        {
            float t = (x - 1.5f) / 29f;
            if (t < 0 || t > 1) return -1;
            if (t < 0.38f) return 8.2f * (0.72f + 0.28f * MathF.Sin(t / 0.38f * MathF.PI / 2));
            float k = (t - 0.38f) / 0.62f;
            return 8.2f * MathF.Sqrt(MathF.Max(0, 1 - k * k * k * 0.9f - k * 0.1f));
        }
        for (int x = 0; x < S; x++)
        {
            float hw = HalfWidth(x + 0.5f);
            if (hw <= 0.4f) continue;
            for (int y = 0; y < S; y++)
            {
                float d = MathF.Abs(y + 0.5f - mid);
                if (d > hw) continue;
                Color c = d > hw - 1.2f ? dark : d > hw - 2.4f ? hull : deck;
                if (c == deck && x % 4 == 0) c = Lighten(deck, 0.88f); // planking
                px[y * S + x] = c;
            }
        }

        // Gun ports along each side.
        int perSide = Math.Max(1, (cls.Guns + 1) / 2);
        for (int g = 0; g < perSide; g++)
        {
            int x = 8 + g * Math.Max(2, 15 / perSide);
            float hw = HalfWidth(x + 0.5f);
            Set(px, x, (int)(mid - hw + 1), new Color(20, 20, 20));
            Set(px, x, (int)(mid + hw - 1), new Color(20, 20, 20));
        }

        // Masts: a dark yard across the beam with a sail billowing forward of it.
        for (int m = 0; m < masts; m++)
        {
            int mx = masts == 1 ? 14 : masts == 2 ? 9 + m * 10 : 7 + m * 7;
            int span = masts == 3 && m != 1 ? 10 : 12;
            for (int y = (int)mid - span; y <= (int)mid + span; y++)
            {
                float dy = (y + 0.5f - mid) / (span + 0.5f);
                float bulge = (1 - dy * dy) * 3.2f;
                Set(px, mx, y, Lighten(hull, 0.45f)); // yard
                for (int dx = 1; dx <= 3; dx++)
                {
                    float shade = dx == 1 ? 0.8f : dx == 2 ? 0.95f : 1.05f;
                    Set(px, mx + dx + (int)bulge, y, Lighten(sail, shade - 0.12f * MathF.Abs(dy)));
                }
                for (int fill = 1; fill <= (int)bulge; fill++) Set(px, mx + fill, y, Lighten(sail, 0.72f));
            }
            Set(px, mx, (int)mid, new Color(50, 32, 18));
        }

        // Ensign at the stern.
        var flag = FlagColor(faction);
        for (int y = 13; y < 19; y++)
            for (int x = 0; x < 4; x++) Set(px, x, y, flag);
        if (faction == Faction.Player) { Set(px, 1, 15, Color.White); Set(px, 2, 15, Color.White); Set(px, 1, 16, Color.White); Set(px, 2, 17, Color.White); }

        // Soft dark outline so ships read on any water colour.
        var outlined = (Color[])px.Clone();
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            if (px[y * S + x].A != 0) continue;
            bool edge = false;
            for (int k = 0; k < 4 && !edge; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                edge = (uint)nx < S && (uint)ny < S && px[ny * S + nx].A == 255;
            }
            if (edge) outlined[y * S + x] = new Color(8, 10, 14) * 0.7f;
        }

        tex = new Texture2D(_device, S, S);
        tex.SetData(outlined);
        _ships[(cls.Id, faction)] = tex;
        return tex;
    }

    /// <summary>
    /// One soldier seen from above, facing +X. Pirates wear pale shirts and red bandanas; the
    /// Navy wears red coats with white crossbelts and black tricorns. Melee troops carry a cutlass,
    /// ranged troops a musket held forward.
    /// </summary>
    /// <param name="coatColor">Uniform colour for state troops (their realm's colour); null = faction default.</param>
    public Texture2D Soldier(UnitKind kind, Faction faction, Color? coatColor = null)
    {
        var key = (kind, faction, coatColor?.PackedValue ?? 0u);
        if (_soldiers.TryGetValue(key, out var tex)) return tex;
        const int S = SoldierSize;
        var px = new Color[S * S];
        bool navy = faction != Faction.Player;
        var coat = coatColor ?? (navy ? new Color(178, 34, 34) : new Color(226, 220, 200));
        var coatShade = SpriteArt.Lighten(coat, 0.72f);
        var skin = new Color(222, 176, 136);
        var hat = navy ? new Color(28, 26, 30) : new Color(170, 34, 30);

        void Put(int x, int y, Color c) { if ((uint)x < S && (uint)y < S) px[y * S + x] = c; }

        // Weapon first so the body overlaps its grip.
        if (kind == UnitKind.Ranged)
        {
            for (int x = 6; x <= 15; x++) Put(x, 11, x >= 14 ? new Color(150, 150, 160) : new Color(96, 64, 36));
            Put(9, 12, new Color(96, 64, 36));
        }
        else
        {
            for (int x = 9; x <= 14; x++) Put(x, 12 - (x - 9) / 3, new Color(205, 210, 220));
            Put(9, 12, new Color(150, 120, 60)); // hilt
        }

        // Shoulders / torso: an ellipse wider across the body than front-to-back.
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float dx = (x + 0.5f - 7f) / 2.8f, dy = (y + 0.5f - 8f) / 4.6f;
            if (dx * dx + dy * dy <= 1f) Put(x, y, dy > 0.35f ? coatShade : coat);
        }
        if (navy) // crossbelts
            for (int k = -3; k <= 3; k++) { Put(7 + k / 2, 8 + k, Color.White); Put(7 - k / 2, 8 + k, Color.White); }

        // Head with hat / bandana.
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float dx = x + 0.5f - 8f, dy = y + 0.5f - 8f;
            float d2 = dx * dx + dy * dy;
            if (navy && d2 <= 9.5f && dx < 1.2f) Put(x, y, hat);          // tricorn brim
            else if (d2 <= 4.6f) Put(x, y, dx < -0.2f ? hat : skin);    // bandana / bare head
        }

        // Dark outline for contrast against any ground.
        var outlined = (Color[])px.Clone();
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            if (px[y * S + x].A != 0) continue;
            bool edge = false;
            for (int k = 0; k < 4 && !edge; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                edge = (uint)nx < S && (uint)ny < S && px[ny * S + nx].A == 255;
            }
            if (edge) outlined[y * S + x] = new Color(10, 10, 12) * 0.75f;
        }

        tex = new Texture2D(_device, S, S);
        tex.SetData(outlined);
        _soldiers[key] = tex;
        return tex;
    }

    public Texture2D Building(BuildingDef def)
    {
        if (_buildings.TryGetValue(def.Id, out var tex)) return tex;
        const int S = BuildingSize;
        var px = new Color[S * S];
        var c = Draw.ParseHex(def.Color, Color.Gray);
        var wall = Lighten(c, 1.0f);
        var roof = Lighten(c, 0.62f);
        var outline = new Color(25, 20, 15);

        // Drop shadow, walls, and a pitched roof (light from top-left).
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            bool inBody = x >= 4 && x < 28 && y >= 6 && y < 28;
            bool inShadow = x >= 6 && x < 30 && y >= 8 && y < 30;
            if (inBody)
            {
                bool edge = x == 4 || x == 27 || y == 6 || y == 27;
                bool roofPart = y < 16;
                var col = roofPart ? (x < 16 ? Lighten(roof, 1.25f) : roof) : wall;
                if (!roofPart && (x - 4) % 6 == 0) col = Lighten(wall, 0.9f);
                px[y * S + x] = edge ? outline : col;
            }
            else if (inShadow) px[y * S + x] = new Color(0, 0, 0, 90);
        }
        // Ridge line and a door.
        for (int x = 5; x < 27; x++) px[15 * S + x] = Lighten(roof, 0.7f);
        for (int y = 21; y < 27; y++) for (int x = 14; x < 18; x++) px[y * S + x] = new Color(60, 40, 22);

        tex = new Texture2D(_device, S, S);
        tex.SetData(px);
        _buildings[def.Id] = tex;
        return tex;
    }

    private static void Set(Color[] px, int x, int y, Color c)
    {
        if ((uint)x < ShipSize && (uint)y < ShipSize) px[y * ShipSize + x] = c;
    }

    public static Color Lighten(Color c, float f) =>
        new((int)Math.Clamp(c.R * f, 0, 255), (int)Math.Clamp(c.G * f, 0, 255), (int)Math.Clamp(c.B * f, 0, 255), c.A);
}
