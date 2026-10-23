namespace PirateEngine.Core.Politics;

public enum Stance { Peace, War, Alliance }

/// <summary>Relation between two states (stored once per unordered pair).</summary>
public sealed class Relation
{
    public State A { get; }
    public State B { get; }
    public Stance Stance { get; internal set; } = Stance.Peace;
    /// <summary>-100 (hatred) .. +100 (friendship); symmetric.</summary>
    public float Opinion { get; internal set; }
    /// <summary>
    /// The pair's natural opinion — from their characters, borders and history. Opinion drifts
    /// back toward it, so the world settles unless something (often the player) disturbs it.
    /// </summary>
    public float Baseline { get; internal set; }
    /// <summary>Towns each side has taken from the other in the current war (A's, B's).</summary>
    internal int GainsA, GainsB;
    internal int GainsOf(State s) => s == A ? GainsA : GainsB;
    public bool IsRivalry => Baseline <= PoliticsSystem.RivalryThreshold;
    /// <summary>No new war between the pair before this game time.</summary>
    public float TruceUntil { get; internal set; }
    /// <summary>Grows during a war; high weariness pushes toward peace.</summary>
    public float WarWeariness { get; internal set; }
    public float Since { get; internal set; }

    public Relation(State a, State b) { A = a; B = b; }
    public State Other(State s) => s == A ? B : A;
}

/// <summary>Pairwise stance and opinion between all states.</summary>
public sealed class Diplomacy
{
    private readonly Dictionary<(int, int), Relation> _relations = new();
    private static (int, int) Key(State a, State b) => a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);

    public IEnumerable<Relation> All => _relations.Values;

    public Relation Get(State a, State b)
    {
        if (a == b) throw new ArgumentException("A state has no relation with itself.");
        var key = Key(a, b);
        if (!_relations.TryGetValue(key, out var r))
        {
            r = a.Id < b.Id ? new Relation(a, b) : new Relation(b, a);
            _relations[key] = r;
        }
        return r;
    }

    public Stance StanceOf(State a, State b) => a == b ? Stance.Alliance : Get(a, b).Stance;
    public bool AtWar(State a, State b) => a != b && Get(a, b).Stance == Stance.War;
    public bool Allied(State a, State b) => a != b && Get(a, b).Stance == Stance.Alliance;
    public float Opinion(State a, State b) => a == b ? 100 : Get(a, b).Opinion;

    public IEnumerable<State> EnemiesOf(State s) =>
        _relations.Values.Where(r => r.Stance == Stance.War && (r.A == s || r.B == s)).Select(r => r.Other(s));

    public IEnumerable<State> AlliesOf(State s) =>
        _relations.Values.Where(r => r.Stance == Stance.Alliance && (r.A == s || r.B == s)).Select(r => r.Other(s));

    internal void AdjustOpinion(State a, State b, float delta)
    {
        if (a == b) return;
        var r = Get(a, b);
        r.Opinion = Math.Clamp(r.Opinion + delta, -100f, 100f);
    }

    internal void Remove(State s)
    {
        foreach (var key in _relations.Where(kv => kv.Value.A == s || kv.Value.B == s).Select(kv => kv.Key).ToList())
            _relations.Remove(key);
    }
}
