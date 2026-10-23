using System.Numerics;
using PirateEngine.Core.AI;
using PirateEngine.Core.Combat;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Simulation;

/// <summary>
/// The root of the simulation. Owns the map, all entities and all systems, and exposes a
/// command API (see GameWorld.Commands.cs) that the input layer calls. Contains no rendering
/// or input code, so it can run headless — the test suite drives it exactly this way.
/// </summary>
public sealed partial class GameWorld : IDisposable
{
    /// <summary>Simulation sub-step. Large frame times / high time-scales are split into these.</summary>
    public const float FixedStep = 1f / 30f;

    public TileMap Map { get; }
    public DefinitionDatabase Defs { get; }
    public GameConfig Config => Defs.Config;
    public FogOfWar Fog { get; }
    public GameEvents Events { get; } = new();
    /// <summary>Water pathfinder (ships).</summary>
    public Pathfinder Pathfinder { get; }
    /// <summary>Land pathfinder (troops), with terrain costs.</summary>
    public Pathfinder LandPathfinder { get; }
    public Pathfinder PathfinderFor(NavDomain d) => d == NavDomain.Land ? LandPathfinder : Pathfinder;
    public Rng Rng { get; }
    public WorldGenSettings GenSettings { get; }

    public float Time { get; private set; }
    public int Gold { get; internal set; }
    public Ship Player { get; private set; } = null!;
    public GridPoint StartTile => Map.StartTile;

    public int ShipsSunkByPlayer { get; internal set; }
    public int TotalGoldEarned { get; internal set; }

    private readonly List<Ship> _ships = new();
    private readonly List<LandUnit> _units = new();
    private readonly List<Building> _buildings = new();
    private readonly Dictionary<int, Building> _buildingById = new();
    private readonly List<Projectile> _projectiles = new();
    private readonly List<Wreck> _wrecks = new();
    private readonly List<Effect> _effects = new();
    public IReadOnlyList<Ship> Ships => _ships;
    /// <summary>Squads ashore (embarked squads live in their ship's <see cref="Ship.Troops"/>).</summary>
    public IReadOnlyList<LandUnit> Units => _units;
    public IReadOnlyList<Building> Buildings => _buildings;
    public IReadOnlyList<Projectile> Projectiles => _projectiles;
    public IReadOnlyList<Wreck> Wrecks => _wrecks;
    public IReadOnlyList<Effect> Effects => _effects;

    public CombatSystem Combat { get; }
    public SpawnDirector Spawner { get; }
    /// <summary>States, settlements and diplomacy.</summary>
    public global::PirateEngine.Core.Politics.PoliticsSystem Politics { get; }
    private int _nextId = 1;
    private float _accumulator;
    private GridPoint _lastRevealTile = GridPoint.Invalid;

    internal int NextId() => _nextId++;

    private GameWorld(TileMap map, DefinitionDatabase defs, WorldGenSettings gen, ulong rngSeed)
    {
        Map = map;
        Defs = defs;
        GenSettings = gen;
        Fog = new FogOfWar(map.Width, map.Height);
        Pathfinder = new Pathfinder(map, NavDomain.Water);
        LandPathfinder = new Pathfinder(map, NavDomain.Land);
        Rng = new Rng(rngSeed);
        Combat = new CombatSystem(this);
        Spawner = new SpawnDirector(this);
        Politics = new global::PirateEngine.Core.Politics.PoliticsSystem(this);
    }

    /// <summary>Generates a brand-new world and places the player at the start harbour.</summary>
    public static GameWorld CreateNew(WorldGenSettings gen, DefinitionDatabase defs,
        IProgress<(string, float)>? progress = null)
    {
        var map = WorldGenerator.Generate(gen, progress);
        return CreateWithMap(map, defs, gen);
    }

    /// <summary>
    /// Starts a new game on an existing (e.g. hand-built or pre-generated) map. With
    /// <paramref name="populatePolitics"/>, AI states and settlements are placed on the map.
    /// </summary>
    public static GameWorld CreateWithMap(TileMap map, DefinitionDatabase defs, WorldGenSettings gen, bool populatePolitics = true)
    {
        map.ClearAllBuildings(); // a new game on a reused map starts with no buildings on it
        var world = new GameWorld(map, defs, gen, Rng.Mix((ulong)(uint)gen.Seed ^ 0xC0FFEE));
        world.Gold = defs.Config.StartGold;
        world.SpawnPlayer(map.StartTile, defs.Ship(defs.Config.StartShip));
        foreach (var (id, amount) in defs.Config.StartCargo)
            world.Player.Cargo.Add(defs.ResourceIndex(id), amount);
        foreach (var unitId in defs.Config.StartTroops)
            world.CreateEmbarkedUnit(defs.Unit(unitId), world.Player);
        if (populatePolitics) world.Politics.Populate(gen.Setup);
        world.RevealAroundPlayer(force: true);
        world.Events.Post("Your ship rides at anchor. Dock at the coast to build.", MessageKind.Info);
        return world;
    }

    /// <summary>Creates an empty world around an existing map (used by save loading and tests).</summary>
    internal static GameWorld FromMap(TileMap map, DefinitionDatabase defs, WorldGenSettings gen, ulong rngSeed) =>
        new(map, defs, gen, rngSeed);

    // ---------------------------------------------------------------------------
    // Update loop
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Advances the simulation by <paramref name="dt"/> seconds of game time, in fixed
    /// sub-steps so behaviour is identical at any frame rate or time-scale.
    /// </summary>
    public void Update(float dt)
    {
        _accumulator += Math.Min(dt, 2f); // never try to catch up more than 2 s
        while (_accumulator >= FixedStep)
        {
            Step(FixedStep);
            _accumulator -= FixedStep;
        }
    }

    /// <summary>Fraction of a step not yet simulated — renderers can use it to interpolate.</summary>
    public float Alpha => _accumulator / FixedStep;

    private void Step(float dt)
    {
        Time += dt;
        Events.Clock = Time;
        DrainPathResults();

        // AI decides; the player's own attack command uses the same steering.
        for (int i = 0; i < _ships.Count; i++)
        {
            var s = _ships[i];
            if (s.IsSunk) continue;
            s.TimeSinceDamaged += dt;
            if (s.Brain != null) s.Brain.Update(this, s, dt);
            else if (s == Player && Player.Target != null) UpdatePlayerAttack(dt);
        }
        for (int i = 0; i < _units.Count; i++)
        {
            var u = _units[i];
            if (u.IsDead) continue;
            u.TimeSinceDamaged += dt;
            if (u.Brain != null) u.Brain.Update(this, u, dt);
            else if (u.Faction == Faction.Player) UpdatePlayerUnit(u, dt);
        }

        for (int i = 0; i < _ships.Count; i++)
            if (!_ships[i].IsSunk) MovementSystem.Move(this, _ships[i], dt);
        for (int i = 0; i < _units.Count; i++)
            if (!_units[i].IsDead) MovementSystem.Move(this, _units[i], dt);
        MovementSystem.Separate(this, _ships, 0.8f);
        MovementSystem.Separate(this, _units, 0.55f);

        UpdateDocking();
        UpdateEmbarking();
        Combat.Update(dt);
        UpdateBuildings(dt);
        UpdateWrecks(dt);
        RevealAroundPlayer();
        Spawner.Update(dt);
        Politics.Update(dt);
        CleanupSunk();

        _effects.RemoveAll(e => Time - e.Time > 2f);
    }

    // ---------------------------------------------------------------------------
    // Entities
    // ---------------------------------------------------------------------------

    private void SpawnPlayer(GridPoint tile, ShipClassDef cls)
    {
        Player = new Ship(NextId(), "The Gull", cls, Faction.Player, ShipRole.Player, tile.Center, Defs.ResourceCount)
        {
            Heading = -MathF.PI / 2,
            Allegiance = Politics.PlayerState,
        };
        _ships.Add(Player);
    }

    internal Ship AddAiShip(SpawnDef spawn, GridPoint tile, string name)
    {
        var faction = spawn.Role == ShipRole.Merchant ? Faction.Merchant : Faction.Navy;
        var ship = new Ship(NextId(), name, spawn.Class!, faction, spawn.Role, tile.Center, Defs.ResourceCount)
        {
            Spawn = spawn,
            Heading = Rng.Range(0, MathF.Tau),
        };
        foreach (var (id, amount) in spawn.Cargo) ship.Cargo.Add(Defs.ResourceIndex(id), amount);
        ship.Brain = spawn.Role == ShipRole.Merchant ? new MerchantBrain(tile) : new HunterBrain(tile);
        _ships.Add(ship);
        return ship;
    }

    /// <summary>
    /// Spawns an AI ship (for scripted events, scenarios or mods). Uses the matching entry from
    /// the spawn table if there is one (for its loot and cargo), otherwise a bare definition.
    /// </summary>
    public Ship SpawnAiShip(string shipClassId, ShipRole role, GridPoint tile, string? name = null)
    {
        if (role == ShipRole.Player) throw new ArgumentException("Use the player ship instead.", nameof(role));
        if (!Map.IsWater(tile)) throw new ArgumentException($"{tile} is not water.", nameof(tile));
        var spawn = Config.Spawns.FirstOrDefault(sp => sp.Ship.Equals(shipClassId, StringComparison.OrdinalIgnoreCase) && sp.Role == role)
                    ?? new SpawnDef { Ship = shipClassId, Role = role, Class = Defs.Ship(shipClassId), LootGold = 0 };
        return AddAiShip(spawn, tile, name ?? (role == ShipRole.Merchant ? "Merchantman" : "HMS Patrol"));
    }

    internal void Despawn(Ship s)
    {
        s.IsSunk = true; // removed by CleanupSunk without a wreck
        s.Spawn = null;
    }

    public Ship? FindShip(int id) => _ships.FirstOrDefault(s => s.Id == id && !s.IsSunk);

    /// <summary>Living ship nearest to <paramref name="pos"/> within <paramref name="radius"/>.</summary>
    public Ship? ShipAt(Vector2 pos, float radius = 0.75f)
    {
        Ship? best = null;
        float bestD = radius;
        foreach (var s in _ships)
        {
            if (s.IsSunk) continue;
            float d = Vector2.Distance(s.Position, pos);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    internal void AddProjectile(Projectile p) => _projectiles.Add(p);
    internal List<Projectile> ProjectileList => _projectiles;
    internal void AddEffect(EffectKind kind, Vector2 pos) => _effects.Add(new Effect(kind, pos, Time));

    internal void SinkShip(Ship victim, MobileEntity? killer)
    {
        if (victim.IsSunk) return;
        victim.IsSunk = true;
        victim.Hull = 0;
        AddEffect(EffectKind.Sink, victim.Position);
        Events.RaiseSunk(victim, killer);
        ClearTargetsOn(victim);
        if (victim.Troops.Count > 0)
        {
            int men = victim.TroopsAboard;
            foreach (var t in victim.TroopList) { t.IsDestroyed = true; t.Carrier = null; }
            victim.TroopList.Clear();
            if (victim == Player) Events.Post($"{men} soldiers went down with the ship.", MessageKind.Danger);
        }
        foreach (var u in _units) if (u.PendingEmbark == victim) u.PendingEmbark = null;

        if (victim == Player)
        {
            HandlePlayerSunk();
            return;
        }

        if (killer == Player) ShipsSunkByPlayer++;
        int gold = victim.Spawn?.LootGold ?? 0;
        var cargo = victim.Cargo.ToArray();
        if (gold > 0 || cargo.Any(c => c > 0))
        {
            _wrecks.Add(new Wreck
            {
                Id = NextId(), Position = victim.Position, Gold = gold, Cargo = cargo,
                TimeLeft = Config.WreckLifetime,
            });
        }
        if (killer == Player)
            Events.Post($"{victim.Name} sunk! Sail over the wreckage to salvage it.", MessageKind.Good);
    }

    private void HandlePlayerSunk()
    {
        int lost = (int)(Gold * Config.DeathGoldPenalty);
        Gold -= lost;
        Events.Post($"The {Player.Name} has been sunk! Lost the cargo and {lost} gold.", MessageKind.Danger);

        // Respawn at the start harbour in a fresh starting ship. The old instance stays
        // flagged sunk and is removed by CleanupSunk.
        var old = Player;
        SpawnPlayer(Map.StartTile, Defs.Ship(Config.StartShip));
        Player.Name = old.Name;
        _lastRevealTile = GridPoint.Invalid;
        Events.RaiseRespawned(Player);
    }

    /// <summary>Drops every reference other entities hold to one that has died or left the map.</summary>
    internal void ClearTargetsOn(MobileEntity gone)
    {
        foreach (var s in _ships) if (s.Target == gone) { s.Target = null; s.ClearPath(); }
        foreach (var u in _units) if (u.Target == gone) u.Target = null;
    }

    private void CleanupSunk()
    {
        for (int i = _ships.Count - 1; i >= 0; i--)
            if (_ships[i].IsSunk) _ships.RemoveAt(i);
        for (int i = _units.Count - 1; i >= 0; i--)
            if (_units[i].IsDestroyed) _units.RemoveAt(i);
        _projectiles.RemoveAll(p => p.Dead);
    }

    private void UpdateWrecks(float dt)
    {
        for (int i = _wrecks.Count - 1; i >= 0; i--)
        {
            var w = _wrecks[i];
            w.TimeLeft -= dt;
            if (!Player.IsSunk && Vector2.Distance(w.Position, Player.Position) < 1.1f)
            {
                int goods = 0;
                for (int r = 0; r < w.Cargo.Length; r++)
                {
                    int moved = Player.Cargo.Add(r, w.Cargo[r]);
                    w.Cargo[r] -= moved;
                    goods += moved;
                }
                if (w.Gold > 0 || goods > 0)
                {
                    EarnGold(w.Gold);
                    Events.Post($"Salvaged {w.Gold} gold and {goods} goods.", MessageKind.Good);
                    w.Gold = 0;
                }
                if (!w.IsEmpty && Player.Cargo.Free == 0 && !w.WarnedFull)
                {
                    w.WarnedFull = true;
                    Events.Post("Hold is full — can't salvage the rest.", MessageKind.Warning);
                }
            }
            if (w.IsEmpty || w.TimeLeft <= 0) _wrecks.RemoveAt(i);
        }
    }

    /// <summary>Adds gold from outside the simulation (scripted rewards, scenarios, mods).</summary>
    public void GrantGold(int amount) => EarnGold(amount);

    internal void EarnGold(int amount)
    {
        if (amount <= 0) return;
        Gold += amount;
        TotalGoldEarned += amount;
    }

    private void RevealAroundPlayer(bool force = false)
    {
        var tile = Player.Tile;
        if (force || tile != _lastRevealTile)
        {
            _lastRevealTile = tile;
            Fog.Reveal(tile, Player.Class.SightRadius);
        }
        // Squads ashore scout the interior as they march.
        foreach (var u in _units)
        {
            if (u.Faction != Faction.Player || u.IsDead) continue;
            var t = u.Tile;
            if (!force && t == u.LastRevealTile) continue;
            u.LastRevealTile = t;
            Fog.Reveal(t, u.Def.SightRadius);
        }
    }

    // ---------------------------------------------------------------------------
    // Navigation helpers shared by commands and AI
    // ---------------------------------------------------------------------------

    private readonly List<GridPoint> _scratchPath = new();
    private PathWorker? _worker, _landWorker;

    /// <summary>
    /// When true, long trips (player orders, merchant routes, long marches) are planned on
    /// background threads and applied a step or two later, so huge maps never cause frame
    /// hitches. Off by default so headless runs and tests are fully deterministic; the game
    /// turns it on.
    /// </summary>
    public bool AsyncPathfinding
    {
        get => _worker != null;
        set
        {
            if (value && _worker == null)
            {
                _worker = new PathWorker(Map, NavDomain.Water);
                _landWorker = new PathWorker(Map, NavDomain.Land);
            }
            else if (!value && _worker != null)
            {
                _worker.Dispose();
                _landWorker!.Dispose();
                _worker = _landWorker = null;
            }
        }
    }

    /// <summary>Trips shorter than this (octile tiles) are always planned synchronously.</summary>
    public float AsyncPathMinDistance { get; set; } = 64f;

    public void Dispose() => AsyncPathfinding = false;

    /// <summary>
    /// Plans a path in the entity's own domain (water for ships, land for troops), synchronously
    /// or on a worker. Returns false only for an immediate, certain failure; otherwise
    /// <paramref name="onDone"/> reports the outcome (possibly later). While a request is
    /// pending the entity holds position.
    /// </summary>
    internal bool RequestPath(MobileEntity m, GridPoint goal, int budget, Action<MobileEntity, bool>? onDone = null)
    {
        m.PathTicket = 0;
        m.PathCallback = null;
        var start = m.Tile;
        var worker = m.Domain == NavDomain.Land ? _landWorker : _worker;
        bool longTrip = Math.Max(Math.Abs(goal.X - start.X), Math.Abs(goal.Y - start.Y)) >= AsyncPathMinDistance;
        if (worker == null || !longTrip)
        {
            bool ok = TrySetPath(m, goal, budget);
            onDone?.Invoke(m, ok);
            return ok;
        }
        var pf = PathfinderFor(m.Domain);
        if (!pf.Passable(start) || !pf.Passable(goal) || Map.RegionAt(start) != Map.RegionAt(goal))
        {
            onDone?.Invoke(m, false);
            return false;
        }
        m.ClearPath();
        m.PathTicket = worker.Enqueue(start, goal, budget);
        m.PathCallback = onDone;
        return true;
    }

    private void DrainPathResults()
    {
        Drain(_worker, _ships);
        Drain(_landWorker, _units);
    }

    private void Drain<T>(PathWorker? worker, List<T> owners) where T : MobileEntity
    {
        if (worker == null) return;
        while (worker.TryDequeue(out var r))
        {
            T? m = null;
            foreach (var s in owners) if (s.PathTicket == r.Ticket) { m = s; break; }
            if (m == null || m.IsDead) continue; // superseded by a newer order, or dead
            m.PathTicket = 0;
            var cb = m.PathCallback;
            m.PathCallback = null;
            bool ok = r.Status is PathStatus.Found or PathStatus.AlreadyThere;
            if (r.Status == PathStatus.Found) ApplyPath(m, r.Path);
            cb?.Invoke(m, ok);
        }
    }

    private void ApplyPath(MobileEntity m, IReadOnlyList<GridPoint> path)
    {
        m.PathBuffer.Clear();
        // The path was planned from the centre of the entity's tile. If it is off-centre
        // (mid-leg, or nudged) and can't see the first waypoint directly, go via the centre.
        if (!PathfinderFor(m.Domain).SegmentClear(m.Position, path[0].Center)) m.PathBuffer.Add(m.Tile);
        m.PathBuffer.AddRange(path);
        m.PathIndex = 0;
    }

    /// <summary>Plans a path in the entity's domain, synchronously. Returns false if unreachable.</summary>
    public bool TrySetPath(MobileEntity m, GridPoint goal, int budget = 250_000)
    {
        var status = PathfinderFor(m.Domain).FindPath(m.Tile, goal, _scratchPath, budget);
        if (status == PathStatus.AlreadyThere)
        {
            m.ClearPath();
            return true;
        }
        if (status != PathStatus.Found) return false;
        ApplyPath(m, _scratchPath);
        return true;
    }

    /// <summary>Random water tile in <paramref name="region"/> within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
    internal GridPoint RandomWaterNear(GridPoint centre, float minRadius, float maxRadius, int region, int attempts = 16)
    {
        for (int i = 0; i < attempts; i++)
        {
            float a = Rng.Range(0, MathF.Tau), r = Rng.Range(minRadius, maxRadius);
            var p = new GridPoint(centre.X + (int)(MathF.Cos(a) * r), centre.Y + (int)(MathF.Sin(a) * r));
            if (Map.InBounds(p) && Map.Region[Map.Index(p)] == region) return p;
        }
        return GridPoint.Invalid;
    }

    /// <summary>Nearest water tile in <paramref name="region"/> to an arbitrary point.</summary>
    internal GridPoint NearestWaterInRegion(GridPoint p, int region, int maxRadius = 12) =>
        Map.FindNearest(p, maxRadius, q => Map.Region[Map.Index(q)] == region);

    /// <summary>Danger level (0 at the start harbour, 1 in the far seas) at a tile.</summary>
    public float DangerAt(GridPoint p)
    {
        float d = p.Distance(Map.StartTile);
        float scale = Math.Min(Map.Width, Map.Height) * 0.45f;
        return Math.Clamp((d - Config.SafeHarbourRadius) / Math.Max(1f, scale - Config.SafeHarbourRadius), 0f, 1f);
    }

    /// <summary>
    /// Whether two entities fight. The sea layer: the Navy hunts the player, merchants only fight
    /// once provoked, and neither takes part in state politics. Everyone else (the player and
    /// states' troops) follows diplomacy: hostile when their states are at war, and militia of an
    /// independent town are hostile to whoever is attacking it.
    /// </summary>
    public static bool IsHostile(MobileEntity a, MobileEntity b)
    {
        if (a == b) return false;
        bool aSea = a.Faction is Faction.Navy or Faction.Merchant, bSea = b.Faction is Faction.Navy or Faction.Merchant;
        if (aSea || bSea)
        {
            if (a.Faction == b.Faction) return false;
            if (a.Faction == Faction.Player) return b.Faction == Faction.Navy || b.Provoked;
            if (b.Faction == Faction.Player) return a.Faction == Faction.Navy || a.Provoked;
            return false; // Navy, merchants and states' troops leave each other alone
        }
        return global::PirateEngine.Core.Politics.PoliticsSystem.AreHostile(a, b);
    }
}
