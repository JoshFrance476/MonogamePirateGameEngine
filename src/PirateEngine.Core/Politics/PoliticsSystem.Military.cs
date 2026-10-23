using System.Numerics;
using PirateEngine.Core.AI;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Politics;

// Sieges and conquest. Far from the player, sieges are resolved by numbers; near the player,
// garrisons muster as real squads, assaults on the player's towns arrive as real squads, and a
// town falls when hostile troops hold its town hall uncontested.
public sealed partial class PoliticsSystem
{
    private const int MaxMilitiaSquads = 3;
    private const int MaxAssaultSquads = 4;

    // ---------------------------------------------------------------------------------
    // Abstract sieges
    // ---------------------------------------------------------------------------------

    private void SiegeTick(float dt)
    {
        LaunchSieges();
        foreach (var s in _settlements.Where(s => s.Siege != null).ToList()) ResolveSiege(s, dt);
    }

    /// <summary>How long an army regroups after a siege (aggressive rulers regroup faster).</summary>
    private float CampaignPause(State st) =>
        _w.Rng.Range(Cfg.CampaignCooldown[0], Cfg.CampaignCooldown[1]) * (1.2f - st.Behaviour.Aggression * 0.4f);

    private void LaunchSieges()
    {
        foreach (var st in ActiveStates.Where(x => !x.IsPlayer).ToList())
        {
            if (st.FieldArmy < 15 || _w.Time < st.NextCampaign || st.SettlementList.Count == 0 ||
                _settlements.Any(s => s.Siege?.Attacker == st)) continue;
            var enemies = Diplomacy.EnemiesOf(st).ToHashSet();
            // Seizing independent towns is rare and only for expansion-minded realms.
            bool eyeingIndependents = st.Behaviour.Expansion >= 0.4f && Overextension(st) == 0 && _w.Time >= st.NextWarAllowed &&
                                      _w.Rng.NextFloat() < Cfg.IndependentConquestChance * (0.5f + st.Behaviour.Expansion);
            Settlement? best = null;
            float bestScore = float.MaxValue;
            foreach (var s in _settlements)
            {
                if (s.Siege != null || s.Owner == st) continue;
                bool target = s.Owner != null ? enemies.Contains(s.Owner) : eyeingIndependents;
                if (!target) continue;
                float dist = st.SettlementList.Min(x => x.Tile.Distance(s.Tile));
                if (dist > (s.Owner == null ? 140 : 220)) continue;
                float score = dist * 0.5f + (s.Garrison + s.Militia.Sum(m => m.Members)) * 3;
                if (score < bestScore) { bestScore = score; best = s; }
            }
            if (best == null) continue;
            float commit = st.FieldArmy * 0.75f;
            st.FieldArmy -= commit;
            best.Siege = new Siege(st, best, commit, _w.Time);
            if (best.Owner == null) best.Aggressors.Add(st);
            _w.Events.RaiseSiegeStarted(best.Siege);
            var kind = best.Owner == PlayerState ? MessageKind.Danger : MessageKind.Info;
            _w.Events.Post($"The {st.Name} lays siege to {best.Name} with {(int)commit} soldiers.", kind);
        }
    }

    private void ResolveSiege(Settlement s, float dt)
    {
        // Another siege resolved earlier this tick may have ended this one (e.g. a state collapsed).
        if (s.Siege is not { } siege) return;
        var atk = siege.Attacker;
        if (!atk.IsActive || s.Owner == atk || (s.Owner != null && !Diplomacy.AtWar(atk, s.Owner)))
        {
            EndSiege(s, returnSoldiers: true);
            return;
        }
        if (siege.IsPhysical || (s.Owner == PlayerState && IsNearPlayer(s))) return; // fought live

        var cfg = Cfg;
        float fort = s.Level(cfg).Fortification * (s.Owner?.Capital == s ? 1.5f : 1f);
        var ownerBeh = s.Owner?.Behaviour ?? PlayerBehaviour;
        float defence = s.Garrison * fort * (0.7f + ownerBeh.Defence * 0.6f) + s.Militia.Where(m => !m.IsDead).Sum(m => m.Members);
        if (s.Owner == PlayerState)
            defence += _w.Units.Where(u => u.Faction == Faction.Player && !u.IsDead && u.Tile.Distance(s.Tile) <= s.Radius(cfg)).Sum(u => u.Members) * 1.5f;

        float k = dt / 3f;
        // Sieges grind: walls and a garrison hold out for minutes, and cost the attacker dearly.
        float attackLoss = (defence * 0.03f + 0.3f) * _w.Rng.Range(0.6f, 1.4f) * k;
        float defLoss = siege.Soldiers * 0.012f * _w.Rng.Range(0.6f, 1.4f) / fort * k;
        siege.Soldiers = Math.Max(0, siege.Soldiers - attackLoss);
        s.Garrison = Math.Max(0, s.Garrison - defLoss);

        if (s.Garrison < 1 && s.Militia.All(m => m.IsDead))
            siege.Progress += 25 * siege.Soldiers / (siege.Soldiers + 10) * k;

        if (siege.Progress >= 100)
        {
            float survivors = siege.Soldiers;
            s.Siege = null;
            atk.NextCampaign = _w.Time + CampaignPause(atk);
            atk.FieldArmy += survivors * 0.5f;
            Transfer(s, atk, garrison: survivors * 0.5f);
        }
        else if (siege.Soldiers < 1)
        {
            _w.Events.Post($"The siege of {s.Name} by the {atk.Name} has failed.", s.Owner == PlayerState ? MessageKind.Good : MessageKind.Info);
            EndSiege(s, returnSoldiers: false);
        }
    }

    /// <summary>Ends a siege. Surviving attackers return to their field army if <paramref name="returnSoldiers"/>.</summary>
    internal void EndSiege(Settlement s, bool returnSoldiers)
    {
        var siege = s.Siege;
        if (siege == null) return;
        s.Siege = null;
        // Armies regroup between campaigns; aggressive rulers regroup faster.
        siege.Attacker.NextCampaign = _w.Time + CampaignPause(siege.Attacker);
        if (returnSoldiers) siege.Attacker.FieldArmy += siege.SoldiersTotal * 0.8f;
        foreach (var u in siege.Squads.Where(u => !u.IsDead)) _w.DespawnUnit(u);
        siege.Squads.Clear();
        if (s.Owner == null) s.Aggressors.Remove(siege.Attacker);
    }

    // ---------------------------------------------------------------------------------
    // Near the player: militia, live assaults, occupation
    // ---------------------------------------------------------------------------------

    private void UpdateNearPlayer(float dt)
    {
        foreach (var s in _settlements.ToList())
        {
            s.Militia.RemoveAll(m => m.IsDead);
            bool near = IsNearPlayer(s);

            if (s.Siege is { } siege)
            {
                siege.Squads.RemoveAll(u => u.IsDead);
                if (s.Owner == PlayerState && near) Materialise(siege);
                else if (siege.IsPhysical && !near) Dematerialise(siege);
                if (s.Owner == PlayerState && near && !siege.IsPhysical && siege.Soldiers < 1)
                {
                    _w.Events.Post($"The assault on {s.Name} has been beaten off!", MessageKind.Good);
                    EndSiege(s, returnSoldiers: false);
                }
            }

            if (!near && s.Militia.Count == 0) continue;
            UpdateMilitia(s, dt);
            UpdateOccupation(s, dt);
        }
    }

    private void UpdateMilitia(Settlement s, float dt)
    {
        float r = s.Radius(Cfg) + 4;
        bool threatened = _w.Units.Any(u => ThreatensSettlement(s, u) && u.Tile.Distance(s.Tile) <= r);
        if (threatened)
        {
            s.CalmTimer = 0;
            while (s.Militia.Count < MaxMilitiaSquads && s.Garrison >= 1) MusterSquad(s);
        }
        else if (s.Militia.Count > 0)
        {
            s.CalmTimer += dt;
            if (s.CalmTimer > 30) DisbandMilitia(s, returnToGarrison: true);
        }
    }

    private void MusterSquad(Settlement s)
    {
        var def = _w.Defs.Units[s.Militia.Count % _w.Defs.Units.Count];
        int members = (int)Math.Min(def.Members, MathF.Floor(s.Garrison));
        if (members <= 0) return;
        var tile = _w.Map.FindNearest(s.Tile, 3, p => _w.Map.IsLand(p) && _w.Map.RegionAt(p) == s.Region && p != s.Tile);
        if (!tile.IsValid) tile = s.Tile;
        s.Garrison -= members;
        var faction = s.Owner == PlayerState ? Faction.Player : Faction.State;
        var u = _w.AddUnitAshore(def, faction, tile, $"{s.Name} Militia");
        u.Allegiance = s.Owner;
        u.HomeSettlement = s;
        u.Hp = members * def.HpPerMember;
        u.Brain = new GarrisonBrain(s.Tile);
        s.Militia.Add(u);
    }

    internal void DisbandMilitia(Settlement s, bool returnToGarrison)
    {
        foreach (var m in s.Militia.Where(m => !m.IsDead))
        {
            if (returnToGarrison) s.Garrison += m.Members;
            _w.DespawnUnit(m);
        }
        s.Militia.Clear();
        s.CalmTimer = 0;
    }

    /// <summary>Turns an abstract siege of a player town into marching squads (a wave at a time).</summary>
    private void Materialise(Siege siege)
    {
        if (siege.Squads.Count > 0 || siege.Soldiers < 1) return;
        var s = siege.Target;
        float a = _w.Rng.Range(0, MathF.Tau), dist = s.Radius(Cfg) + 4;
        var around = new GridPoint(s.Tile.X + (int)(MathF.Cos(a) * dist), s.Tile.Y + (int)(MathF.Sin(a) * dist));
        var start = _w.Map.FindNearest(around, 8, p => _w.Map.IsLand(p) && _w.Map.RegionAt(p) == s.Region);
        if (!start.IsValid) start = s.Tile;
        for (int i = 0; i < MaxAssaultSquads && siege.Soldiers >= 1; i++)
        {
            var def = _w.Defs.Units[i % _w.Defs.Units.Count];
            int members = (int)Math.Min(def.Members, MathF.Floor(siege.Soldiers));
            siege.Soldiers -= members;
            var u = _w.AddUnitAshore(def, Faction.State, start, $"{siege.Attacker.Name.Split(' ')[0]} {def.Name}");
            u.Allegiance = siege.Attacker;
            u.Hp = members * def.HpPerMember;
            u.Brain = new AssaultBrain(s);
            siege.Squads.Add(u);
        }
        _w.Events.Post($"Troops of the {siege.Attacker.Name} are marching on {s.Name}!", MessageKind.Danger);
    }

    private void Dematerialise(Siege siege)
    {
        foreach (var u in siege.Squads.Where(u => !u.IsDead))
        {
            siege.Soldiers += u.Members;
            _w.DespawnUnit(u);
        }
        siege.Squads.Clear();
    }

    /// <summary>
    /// Hostile troops standing at the town hall, with no defenders nearby and the garrison spent,
    /// take the town after <c>captureSeconds</c>.
    /// </summary>
    private void UpdateOccupation(Settlement s, float dt)
    {
        var hall = s.Tile.Center;
        MobileEntity? occupier = null;
        bool defended = false;
        foreach (var u in _w.Units)
        {
            if (u.IsDead) continue;
            float d = Vector2.Distance(u.Position, hall);
            if (d > 4f) continue;
            if (ThreatensSettlement(s, u)) { if (d <= Cfg.CaptureRadius && u.Allegiance != null) occupier ??= u; }
            else if (u.HomeSettlement == s || (s.Owner != null && u.Allegiance == s.Owner)) defended = true;
        }
        if (occupier != null && !defended && s.Garrison < 1)
        {
            if (s.Occupier != occupier.Allegiance) { s.Occupier = occupier.Allegiance; s.OccupationProgress = 0; }
            s.OccupationProgress += dt;
            if (s.OccupationProgress >= Cfg.CaptureSeconds) Capture(s, s.Occupier!);
        }
        else
        {
            s.OccupationProgress = Math.Max(0, s.OccupationProgress - dt * 2);
            if (s.OccupationProgress == 0) s.Occupier = null;
        }
    }

    private void Capture(Settlement s, State by)
    {
        float garrison = 2;
        if (s.Siege is { } siege && siege.Attacker == by)
        {
            // The victorious assault troops become the new garrison.
            garrison = Math.Max(2, siege.Squads.Where(u => !u.IsDead).Sum(u => u.Members) * 0.5f + siege.Soldiers * 0.5f);
            by.FieldArmy += siege.Soldiers * 0.5f;
            siege.Soldiers = 0;
        }
        Transfer(s, by, garrison);
    }

    // ---------------------------------------------------------------------------------
    // Player attacks
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The player declares hostility toward a settlement: war with its owner, or (independent)
    /// an attack on the town. Its militia then muster against the player's troops.
    /// </summary>
    public string? PlayerCannotAttack(Settlement s)
    {
        if (s.Owner == PlayerState) return "It's your own settlement";
        if (s.Owner == null) return s.Aggressors.Contains(PlayerState) ? "Already attacking it" : null;
        if (Diplomacy.AtWar(PlayerState, s.Owner)) return "Already at war with its owner";
        if (Diplomacy.Allied(PlayerState, s.Owner)) return "Its owner is your ally";
        return null;
    }

    public bool PlayerAttackSettlement(Settlement s)
    {
        if (PlayerCannotAttack(s) is { } why) { _w.Events.Post($"Can't attack {s.Name}: {why}.", MessageKind.Warning); return false; }
        if (s.Owner == null)
        {
            s.Aggressors.Add(PlayerState);
            _w.Events.Post($"You move against {s.Name}. Defeat its militia and hold the town hall to take it.", MessageKind.Warning);
        }
        else
        {
            DeclareWar(PlayerState, s.Owner);
        }
        return true;
    }

    /// <summary>
    /// Called when the player attacks an entity of a state it isn't fighting: aggression means
    /// war (or, for an independent town's militia, an attack on that town).
    /// </summary>
    internal void OnPlayerAggression(MobileEntity target)
    {
        if (target.Faction != Faction.State || GameWorld.IsHostile(_w.Player, target)) return;
        if (target.Allegiance is { } st && !st.IsPlayer)
        {
            if (Diplomacy.Allied(PlayerState, st)) BreakAlliance(PlayerState, st);
            DeclareWar(PlayerState, st);
        }
        else if (target.HomeSettlement is { } home && home.Owner == null)
        {
            home.Aggressors.Add(PlayerState);
        }
    }
}
