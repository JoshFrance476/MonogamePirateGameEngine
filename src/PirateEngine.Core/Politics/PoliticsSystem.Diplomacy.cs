using PirateEngine.Core.Simulation;

namespace PirateEngine.Core.Politics;

public enum OfferKind { Peace, Alliance }

/// <summary>A proposal from an AI state waiting for the player's answer.</summary>
public sealed record DiplomaticOffer(State From, OfferKind Kind, float Expires);

// Diplomacy: opinion drift, AI war/peace/alliance decisions shaped by behaviour, player offers.
public sealed partial class PoliticsSystem
{
    private readonly List<DiplomaticOffer> _offers = new();
    /// <summary>Proposals to the player awaiting an answer.</summary>
    public IReadOnlyList<DiplomaticOffer> PendingOffers => _offers;

    /// <summary>Smallest distance between any two settlements of two states.</summary>
    public float BorderDistance(State a, State b)
    {
        float best = float.MaxValue;
        foreach (var x in a.SettlementList)
            foreach (var y in b.SettlementList)
                best = Math.Min(best, x.Tile.Distance(y.Tile));
        return best;
    }

    private void DiplomacyTick()
    {
        _offers.RemoveAll(o => o.Expires < _w.Time || !o.From.IsActive);
        var active = ActiveStates.ToList();

        // ---- opinion: disturbances push it, and it settles back toward its natural level ----
        foreach (var r in Diplomacy.All.ToList())
        {
            if (!r.A.IsActive || !r.B.IsActive) continue;
            float d = 0;
            if (Diplomacy.EnemiesOf(r.A).Intersect(Diplomacy.EnemiesOf(r.B)).Any()) d += 1.5f; // common enemy
            if (r.Stance == Stance.War)
            {
                d -= 2;
                // Big wars on many fronts tire a realm faster.
                r.WarWeariness += 1 + 0.25f * (Overextension(r.A) + Overextension(r.B) + Diplomacy.EnemiesOf(r.A).Count() + Diplomacy.EnemiesOf(r.B).Count() - 2);
            }
            if (r.Stance == Stance.Alliance) d += 0.5f;
            // A realm that has grown far past its station worries everyone - the player included.
            if (IsDominant(r.A)) d -= 1;
            if (IsDominant(r.B)) d -= 1;
            Diplomacy.AdjustOpinion(r.A, r.B, d + (r.Baseline - r.Opinion) * Cfg.OpinionRecovery);
        }

        // ---- AI decisions -----------------------------------------------------------
        foreach (var a in active.Where(s => !s.IsPlayer).OrderBy(_ => _w.Rng.NextFloat()))
        {
            // At most one new war per state per tick: against the target it most wants to fight.
            var target = active.Where(b => b != a && b.IsActive && Diplomacy.StanceOf(a, b) == Stance.Peace)
                .OrderByDescending(b => WarScore(a, b) + (WarReason(a, b) > 0 ? 30 : 0)).FirstOrDefault();
            if (target != null && a.IsActive) ConsiderWar(a, target, Diplomacy.Get(a, target));

            foreach (var b in active)
            {
                if (b == a || !a.IsActive || !b.IsActive) continue;
                var r = Diplomacy.Get(a, b);
                switch (r.Stance)
                {
                    case Stance.Peace:
                        ConsiderAlliance(a, b, r);
                        break;
                    case Stance.War:
                        ConsiderPeace(a, b, r);
                        break;
                    case Stance.Alliance:
                        if (r.Opinion < 5) BreakAlliance(a, b);
                        break;
                }
            }
        }
    }

    /// <summary>The AI's appetite for war against <paramref name="b"/> (above 40 = declares).</summary>
    public float WarScore(State a, State b)
    {
        var beh = a.Behaviour;
        var r = Diplomacy.Get(a, b);
        float bStrength = b.Strength + Diplomacy.AlliesOf(b).Sum(x => x.Strength) * 0.5f;
        if (b.IsPlayer) bStrength += _w.PlayerSquads.Sum(u => u.Members) * 0.5f + _w.Player.TroopsAboard * 0.5f + _w.Player.Class.Guns * 2;
        float ratio = (a.Strength + 1) / (bStrength + 1);
        bool neighbour = BorderDistance(a, b) < 90;
        // Only aggressive or opportunistic rulers see a military advantage as a reason for war.
        float appetite = 0.2f + beh.Aggression * 0.5f + beh.Opportunism * 0.5f;
        return beh.Aggression * 45
               - r.Opinion * 0.4f
               + Math.Clamp(ratio - 1, -1.5f, 2f) * 25 * appetite
               + beh.Opportunism * (Diplomacy.EnemiesOf(b).Any() ? 25 : 0)
               + (IsDominant(b) ? 10 + beh.Opportunism * 15 : 0)   // gang up on the strongest
               - Overextension(a) * 10                             // sprawling realms stop expanding
               + (neighbour ? 10 : -25)
               - beh.Peacefulness * 15;
    }

    /// <summary>Settlements held beyond what the realm can comfortably govern.</summary>
    public int Overextension(State s) => Math.Max(0, s.SettlementList.Count - Cfg.MaxSettlementsPerState);

    /// <summary>
    /// A state that has grown far beyond the size it started with (for the player, beyond a
    /// single town): its neighbours grow wary and may gang up on it.
    /// </summary>
    public bool IsDominant(State s)
    {
        if (!s.IsActive) return false;
        int start = Math.Max(1, s.InitialTowns);
        return s.SettlementList.Count >= start * 1.5f + 2;
    }

    /// <summary>
    /// Whether <paramref name="a"/> has a reason to act against <paramref name="b"/> now: the
    /// target is already at war, has grown too strong, or is the player and has provoked them.
    /// Without one, wars start only rarely, so the world stays settled unless disturbed.
    /// </summary>
    public bool HasCasusBelli(State a, State b) => WarReason(a, b) > 0;

    /// <summary>
    /// How strongly circumstances invite <paramref name="a"/> to attack <paramref name="b"/>
    /// (0 = no reason): provocation by the player or a rising power count fully; a target that
    /// is already losing towns in another war tempts the opportunistic, like vultures.
    /// </summary>
    private float WarReason(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        if ((b.IsPlayer && r.Opinion <= -40) || IsDominant(b)) return 1f;
        bool wounded = Diplomacy.EnemiesOf(b).Any(e => e != a && Diplomacy.Get(e, b).GainsOf(e) > 0);
        if (wounded) return 0.5f * a.Behaviour.Opportunism;
        return 0f;
    }

    private bool ConsiderWar(State a, State b, Relation r)
    {
        if (_w.Time < r.TruceUntil || b.SettlementList.Count == 0) return false;
        if (b.IsPlayer && !b.Founded) return false;
        if (Diplomacy.EnemiesOf(a).Count() >= 2) return false;
        if (WarScore(a, b) + _w.Rng.Range(-10f, 10f) <= 40) return false;
        float reason = WarReason(a, b);
        // Between wars a realm stays home - unless the player is the one provoking it.
        if (_w.Time < a.NextWarAllowed && !(b.IsPlayer && reason > 0)) return false;
        float chance = reason > 0 ? Cfg.ReactiveWarChance * reason : Cfg.ProactiveWarChance;
        if (_w.Rng.NextFloat() >= chance) return false;
        DeclareWar(a, b);
        return true;
    }

    /// <summary>Declares war; the defender's allies may join in.</summary>
    public void DeclareWar(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        if (r.Stance == Stance.War) return;
        r.Stance = Stance.War;
        r.Since = _w.Time;
        r.WarWeariness = 0;
        r.GainsA = r.GainsB = 0;
        Diplomacy.AdjustOpinion(a, b, -30);
        _offers.RemoveAll(o => (o.From == a && b.IsPlayer) || (o.From == b && a.IsPlayer));
        _w.Events.RaiseWarDeclared(a, b);
        var kind = a.IsPlayer || b.IsPlayer ? MessageKind.Danger : MessageKind.Info;
        _w.Events.Post($"The {a.Name} has declared war on the {b.Name}!", kind);

        foreach (var ally in Diplomacy.AlliesOf(b).ToList())
        {
            if (ally == a || Diplomacy.AtWar(ally, a)) continue;
            if (ally.IsPlayer)
            {
                _w.Events.Post($"Your ally the {b.Name} has been attacked by the {a.Name}.", MessageKind.Warning);
                continue;
            }
            if (Diplomacy.Opinion(ally, a) < 40)
            {
                if (Diplomacy.Allied(ally, a)) BreakAlliance(ally, a);
                var ar = Diplomacy.Get(ally, a);
                ar.Stance = Stance.War;
                ar.Since = _w.Time;
                ar.WarWeariness = 0;
                _w.Events.RaiseWarDeclared(ally, a);
                _w.Events.Post($"The {ally.Name} joins its ally against the {a.Name}.", a.IsPlayer ? MessageKind.Danger : MessageKind.Info);
            }
        }
    }

    /// <summary>How much <paramref name="a"/> wants peace with <paramref name="b"/> (above 30 = yes).</summary>
    public float PeaceWillingness(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        float ratio = (a.Strength + 1) / (b.Strength + 1);
        return r.WarWeariness * (0.3f + a.Behaviour.Peacefulness) * 1.5f
               + (ratio < 0.7f ? 30 : 0)
               + (a.SettlementList.Count <= 1 && !a.IsPlayer && (ratio < 1.2f || a.SettlementList.Any(x => x.Siege != null)) ? 40 : 0) // last stand
               + (b.SettlementList.Count <= 1 ? 25 * (1 - a.Behaviour.Aggression) : 0)       // mercy for a broken foe
               + r.GainsOf(a) * 25 * (0.5f + a.Behaviour.Opportunism)                         // has taken its prize
               - a.Behaviour.Aggression * 20
               + a.Behaviour.Peacefulness * 15
               + r.Opinion * 0.2f;
    }

    private void ConsiderPeace(State a, State b, Relation r)
    {
        if (_w.Time - r.Since < Cfg.DiplomacyInterval * 3) return; // wars last a while
        if (PeaceWillingness(a, b) <= 35) return;
        if (b.IsPlayer)
        {
            if (!_offers.Any(o => o.From == a && o.Kind == OfferKind.Peace))
            {
                _offers.Add(new DiplomaticOffer(a, OfferKind.Peace, _w.Time + 120));
                _w.Events.Post($"The {a.Name} offers you peace. Answer in the States screen [K].", MessageKind.Warning);
            }
            return;
        }
        if (PeaceWillingness(b, a) > 25) MakePeace(a, b);
    }

    public void MakePeace(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        if (r.Stance != Stance.War) return;
        r.Stance = Stance.Peace;
        r.TruceUntil = _w.Time + Cfg.TruceSeconds;
        r.WarWeariness = 0;
        r.Since = _w.Time;
        r.GainsA = r.GainsB = 0;
        // A realm just out of a war won't go looking for another for a good while.
        a.NextWarAllowed = Math.Max(a.NextWarAllowed, _w.Time + Cfg.WarCooldown);
        b.NextWarAllowed = Math.Max(b.NextWarAllowed, _w.Time + Cfg.WarCooldown);
        Diplomacy.AdjustOpinion(a, b, 10);
        // Lift sieges between the two.
        foreach (var s in _settlements.Where(s => s.Siege != null && ((s.Siege.Attacker == a && s.Owner == b) || (s.Siege.Attacker == b && s.Owner == a))).ToList())
            EndSiege(s, returnSoldiers: true);
        _offers.RemoveAll(o => o.Kind == OfferKind.Peace && (o.From == a || o.From == b));
        _w.Events.RaisePeaceMade(a, b);
        _w.Events.Post($"The {a.Name} and the {b.Name} have made peace.", a.IsPlayer || b.IsPlayer ? MessageKind.Good : MessageKind.Info);
    }

    private void ConsiderAlliance(State a, State b, Relation r)
    {
        if (r.Opinion < 50) return;
        if ((a.Behaviour.Peacefulness + a.Behaviour.Trade) / 2 < 0.45f) return;
        if (_w.Rng.NextFloat() > 0.2f) return;
        if (Diplomacy.AlliesOf(a).Count() >= 2) return;
        if (b.IsPlayer)
        {
            if (!b.Founded || _offers.Any(o => o.From == a && o.Kind == OfferKind.Alliance)) return;
            _offers.Add(new DiplomaticOffer(a, OfferKind.Alliance, _w.Time + 120));
            _w.Events.Post($"The {a.Name} proposes an alliance. Answer in the States screen [K].", MessageKind.Good);
            return;
        }
        if (Diplomacy.Opinion(b, a) > 40 && b.Behaviour.Aggression < 0.8f) FormAlliance(a, b);
    }

    public void FormAlliance(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        r.Stance = Stance.Alliance;
        r.Since = _w.Time;
        _offers.RemoveAll(o => o.Kind == OfferKind.Alliance && (o.From == a || o.From == b));
        _w.Events.RaiseAllianceFormed(a, b);
        _w.Events.Post($"The {a.Name} and the {b.Name} have formed an alliance.", a.IsPlayer || b.IsPlayer ? MessageKind.Good : MessageKind.Info);
    }

    public void BreakAlliance(State a, State b)
    {
        var r = Diplomacy.Get(a, b);
        if (r.Stance != Stance.Alliance) return;
        r.Stance = Stance.Peace;
        r.Since = _w.Time;
        Diplomacy.AdjustOpinion(a, b, -15);
        _w.Events.Post($"The alliance between the {a.Name} and the {b.Name} has ended.", a.IsPlayer || b.IsPlayer ? MessageKind.Warning : MessageKind.Info);
    }

    // ---------------------------------------------------------------------------------
    // Player diplomacy
    // ---------------------------------------------------------------------------------

    /// <summary>Why the player can't take a diplomatic action toward a state right now (null = allowed).</summary>
    public string? PlayerCannot(State target, string action)
    {
        if (target.IsPlayer) return "That's your own state";
        if (!target.IsActive) return "That state has fallen";
        var r = Diplomacy.Get(PlayerState, target);
        return action switch
        {
            "war" when r.Stance == Stance.War => "Already at war",
            "war" when r.Stance == Stance.Alliance => "Break the alliance first",
            "peace" when r.Stance != Stance.War => "Not at war",
            "alliance" when r.Stance == Stance.Alliance => "Already allies",
            "alliance" when r.Stance == Stance.War => "Make peace first",
            "alliance" when !PlayerState.Founded => "Found a settlement first",
            "gift" when _w.Gold < Cfg.GiftGold => $"Needs {Cfg.GiftGold} gold",
            "break" when r.Stance != Stance.Alliance => "Not allies",
            _ => null,
        };
    }

    public bool PlayerDeclareWar(State target)
    {
        if (PlayerCannot(target, "war") is { } why) { _w.Events.Post($"Can't declare war: {why}.", MessageKind.Warning); return false; }
        DeclareWar(PlayerState, target);
        // The world takes sides: the target's friends resent you, its enemies warm to you.
        foreach (var st in ActiveStates.Where(x => !x.IsPlayer && x != target))
        {
            var rel = Diplomacy.Get(st, target);
            if (rel.Stance == Stance.Alliance) Diplomacy.AdjustOpinion(PlayerState, st, -15);
            else if (rel.Stance == Stance.War) Diplomacy.AdjustOpinion(PlayerState, st, 12);
            else if (rel.IsRivalry) Diplomacy.AdjustOpinion(PlayerState, st, 8);
            else Diplomacy.AdjustOpinion(PlayerState, st, -3 * st.Behaviour.Peacefulness);
        }
        return true;
    }

    /// <summary>Proposes peace; the AI accepts if war-weary, losing or peaceable enough.</summary>
    public bool PlayerProposePeace(State target)
    {
        if (PlayerCannot(target, "peace") is { } why) { _w.Events.Post($"Can't propose peace: {why}.", MessageKind.Warning); return false; }
        if (PeaceWillingness(target, PlayerState) > 20)
        {
            MakePeace(PlayerState, target);
            return true;
        }
        Diplomacy.AdjustOpinion(PlayerState, target, -2);
        _w.Events.Post($"The {target.Name} rejects your offer of peace.", MessageKind.Warning);
        return false;
    }

    public bool PlayerProposeAlliance(State target)
    {
        if (PlayerCannot(target, "alliance") is { } why) { _w.Events.Post($"Can't propose an alliance: {why}.", MessageKind.Warning); return false; }
        float opinion = Diplomacy.Opinion(target, PlayerState);
        if (opinion >= 40 && target.Behaviour.Aggression < 0.8f)
        {
            FormAlliance(PlayerState, target);
            return true;
        }
        _w.Events.Post($"The {target.Name} declines an alliance (opinion {opinion:0}, needs 40).", MessageKind.Warning);
        return false;
    }

    public bool PlayerBreakAlliance(State target)
    {
        if (PlayerCannot(target, "break") is { } why) { _w.Events.Post($"Can't break alliance: {why}.", MessageKind.Warning); return false; }
        BreakAlliance(PlayerState, target);
        return true;
    }

    /// <summary>Sends gold; improves their opinion of you (mercantile states value it more).</summary>
    public bool PlayerGift(State target)
    {
        if (PlayerCannot(target, "gift") is { } why) { _w.Events.Post($"Can't send a gift: {why}.", MessageKind.Warning); return false; }
        _w.Gold -= Cfg.GiftGold;
        target.Treasury += Cfg.GiftGold;
        float gain = 12 * (1 + target.Behaviour.Trade * 0.5f);
        Diplomacy.AdjustOpinion(PlayerState, target, gain);
        _w.Events.Post($"The {target.Name} accepts your gift (opinion +{gain:0}).", MessageKind.Good);
        return true;
    }

    /// <summary>
    /// How the world takes the player conquering a town: nearby realms grow wary (the more
    /// peaceable, the more so), the former owner's allies resent it, its enemies approve.
    /// </summary>
    private void ReactToPlayerConquest(Settlement s, State? from)
    {
        foreach (var st in ActiveStates.Where(x => !x.IsPlayer && x != from && x.SettlementList.Count > 0).ToList())
        {
            float dist = st.SettlementList.Min(x => x.Tile.Distance(s.Tile));
            if (dist > 220) continue;
            float near = 1 - dist / 220;
            float delta = -(3 + 8 * st.Behaviour.Peacefulness) * near;
            if (from != null)
            {
                var rel = Diplomacy.Get(st, from);
                if (rel.Stance == Stance.Alliance) delta -= 15;
                else if (rel.Stance == Stance.War) delta += 12;
                else if (rel.IsRivalry) delta += 10;
            }
            Diplomacy.AdjustOpinion(PlayerState, st, delta);
        }
    }

    public void AnswerOffer(DiplomaticOffer offer, bool accept)
    {
        if (!_offers.Remove(offer)) return;
        if (!accept)
        {
            Diplomacy.AdjustOpinion(PlayerState, offer.From, -5);
            _w.Events.Post($"You turned down the {offer.From.Name}.", MessageKind.Info);
            return;
        }
        if (offer.Kind == OfferKind.Peace) MakePeace(offer.From, PlayerState);
        else if (Diplomacy.StanceOf(PlayerState, offer.From) == Stance.Peace) FormAlliance(offer.From, PlayerState);
    }
}
