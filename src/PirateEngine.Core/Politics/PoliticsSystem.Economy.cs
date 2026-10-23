using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Politics;

// Natural development: logistics, food, growth, taxes, garrisons, construction, colonisation.
public sealed partial class PoliticsSystem
{
    private void EconomyTick()
    {
        foreach (var s in _settlements.ToList()) SettlementTick(s);
        foreach (var st in _states.Where(x => !x.IsPlayer && x.IsActive).ToList()) StateEconomyTick(st);
    }

    private void SettlementTick(Settlement s)
    {
        s.LastBuildingGold = s.GoldAccumulator;
        s.LastIncome = s.GoldAccumulator;
        s.GoldAccumulator = 0;
        Logistics(s);

        // ---- food ------------------------------------------------------------------
        int need = Math.Max(1, (int)MathF.Ceiling(s.Population * Cfg.FoodPerPop));
        float eaten = Forage(s);
        foreach (var food in _w.Defs.Resources.Where(r => r.Food > 0).OrderBy(r => r.Food))
        {
            while (eaten < need && s.Stores[food.Index] > 0)
            {
                s.Stores.Remove(food.Index, 1);
                eaten += food.Food;
            }
        }
        s.FedRatio = Math.Clamp(eaten / need, 0f, 1f);

        // ---- growth: a slow approach to what the town's buildings support -------------
        s.PopulationCap = Cfg.PopBase + Cfg.PopPerBuilding * s.BuildingList.Count + s.LevelIndex * 10;
        if (s.FedRatio >= 0.99f)
        {
            if (s.Population < s.PopulationCap)
            {
                float rate = Cfg.GrowthRate * (s.Owner == PlayerState ? Cfg.PlayerGrowthMultiplier : 1f);
                s.GrowthProgress += Math.Max(0.02f, (s.PopulationCap - s.Population) * rate);
                int born = (int)s.GrowthProgress;
                s.GrowthProgress -= born;
                s.Population = Math.Min(s.PopulationCap, s.Population + born);
            }
            else if (s.Population > s.PopulationCap) s.Population--;
        }
        else
        {
            s.GrowthProgress = 0;
            s.Population = Math.Max(3, s.Population - Math.Max(1, (int)MathF.Round(s.Population * (1 - s.FedRatio) * 0.04f)));
        }
        RefreshLevel(s);

        // ---- taxes -----------------------------------------------------------------
        var beh = s.Owner?.Behaviour ?? PlayerBehaviour;
        float tax = s.Population * Cfg.TaxPerPop * (0.75f + beh.Trade * 0.5f);
        AddFunds(s.Owner, s, tax);
        s.LastIncome += tax;

        // ---- surplus goods are shipped off by local merchants (keeps stores from clogging) --
        if (s.Owner != PlayerState) Export(s);

        // ---- merchants: trade capital that buys the player's goods ---------------------
        float purseCap = PurseCap(s);
        if (s.Owner != null)
        {
            if (s.Treasury < purseCap) s.Treasury = Math.Min(purseCap, s.Treasury + s.Population * 0.06f * (0.5f + beh.Trade));
            else
            {
                // Customs: half of what the merchants hold beyond their needs goes to the crown.
                float excess = (s.Treasury - purseCap) * 0.5f;
                s.Treasury -= excess;
                AddFunds(s.Owner, s, excess);
                s.LastIncome += excess;
            }
        }

        // ---- garrison (local militia grows on its own; barracks train faster) --------
        int barracks = s.BuildingList.Count(b => b.HasService("barracks"));
        float target = GarrisonTarget(s);
        float present = s.Garrison + s.Militia.Where(m => !m.IsDead).Sum(m => m.Members);
        if (present < target) s.Garrison += Math.Min(target - present, 1 + barracks);
        else if (s.Garrison > target * 1.5f + 10 && s.Siege == null)
        {
            // Surplus soldiers (e.g. a conquering army left behind) rejoin the field army or go home.
            float surplus = (s.Garrison - target * 1.5f - 10) * 0.2f + 1;
            s.Garrison -= surplus;
            if (s.Owner is { IsPlayer: false } st) st.FieldArmy += surplus;
        }

        // ---- construction (computer-run towns only): when crowded or short of food -----
        if (s.Owner != PlayerState && s.BuildingList.Count < s.Level(Cfg).MaxBuildings)
        {
            bool crowded = s.Population >= s.PopulationCap * 0.9f;
            bool hungry = s.FedRatio < 0.95f;
            float chance = Cfg.ConstructionChance * (0.5f + beh.Expansion) * (hungry ? 2f : crowded ? 1f : 0f);
            if (chance > 0 && _w.Rng.NextFloat() < chance) TryDevelop(s);
        }
    }

    /// <summary>The garrison a town keeps in peacetime.</summary>
    public float GarrisonTarget(Settlement s)
    {
        var beh = s.Owner?.Behaviour ?? PlayerBehaviour;
        int barracks = s.BuildingList.Count(b => b.HasService("barracks"));
        return s.Population * Cfg.GarrisonPerPop * (0.6f + beh.Defence) + barracks * 6;
    }

    /// <summary>What the town's merchants keep on hand to trade with.</summary>
    public float PurseCap(Settlement s) => 150 + s.Population * 4;

    /// <summary>The field army a state keeps when at its natural strength.</summary>
    public float ArmyCap(State st) => st.Population * 0.1f * (0.5f + st.Behaviour.Aggression) + 10;

    /// <summary>
    /// Local merchants ship a share of any surplus away each tick, earning a little for the
    /// town's purse. Without it a town that makes more than it uses would clog its stores and
    /// stall its workshops (including its food supply) over a long playthrough.
    /// </summary>
    private void Export(Settlement s)
    {
        int cap = s.Stores.Capacity;
        for (int res = 0; res < _w.Defs.ResourceCount; res++)
        {
            int have = s.Stores[res];
            if (have == 0) continue;
            var def = _w.Defs.Resources[res];
            int keep = def.Food > 0 ? Math.Max(20, cap * 2 / 5) : Math.Max(15, cap / 8);
            if (have <= keep) continue;
            int sold = (int)MathF.Ceiling((have - keep) * 0.25f);
            s.Stores.Remove(res, sold);
            s.Treasury += sold * def.Value * 0.4f;
        }
    }

    /// <summary>Food units the town eats per economy tick.</summary>
    public int FoodNeed(Settlement s) => Math.Max(1, (int)MathF.Ceiling(s.Population * Cfg.FoodPerPop));

    /// <summary>Economy ticks per game minute (for per-minute figures in the UI).</summary>
    public float TicksPerMinute => 60f / Cfg.EconomyInterval;

    /// <summary>
    /// Nominal output of the town's workshops per game minute, by resource (gold as resource -1),
    /// from each building's current (or first) recipe. Buildings stalled for inputs still count:
    /// this is capacity, while <see cref="Building.State"/> says whether each one is working.
    /// </summary>
    public List<(int resource, float perMinute)> ProductionPerMinute(Settlement s)
    {
        var totals = new Dictionary<int, float>();
        foreach (var b in s.BuildingList)
        {
            var r = b.CurrentRecipe ?? (b.Def.Recipes.Count > 0 ? b.Def.Recipes[0] : null);
            if (r == null || r.CycleSeconds <= 0) continue;
            float k = 60f / r.CycleSeconds;
            foreach (var (res, n) in r.OutputList) totals[res] = totals.GetValueOrDefault(res) + n * k;
            if (r.GoldOutput > 0) totals[-1] = totals.GetValueOrDefault(-1) + r.GoldOutput * k;
        }
        return totals.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Food gathered without buildings: hunting, fishing and foraging on nearby land.</summary>
    public float Forage(Settlement s)
    {
        int fertile = 0;
        int r = (int)s.Radius(Cfg);
        for (int dy = -r; dy <= r; dy += 2)
        for (int dx = -r; dx <= r; dx += 2)
        {
            var p = new GridPoint(s.Tile.X + dx, s.Tile.Y + dy);
            if (!_w.Map.InBounds(p)) continue;
            var b = _w.Map.GetBiome(p);
            if (b is Biome.Grassland or Biome.Savanna or Biome.TemperateForest or Biome.Jungle or Biome.Beach or Biome.ShallowWater) fertile++;
        }
        return 1f + fertile * 0.04f;
    }

    /// <summary>
    /// Town logistics: carts move every building's output into the town stores and deliver the
    /// inputs each building needs. Ships only need to carry goods <i>between</i> towns.
    /// </summary>
    private void Logistics(Settlement s)
    {
        Array.Clear(s.LastProduced);
        foreach (var b in s.BuildingList)
            foreach (var (res, amount) in b.Output.NonZero().ToList())
                s.LastProduced[res] += b.Output.TransferTo(s.Stores, res, amount);
        // Food goes to workshops that turn it into more food (a galley); anything else (a market
        // selling rations) only gets food the town can spare beyond a comfortable reserve.
        bool spareFood = FoodStock(s) >= FoodNeed(s) * 6;
        foreach (var b in s.BuildingList)
        {
            bool makesFood = b.Def.Recipes.Any(r => r.OutputList.Any(o => _w.Defs.Resources[o.res].Food > 0));
            foreach (var recipe in b.Def.Recipes)
                foreach (var (res, need) in recipe.InputList)
                {
                    if (!makesFood && !spareFood && _w.Defs.Resources[res].Food > 0) continue;
                    int want = need * 2 - b.Input[res];
                    if (want > 0) s.Stores.TransferTo(b.Input, res, want);
                }
        }
    }

    // ---------------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------------

    /// <summary>Whether a computer-run settlement may place <paramref name="def"/> on a tile.</summary>
    internal bool CanSettlementPlace(Settlement s, BuildingDef def, GridPoint p)
    {
        if (!_w.Map.IsLand(p) || _w.Map.BuildingAt(p) >= 0) return false;
        if (!def.AllowsBiome(_w.Map.GetBiome(p))) return false;
        if (def.RequiresCoast && !_w.Map.IsCoast(p)) return false;
        return TerritoryAt(p) == s;
    }

    private GridPoint FindSite(Settlement s, BuildingDef def)
    {
        int r = (int)s.Radius(Cfg);
        GridPoint best = GridPoint.Invalid;
        float bestScore = float.MaxValue;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            var p = new GridPoint(s.Tile.X + dx, s.Tile.Y + dy);
            if (!CanSettlementPlace(s, def, p)) continue;
            // Prefer tiles near the hall, with a little randomness so towns don't look gridded.
            float score = p.Distance(s.Tile) + (Rng.Hash(p.X, p.Y, s.Id) & 7) * 0.3f;
            if (score < bestScore && p.Distance(s.Tile) >= 1.5f) { bestScore = score; best = p; }
        }
        return best;
    }

    private void TryDevelop(Settlement s)
    {
        bool hungry = s.FedRatio < 1f || FoodStock(s) < s.Population * 0.3f;
        var (chosen, site) = ChooseDevelopment(s, hungry);
        if (chosen == null) return;

        // Pay: gold from the owner's purse; materials from the stores, or gold in lieu.
        float cost = chosen.GoldCost;
        foreach (var (res, amount) in chosen.BuildCostList)
            cost += Math.Max(0, amount - s.Stores[res]) * _w.Defs.Resources[res].Value * 2;
        float funds = s.Owner != null ? TreasuryOf(s.Owner) : s.Treasury;
        if (funds < cost) return;
        foreach (var (res, amount) in chosen.BuildCostList) s.Stores.Remove(res, amount);
        AddFunds(s.Owner, s, -cost);
        var b = _w.AddBuildingInternal(chosen, site);
        Attach(b, s);
    }

    /// <summary>What a computer-run town would build next, and where (null if nothing fits).</summary>
    internal (BuildingDef? def, GridPoint site) ChooseDevelopment(Settlement s, bool hungry)
    {
        var beh = s.Owner?.Behaviour ?? PlayerBehaviour;
        var produced = new HashSet<int>(s.BuildingList.SelectMany(b => b.Def.Recipes).SelectMany(r => r.OutputList).Select(o => o.res));

        BuildingDef? chosen = null;
        float bestScore = 0;
        GridPoint site = GridPoint.Invalid;
        foreach (var def in _w.Defs.Buildings)
        {
            if (def.IsTownHallDef() || def.HasService("shipyard")) continue;
            int count = s.BuildingList.Count(b => b.Def == def);
            float score;
            var outputs = def.Recipes.SelectMany(r => r.OutputList).Select(o => o.res).ToList();
            bool isFood = outputs.Any(r => _w.Defs.Resources[r].Food > 0);
            bool isMarket = def.Recipes.Any(r => r.GoldOutput > 0);
            if (def.HasService("barracks")) score = count == 0 ? 0.3f + beh.Defence * 0.8f : 0;
            else if (def.Recipes.Count == 0) continue;
            else if (isMarket)
            {
                float luxuries = def.Recipes.SelectMany(r => r.InputList).Sum(i => s.Stores[i.res] + (produced.Contains(i.res) ? 5 : 0));
                score = luxuries >= 5 ? 1f + beh.Trade : 0;
            }
            else if (def.Recipes.All(r => r.InputList.Length == 0))
            {
                score = isFood ? (hungry ? 3f : 0.9f) : 1f;
                if (outputs.Any(o => _w.Defs.Resources[o].Id is "wood" or "stone")) score += 0.4f;
                // Raw goods a local workshop is waiting for.
                if (s.BuildingList.Any(b => b.Def.AcceptsInput.Length > 0 && outputs.Any(o => b.Def.AcceptsInput[o]))) score += 0.8f;
            }
            else
            {
                bool inputsAvailable = def.Recipes.Any(r => r.InputList.All(i => produced.Contains(i.res) || s.Stores[i.res] >= i.amount * 3));
                if (!inputsAvailable) continue;
                score = 1.3f + (isFood && hungry ? 1.5f : 0) + beh.Trade * 0.3f;
            }
            score /= 1 + count * (isFood ? 0.8f : 1.5f);
            score *= _w.Rng.Range(0.8f, 1.2f);
            if (score <= bestScore) continue;
            var p = FindSite(s, def);
            if (!p.IsValid) continue;
            bestScore = score;
            chosen = def;
            site = p;
        }
        return (chosen, site);
    }

    private float FoodStock(Settlement s) =>
        _w.Defs.Resources.Where(r => r.Food > 0).Sum(r => s.Stores[r.Index] * r.Food);

    // ---------------------------------------------------------------------------------
    // State economy: armies and colonies
    // ---------------------------------------------------------------------------------

    private void StateEconomyTick(State st)
    {
        var beh = st.Behaviour;
        // Upkeep, then recruitment from what's left.
        float upkeep = st.FieldArmy * 0.05f;
        if (st.Treasury >= upkeep) st.Treasury -= upkeep;
        else st.FieldArmy *= 0.95f; // unpaid soldiers desert

        float cap = ArmyCap(st);
        if (st.FieldArmy > cap * 1.2f) st.FieldArmy -= (st.FieldArmy - cap) * 0.1f; // demobilise after a war
        if (st.FieldArmy < cap)
        {
            float budget = st.Treasury * (0.1f + beh.Aggression * 0.25f);
            float n = Math.Min(budget / Cfg.SoldierCost, cap - st.FieldArmy);
            if (n > 0)
            {
                st.FieldArmy += n;
                st.Treasury -= n * Cfg.SoldierCost;
            }
        }

        // Court, fleet and patronage: big treasuries don't pile up forever.
        if (st.Treasury > 1500) st.Treasury -= (st.Treasury - 1500) * 0.15f;

        TryBreakaway(st);

        // New colonies are rare: only a prosperous, crowded realm at peace sends settlers out.
        if (st.SettlementList.Count < Cfg.MaxSettlementsPerState &&
            st.Treasury >= Cfg.FoundCost &&
            st.Capital is { } cap0 && cap0.Population >= cap0.PopulationCap * 0.85f &&
            !Diplomacy.EnemiesOf(st).Any() &&
            _w.Rng.NextFloat() < Cfg.ColonyChance * (0.5f + beh.Expansion))
            TryColonise(st);
    }

    /// <summary>
    /// An overextended realm may lose a distant, poorly-held town to independence.
    /// Keeps a single conqueror from swallowing the whole map.
    /// </summary>
    private void TryBreakaway(State st)
    {
        int excess = Overextension(st);
        if (excess <= 0 || st.Capital == null) return;
        if (_w.Rng.NextFloat() > 0.02f * excess) return;
        var rebel = st.SettlementList
            .Where(s => s != st.Capital && s.Siege == null && !IsNearPlayer(s))
            .OrderByDescending(s => s.Tile.Distance(st.Capital.Tile) - s.Garrison)
            .FirstOrDefault();
        if (rebel == null) return;
        Transfer(rebel, null, Math.Max(rebel.Garrison, 6));
        rebel.Aggressors.Add(st);
    }

    /// <summary>Scores a candidate town site by nearby food land; -1 if unusable.</summary>
    internal float SiteQuality(GridPoint p, float minPlayerDistance = 15f)
    {
        if (!_w.Map.IsLand(p) || !Biomes.Info(_w.Map.GetBiome(p)).IsBuildable || _w.Map.BuildingAt(p) >= 0) return -1;
        if (_w.Map.RegionSizes[_w.Map.RegionAt(p)] < 40) return -1;
        if (NearestSettlementDistance(p) < Cfg.MinSettlementDistance) return -1;
        if (p.Distance(_w.Map.StartTile) < _w.Config.SafeHarbourRadius) return -1;
        if (_w.Buildings.Any(b => b.Tile.Distance(p) < minPlayerDistance)) return -1;
        int fertile = 0, coast = 0;
        for (int dy = -6; dy <= 6; dy += 2)
        for (int dx = -6; dx <= 6; dx += 2)
        {
            var q = new GridPoint(p.X + dx, p.Y + dy);
            if (!_w.Map.InBounds(q)) continue;
            var b = _w.Map.GetBiome(q);
            if (b is Biome.Grassland or Biome.Savanna or Biome.TemperateForest or Biome.Jungle) fertile++;
            if (b == Biome.ShallowWater) coast++;
        }
        if (fertile < 6) return -1;
        if (!PortNear(p).IsValid) return -1; // every town is a port ships can reach
        return fertile + Math.Min(coast, 6) * 0.5f;
    }

    /// <summary>
    /// A coastal tile on the open sea, close enough to <paramref name="p"/> that a ship docked
    /// there can trade with a town hall at <paramref name="p"/>; Invalid if none.
    /// </summary>
    public GridPoint PortNear(GridPoint p)
    {
        var map = _w.Map;
        int region = map.RegionAt(p);
        int ocean = map.MainOceanRegion;
        return map.FindNearest(p, (int)_w.Config.DockReach - 2, q =>
        {
            if (map.RegionAt(q) != region || !map.IsCoast(q)) return false;
            for (int i = 0; i < 4; i++)
            {
                var n = q + GridPoint.Neighbours8[i];
                if (map.IsWater(n) && (ocean < 0 || map.RegionAt(n) == ocean)) return true;
            }
            return false;
        });
    }

    /// <summary>Where the player's ship should dock to trade with a town.</summary>
    public GridPoint DockingSpot(Settlement s) => PortNear(s.Tile);

    private void TryColonise(State st)
    {
        GridPoint best = GridPoint.Invalid;
        float bestScore = 0;
        for (int i = 0; i < 60; i++)
        {
            var from = st.SettlementList[_w.Rng.Next(st.SettlementList.Count)].Tile;
            float a = _w.Rng.Range(0, MathF.Tau), r = _w.Rng.Range(Cfg.MinSettlementDistance, 110);
            var p = new GridPoint(from.X + (int)(MathF.Cos(a) * r), from.Y + (int)(MathF.Sin(a) * r));
            float q = SiteQuality(p);
            if (q < 0) continue;
            q -= r * 0.03f;
            if (q > bestScore) { bestScore = q; best = p; }
        }
        if (!best.IsValid) return;
        st.Treasury -= Cfg.FoundCost;
        if (st.Capital != null) st.Capital.Population = Math.Max(10, st.Capital.Population - 6);
        var s = Found(best, st);
        s.Stores.Add(_w.Defs.ResourceIndex("grain"), 10);
        s.Stores.Add(_w.Defs.ResourceIndex("wood"), 15);
        _w.Events.Post($"The {st.Name} has founded a colony at {s.Name}.", MessageKind.Info);
    }
}

internal static class BuildingDefExtensions
{
    public static bool IsTownHallDef(this BuildingDef d) => d.Services.Contains("town_hall");
    public static bool HasService(this BuildingDef d, string service) => d.Services.Contains(service, StringComparer.OrdinalIgnoreCase);
}
