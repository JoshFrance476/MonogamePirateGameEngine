using PirateEngine.Core.Persistence;

namespace PirateEngine.Core.Politics;

public sealed partial class PoliticsSystem
{
    internal PoliticsSave Save()
    {
        var data = new PoliticsSave { UsedNames = _usedNames.ToList(), SetupId = SetupId };
        foreach (var s in _states)
        {
            data.States.Add(new StateSave
            {
                Id = s.Id, Name = s.Name, Color = s.Color, Behaviour = s.Behaviour.Id, IsPlayer = s.IsPlayer,
                Founded = s.Founded, Collapsed = s.Collapsed, Treasury = s.Treasury, FieldArmy = s.FieldArmy,
                CapitalId = s.Capital?.Id, InitialTowns = s.InitialTowns, NextWarAllowed = s.NextWarAllowed,
                NextCampaign = s.NextCampaign,
            });
        }
        foreach (var r in Diplomacy.All)
        {
            data.Relations.Add(new RelationSave
            {
                A = r.A.Id, B = r.B.Id, Stance = r.Stance, Opinion = r.Opinion, TruceUntil = r.TruceUntil,
                WarWeariness = r.WarWeariness, Since = r.Since, Baseline = r.Baseline, GainsA = r.GainsA, GainsB = r.GainsB,
            });
        }
        foreach (var s in _settlements)
        {
            data.Settlements.Add(new SettlementSave
            {
                Id = s.Id, Name = s.Name, OwnerId = s.Owner?.Id, TownHallId = s.TownHall.Id,
                Population = s.Population, Treasury = s.Treasury, FedRatio = s.FedRatio, FoundedAt = s.FoundedAt,
                Garrison = s.Garrison + s.Militia.Where(m => !m.IsDead).Sum(m => m.Members),
                Stores = s.Stores.NonZero().ToDictionary(t => _w.Defs.Resources[t.resource].Id, t => t.amount),
                Aggressors = s.Aggressors.Select(a => a.Id).ToList(),
                Siege = s.Siege == null ? null : new SiegeSave
                {
                    AttackerId = s.Siege.Attacker.Id, Soldiers = s.Siege.SoldiersTotal,
                    Progress = s.Siege.Progress, StartedAt = s.Siege.StartedAt,
                },
            });
        }
        foreach (var o in _offers) data.Offers.Add(new OfferSave { From = o.From.Id, Kind = o.Kind, Expires = o.Expires });
        return data;
    }

    internal void Restore(PoliticsSave data, List<BuildingSave> buildings)
    {
        foreach (var n in data.UsedNames) _usedNames.Add(n);
        SetupId = data.SetupId;
        var byId = new Dictionary<int, State> { [PlayerState.Id] = PlayerState };
        foreach (var ss in data.States)
        {
            State st;
            if (ss.IsPlayer) st = PlayerState;
            else
            {
                var beh = Cfg.Behaviours.FirstOrDefault(b => b.Id == ss.Behaviour) ?? Cfg.Behaviours.FirstOrDefault() ?? PlayerBehaviour;
                st = new State(ss.Id, ss.Name, ss.Color, beh, isPlayer: false, Diplomacy);
                _states.Add(st);
                _nextStateId = Math.Max(_nextStateId, ss.Id + 1);
            }
            st.Name = ss.Name;
            st.Founded = ss.Founded;
            st.Collapsed = ss.Collapsed;
            st.Treasury = ss.Treasury;
            st.FieldArmy = ss.FieldArmy;
            st.InitialTowns = ss.InitialTowns;
            st.NextWarAllowed = ss.NextWarAllowed;
            st.NextCampaign = ss.NextCampaign;
            byId[ss.Id] = st;
        }
        foreach (var rs in data.Relations)
        {
            if (!byId.TryGetValue(rs.A, out var a) || !byId.TryGetValue(rs.B, out var b) || a == b) continue;
            var r = Diplomacy.Get(a, b);
            r.Stance = rs.Stance;
            r.Opinion = rs.Opinion;
            r.TruceUntil = rs.TruceUntil;
            r.WarWeariness = rs.WarWeariness;
            r.Since = rs.Since;
            r.Baseline = rs.Baseline;
            r.GainsA = rs.GainsA;
            r.GainsB = rs.GainsB;
        }
        foreach (var ss in data.Settlements)
        {
            var hall = _w.GetBuilding(ss.TownHallId);
            if (hall == null) continue;
            var owner = ss.OwnerId is int oid ? byId.GetValueOrDefault(oid) : null;
            var s = new Settlement(ss.Id, ss.Name, hall, _w.Defs.ResourceCount, Cfg.Levels[0].Storage)
            {
                Owner = owner, Population = ss.Population, Garrison = ss.Garrison, Treasury = ss.Treasury,
                FedRatio = ss.FedRatio, FoundedAt = ss.FoundedAt,
            };
            hall.Settlement = s;
            _settlements.Add(s);
            owner?.SettlementList.Add(s);
            RefreshLevel(s);
            var arr = new int[_w.Defs.ResourceCount];
            foreach (var (id, n) in ss.Stores) if (_w.Defs.TryResource(id, out var res)) arr[res.Index] = n;
            s.Stores.Load(arr);
            foreach (var a in ss.Aggressors) if (byId.TryGetValue(a, out var ag)) s.Aggressors.Add(ag);
            if (ss.Siege is { } sg && byId.TryGetValue(sg.AttackerId, out var atk))
                s.Siege = new Siege(atk, s, sg.Soldiers, sg.StartedAt) { Progress = sg.Progress };
        }
        foreach (var bs in buildings)
        {
            if (bs.SettlementId is not int sid) continue;
            var b = _w.GetBuilding(bs.Id);
            var s = FindSettlement(sid);
            if (b != null && s != null && b != s.TownHall) Attach(b, s);
        }
        foreach (var ss in data.States)
            if (ss.CapitalId is int cid && byId.TryGetValue(ss.Id, out var st)) st.Capital = FindSettlement(cid);
        foreach (var os in data.Offers)
            if (byId.TryGetValue(os.From, out var from)) _offers.Add(new DiplomaticOffer(from, os.Kind, os.Expires));
    }
}
