using Microsoft.Xna.Framework;
using PirateEngine.Core.Data;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Game.Input;
using PirateEngine.Game.Rendering;
using PirateEngine.Game.UI;

namespace PirateEngine.Game;

/// <summary>
/// Scripted set-ups selectable with <c>--scenario name</c>, for demos, quick testing of a
/// feature, and automated screenshots (<c>--screenshot file.png</c>). They use only the
/// public world API, so they double as usage examples.
/// </summary>
public static class DemoScenarios
{
    public static readonly string[] Names = { "overview", "harbour", "goods", "battle", "voyage", "landing", "troops", "realms", "town", "colony", "market", "states" };

    public static void Apply(string name, GameWorld w, Camera2D cam, PlayerController pc, Hud hud)
    {
        switch (name.ToLowerInvariant())
        {
            case "overview":
                w.Fog.Enabled = false;
                cam.Follow = null;
                cam.Position = new Vector2(w.Map.Width / 2f, w.Map.Height / 2f);
                cam.SetZoom(Math.Min(cam.ViewportWidth / (float)w.Map.Width, (cam.ViewportHeight - 40) / (float)w.Map.Height) * 0.95f);
                pc.ShowCargo = false;
                break;

            case "harbour":
            case "goods":
                BuildHarbour(w);
                cam.SetZoom(40f);
                cam.Follow = null;
                cam.Position = EntityRenderer.V(w.Player.DockTile.Center) + new Vector2(3f, 0);
                hud.ShowTab(name == "goods" ? "goods" : "build");
                if (name == "harbour") pc.BuildDef = w.Defs.Buildings.FirstOrDefault(b => b.Id == "sawmill");
                break;

            case "landing":
            {
                var focus = Landing(w, pc);
                cam.SetZoom(44f);
                cam.Follow = null;
                cam.Position = focus;
                break;
            }

            case "troops":
                Troops(w);
                cam.SetZoom(40f);
                cam.Follow = null;
                cam.Position = EntityRenderer.V(w.Player.DockTile.Center) + new Vector2(3f, 0);
                hud.ShowTab("troops");
                break;

            case "battle":
                Battle(w);
                cam.SetZoom(34f);
                break;

            case "voyage":
                Voyage(w);
                cam.SetZoom(5f);
                cam.Follow = null;
                cam.Position = EntityRenderer.V(w.Player.Position);
                break;

            case "realms":
            {
                // The established world as it stands at the start, seen from above with the fog lifted.
                w.Fog.Enabled = false;
                var big = w.Politics.States.Where(st => !st.IsPlayer && st.IsActive).OrderByDescending(st => st.Settlements.Count).First();
                var towns = big.Settlements.ToList();
                cam.Follow = null;
                cam.Position = new Vector2((float)towns.Average(t => t.Tile.X), (float)towns.Average(t => t.Tile.Y));
                cam.SetZoom(4.2f);
                pc.ShowCargo = false;
                break;
            }

            case "town":
            case "colony":
            {
                var town = FoundTown(w);
                cam.SetZoom(name == "colony" ? 34f : 30f);
                cam.Follow = null;
                cam.Position = town != null ? EntityRenderer.V(town.Tile.Center) - new Vector2(2, 0) : EntityRenderer.V(w.Player.DockTile.Center);
                if (town != null && name == "town") pc.OpenSettlementScreen(town);
                if (name == "colony") hud.ShowTab("goods");
                break;
            }

            case "market":
            {
                var town = SailToMarket(w);
                cam.SetZoom(30f);
                cam.Follow = null;
                cam.Position = EntityRenderer.V(w.Player.DockTile.Center);
                if (town != null) pc.OpenSettlementScreen(town);
                break;
            }

            case "states":
            {
                FoundTown(w);
                Run(w, 15 * 60);
                var p = w.Politics;
                var subject = p.States.Where(st => !st.IsPlayer && st.IsActive)
                    .OrderByDescending(st => p.Diplomacy.EnemiesOf(st).Count()).ThenByDescending(st => st.Settlements.Count).First();
                pc.OpenStateScreen(subject);
                break;
            }

            default:
                w.Events.Post($"Unknown scenario '{name}'. Try: {string.Join(", ", Names)}", MessageKind.Warning);
                break;
        }
    }

    private static void Run(GameWorld w, float seconds, Func<bool>? until = null)
    {
        for (float t = 0; t < seconds; t += GameWorld.FixedStep)
        {
            w.Update(GameWorld.FixedStep);
            if (until?.Invoke() == true) return;
        }
    }

    /// <summary>Docks at the nearest shore and builds a small working colony.</summary>
    public static void BuildHarbour(GameWorld w)
    {
        var land = w.Map.FindNearest(w.Player.Tile, 8, p => w.Map.IsLand(p));
        w.CommandDock(land);
        Run(w, 30, () => w.Player.IsDocked);
        if (!w.Player.IsDocked) return;

        w.GrantGold(1500);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("wood"), 40);
        string[] wanted = { "lumber_camp", "sawmill", "farm", "fishery", "quarry", "cotton_field", "plantation", "market", "galley", "trapper" };
        foreach (var id in wanted)
        {
            var def = w.Defs.Building(id);
            var spot = w.Map.FindNearest(w.Player.DockTile, (int)w.Config.DockReach,
                p => w.CanPlace(def, p) == PlacementError.None);
            if (spot.IsValid) w.PlaceBuilding(def, spot);
        }
        Run(w, 35);
        w.AutoTransfer();
        Run(w, 12);
    }

    /// <summary>
    /// The harbour colony plus a Town Hall: founds the player's settlement and state.
    /// Then stocks the stores and lets the town grow for a few minutes.
    /// </summary>
    public static Core.Politics.Settlement? FoundTown(GameWorld w)
    {
        BuildHarbour(w);
        if (!w.Player.IsDocked) return null;
        var hall = w.Defs.TownHall;
        w.GrantGold(hall.GoldCost + 200);
        foreach (var (res, n) in hall.BuildCostList) w.Player.Cargo.Add(res, n);
        var spot = w.Map.FindNearest(w.Player.DockTile, (int)w.Config.DockReach,
            p => p.Distance(w.Player.DockTile) >= 2.5f && w.CanPlace(hall, p) == PlacementError.None);
        if (!spot.IsValid) return null;
        var town = w.PlaceBuilding(hall, spot)?.Settlement;
        if (town == null) return null;
        w.AutoTransfer();
        foreach (var (res, n) in w.Player.Cargo.NonZero().ToList())
            if (w.Defs.Resources[res].Food > 0) w.Politics.UnloadToStores(town, res, n);
        Run(w, 150);
        return town;
    }

    /// <summary>Sails to the nearest town of a computer-run state and docks by its town hall.</summary>
    private static Core.Politics.Settlement? SailToMarket(GameWorld w)
    {
        var p = w.Politics;
        w.Config.SafeHarbourRadius = 1000; // a quiet voyage for the demo
        var town = p.Settlements.Where(s => s.Owner != null && !s.Owner.IsPlayer)
            .OrderBy(s => s.Tile.Distance(w.Player.Tile)).FirstOrDefault();
        if (town == null) return null;
        var dock = p.DockingSpot(town);
        if (!dock.IsValid) return town;
        w.CommandDock(dock);
        Run(w, 240, () => w.Player.IsDocked);
        w.Fog.Reveal(town.Tile, 14);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("rum"), 6);
        w.Player.Cargo.Add(w.Defs.ResourceIndex("furs"), 8);
        return town;
    }

    private static bool DockNearest(GameWorld w)
    {
        var land = w.Map.FindNearest(w.Player.Tile, 8, p => w.Map.IsLand(p));
        w.CommandDock(land);
        Run(w, 30, () => w.Player.IsDocked);
        return w.Player.IsDocked;
    }

    /// <summary>
    /// Beach assault: land both starting squads, find a Navy garrison post a few tiles inland,
    /// and attack it. Returns the camera focus point.
    /// </summary>
    private static Vector2 Landing(GameWorld w, PlayerController pc)
    {
        w.Config.SafeHarbourRadius = 0; // demo fights right by the start harbour
        if (!DockNearest(w)) return EntityRenderer.V(w.Player.Position);
        w.DisembarkAll();
        var dock = w.Player.DockTile;
        int island = w.Map.RegionAt(dock);
        bool Inland(GridPoint p, float min) => w.Map.IsLand(p) && w.Map.RegionAt(p) == island && p.Distance(dock) >= min && !w.Map.IsCoast(p);
        var post = w.Map.FindNearest(dock, 14, p => Inland(p, 8));
        if (!post.IsValid) return EntityRenderer.V(w.Player.Position);
        var navyMuskets = w.SpawnAiUnit("musketeers", post);
        var navyCrew = w.SpawnAiUnit("cutlass_crew", w.Map.FindNearest(post, 3, p => Inland(p, 6) && p != post));
        var crew = w.Units.First(u => u.Faction == Faction.Player && !u.Def.IsRanged);
        var muskets = w.Units.First(u => u.Faction == Faction.Player && u.Def.IsRanged);
        w.CommandUnitAttack(crew, navyCrew);
        w.CommandUnitAttack(muskets, navyMuskets);
        Run(w, 4.5f);
        Run(w, 4, () => w.Projectiles.Count(p => p.Kind == ProjectileKind.Musket) >= 6);
        pc.SelectedUnit = muskets.IsDead ? null : muskets;
        var a = muskets.IsDead ? crew.Position : muskets.Position;
        return EntityRenderer.V(System.Numerics.Vector2.Lerp(a, navyMuskets.Position, 0.5f));
    }

    /// <summary>Docked with a Barracks built, one squad ashore and one aboard.</summary>
    private static void Troops(GameWorld w)
    {
        if (!DockNearest(w)) return;
        w.GrantGold(800);
        w.Player.Cargo.Clear();
        foreach (var (id, n) in new[] { ("wood", 10), ("stone", 6), ("rations", 8), ("iron", 2), ("cannonballs", 20) })
            w.Player.Cargo.Add(w.Defs.ResourceIndex(id), n);
        var barracks = w.Defs.Building("barracks");
        var spot = w.Map.FindNearest(w.Player.DockTile, (int)w.Config.DockReach, p => w.CanPlace(barracks, p) == PlacementError.None);
        if (spot.IsValid) w.PlaceBuilding(barracks, spot);
        var crew = w.Player.Troops.First(t => !t.Def.IsRanged);
        w.Disembark(crew);
        var dest = w.Map.FindNearest(w.Player.DockTile, 6, p => w.Map.IsLand(p) && p.Distance(w.Player.DockTile) >= 4);
        if (dest.IsValid) w.CommandUnitMove(crew, dest);
        Run(w, 6);
    }

    private static void Battle(GameWorld w)
    {
        // The demo fights right outside the start harbour, which is normally a refuge.
        w.Config.SafeHarbourRadius = 0;
        var p = w.Player.Tile;
        int region = w.Map.RegionAt(p);
        GridPoint Near(int dx, int dy) => w.Map.FindNearest(new GridPoint(p.X + dx, p.Y + dy), 10, q => w.Map.RegionAt(q) == region);
        var brig = w.SpawnAiShip("navy_brig", ShipRole.Hunter, Near(9, 3), "HMS Vigilant");
        w.SpawnAiShip("navy_cutter", ShipRole.Hunter, Near(-8, 6), "HMS Swift");
        w.CommandAttack(brig);
        Run(w, 7);
        Run(w, 6, () => w.Projectiles.Count >= 3);
    }

    private static void Voyage(GameWorld w)
    {
        var map = w.Map;
        var far = map.FindNearest(new GridPoint(map.Width / 5, map.Height * 4 / 5), map.Width / 2,
            q => map.RegionAt(q) == map.MainOceanRegion);
        w.AsyncPathfinding = false; // plan immediately so the course shows on the first frame
        w.CommandMoveTo(far);
        w.AsyncPathfinding = true;
        Run(w, 25);
    }
}
