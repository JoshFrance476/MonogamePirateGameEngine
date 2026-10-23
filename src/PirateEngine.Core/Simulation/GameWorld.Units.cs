using System.Numerics;
using PirateEngine.Core.AI;
using PirateEngine.Core.Data;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Simulation;

// Land units: creation, landing and boarding, recruitment, player orders, and death.
public sealed partial class GameWorld
{
    private readonly Dictionary<string, int> _squadNumbers = new();

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private string NextPlayerSquadName(UnitDef def)
    {
        int n = _squadNumbers.GetValueOrDefault(def.Id) + 1;
        _squadNumbers[def.Id] = n;
        return $"{Ordinal(n)} {def.Name}";
    }

    /// <summary>Creates a player squad already aboard <paramref name="ship"/> (ignores capacity).</summary>
    internal LandUnit CreateEmbarkedUnit(UnitDef def, Ship ship, string? name = null, int id = -1)
    {
        if (id < 0) id = NextId();
        else _nextId = Math.Max(_nextId, id + 1);
        var u = new LandUnit(id, name ?? NextPlayerSquadName(def), def, ship.Faction, ship.Position)
        {
            Carrier = ship,
            Allegiance = ship.Allegiance,
        };
        ship.TroopList.Add(u);
        return u;
    }

    internal LandUnit AddUnitAshore(UnitDef def, Faction faction, GridPoint tile, string name, int id = -1)
    {
        if (id < 0) id = NextId();
        else _nextId = Math.Max(_nextId, id + 1);
        var u = new LandUnit(id, name, def, faction, tile.Center) { Heading = Rng.Range(0, MathF.Tau) };
        _units.Add(u);
        return u;
    }

    /// <summary>Spawns a Navy garrison squad ashore (spawn table entry supplies its loot).</summary>
    internal LandUnit AddGarrison(UnitSpawnDef spawn, GridPoint tile)
    {
        var u = AddUnitAshore(spawn.Def!, Faction.Navy, tile, "Navy " + spawn.Def!.Name);
        u.Spawn = spawn;
        u.Brain = new GarrisonBrain(tile);
        return u;
    }

    /// <summary>
    /// Spawns an AI squad ashore (for scripted events, scenarios or mods). Navy squads get a
    /// garrison brain guarding the spot they were placed on.
    /// </summary>
    public LandUnit SpawnAiUnit(string unitId, GridPoint tile, Faction faction = Faction.Navy)
    {
        if (!Map.IsLand(tile)) throw new ArgumentException($"{tile} is not land.", nameof(tile));
        if (faction == Faction.Player) throw new ArgumentException("Use RecruitUnit / CreatePlayerUnitAshore for player squads.");
        var def = Defs.Unit(unitId);
        var spawn = Config.UnitSpawns.FirstOrDefault(s => s.Def == def) ?? new UnitSpawnDef { Unit = def.Id, Def = def, LootGold = 0 };
        var u = AddGarrison(spawn, tile);
        return u;
    }

    /// <summary>Places a new player squad directly ashore (scenarios, tests, mods).</summary>
    public LandUnit CreatePlayerUnitAshore(string unitId, GridPoint tile)
    {
        if (!Map.IsLand(tile)) throw new ArgumentException($"{tile} is not land.", nameof(tile));
        var def = Defs.Unit(unitId);
        var u = AddUnitAshore(def, Faction.Player, tile, NextPlayerSquadName(def));
        u.Allegiance = Politics.PlayerState;
        return u;
    }

    public LandUnit? FindUnit(int id) => _units.FirstOrDefault(u => u.Id == id && !u.IsDead);

    /// <summary>Living squad ashore nearest to <paramref name="pos"/> within <paramref name="radius"/>.</summary>
    public LandUnit? UnitAt(Vector2 pos, float radius = 0.6f)
    {
        LandUnit? best = null;
        float bestD = radius;
        foreach (var u in _units)
        {
            if (u.IsDead) continue;
            float d = Vector2.Distance(u.Position, pos);
            if (d < bestD) { bestD = d; best = u; }
        }
        return best;
    }

    // ---------------------------------------------------------------------------------
    // Landing and boarding
    // ---------------------------------------------------------------------------------

    /// <summary>Why a squad can't be landed right now (or null if it can).</summary>
    public string? CannotDisembark(LandUnit u)
    {
        if (u.Carrier != Player) return "Not aboard your ship";
        if (!Player.IsDocked) return "Dock at a shore first";
        return LandingTile().IsValid ? null : "No room to land";
    }

    /// <summary>The dock tile or a neighbouring land tile on the same island, least crowded first.</summary>
    private GridPoint LandingTile()
    {
        var dock = Player.DockTile;
        int region = Map.RegionAt(dock);
        GridPoint best = GridPoint.Invalid;
        float bestScore = float.MaxValue;
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            var p = new GridPoint(dock.X + dx, dock.Y + dy);
            if (!Map.IsLand(p) || Map.RegionAt(p) != region) continue;
            int crowd = _units.Count(u => !u.IsDead && u.Tile == p);
            float score = crowd * 10 + p.Distance(dock);
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>Lands a squad from the docked player ship onto the shore next to the dock.</summary>
    public bool Disembark(LandUnit u)
    {
        var reason = CannotDisembark(u);
        if (reason != null)
        {
            Events.Post($"Can't land {u.Name}: {reason}.", MessageKind.Warning);
            return false;
        }
        var tile = LandingTile();
        Player.TroopList.Remove(u);
        u.Carrier = null;
        u.Position = u.PreviousPosition = tile.Center;
        u.Heading = u.PreviousHeading = MathF.Atan2(tile.Y - Player.Position.Y + 0.5f, tile.X - Player.Position.X + 0.5f);
        u.Target = null;
        u.PendingEmbark = null;
        u.ClearPath();
        u.LastRevealTile = GridPoint.Invalid;
        u.ReloadTimer = 1f;
        _units.Add(u);
        AddEffect(EffectKind.Dust, u.Position);
        Events.RaiseUnitLanded(u);
        Events.Post($"{u.Name} ({u.Members} men) landed.", MessageKind.Info);
        return true;
    }

    /// <summary>Lands every squad aboard. Returns how many were landed.</summary>
    public int DisembarkAll()
    {
        int n = 0;
        foreach (var u in Player.Troops.ToList()) if (Disembark(u)) n++;
        return n;
    }

    /// <summary>Why a squad can't board the player ship (or null if it can).</summary>
    public string? CannotEmbark(LandUnit u)
    {
        if (u.Faction != Faction.Player || u.IsDead || u.IsEmbarked || u.IsMilitia) return "Not one of your squads ashore";
        if (!Player.IsDocked) return "Your ship must be docked";
        if (Map.RegionAt(u.Tile) != Map.RegionAt(Player.DockTile)) return "Your ship is docked at another island";
        if (u.Members > Player.TroopSpaceFree) return $"Not enough room aboard ({Player.TroopSpaceFree} places free)";
        return null;
    }

    /// <summary>Orders a squad to march to the docked ship and board it.</summary>
    public bool CommandEmbark(LandUnit u)
    {
        var reason = CannotEmbark(u);
        if (reason != null)
        {
            Events.Post($"{u.Name} can't board: {reason}.", MessageKind.Warning);
            return false;
        }
        u.Target = null;
        u.PendingEmbark = Player;
        if (Vector2.Distance(u.Position, Player.Position) <= Config.EmbarkDistance)
        {
            u.ClearPath();
            UpdateEmbarking();
            return true;
        }
        RequestPath(u, Player.DockTile, 400_000, (m, ok) =>
        {
            if (ok) return;
            ((LandUnit)m).PendingEmbark = null;
            Events.Post($"{m.Name} can't find a way to the ship.", MessageKind.Warning);
        });
        return true;
    }

    private void UpdateEmbarking()
    {
        for (int i = _units.Count - 1; i >= 0; i--)
        {
            var u = _units[i];
            var ship = u.PendingEmbark;
            if (ship == null || u.IsDead) continue;
            if (ship.IsSunk || !ship.IsDocked || Map.RegionAt(ship.DockTile) != Map.RegionAt(u.Tile))
            {
                u.PendingEmbark = null;
                u.ClearPath();
                Events.Post($"{u.Name}: the ship left before they could board.", MessageKind.Warning);
                continue;
            }
            if (Vector2.Distance(u.Position, ship.Position) > Config.EmbarkDistance) continue;
            if (u.Members > ship.TroopSpaceFree)
            {
                u.PendingEmbark = null;
                Events.Post($"No room aboard for {u.Name}.", MessageKind.Warning);
                continue;
            }
            _units.RemoveAt(i);
            ClearTargetsOn(u);
            u.PendingEmbark = null;
            u.Target = null;
            u.ClearPath();
            u.PathTicket = 0;
            u.Carrier = ship;
            ship.TroopList.Add(u);
            Events.RaiseUnitEmbarked(u);
            Events.Post($"{u.Name} is back aboard.", MessageKind.Info);
        }
    }

    // ---------------------------------------------------------------------------------
    // Recruitment
    // ---------------------------------------------------------------------------------

    public bool BarracksInReach() => BuildingsInReach().Any(b => b.HasService("barracks"));

    public string? CannotRecruit(UnitDef def)
    {
        if (!Player.IsDocked) return "Dock first";
        if (!BarracksInReach()) return "Needs a Barracks in reach";
        if (Gold < def.RecruitGold) return "Not enough gold";
        if (!Player.Cargo.HasAll(def.RecruitCostList)) return "Missing supplies in hold";
        if (def.Members > Player.TroopSpaceFree) return "Not enough room aboard";
        return null;
    }

    /// <summary>Recruits a full-strength squad at a Barracks; it joins the ship's troops.</summary>
    public LandUnit? RecruitUnit(UnitDef def)
    {
        var reason = CannotRecruit(def);
        if (reason != null)
        {
            Events.Post($"Can't recruit {def.Name}: {reason}.", MessageKind.Warning);
            return null;
        }
        Gold -= def.RecruitGold;
        Player.Cargo.TryRemoveAll(def.RecruitCostList);
        var u = CreateEmbarkedUnit(def, Player);
        Events.Post($"Recruited the {u.Name}.", MessageKind.Good);
        return u;
    }

    // ---------------------------------------------------------------------------------
    // Player orders
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// March a squad to a tile. Squads can't cross water: clicking the sea or another island
    /// sends them to the nearest reachable spot on their own island.
    /// </summary>
    public CommandResult CommandUnitMove(LandUnit u, GridPoint tile)
    {
        if (u.IsDead || u.IsEmbarked || u.Faction != Faction.Player || u.IsMilitia || !Map.InBounds(tile)) return CommandResult.Invalid;
        u.Target = null;
        u.PendingEmbark = null;
        int region = Map.RegionAt(u.Tile);
        var goal = Map.IsLand(tile) && Map.RegionAt(tile) == region
            ? tile
            : Map.FindNearest(tile, 8, p => Map.IsLand(p) && Map.Region[Map.Index(p)] == region);
        if (!goal.IsValid)
        {
            Events.Post($"{u.Name} can't get there on foot - carry them by ship.", MessageKind.Warning);
            return CommandResult.Unreachable;
        }
        if (!RequestPath(u, goal, 400_000, (m, ok) => { if (!ok) Events.Post($"{m.Name} can't find a way there.", MessageKind.Warning); }))
            return CommandResult.Unreachable;
        return CommandResult.Ok;
    }

    /// <summary>
    /// Attack a ship or squad. Melee squads can only fight troops; musketeers can also fire on
    /// ships within range of the shore.
    /// </summary>
    public CommandResult CommandUnitAttack(LandUnit u, MobileEntity target)
    {
        if (u.IsDead || u.IsEmbarked || u.Faction != Faction.Player || u.IsMilitia || target.IsDead || target.Faction == Faction.Player)
            return CommandResult.Invalid;
        if (target is LandUnit { IsEmbarked: true }) return CommandResult.Invalid;
        if (!u.Def.IsRanged && target is Ship)
        {
            Events.Post($"{u.Name} can't fight a ship with cutlasses. Use musketeers.", MessageKind.Warning);
            return CommandResult.Invalid;
        }
        Politics.OnPlayerAggression(target);
        u.PendingEmbark = null;
        u.Target = target;
        u.SteerTimer = 0;
        if (target.Faction == Faction.Merchant && !target.Provoked)
            Events.Post($"{u.Name} opens fire on the merchant {target.Name} - they'll fight back.", MessageKind.Warning);
        return CommandResult.Ok;
    }

    public void CommandUnitStop(LandUnit u)
    {
        u.Target = null;
        u.PendingEmbark = null;
        u.PathTicket = 0;
        u.ClearPath();
    }

    /// <summary>Player squads: carry out an attack order, otherwise defend themselves.</summary>
    private void UpdatePlayerUnit(LandUnit u, float dt)
    {
        if (u.Target != null)
        {
            var t = u.Target;
            if (t.IsDead || t is LandUnit { IsEmbarked: true } || u.DistanceTo(t) > 40f)
            {
                u.Target = null;
                u.ClearPath();
                return;
            }
            UnitSteering.Engage(this, u, t, dt);
            return;
        }
        // Idle and not marching or boarding: close with enemy troops that come near.
        if (u.IsMoving || u.PathPending || u.PendingEmbark != null) return;
        var threat = UnitSteering.NearestHostileUnit(this, u, u.Def.AutoEngageRadius);
        if (threat != null)
        {
            u.Target = threat;
            u.SteerTimer = 0;
        }
    }

    // ---------------------------------------------------------------------------------
    // Death
    // ---------------------------------------------------------------------------------

    internal void KillUnit(LandUnit victim, MobileEntity? killer)
    {
        if (victim.IsDestroyed) return;
        victim.IsDestroyed = true;
        victim.Hp = 0;
        victim.ClearPath();
        AddEffect(EffectKind.UnitDeath, victim.Position);
        ClearTargetsOn(victim);
        Events.RaiseUnitKilled(victim, killer);

        if (victim.Faction == Faction.Player)
        {
            Events.Post($"The {victim.Name} has been wiped out!", MessageKind.Danger);
            return;
        }
        if (killer?.Faction == Faction.Player)
        {
            UnitsDefeatedByPlayer++;
            int loot = victim.Spawn?.LootGold ?? 0;
            EarnGold(loot);
            Events.Post(loot > 0 ? $"{victim.Name} defeated! Looted {loot} gold." : $"{victim.Name} defeated!", MessageKind.Good);
        }
    }

    internal void DespawnUnit(LandUnit u)
    {
        u.IsDestroyed = true; // removed by cleanup, no loot or events
        u.Spawn = null;
        ClearTargetsOn(u);
    }

    public int UnitsDefeatedByPlayer { get; internal set; }

    /// <summary>All player squads, ashore and aboard.</summary>
    public IEnumerable<LandUnit> PlayerSquads =>
        _units.Where(u => u.Faction == Faction.Player && !u.IsDead && !u.IsMilitia).Concat(Player.Troops);
}
