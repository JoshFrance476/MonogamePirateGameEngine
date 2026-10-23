using System.Numerics;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Politics;

/// <summary>
/// Computer-controlled states and settlements, and the player's place among them.
/// <para>
/// Everything here runs on slow ticks (economy every ~10 s, diplomacy every ~20 s, sieges every
/// ~3 s), so it costs almost nothing however many settlements exist. Detail follows the player:
/// sieges and defences far away are resolved by numbers, while near the player garrisons muster
/// as real squads and a town falls only when hostile troops hold its town hall.
/// </para>
/// Split across files: core (this), Economy, Diplomacy, Military, Setup.
/// </summary>
public sealed partial class PoliticsSystem
{
    private readonly GameWorld _w;
    public Diplomacy Diplomacy { get; } = new();
    private readonly List<State> _states = new();
    private readonly List<Settlement> _settlements = new();
    public IReadOnlyList<State> States => _states;
    public IReadOnlyList<Settlement> Settlements => _settlements;
    /// <summary>The player's state (exists from the start; founded with the first settlement).</summary>
    public State PlayerState { get; }
    public bool Enabled { get; set; } = true;

    private PoliticsConfig Cfg => _w.Config.Politics;
    private float _econTimer, _diploTimer, _siegeTimer;
    private int _nextStateId = 1;
    private readonly HashSet<string> _usedNames = new();

    public static readonly StateBehaviourDef PlayerBehaviour = new()
    {
        Id = "player", Name = "Player", Description = "Your own state.",
        Aggression = 0.5f, Expansion = 0.5f, Trade = 0.5f, Defence = 0.5f, Peacefulness = 0.5f,
    };
    public const string PlayerColor = "#F5D76E";

    public PoliticsSystem(GameWorld world)
    {
        _w = world;
        PlayerState = new State(_nextStateId++, "Brethren of the Coast", PlayerColor, PlayerBehaviour, isPlayer: true, Diplomacy)
        {
            Founded = false,
        };
        _states.Add(PlayerState);
    }

    // ---------------------------------------------------------------------------------
    // Hostility
    // ---------------------------------------------------------------------------------

    /// <summary>Political hostility between two non-sea entities (see GameWorld.IsHostile).</summary>
    public static bool AreHostile(MobileEntity a, MobileEntity b)
    {
        var sa = a.Allegiance;
        var sb = b.Allegiance;
        if (sa == null && sb == null) return false; // two independents' militia
        if (sa == null) return a.HomeSettlement != null && a.HomeSettlement.Aggressors.Contains(sb!);
        if (sb == null) return b.HomeSettlement != null && b.HomeSettlement.Aggressors.Contains(sa);
        if (sa == sb) return false;
        return sa.Diplomacy.AtWar(sa, sb);
    }

    /// <summary>True if <paramref name="state"/> is currently fighting <paramref name="s"/>'s owner (or attacking it, if independent).</summary>
    public bool IsHostileTo(Settlement s, State? state)
    {
        if (state == null) return false;
        if (s.Owner == null) return s.Aggressors.Contains(state);
        return state != s.Owner && Diplomacy.AtWar(state, s.Owner);
    }

    /// <summary>True if a squad is attacking <paramref name="s"/> (hostile to its owner or one of its aggressors).</summary>
    public bool ThreatensSettlement(Settlement s, MobileEntity m) =>
        !m.IsDead && m.HomeSettlement != s && IsHostileTo(s, m.Allegiance);

    // ---------------------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------------------

    public State? FindState(int id) => _states.FirstOrDefault(s => s.Id == id);
    public Settlement? FindSettlement(int id) => _settlements.FirstOrDefault(s => s.Id == id);
    public IEnumerable<State> ActiveStates => _states.Where(s => s.IsActive);

    /// <summary>The settlement whose territory covers a tile: the nearest town hall on the same island within its radius.</summary>
    public Settlement? TerritoryAt(GridPoint p)
    {
        if (!_w.Map.IsLand(p)) return null;
        int region = _w.Map.RegionAt(p);
        Settlement? best = null;
        float bestD = float.MaxValue;
        foreach (var s in _settlements)
        {
            if (s.Region != region) continue;
            float d = p.Distance(s.Tile);
            if (d <= s.Radius(Cfg) && d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    public float NearestSettlementDistance(GridPoint p) =>
        _settlements.Count == 0 ? float.MaxValue : _settlements.Min(s => s.Tile.Distance(p));

    /// <summary>Distance from a point to the nearest of the player's ship and squads ashore.</summary>
    public float DistanceToPlayer(Vector2 p)
    {
        float d = _w.Player.IsSunk ? float.MaxValue : Vector2.Distance(_w.Player.Position, p);
        foreach (var u in _w.Units)
            if (u.Faction == Faction.Player && !u.IsMilitia && !u.IsDead) d = Math.Min(d, Vector2.Distance(u.Position, p));
        return d;
    }

    public bool IsNearPlayer(Settlement s) => DistanceToPlayer(s.Tile.Center) <= Cfg.PhysicalRadius;

    public float TreasuryOf(State s) => s.IsPlayer ? _w.Gold : s.Treasury;

    internal void AddFunds(State? s, Settlement? fallback, float amount)
    {
        if (amount == 0) return;
        if (s == null) { if (fallback != null) fallback.Treasury = Math.Max(0, fallback.Treasury + amount); return; }
        if (s.IsPlayer)
        {
            if (amount > 0) _w.EarnGold((int)MathF.Round(amount));
            else _w.Gold = Math.Max(0, _w.Gold + (int)MathF.Round(amount));
        }
        else s.Treasury = Math.Max(0, s.Treasury + amount);
    }

    /// <summary>Gold produced by a building (e.g. a market) goes to whoever owns it.</summary>
    internal void RouteBuildingGold(Building b, int gold)
    {
        if (gold <= 0) return;
        if (b.Settlement == null) { _w.EarnGold(gold); return; } // player outpost
        AddFunds(b.Settlement.Owner, b.Settlement, gold);
        b.Settlement.GoldAccumulator += gold;
    }

    /// <summary>Whether the player controls a building (outposts and buildings of the player's settlements).</summary>
    public bool IsPlayerOwned(Building b) => b.Settlement == null || b.Settlement.Owner == PlayerState;

    // ---------------------------------------------------------------------------------
    // Creation
    // ---------------------------------------------------------------------------------

    internal State CreateState(StateBehaviourDef behaviour, string realmName, string color, string? fullName = null)
    {
        var title = behaviour.Titles.Count > 0 ? behaviour.Titles[_w.Rng.Next(behaviour.Titles.Count)] : "State of {0}";
        var s = new State(_nextStateId++, fullName ?? string.Format(title, realmName), color, behaviour, isPlayer: false, Diplomacy)
        {
            Treasury = Cfg.StartTreasury,
        };
        _states.Add(s);
        foreach (var other in _states)
        {
            if (other == s) continue;
            var r = Diplomacy.Get(s, other);
            r.Opinion = r.Baseline = _w.Rng.Range(-10, 15);
        }
        return s;
    }

    internal string NewSettlementName()
    {
        var pool = Cfg.SettlementNames.Where(n => !_usedNames.Contains(n)).ToList();
        string name;
        if (pool.Count > 0) name = pool[_w.Rng.Next(pool.Count)];
        else
        {
            // Out of names: new towns are named after old ones.
            string[] prefixes = { "New", "Port", "Fort", "Little", "Upper", "Lower", "East", "West", "North", "South" };
            name = "";
            for (int i = 0; i < 40 && (name.Length == 0 || _usedNames.Contains(name)); i++)
            {
                var root = Cfg.SettlementNames[_w.Rng.Next(Cfg.SettlementNames.Count)];
                name = $"{prefixes[_w.Rng.Next(prefixes.Length)]} {root.Split(' ').Last()}";
            }
            if (_usedNames.Contains(name)) name = $"Settlement {_settlements.Count + 1}";
        }
        _usedNames.Add(name);
        return name;
    }

    /// <summary>
    /// Founds a settlement around a new town hall at <paramref name="tile"/>. Unowned buildings in
    /// its territory (player outposts) join it if the owner is the player.
    /// </summary>
    internal Settlement Found(GridPoint tile, State? owner, string? name = null, int? population = null,
        Building? existingTownHall = null, int id = -1)
    {
        var hall = existingTownHall ?? _w.AddBuildingInternal(_w.Defs.TownHall, tile);
        if (id < 0) id = _w.NextId();
        name ??= NewSettlementName();
        _usedNames.Add(name);
        var s = new Settlement(id, name, hall, _w.Defs.ResourceCount, Cfg.Levels[0].Storage)
        {
            Owner = owner,
            FoundedAt = _w.Time,
            Population = population ?? Cfg.StartPopulation,
            Garrison = owner == PlayerState ? 8 : 2,
            Treasury = owner == null ? Cfg.StartTreasury * 0.5f : 100, // independents: town treasury; owned towns: merchants' purse
        };
        hall.Settlement = s;
        _settlements.Add(s);
        RefreshLevel(s);
        if (owner != null)
        {
            owner.SettlementList.Add(s);
            owner.Capital ??= s;
        }
        // Player outposts inside the new player town join it.
        if (owner == PlayerState)
        {
            foreach (var b in _w.Buildings)
                if (b.Settlement == null && b != hall && TerritoryAt(b.Tile) == s) Attach(b, s);
        }
        _w.Events.RaiseSettlementFounded(s);
        return s;
    }

    internal void Attach(Building b, Settlement s)
    {
        b.Settlement = s;
        if (!s.BuildingList.Contains(b)) s.BuildingList.Add(b);
    }

    internal void Detach(Building b)
    {
        b.Settlement?.BuildingList.Remove(b);
        b.Settlement = null;
    }

    /// <summary>Called when the player places a building: joins the settlement whose territory it is in.</summary>
    internal void OnPlayerBuilding(Building b)
    {
        if (b.IsTownHall)
        {
            var s = Found(b.Tile, PlayerState, existingTownHall: b);
            if (!PlayerState.Founded) FoundPlayerState(s);
            _w.Events.Post($"Founded {s.Name}! Buildings in its territory now share the town stores.", MessageKind.Good);
            return;
        }
        var t = TerritoryAt(b.Tile);
        if (t != null && t.Owner == PlayerState) Attach(b, t);
    }

    private void FoundPlayerState(Settlement capital)
    {
        PlayerState.Founded = true;
        PlayerState.Name = $"Free State of {capital.Name}";
        PlayerState.Capital = capital;
        _w.Events.Post($"You have founded the {PlayerState.Name}. Other states will now deal with you as a nation.", MessageKind.Good);
        // A newly recognised state is left alone for a while before anyone may declare war on it.
        foreach (var s in ActiveStates.Where(s => !s.IsPlayer))
        {
            var r = Diplomacy.Get(PlayerState, s);
            if (r.Stance == Stance.Peace) r.TruceUntil = Math.Max(r.TruceUntil, _w.Time + Cfg.NewStateGrace);
        }
    }

    /// <summary>Updates level and stores capacity from population.</summary>
    internal void RefreshLevel(Settlement s)
    {
        int lvl = 0;
        for (int i = 0; i < Cfg.Levels.Count; i++) if (s.Population >= Cfg.Levels[i].MinPopulation) lvl = i;
        if (lvl > s.LevelIndex && s.FoundedAt < _w.Time)
        {
            var kind = s.Owner == PlayerState ? MessageKind.Good : MessageKind.Info;
            if (s.Owner == PlayerState || IsNearPlayer(s))
                _w.Events.Post($"{s.Name} has grown into a {Cfg.Levels[lvl].Name}.", kind);
        }
        s.LevelIndex = lvl;
        s.Stores.Capacity = Cfg.Levels[lvl].Storage;
    }

    // ---------------------------------------------------------------------------------
    // Change of ownership
    // ---------------------------------------------------------------------------------

    /// <summary>Hands a settlement (and all its buildings) to a new owner (null = independent).</summary>
    public void Transfer(Settlement s, State? newOwner, float garrison)
    {
        var old = s.Owner;
        if (old == newOwner) return;
        if (old != null)
        {
            old.SettlementList.Remove(s);
            if (old.Capital == s) old.Capital = old.SettlementList.OrderByDescending(x => x.Population).FirstOrDefault();
        }
        DisbandMilitia(s, returnToGarrison: false);
        EndSiege(s, returnSoldiers: false);
        s.Owner = newOwner;
        s.Aggressors.Clear();
        s.OccupationProgress = 0;
        s.Occupier = null;
        s.Garrison = garrison;
        s.Population = Math.Max(3, (int)(s.Population * 0.85f));
        if (newOwner != null)
        {
            newOwner.SettlementList.Add(s);
            newOwner.Capital ??= s;
            if (newOwner.IsPlayer && !newOwner.Founded) FoundPlayerState(s);
        }
        if (old != null && newOwner != null)
        {
            Diplomacy.AdjustOpinion(old, newOwner, -40);
            var rel = Diplomacy.Get(old, newOwner);
            if (rel.Stance == Stance.War) { if (rel.A == newOwner) rel.GainsA++; else rel.GainsB++; }
        }
        if (newOwner == PlayerState) ReactToPlayerConquest(s, old);
        _w.Events.RaiseSettlementCaptured(s, old, newOwner);

        string from = old?.Name ?? "its people";
        string msg = newOwner == null ? $"{s.Name} has broken free of {from}." : $"{newOwner.Name} has taken {s.Name} from {from}!";
        var kind = newOwner == PlayerState ? MessageKind.Good : old == PlayerState ? MessageKind.Danger : MessageKind.Info;
        _w.Events.Post(msg, kind);

        if (old != null && !old.IsPlayer && old.SettlementList.Count == 0) Collapse(old);
    }

    private void Collapse(State s)
    {
        s.Collapsed = true;
        s.FieldArmy = 0;
        foreach (var siege in _settlements.Select(x => x.Siege).Where(x => x?.Attacker == s).ToList()) EndSiege(siege!.Target, false);
        foreach (var r in Diplomacy.All.Where(r => r.A == s || r.B == s)) r.Stance = Stance.Peace;
        _w.Events.RaiseStateCollapsed(s);
        _w.Events.Post($"The {s.Name} has fallen.", MessageKind.Info);
    }

    // ---------------------------------------------------------------------------------
    // Update
    // ---------------------------------------------------------------------------------

    public void Update(float dt)
    {
        if (!Enabled) return;
        _econTimer += dt;
        _diploTimer += dt;
        _siegeTimer += dt;
        if (_econTimer >= Cfg.EconomyInterval) { _econTimer -= Cfg.EconomyInterval; EconomyTick(); }
        if (_diploTimer >= Cfg.DiplomacyInterval) { _diploTimer -= Cfg.DiplomacyInterval; DiplomacyTick(); }
        if (_siegeTimer >= Cfg.SiegeInterval) { _siegeTimer -= Cfg.SiegeInterval; SiegeTick(Cfg.SiegeInterval); }
        UpdateNearPlayer(dt);
    }

    /// <summary>Runs the slow ticks immediately (tests, and to give a new world some history).</summary>
    public void ForceTicks(int economy, int diplomacy = 0, int sieges = 0)
    {
        for (int i = 0; i < economy; i++) EconomyTick();
        for (int i = 0; i < diplomacy; i++) DiplomacyTick();
        for (int i = 0; i < sieges; i++) SiegeTick(Cfg.SiegeInterval);
    }
}
