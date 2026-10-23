using PirateEngine.Core.Simulation;

namespace PirateEngine.Core.Politics;

// The player's dealings with towns from a docked ship: own town stores, and trade with others.
public sealed partial class PoliticsSystem
{
    /// <summary>Settlements whose town hall is within reach of the docked player ship.</summary>
    public IEnumerable<Settlement> SettlementsInReach()
    {
        var p = _w.Player;
        if (!p.IsDocked) yield break;
        int region = _w.Map.RegionAt(p.DockTile);
        foreach (var s in _settlements)
            if (s.Region == region && s.Tile.Distance(p.DockTile) <= _w.Config.DockReach + 2)
                yield return s;
    }

    public bool InReach(Settlement s) => SettlementsInReach().Contains(s);

    // ---- own towns: free use of the stores ----------------------------------------------

    public int LoadFromStores(Settlement s, int resource, int amount)
    {
        if (s.Owner != PlayerState || !InReach(s)) return 0;
        return s.Stores.TransferTo(_w.Player.Cargo, resource, amount);
    }

    public int UnloadToStores(Settlement s, int resource, int amount)
    {
        if (s.Owner != PlayerState || !InReach(s)) return 0;
        return _w.Player.Cargo.TransferTo(s.Stores, resource, amount);
    }

    // ---- foreign towns: trade ---------------------------------------------------------

    public string? CannotTrade(Settlement s)
    {
        if (s.Owner == PlayerState) return "Your own town: use its stores";
        if (!InReach(s)) return "Dock within reach of its town hall";
        if (IsHostileTo(s, PlayerState)) return "You are at war with it";
        return null;
    }

    private float Scarcity(Settlement s, int resource) => Math.Clamp(1.6f - s.Stores[resource] / 40f, 0.5f, 2f);

    /// <summary>Gold the town charges the player per unit.</summary>
    public int BuyPrice(Settlement s, int resource)
    {
        float v = _w.Defs.Resources[resource].Value;
        return (int)MathF.Ceiling(v * Scarcity(s, resource) * 1.15f);
    }

    /// <summary>Gold the town pays the player per unit (mercantile owners pay better).</summary>
    public int SellPrice(Settlement s, int resource)
    {
        float v = _w.Defs.Resources[resource].Value;
        float trade = s.Owner?.Behaviour.Trade ?? 0.5f;
        return Math.Max(1, (int)MathF.Floor(v * Scarcity(s, resource) * 0.85f * (0.9f + trade * 0.2f)));
    }

    /// <summary>The merchants' purse: gold the town's traders have to buy the player's goods.</summary>
    public float MerchantFunds(Settlement s) => s.Treasury;

    /// <summary>Buys up to <paramref name="amount"/> units from a town. Returns units bought.</summary>
    public int BuyFrom(Settlement s, int resource, int amount)
    {
        if (CannotTrade(s) != null) return 0;
        int bought = 0;
        while (bought < amount && s.Stores[resource] > 0 && _w.Player.Cargo.Free > 0)
        {
            int price = BuyPrice(s, resource);
            if (_w.Gold < price) break;
            _w.Gold -= price;
            s.Treasury += price;
            s.Stores.TransferTo(_w.Player.Cargo, resource, 1);
            bought++;
        }
        if (bought > 0) Goodwill(s, bought);
        return bought;
    }

    /// <summary>Sells up to <paramref name="amount"/> units to a town (while it can pay). Returns units sold.</summary>
    public int SellTo(Settlement s, int resource, int amount)
    {
        if (CannotTrade(s) != null) return 0;
        int sold = 0;
        while (sold < amount && _w.Player.Cargo[resource] > 0 && s.Stores.Free > 0)
        {
            int price = SellPrice(s, resource);
            if (s.Treasury < price) break;
            s.Treasury -= price;
            _w.EarnGold(price);
            _w.Player.Cargo.TransferTo(s.Stores, resource, 1);
            sold++;
        }
        if (sold > 0) Goodwill(s, sold);
        return sold;
    }

    private void Goodwill(Settlement s, int units)
    {
        if (s.Owner == null || s.Owner.IsPlayer) return;
        Diplomacy.AdjustOpinion(PlayerState, s.Owner, Math.Min(3f, units * 0.15f * (0.5f + s.Owner.Behaviour.Trade)));
    }
}
