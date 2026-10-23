using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Util;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Politics;

// The political map a playthrough starts with, built from a setup.json preset: established
// states and towns already at their natural size, dense heartlands, a sparse frontier, empty
// wilderness, and long-standing friendships and grudges between the realms.
public sealed partial class PoliticsSystem
{
    /// <summary>The preset this world was populated from (null if never populated).</summary>
    public string? SetupId { get; private set; }

    /// <summary>Populates the world from a setup preset (null = the default preset).</summary>
    public void Populate(string? presetId = null) => Populate(_w.Defs.SetupPreset(presetId));

    public void Populate(WorldSetupDef preset)
    {
        SetupId = preset.Id;
        new SetupBuilder(this, preset).Run();
    }

    /// <summary>Settlement-density value (0..1) at a tile, as the setup generator saw it; 0 if unknown.</summary>
    public float DensityAt(GridPoint p) => _densityAt?.Invoke(p) ?? 0f;
    private Func<GridPoint, float>? _densityAt;

    /// <summary>Opinion below which two states count as old rivals.</summary>
    public const float RivalryThreshold = -35f;

    /// <summary>
    /// Builds the starting political map. Steps: candidate town sites (fertile land by the open
    /// sea) → heartland centres → a density field → the start port → state capitals (hand-placed
    /// ones first, then the great realms in heartlands, then minor ones in the frontier) → each
    /// realm's towns, shared out round-robin within its radius → free cities and independent
    /// towns by density → every town established at its level with balanced food, buildings,
    /// stores and garrison → starting diplomacy.
    /// </summary>
    private sealed class SetupBuilder
    {
        private sealed class Site
        {
            public GridPoint P;
            public float Quality;
            public int Fertile;
            public float Density;
            public bool Used;
        }

        private sealed class Realm
        {
            public State State = null!;
            public StateSizeDef Size = null!;
            public Settlement Capital = null!;
            public int Wanted;
            public Vector2 Centre => Capital.Tile.Center;
        }

        private readonly PoliticsSystem _p;
        private readonly GameWorld _w;
        private readonly WorldSetupDef _d;
        private readonly TileMap _m;
        private readonly Rng _r;
        private readonly float _scale;
        private readonly List<Site> _sites = new();
        private readonly List<(Vector2 c, float r)> _heartlands = new();
        private readonly List<Realm> _realms = new();
        private readonly Dictionary<Settlement, int> _targetLevel = new();
        private readonly List<string> _palette;
        private Noise _noise = null!;

        public SetupBuilder(PoliticsSystem p, WorldSetupDef d)
        {
            _p = p;
            _w = p._w;
            _d = d;
            _m = _w.Map;
            _r = _w.Rng;
            _scale = Math.Clamp(MathF.Pow(_m.Count / (float)Math.Max(1, d.ReferenceArea), 0.75f), 0.2f, 12f);
            _palette = p.Cfg.StateColors.ToList();
        }

        private int Scaled(int count) => count <= 0 ? 0 : Math.Max(1, (int)MathF.Round(count * _scale));

        public void Run()
        {
            FindSites();
            if (_sites.Count == 0) return;
            _noise = new Noise(Rng.Mix((ulong)(uint)_m.Seed ^ 0x5E771E));

            var specs = ExpandSpecs();
            PlaceHeartlands(specs);
            foreach (var s in _sites) s.Density = DensityAt(s.P);
            _p._densityAt = DensityAt;

            PlaceStartPort();
            foreach (var spec in specs) PlaceCapital(spec);
            GrowRealms();
            PlaceIndependents();

            // Establish big towns first so they get first pick of shared land.
            foreach (var s in _targetLevel.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList())
                Establish(s, _targetLevel[s]);

            foreach (var realm in _realms) FinishState(realm);
            Relations();

            // One quiet tick so stores, food and incomes show real values from the first frame.
            _p.EconomyTick();
            foreach (var s in _p._settlements) s.FoundedAt = -_r.Range(1800f, 36000f); // "an old town"
        }

        // ---------------------------------------------------------------------------------
        // Sites and density
        // ---------------------------------------------------------------------------------

        /// <summary>Distance (in tiles, up to <paramref name="max"/>) from each land tile to a dockable open-sea coast.</summary>
        private byte[] PortDistance(int max)
        {
            var dist = new byte[_m.Count];
            Array.Fill(dist, byte.MaxValue);
            var queue = new Queue<int>();
            int ocean = _m.MainOceanRegion;
            for (int y = 0; y < _m.Height; y++)
            for (int x = 0; x < _m.Width; x++)
            {
                if (!_m.IsLand(x, y)) continue;
                for (int k = 0; k < 4; k++)
                {
                    var n = new GridPoint(x, y) + GridPoint.Neighbours8[k];
                    if (_m.IsWater(n) && (ocean < 0 || _m.RegionAt(n) == ocean))
                    {
                        int i = _m.Index(x, y);
                        dist[i] = 0;
                        queue.Enqueue(i);
                        break;
                    }
                }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (dist[i] >= max) continue;
                var p = _m.PointOf(i);
                for (int k = 0; k < 4; k++)
                {
                    var n = p + GridPoint.Neighbours8[k];
                    if (!_m.IsLand(n)) continue;
                    int j = _m.Index(n);
                    if (dist[j] != byte.MaxValue) continue;
                    dist[j] = (byte)(dist[i] + 1);
                    queue.Enqueue(j);
                }
            }
            return dist;
        }

        /// <summary>One candidate town site per small cell: buildable, fertile land close to the open sea.</summary>
        private void FindSites()
        {
            int reach = Math.Max(1, (int)_w.Config.DockReach - 2);
            var port = PortDistance(reach);
            const int cell = 5;
            for (int cy = 0; cy < _m.Height / cell; cy++)
            for (int cx = 0; cx < _m.Width / cell; cx++)
            {
                Site? best = null;
                for (int k = 0; k < 3; k++)
                {
                    uint h = Rng.Hash(cx, cy, k + 11);
                    var p = new GridPoint(cx * cell + (int)(h % cell), cy * cell + (int)((h >> 8) % cell));
                    if (!_m.InBounds(p) || !_m.IsLand(p) || port[_m.Index(p)] > reach) continue;
                    if (!Biomes.Info(_m.GetBiome(p)).IsBuildable || _m.BuildingAt(p) >= 0) continue;
                    if (_m.RegionSizes[_m.RegionAt(p)] < 40) continue;
                    var (fertile, coast) = Fertility(p);
                    if (fertile < 2) continue;
                    float q = fertile + Math.Min(coast, 6) * 0.5f;
                    if (best == null || q > best.Quality) best = new Site { P = p, Quality = q, Fertile = fertile };
                }
                if (best != null) _sites.Add(best);
            }
        }

        private (int fertile, int coast) Fertility(GridPoint p)
        {
            int fertile = 0, coast = 0;
            for (int dy = -6; dy <= 6; dy += 2)
            for (int dx = -6; dx <= 6; dx += 2)
            {
                var q = new GridPoint(p.X + dx, p.Y + dy);
                if (!_m.InBounds(q)) continue;
                var b = _m.GetBiome(q);
                if (b is Biome.Grassland or Biome.Savanna or Biome.TemperateForest or Biome.Jungle) fertile++;
                if (b == Biome.ShallowWater) coast++;
            }
            return (fertile, coast);
        }

        private List<(StateSpecDef spec, StateSizeDef size)> ExpandSpecs()
        {
            var list = new List<(StateSpecDef, StateSizeDef)>();
            foreach (var spec in _d.States)
            {
                var size = _d.Sizes[spec.Size];
                int n = spec.IsExplicit ? Math.Min(1, spec.Count) : Scaled(spec.Count);
                for (int i = 0; i < n; i++) list.Add((spec, size));
            }
            // Hand-placed states first, then the largest realms, so they get the best land.
            return list.OrderByDescending(x => x.Item1.At != null)
                       .ThenByDescending(x => x.Item1.IsExplicit)
                       .ThenByDescending(x => x.Item2.Heartland)
                       .ThenByDescending(x => x.Item2.Radius)
                       .ToList();
        }

        /// <summary>
        /// Heartland centres: hand-placed heartland states bring their own; the rest go where
        /// candidate sites cluster most thickly, spaced apart and away from the player's start.
        /// </summary>
        private void PlaceHeartlands(List<(StateSpecDef spec, StateSizeDef size)> specs)
        {
            var den = _d.Density;
            float RandRadius() => _r.Range(den.HeartlandRadius[0], den.HeartlandRadius[1]);
            foreach (var (spec, size) in specs)
            {
                if (spec.At == null || !size.Heartland) continue;
                _heartlands.Add((AtPoint(spec.At), RandRadius()));
            }
            int wanted = Math.Max(_heartlands.Count, Scaled(den.Heartlands));
            if (wanted == 0) return;

            // Richness: candidate sites within 50 tiles (bucketed for speed).
            const int bucket = 25;
            int bw = _m.Width / bucket + 1, bh = _m.Height / bucket + 1;
            var counts = new int[bw * bh];
            foreach (var s in _sites) counts[(s.P.Y / bucket) * bw + s.P.X / bucket]++;
            float Richness(GridPoint p)
            {
                int bx = p.X / bucket, by = p.Y / bucket, sum = 0;
                for (int y = by - 2; y <= by + 2; y++)
                for (int x = bx - 2; x <= bx + 2; x++)
                    if ((uint)x < bw && (uint)y < bh) sum += counts[y * bw + x];
                return sum;
            }

            var ranked = _sites.Where(s => s.Fertile >= 6)
                .Select(s => (s, rich: Richness(s.P) * _r.Range(0.75f, 1.25f)))
                .OrderByDescending(x => x.rich).Select(x => x.s).ToList();
            float spacing = den.HeartlandSpacing * den.HeartlandRadius[1];
            for (int attempt = 0; attempt < 6 && _heartlands.Count < wanted; attempt++)
            {
                foreach (var s in ranked)
                {
                    if (_heartlands.Count >= wanted) break;
                    var c = s.P.Center;
                    if (s.P.Distance(_m.StartTile) < den.HeartlandClearance) continue;
                    if (_heartlands.Any(h => Vector2.Distance(h.c, c) < spacing)) continue;
                    _heartlands.Add((c, RandRadius()));
                }
                spacing *= 0.8f; // crowded or small map: allow heartlands closer together
            }
        }

        private Vector2 AtPoint(float[] at) => new(at[0] * (_m.Width - 1), at[1] * (_m.Height - 1));

        private float DensityAt(GridPoint p)
        {
            var c = p.Center;
            float h = 0;
            foreach (var (hc, hr) in _heartlands)
            {
                float d = Vector2.Distance(c, hc) / hr;
                h += MathF.Exp(-1.1f * d * d);
            }
            float scale = Math.Max(10f, _d.Density.NoiseScale);
            float n = _d.Density.NoiseWeight * (_noise.Fbm(p.X / scale, p.Y / scale, 3) + 1) * 0.5f;
            return Math.Clamp(h + n - 0.12f, 0f, 1f);
        }

        private float Spacing(float density) => _d.Spacing.Sparse + (_d.Spacing.Dense - _d.Spacing.Sparse) * density;

        /// <summary>Whether a town may be founded at a site given everything placed so far.</summary>
        private bool Free(Site s, float clearance)
        {
            if (s.Used || _m.BuildingAt(s.P) >= 0) return false;
            if (s.P.Distance(_m.StartTile) < clearance) return false;
            float spacing = Spacing(s.Density);
            foreach (var t in _p._settlements)
                if (t.Tile.Distance(s.P) < spacing) return false;
            return true;
        }

        private Settlement Found(Site site, State? owner, int level, string? name = null)
        {
            site.Used = true;
            var s = _p.Found(site.P, owner, name, population: 5);
            _targetLevel[s] = level;
            return s;
        }

        /// <summary>Level from a list, denser places favouring later (bigger) entries.</summary>
        private int PickLevel(IReadOnlyList<string> levels, float density)
        {
            float u = MathF.Pow(_r.NextFloat(), 1.6f - density);
            int i = Math.Min(levels.Count - 1, (int)(u * levels.Count));
            return _w.Defs.LevelIndex(levels[i]);
        }

        // ---------------------------------------------------------------------------------
        // Placement
        // ---------------------------------------------------------------------------------

        /// <summary>A friendly independent harbour near the start, so the first port of call is close.</summary>
        private void PlaceStartPort()
        {
            if (_d.StartPort is not { Enabled: true } sp) return;
            float lo = sp.Distance[0], hi = sp.Distance[1], mid = (lo + hi) / 2;
            var site = _sites
                .Where(s => s.Fertile >= 3 && Free(s, lo))
                .Select(s => (s, d: s.P.Distance(_m.StartTile)))
                .Where(x => x.d <= hi)
                .OrderBy(x => MathF.Abs(x.d - mid) - x.s.Quality * 0.4f)
                .Select(x => x.s).FirstOrDefault();
            if (site == null) return;
            Found(site, null, _w.Defs.LevelIndex(sp.Level), sp.Name);
        }

        private StateBehaviourDef PickBehaviour(StateSpecDef spec)
        {
            if (spec.Behaviour != null) return _w.Defs.Behaviour(spec.Behaviour);
            var all = _p.Cfg.Behaviours;
            var used = _realms.Select(r => r.State.Behaviour).ToHashSet();
            var fresh = all.Where(b => !used.Contains(b)).ToList();
            var pool = fresh.Count > 0 ? fresh : all;
            return pool[_r.Next(pool.Count)];
        }

        private string NextColor()
        {
            var used = _p._states.Select(s => s.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var c = _palette.FirstOrDefault(x => !used.Contains(x));
            if (c != null) return c;
            uint h = Rng.Hash(_p._states.Count, 7, 99);
            return $"#{(h & 0x7F) + 0x60:X2}{((h >> 8) & 0x7F) + 0x60:X2}{((h >> 16) & 0x7F) + 0x60:X2}";
        }

        private void PlaceCapital((StateSpecDef spec, StateSizeDef size) item)
        {
            var (spec, size) = item;
            float myR = size.Radius;
            bool ClearOfRealms(Site s, float factor) =>
                _realms.All(o => Vector2.Distance(o.Centre, s.P.Center) >= (myR + o.Size.Radius) * factor);

            Site? site = null;
            if (spec.At != null)
            {
                var at = AtPoint(spec.At);
                site = _sites.Where(s => s.Fertile >= 3 && Free(s, _d.CapitalClearance))
                    .OrderBy(s => Vector2.Distance(s.P.Center, at)).FirstOrDefault();
            }
            else if (size.Heartland && _heartlands.Count > 0)
            {
                // An empty heartland if any is left; otherwise share one with an existing realm.
                var empty = _heartlands.Where(h => !_realms.Any(r => Vector2.Distance(r.Centre, h.c) < h.r * 0.6f))
                    .OrderByDescending(h => h.r).ToList();
                if (empty.Count > 0)
                {
                    var (c, r) = empty[0];
                    site = _sites.Where(s => s.Fertile >= 6 && Vector2.Distance(s.P.Center, c) <= r * 0.4f &&
                                             Free(s, _d.CapitalClearance) && ClearOfRealms(s, 0.5f))
                        .OrderByDescending(s => s.Quality * 0.15f + s.Density * 2 - Vector2.Distance(s.P.Center, c) * 0.02f)
                        .FirstOrDefault();
                }
                if (site == null)
                {
                    foreach (var factor in new[] { 0.6f, 0.45f, 0.35f })
                    {
                        site = _sites.Where(s => s.Fertile >= 6 && s.Density >= 0.35f && Free(s, _d.CapitalClearance) && ClearOfRealms(s, factor))
                            .OrderByDescending(s => s.Density + s.Quality * 0.03f + _r.Range(0f, 0.3f))
                            .FirstOrDefault();
                        if (site != null) break;
                    }
                }
            }
            if (site == null)
            {
                // Minor realms: out in the frontier, between the great powers.
                foreach (var (lo, factor) in new[] { (0.15f, 0.6f), (0.05f, 0.45f), (0f, 0.35f) })
                {
                    site = _sites.Where(s => s.Fertile >= 5 && s.Density >= lo && Free(s, _d.CapitalClearance) && ClearOfRealms(s, factor))
                        .OrderByDescending(s => 1 - MathF.Abs(s.Density - 0.4f) + s.Quality * 0.03f + _r.Range(0f, 0.6f))
                        .FirstOrDefault();
                    if (site != null) break;
                }
            }
            if (site == null) return; // the map has no room for it

            var beh = PickBehaviour(spec);
            string capitalName = spec.Capital ?? _p.NewSettlementName();
            _p._usedNames.Add(capitalName);
            var st = _p.CreateState(beh, capitalName, spec.Color ?? NextColor(), spec.Name);
            st.Treasury = size.Treasury;
            var capital = Found(site, st, _w.Defs.LevelIndex(size.CapitalLevel), capitalName);
            st.Capital = capital;
            _realms.Add(new Realm { State = st, Size = size, Capital = capital, Wanted = _r.Next(size.Towns[0], size.Towns[1] + 1) - 1 });
        }

        /// <summary>
        /// Each realm claims towns within its radius, nearer its own capital than any other
        /// (weighted by realm size), taking turns so neighbours share contested land.
        /// </summary>
        private void GrowRealms()
        {
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var realm in _realms.Where(r => r.Wanted > 0).OrderByDescending(r => r.Wanted))
                {
                    var c = realm.Centre;
                    float rad = realm.Size.Radius;
                    Site? best = null;
                    float bestScore = float.MinValue;
                    foreach (var s in _sites)
                    {
                        float d = Vector2.Distance(s.P.Center, c);
                        if (d > rad || s.Fertile < 4 || s.Density < _d.Density.Wilderness * 0.5f) continue;
                        if (!Free(s, _d.TownClearance)) continue;
                        float mine = d / rad;
                        if (_realms.Any(o => o != realm && Vector2.Distance(s.P.Center, o.Centre) / o.Size.Radius < mine)) continue;
                        float score = s.Quality * 0.1f + s.Density * 2 - mine * 1.2f + _r.Range(0f, 0.6f);
                        if (score > bestScore) { bestScore = score; best = s; }
                    }
                    if (best == null) { realm.Wanted = 0; continue; }
                    Found(best, realm.State, PickLevel(realm.Size.TownLevels, best.Density));
                    realm.Wanted--;
                    progress = true;
                }
            }
        }

        private bool OutsideRealms(Site s, float factor) =>
            _realms.All(r => Vector2.Distance(s.P.Center, r.Centre) > r.Size.Radius * factor);

        /// <summary>Free cities in the gaps between realms, then independent towns where people live.</summary>
        private void PlaceIndependents()
        {
            var ind = _d.Independents;
            int cityLevel = _w.Defs.LevelIndex(ind.FreeCityLevel);
            for (int i = 0, n = Scaled(ind.FreeCities); i < n; i++)
            {
                Site? site = null;
                foreach (var (minDensity, factor) in new[] { (0.45f, 0.85f), (0.3f, 0.7f), (0.15f, 0.55f) })
                {
                    site = _sites.Where(s => s.Fertile >= 6 && s.Density >= minDensity && Free(s, _d.TownClearance) && OutsideRealms(s, factor))
                        .OrderByDescending(s => s.Density + s.Quality * 0.03f + _r.Range(0f, 0.4f)).FirstOrDefault();
                    if (site != null) break;
                }
                if (site == null) break;
                Found(site, null, cityLevel);
            }

            // Weighted by density: thick where the heartlands fade into the frontier, rare beyond.
            int count = Scaled(ind.Count);
            for (int i = 0; i < count; i++)
            {
                var pool = _sites.Where(s => s.Fertile >= 4 && s.Density >= _d.Density.Wilderness && OutsideRealms(s, 0.7f) && Free(s, _d.TownClearance)).ToList();
                if (pool.Count == 0) break;
                float total = pool.Sum(s => s.Density + 0.05f);
                float pick = _r.NextFloat() * total;
                Site chosen = pool[^1];
                foreach (var s in pool)
                {
                    pick -= s.Density + 0.05f;
                    if (pick <= 0) { chosen = s; break; }
                }
                Found(chosen, null, PickLevel(ind.Levels, chosen.Density));
            }
        }

        // ---------------------------------------------------------------------------------
        // Establishing a town at its natural size
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Gives a town the population, buildings, food supply, stores and garrison of a place
        /// that has stood for generations at the given level — near (not at) the population its
        /// buildings support, so it grows only slowly from here.
        /// </summary>
        private void Establish(Settlement s, int level)
        {
            var cfg = _p.Cfg;
            var levels = cfg.Levels;
            level = Math.Clamp(level, 0, levels.Count - 1);
            int lo = levels[level].MinPopulation;
            int hi = level + 1 < levels.Count ? levels[level + 1].MinPopulation : (int)(lo * 1.8f + 40);
            int target = Math.Max(8, (int)_r.Range(lo + (hi - lo) * 0.15f, lo + (hi - lo) * 0.85f));
            float fill = _r.Range(_d.Fill[0], _d.Fill[1]);

            s.Population = target;
            _p.RefreshLevel(s);
            var lv = s.Level(cfg);
            int wantBuildings = Math.Clamp((int)MathF.Ceiling((target / fill - cfg.PopBase - s.LevelIndex * 10) / (float)cfg.PopPerBuilding),
                0, lv.MaxBuildings);

            for (int i = 0; i < lv.MaxBuildings; i++)
            {
                bool hungry = FoodSupply(s) < _p.FoodNeed(s) * 1.08f;
                if (!hungry && s.BuildingList.Count >= wantBuildings) break;
                var (def, site) = _p.ChooseDevelopment(s, hungry);
                if (def == null) break;
                _p.Attach(_w.AddBuildingInternal(def, site), s);
            }

            // Live within the means of the land and the buildings.
            int cap = cfg.PopBase + cfg.PopPerBuilding * s.BuildingList.Count + s.LevelIndex * 10;
            float fedBy = FoodSupply(s) / 1.04f / Math.Max(0.01f, cfg.FoodPerPop);
            s.Population = Math.Max(5, (int)Math.Min(target, Math.Min(cap * fill, fedBy)));
            _p.RefreshLevel(s);
            s.PopulationCap = cfg.PopBase + cfg.PopPerBuilding * s.BuildingList.Count + s.LevelIndex * 10;
            s.FedRatio = 1f;

            // Stores: a few days' food and some of everything it makes.
            int need = _p.FoodNeed(s);
            var foods = _w.Defs.Resources.Where(r => r.Food > 0).ToList();
            var made = s.BuildingList.SelectMany(b => b.Def.Recipes).SelectMany(r => r.OutputList).Select(o => o.res).Distinct().ToList();
            var madeFood = foods.Where(f => made.Contains(f.Index)).ToList();
            if (madeFood.Count == 0) madeFood = foods.Where(f => f.Id == "grain").ToList();
            foreach (var f in madeFood) s.Stores.Add(f.Index, Math.Min(s.Stores.Free, need * 6 / Math.Max(1, madeFood.Count)));
            foreach (var res in made)
                if (_w.Defs.Resources[res].Food <= 0) s.Stores.Add(res, Math.Min(s.Stores.Free, _r.Next(8, 25)));
            foreach (var id in new[] { "wood", "stone" })
                if (_w.Defs.TryResource(id, out var rd)) s.Stores.Add(rd.Index, Math.Min(s.Stores.Free, _r.Next(10, 25)));

            s.Garrison = _p.GarrisonTarget(s);
            s.Treasury = s.Owner == null ? 150 + s.Population * 2.5f : _p.PurseCap(s) * _r.Range(0.5f, 0.8f);
        }

        /// <summary>Food per economy tick: foraging plus what the workshops add (net of food they use).</summary>
        private float FoodSupply(Settlement s)
        {
            float tick = _p.Cfg.EconomyInterval;
            float total = _p.Forage(s);
            foreach (var b in s.BuildingList)
            {
                if (b.Def.Recipes.Count == 0) continue;
                var r = b.Def.Recipes[0];
                float made = r.OutputList.Sum(o => _w.Defs.Resources[o.res].Food * o.amount);
                float used = r.InputList.Sum(i => _w.Defs.Resources[i.res].Food * i.amount);
                if (made > 0) total += (made - used) * tick / r.CycleSeconds;
            }
            return total;
        }

        private void FinishState(Realm realm)
        {
            var st = realm.State;
            st.InitialTowns = st.SettlementList.Count;
            st.Capital = realm.Capital; // the seat of government stays where it was founded
            st.FieldArmy = _p.ArmyCap(st) * _r.Range(0.7f, 0.9f);
            // The realms have just settled their last quarrels: nobody goes looking for a new
            // war for a while unless something (usually the player) gives them a reason.
            st.NextWarAllowed = _r.Range(1f, 3f) * _p.Cfg.WarCooldown;
        }

        // ---------------------------------------------------------------------------------
        // Diplomacy: natural opinions, alliances, rivalries, standing wars
        // ---------------------------------------------------------------------------------

        private void Relations()
        {
            var dip = _p.Diplomacy;
            var states = _realms.Select(r => r.State).ToList();
            var rel = _d.Relations;
            var pairs = new List<(State a, State b, bool neighbours)>();
            for (int i = 0; i < states.Count; i++)
            for (int j = i + 1; j < states.Count; j++)
            {
                var a = states[i];
                var b = states[j];
                var ba = a.Behaviour;
                var bb = b.Behaviour;
                bool neighbours = _p.BorderDistance(a, b) < 90;
                float baseline = 10 * (ba.Peacefulness + bb.Peacefulness) + 8 * (ba.Trade + bb.Trade)
                                 - 25 * Math.Max(ba.Aggression, bb.Aggression) * (neighbours ? 1f : 0.3f)
                                 - (neighbours ? 8 : 0) - 6 + _r.Range(-rel.Spread, rel.Spread);
                var r = dip.Get(a, b);
                r.Baseline = Math.Clamp(baseline, -90, 90);
                pairs.Add((a, b, neighbours));
            }

            // Old rivalries between neighbours who already dislike each other.
            var rivals = pairs.Where(x => x.neighbours).OrderBy(x => dip.Get(x.a, x.b).Baseline).Take(rel.Rivalries).ToList();
            foreach (var (a, b, _) in rivals)
            {
                var r = dip.Get(a, b);
                r.Baseline = Math.Min(r.Baseline - 30, RivalryThreshold - 10);
            }

            // Alliances between friendly, peaceable states (at most one each to begin with).
            int alliances = 0;
            foreach (var (a, b, _) in pairs.OrderByDescending(x => dip.Get(x.a, x.b).Baseline))
            {
                if (alliances >= rel.Alliances) break;
                var r = dip.Get(a, b);
                if (r.Baseline <= 0 || Math.Min(a.Behaviour.Peacefulness, b.Behaviour.Peacefulness) < 0.35f) continue;
                if (dip.AlliesOf(a).Any() || dip.AlliesOf(b).Any()) continue;
                r.Stance = Stance.Alliance;
                r.Baseline = Math.Min(90, r.Baseline + 15);
                r.Since = -_r.Range(1800f, 20000f);
                alliances++;
            }

            // Wars already under way, between rivals.
            foreach (var (a, b, _) in rivals.Take(rel.Wars))
            {
                var r = dip.Get(a, b);
                if (r.Stance != Stance.Peace) continue;
                r.Stance = Stance.War;
                r.Since = -_r.Range(600f, 6000f);
                r.WarWeariness = _r.Range(5f, 25f);
            }

            foreach (var r in dip.All) if (!r.A.IsPlayer && !r.B.IsPlayer) r.Opinion = r.Baseline;

            // How each realm regards newcomers like you.
            foreach (var st in states)
            {
                var b = st.Behaviour;
                var r = dip.Get(_p.PlayerState, st);
                r.Baseline = 5 + b.Trade * 10 + b.Peacefulness * 5 - b.Aggression * 15 + _r.Range(-5f, 5f);
                r.Opinion = r.Baseline;
            }
        }
    }
}
