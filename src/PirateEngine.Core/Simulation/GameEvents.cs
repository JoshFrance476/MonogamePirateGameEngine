using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;

namespace PirateEngine.Core.Simulation;

public enum MessageKind { Info, Good, Warning, Danger }

public readonly record struct GameMessage(string Text, MessageKind Kind, float Time);

/// <summary>
/// Simulation → presentation notifications. The renderer, audio and UI subscribe here; the
/// simulation never references them back.
/// </summary>
public sealed class GameEvents
{
    private readonly LinkedList<GameMessage> _log = new();
    public int MaxLogEntries { get; set; } = 60;
    public IEnumerable<GameMessage> Log => _log;

    public event Action<GameMessage>? MessagePosted;
    public event Action<Ship, MobileEntity?>? ShipSunk;           // victim, killer
    public event Action<Ship, MobileEntity, int>? ShipDamaged;    // victim, attacker, damage
    /// <summary>A ship broadside or a musket volley.</summary>
    public event Action<MobileEntity>? VolleyFired;
    public event Action<LandUnit, MobileEntity, int>? UnitDamaged; // victim, attacker, damage
    public event Action<LandUnit, MobileEntity?>? UnitKilled;      // victim, killer
    public event Action<LandUnit>? UnitLanded;
    public event Action<LandUnit>? UnitEmbarked;
    public event Action<Building>? BuildingPlaced;
    public event Action<Building>? BuildingRemoved;
    public event Action<Ship>? ShipDocked;
    public event Action<Ship>? ShipUndocked;
    public event Action<Ship>? PlayerRespawned;
    public event Action<Politics.Settlement>? SettlementFounded;
    /// <summary>settlement, previous owner, new owner (null = independent).</summary>
    public event Action<Politics.Settlement, Politics.State?, Politics.State?>? SettlementCaptured;
    public event Action<Politics.State, Politics.State>? WarDeclared;
    public event Action<Politics.State, Politics.State>? PeaceMade;
    public event Action<Politics.State, Politics.State>? AllianceFormed;
    public event Action<Politics.State>? StateCollapsed;
    public event Action<Politics.Siege>? SiegeStarted;

    internal float Clock;

    public void Post(string text, MessageKind kind = MessageKind.Info)
    {
        var m = new GameMessage(text, kind, Clock);
        _log.AddLast(m);
        while (_log.Count > MaxLogEntries) _log.RemoveFirst();
        MessagePosted?.Invoke(m);
    }

    internal void RaiseSunk(Ship victim, MobileEntity? killer) => ShipSunk?.Invoke(victim, killer);
    internal void RaiseDamaged(Ship victim, MobileEntity attacker, int dmg) => ShipDamaged?.Invoke(victim, attacker, dmg);
    internal void RaiseVolley(MobileEntity s) => VolleyFired?.Invoke(s);
    internal void RaiseUnitDamaged(LandUnit victim, MobileEntity attacker, int dmg) => UnitDamaged?.Invoke(victim, attacker, dmg);
    internal void RaiseUnitKilled(LandUnit victim, MobileEntity? killer) => UnitKilled?.Invoke(victim, killer);
    internal void RaiseUnitLanded(LandUnit u) => UnitLanded?.Invoke(u);
    internal void RaiseUnitEmbarked(LandUnit u) => UnitEmbarked?.Invoke(u);
    internal void RaiseBuildingPlaced(Building b) => BuildingPlaced?.Invoke(b);
    internal void RaiseBuildingRemoved(Building b) => BuildingRemoved?.Invoke(b);
    internal void RaiseDocked(Ship s) => ShipDocked?.Invoke(s);
    internal void RaiseUndocked(Ship s) => ShipUndocked?.Invoke(s);
    internal void RaiseRespawned(Ship s) => PlayerRespawned?.Invoke(s);
    internal void RaiseSettlementFounded(Politics.Settlement s) => SettlementFounded?.Invoke(s);
    internal void RaiseSettlementCaptured(Politics.Settlement s, Politics.State? from, Politics.State? to) => SettlementCaptured?.Invoke(s, from, to);
    internal void RaiseWarDeclared(Politics.State a, Politics.State b) => WarDeclared?.Invoke(a, b);
    internal void RaisePeaceMade(Politics.State a, Politics.State b) => PeaceMade?.Invoke(a, b);
    internal void RaiseAllianceFormed(Politics.State a, Politics.State b) => AllianceFormed?.Invoke(a, b);
    internal void RaiseStateCollapsed(Politics.State s) => StateCollapsed?.Invoke(s);
    internal void RaiseSiegeStarted(Politics.Siege s) => SiegeStarted?.Invoke(s);
}
