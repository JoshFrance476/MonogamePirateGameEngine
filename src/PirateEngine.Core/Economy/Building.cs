using PirateEngine.Core.Data;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Economy;

public enum ProductionState { Idle, Producing, WaitingForInputs, OutputFull, NoRecipe }

/// <summary>
/// A placed building. Production model: when idle, try each recipe (round-robin from the last
/// one used); if its inputs are in the input store and its outputs will fit in the output
/// store, consume the inputs and start a timed cycle. On completion, emit outputs (and gold).
/// Inputs are reserved at cycle start, so loading/unloading mid-cycle can never break it.
/// </summary>
public sealed class Building
{
    public int Id { get; }
    public BuildingDef Def { get; }
    public GridPoint Tile { get; }
    /// <summary>Landmass region id this building sits on.</summary>
    public int Region { get; }
    /// <summary>
    /// The settlement this building belongs to (its town hall's settlement for the town hall
    /// itself), or null for a player outpost outside any town.
    /// </summary>
    public Politics.Settlement? Settlement { get; internal set; }
    public bool IsTownHall => Def.Services.Contains("town_hall");

    public Inventory Input { get; }
    public Inventory Output { get; }

    public ProductionState State { get; private set; } = ProductionState.Idle;
    public int ActiveRecipe { get; private set; } = -1;
    public float CycleProgress { get; private set; }
    public int CyclesCompleted { get; private set; }
    private int _nextRecipe;

    public RecipeDef? CurrentRecipe => ActiveRecipe >= 0 ? Def.Recipes[ActiveRecipe] : null;
    public float Progress01 => CurrentRecipe is { } r ? Math.Clamp(CycleProgress / r.CycleSeconds, 0f, 1f) : 0f;

    public Building(int id, BuildingDef def, GridPoint tile, int region, int resourceTypes)
    {
        Id = id;
        Def = def;
        Tile = tile;
        Region = region;
        Input = new Inventory(resourceTypes, def.Storage);
        Output = new Inventory(resourceTypes, def.Storage);
        if (def.Recipes.Count == 0) State = ProductionState.NoRecipe;
    }

    /// <summary>Advances production. Returns gold earned this tick.</summary>
    public int Update(float dt)
    {
        if (Def.Recipes.Count == 0) return 0;
        int gold = 0;

        if (ActiveRecipe < 0) TryStartCycle();
        if (ActiveRecipe < 0) return 0;

        var recipe = Def.Recipes[ActiveRecipe];
        CycleProgress += dt;
        if (CycleProgress < recipe.CycleSeconds) return 0;

        // Cycle complete. Output space was checked at start, but the player may have
        // unloaded outputs since then (only freeing space), so this always fits.
        foreach (var (res, amount) in recipe.OutputList) Output.Add(res, amount);
        gold += recipe.GoldOutput;
        CyclesCompleted++;
        float carry = CycleProgress - recipe.CycleSeconds;
        ActiveRecipe = -1;
        CycleProgress = 0;
        State = ProductionState.Idle;

        // Chain straight into the next cycle so high time-scales don't lose throughput.
        if (TryStartCycle()) CycleProgress = Math.Min(carry, Def.Recipes[ActiveRecipe].CycleSeconds * 0.99f);
        return gold;
    }

    private bool TryStartCycle()
    {
        int n = Def.Recipes.Count;
        bool anyOutputBlocked = false;
        for (int k = 0; k < n; k++)
        {
            int idx = (_nextRecipe + k) % n;
            var r = Def.Recipes[idx];
            if (!Input.HasAll(r.InputList)) continue;
            if (Output.Free < Inventory.Sum(r.OutputList)) { anyOutputBlocked = true; continue; }

            Input.TryRemoveAll(r.InputList);
            ActiveRecipe = idx;
            CycleProgress = 0;
            _nextRecipe = (idx + 1) % n;
            State = ProductionState.Producing;
            return true;
        }
        State = anyOutputBlocked ? ProductionState.OutputFull : ProductionState.WaitingForInputs;
        return false;
    }

    /// <summary>How many more units of <paramref name="resource"/> this building will accept.</summary>
    public int InputSpaceFor(int resource) => Def.AcceptsInput[resource] ? Input.Free : 0;

    /// <summary>Restores saved state (used by the save system).</summary>
    internal void RestoreState(int activeRecipe, float progress, int cycles, int nextRecipe)
    {
        ActiveRecipe = activeRecipe >= 0 && activeRecipe < Def.Recipes.Count ? activeRecipe : -1;
        CycleProgress = progress;
        CyclesCompleted = cycles;
        _nextRecipe = nextRecipe;
        State = ActiveRecipe >= 0 ? ProductionState.Producing
            : Def.Recipes.Count == 0 ? ProductionState.NoRecipe : ProductionState.Idle;
    }

    internal int NextRecipeIndex => _nextRecipe;

    public bool HasService(string service) => Def.Services.Contains(service, StringComparer.OrdinalIgnoreCase);
}
