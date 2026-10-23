namespace PirateEngine.Core.Economy;

/// <summary>
/// A bounded store of resources. Indexed by resource index (dense array — there are only a
/// few dozen resource types), with a shared total capacity.
/// </summary>
public sealed class Inventory
{
    private readonly int[] _amounts;

    public int Capacity { get; set; }
    public int Total { get; private set; }
    public int Free => Math.Max(0, Capacity - Total);
    public int ResourceTypes => _amounts.Length;

    /// <summary>Raised after any change, with (resource, delta).</summary>
    public event Action<int, int>? Changed;

    public Inventory(int resourceTypes, int capacity)
    {
        _amounts = new int[resourceTypes];
        Capacity = capacity;
    }

    public int this[int resource] => _amounts[resource];
    public int Get(int resource) => _amounts[resource];

    public bool Has(int resource, int amount) => _amounts[resource] >= amount;

    public bool HasAll(ReadOnlySpan<(int res, int amount)> items)
    {
        foreach (var (res, amount) in items) if (_amounts[res] < amount) return false;
        return true;
    }

    /// <summary>Adds up to <paramref name="amount"/>; returns how many actually fit.</summary>
    public int Add(int resource, int amount)
    {
        if (amount <= 0) return 0;
        int n = Math.Min(amount, Free);
        if (n <= 0) return 0;
        _amounts[resource] += n;
        Total += n;
        Changed?.Invoke(resource, n);
        return n;
    }

    /// <summary>Removes up to <paramref name="amount"/>; returns how many were removed.</summary>
    public int Remove(int resource, int amount)
    {
        if (amount <= 0) return 0;
        int n = Math.Min(amount, _amounts[resource]);
        if (n <= 0) return 0;
        _amounts[resource] -= n;
        Total -= n;
        Changed?.Invoke(resource, -n);
        return n;
    }

    /// <summary>Removes all items atomically, or nothing if any is short.</summary>
    public bool TryRemoveAll(ReadOnlySpan<(int res, int amount)> items)
    {
        if (!HasAll(items)) return false;
        foreach (var (res, amount) in items) Remove(res, amount);
        return true;
    }

    /// <summary>Space needed to add all items (ignoring what's already present).</summary>
    public static int Sum(ReadOnlySpan<(int res, int amount)> items)
    {
        int s = 0;
        foreach (var (_, a) in items) s += a;
        return s;
    }

    /// <summary>Moves up to <paramref name="amount"/> of a resource to another inventory. Returns units moved.</summary>
    public int TransferTo(Inventory other, int resource, int amount)
    {
        int n = Math.Min(Math.Min(amount, _amounts[resource]), other.Free);
        if (n <= 0) return 0;
        Remove(resource, n);
        other.Add(resource, n);
        return n;
    }

    public void Clear()
    {
        for (int i = 0; i < _amounts.Length; i++)
        {
            if (_amounts[i] == 0) continue;
            int a = _amounts[i];
            _amounts[i] = 0;
            Changed?.Invoke(i, -a);
        }
        Total = 0;
    }

    public IEnumerable<(int resource, int amount)> NonZero()
    {
        for (int i = 0; i < _amounts.Length; i++)
            if (_amounts[i] > 0) yield return (i, _amounts[i]);
    }

    public int[] ToArray() => (int[])_amounts.Clone();

    public void Load(int[] amounts)
    {
        Array.Clear(_amounts);
        Total = 0;
        for (int i = 0; i < Math.Min(amounts.Length, _amounts.Length); i++)
        {
            _amounts[i] = Math.Max(0, amounts[i]);
            Total += _amounts[i];
        }
    }
}
