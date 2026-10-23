using PirateEngine.Core.Data;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.AI;

/// <summary>
/// Keeps the world around the player populated without simulating the whole map: AI ships
/// spawn in a ring just outside sight range and Navy garrisons appear on nearby islands; both
/// are culled once far behind. The mix and density scale with "danger" — distance from the
/// start harbour — so the far seas hold heavier warships while the home waters stay safe to
/// learn in. Garrisons never appear inside the safe harbour or next to the player's buildings.
/// </summary>
public sealed class SpawnDirector
{
    private readonly GameWorld _w;
    private float _timer = 3f;
    public bool Enabled { get; set; } = true;

    private static readonly string[] NavyNames =
    {
        "Resolute", "Vigilant", "Dauntless", "Swift", "Valiant", "Intrepid", "Relentless", "Sentinel",
        "Defiance", "Tenacious", "Warden", "Steadfast", "Gallant", "Implacable", "Arbiter", "Lion",
    };
    private static readonly string[] MerchantNames =
    {
        "Merry Wanderer", "Silver Gull", "Good Fortune", "Patience", "Amity", "Bountiful", "Morning Star",
        "Prosper", "Hopewell", "Sea Lark", "Endeavour", "Constance", "Diligence", "Plenty",
    };

    public SpawnDirector(GameWorld world) => _w = world;

    public int TargetPopulation(float danger) =>
        (int)MathF.Round(_w.Config.MaxAiShips * (0.35f + 0.65f * danger));

    public void Update(float dt)
    {
        if (!Enabled) return;
        _timer -= dt;
        if (_timer > 0) return;
        _timer = 1.5f;

        var player = _w.Player;
        int alive = 0;
        foreach (var s in _w.Ships)
        {
            if (s.IsSunk || s.Faction == Faction.Player) continue;
            bool engaged = s.Target == player || player.Target == s;
            if (!engaged && s.DistanceTo(player) > _w.Config.DespawnDistance) _w.Despawn(s);
            else alive++;
        }

        UpdateGarrisons();
        if (alive >= TargetPopulation(_w.DangerAt(player.Tile))) return;
        TrySpawnOne();
    }

    public int TargetGarrisons(float danger) =>
        (int)MathF.Round(_w.Config.MaxAiUnits * (0.4f + 0.6f * danger));

    private void UpdateGarrisons()
    {
        var player = _w.Player;
        int alive = 0;
        foreach (var u in _w.Units)
        {
            if (u.IsDead || u.Faction == Faction.Player) continue;
            bool engaged = u.Target != null && u.Target.Faction == Faction.Player;
            if (!engaged && u.DistanceTo(player) > _w.Config.DespawnDistance && NearestPlayerSquad(u.Position) > _w.Config.DespawnDistance)
                _w.DespawnUnit(u);
            else alive++;
        }
        if (_w.Config.UnitSpawns.Count > 0 && alive < TargetGarrisons(_w.DangerAt(player.Tile)))
            TrySpawnGarrison();
    }

    private float NearestPlayerSquad(System.Numerics.Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var u in _w.Units)
            if (u.Faction == Faction.Player && !u.IsDead) best = Math.Min(best, System.Numerics.Vector2.Distance(u.Position, p));
        return best;
    }

    /// <summary>
    /// Spawns one Navy garrison on land in a ring around the player. Returns it, or null if no
    /// valid site was found this time (not land, island too small, in the safe harbour, too close
    /// to the player's buildings or troops).
    /// </summary>
    public LandUnit? TrySpawnGarrison()
    {
        var cfg = _w.Config;
        var centre = _w.Player.Tile;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            float a = _w.Rng.Range(0, MathF.Tau), r = _w.Rng.Range(cfg.UnitSpawnMinDistance, cfg.UnitSpawnMaxDistance);
            var tile = new GridPoint(centre.X + (int)(MathF.Cos(a) * r), centre.Y + (int)(MathF.Sin(a) * r));
            if (!_w.Map.IsLand(tile)) continue;
            if (_w.Map.RegionSizes[_w.Map.RegionAt(tile)] < 60) continue;
            if (tile.Distance(_w.Map.StartTile) < cfg.SafeHarbourRadius) continue;
            if (_w.Buildings.Any(b => b.Tile.Distance(tile) < cfg.GarrisonBuildingClearance)) continue;
            if (_w.Politics.NearestSettlementDistance(tile) < 20) continue; // towns keep their own peace
            if (NearestPlayerSquad(tile.Center) < 12f) continue;

            float danger = _w.DangerAt(tile);
            var candidates = cfg.UnitSpawns.Where(sp => danger >= sp.MinDanger && danger <= sp.MaxDanger).ToList();
            if (candidates.Count == 0) return null;
            float total = candidates.Sum(c => c.Weight), pick = _w.Rng.NextFloat() * total;
            var chosen = candidates[^1];
            foreach (var c in candidates)
            {
                pick -= c.Weight;
                if (pick <= 0) { chosen = c; break; }
            }
            return _w.AddGarrison(chosen, tile);
        }
        return null;
    }

    /// <summary>Spawns one ship in the ring around the player. Returns it, or null if no valid spot/entry.</summary>
    public Ship? TrySpawnOne()
    {
        var player = _w.Player;
        int region = _w.Map.RegionAt(player.Tile);
        var tile = _w.RandomWaterNear(player.Tile, _w.Config.SpawnMinDistance, _w.Config.SpawnMaxDistance, region);
        if (!tile.IsValid) return null;

        float danger = _w.DangerAt(tile);
        bool inSafeHarbour = tile.Distance(_w.Map.StartTile) < _w.Config.SafeHarbourRadius;
        var candidates = _w.Config.Spawns
            .Where(sp => danger >= sp.MinDanger && danger <= sp.MaxDanger && !(inSafeHarbour && sp.Role == ShipRole.Hunter))
            .ToList();
        if (candidates.Count == 0) return null;

        float total = candidates.Sum(c => c.Weight), pick = _w.Rng.NextFloat() * total;
        var chosen = candidates[^1];
        foreach (var c in candidates)
        {
            pick -= c.Weight;
            if (pick <= 0) { chosen = c; break; }
        }

        string name = chosen.Role == ShipRole.Merchant
            ? MerchantNames[_w.Rng.Next(MerchantNames.Length)]
            : "HMS " + NavyNames[_w.Rng.Next(NavyNames.Length)];
        return _w.AddAiShip(chosen, tile, name);
    }
}
