using System.Numerics;
using System.Text.Json;
using PirateEngine.Core.AI;
using PirateEngine.Core.Data;
using PirateEngine.Core.Economy;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Persistence;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Simulation;

public sealed partial class GameWorld
{
    // ---- saving ------------------------------------------------------------------

    public SaveData ToSaveData()
    {
        var data = new SaveData
        {
            Generation = GenSettings,
            Time = Time,
            Gold = Gold,
            ShipsSunk = ShipsSunkByPlayer,
            GoldEarned = TotalGoldEarned,
            RngState = Rng.State,
            NextId = _nextId,
            Player = SaveShip(Player),
            FogEnabled = Fog.Enabled,
            Fog = Fog.Serialize(),
        };
        foreach (var s in _ships)
            if (s != Player && !s.IsSunk) data.AiShips.Add(SaveShip(s));
        foreach (var b in _buildings)
        {
            data.Buildings.Add(new BuildingSave
            {
                Id = b.Id, Def = b.Def.Id, X = b.Tile.X, Y = b.Tile.Y,
                Input = ToDict(b.Input), Output = ToDict(b.Output),
                ActiveRecipe = b.ActiveRecipe, Progress = b.CycleProgress, Cycles = b.CyclesCompleted,
                NextRecipe = b.NextRecipeIndex, SettlementId = b.Settlement?.Id,
            });
        }
        foreach (var u in Player.Troops) data.Units.Add(SaveUnit(u, embarked: true));
        // Militia and assault squads are saved as numbers in their settlement / siege instead.
        foreach (var u in _units) if (!u.IsDead && u.Faction != Faction.State && !u.IsMilitia) data.Units.Add(SaveUnit(u, embarked: false));
        data.Politics = Politics.Save();
        data.SquadNumbers = new Dictionary<string, int>(_squadNumbers);
        data.UnitsDefeated = UnitsDefeatedByPlayer;
        foreach (var w in _wrecks)
        {
            data.Wrecks.Add(new WreckSave
            {
                X = w.Position.X, Y = w.Position.Y, Gold = w.Gold, TimeLeft = w.TimeLeft,
                Cargo = w.Cargo.Select((a, i) => (a, i)).Where(t => t.a > 0).ToDictionary(t => Defs.Resources[t.i].Id, t => t.a),
            });
        }
        return data;
    }

    private UnitSave SaveUnit(LandUnit u, bool embarked)
    {
        var home = (u.Brain as GarrisonBrain)?.Home;
        return new UnitSave
        {
            Id = u.Id, Name = u.Name, Unit = u.Def.Id, Faction = u.Faction, Hp = u.Hp, Embarked = embarked,
            X = u.Position.X, Y = u.Position.Y, Heading = u.Heading,
            SpawnIndex = u.Spawn != null ? Config.UnitSpawns.IndexOf(u.Spawn) : -1,
            HomeX = home?.X, HomeY = home?.Y,
        };
    }

    private ShipSave SaveShip(Ship s) => new()
    {
        Id = s.Id, Name = s.Name, Class = s.Class.Id, Faction = s.Faction, Role = s.Role,
        X = s.Position.X, Y = s.Position.Y, Heading = s.Heading, Hull = s.Hull,
        Cargo = ToDict(s.Cargo),
        DockX = s.IsDocked ? s.DockTile.X : null, DockY = s.IsDocked ? s.DockTile.Y : null,
        Provoked = s.Provoked,
        SpawnIndex = s.Spawn != null ? Config.Spawns.IndexOf(s.Spawn) : -1,
    };

    private Dictionary<string, int> ToDict(Inventory inv) =>
        inv.NonZero().ToDictionary(t => Defs.Resources[t.resource].Id, t => t.amount);

    private void FromDict(Inventory inv, Dictionary<string, int> d)
    {
        var arr = new int[Defs.ResourceCount];
        foreach (var (id, amount) in d)
            if (Defs.TryResource(id, out var r)) arr[r.Index] = amount; // unknown ids (removed mods) are dropped
        inv.Load(arr);
    }

    public void SaveToFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ToSaveData(), DefinitionDatabase.JsonOptions));
        File.Move(tmp, path, overwrite: true); // atomic replace: a crash mid-save can't corrupt the old file
    }

    // ---- loading -----------------------------------------------------------------

    public static GameWorld LoadFromFile(string path, DefinitionDatabase defs, IProgress<(string, float)>? progress = null)
    {
        var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path), DefinitionDatabase.JsonOptions)
                   ?? throw new InvalidDataException("Empty save file.");
        return FromSaveData(data, defs, progress);
    }

    public static GameWorld FromSaveData(SaveData data, DefinitionDatabase defs, IProgress<(string, float)>? progress = null)
    {
        if (data.Version > SaveData.SaveVersion)
            throw new InvalidDataException($"Save version {data.Version} is newer than this build supports ({SaveData.SaveVersion}).");

        var map = WorldGenerator.Generate(data.Generation, progress);
        var w = new GameWorld(map, defs, data.Generation, 1)
        {
            Time = data.Time,
            Gold = data.Gold,
            ShipsSunkByPlayer = data.ShipsSunk,
            TotalGoldEarned = data.GoldEarned,
        };
        w.Rng.State = data.RngState;
        w.Events.Clock = data.Time;

        w.Player = w.RestoreShip(data.Player);
        w.Player.Allegiance = w.Politics.PlayerState;
        foreach (var s in data.AiShips)
        {
            if (s.SpawnIndex < 0 || s.SpawnIndex >= defs.Config.Spawns.Count) continue; // spawn table changed
            w.RestoreShip(s);
        }
        foreach (var b in data.Buildings)
        {
            var p = new GridPoint(b.X, b.Y);
            if (!defs.Buildings.Any(d => d.Id.Equals(b.Def, StringComparison.OrdinalIgnoreCase))) continue;
            if (!map.IsLand(p)) continue;
            var building = w.AddBuildingInternal(defs.Building(b.Def), p, b.Id);
            w.FromDict(building.Input, b.Input);
            w.FromDict(building.Output, b.Output);
            building.RestoreState(b.ActiveRecipe, b.Progress, b.Cycles, b.NextRecipe);
        }
        foreach (var ws in data.Wrecks)
        {
            var cargo = new int[defs.ResourceCount];
            foreach (var (id, a) in ws.Cargo) if (defs.TryResource(id, out var r)) cargo[r.Index] = a;
            w._wrecks.Add(new Wreck { Id = w.NextId(), Position = new Vector2(ws.X, ws.Y), Gold = ws.Gold, Cargo = cargo, TimeLeft = ws.TimeLeft });
        }

        foreach (var us in data.Units)
        {
            if (!defs.TryUnit(us.Unit, out var def)) continue; // unit type removed from content
            if (us.Faction == Faction.State) continue; // older saves: re-mustered from garrisons instead
            if (us.Embarked)
            {
                var u = w.CreateEmbarkedUnit(def, w.Player, us.Name, us.Id);
                u.Hp = Math.Clamp(us.Hp, 1, def.MaxHp);
                continue;
            }
            var tile = GridPoint.FromWorld(new Vector2(us.X, us.Y));
            if (!map.IsLand(tile)) continue;
            var unit = w.AddUnitAshore(def, us.Faction, tile, us.Name, us.Id);
            unit.Position = unit.PreviousPosition = new Vector2(us.X, us.Y);
            unit.Heading = us.Heading;
            unit.Hp = Math.Clamp(us.Hp, 1, def.MaxHp);
            if (us.Faction == Faction.Player) unit.Allegiance = w.Politics.PlayerState;
            if (us.Faction != Faction.Player)
            {
                if (us.SpawnIndex >= 0 && us.SpawnIndex < defs.Config.UnitSpawns.Count) unit.Spawn = defs.Config.UnitSpawns[us.SpawnIndex];
                var home = us.HomeX is int hx && us.HomeY is int hy ? new GridPoint(hx, hy) : tile;
                unit.Brain = new GarrisonBrain(home);
            }
        }
        foreach (var (k, v) in data.SquadNumbers) w._squadNumbers[k] = v;
        w.Politics.Restore(data.Politics, data.Buildings);
        w.UnitsDefeatedByPlayer = data.UnitsDefeated;

        w.Fog.Enabled = data.FogEnabled;
        if (!string.IsNullOrEmpty(data.Fog)) w.Fog.Deserialize(data.Fog);
        w._nextId = Math.Max(w._nextId, data.NextId);
        w.RevealAroundPlayer(force: true);
        w.Events.Post("Game loaded.", MessageKind.Info);
        return w;
    }

    private Ship RestoreShip(ShipSave s)
    {
        var cls = Defs.Ship(s.Class);
        var spawn = s.SpawnIndex >= 0 ? Config.Spawns[s.SpawnIndex] : null;
        var ship = new Ship(s.Id, s.Name, cls, s.Faction, s.Role, new Vector2(s.X, s.Y), Defs.ResourceCount)
        {
            Heading = s.Heading,
            Hull = Math.Clamp(s.Hull, 1, cls.Hull),
            Provoked = s.Provoked,
            Spawn = spawn,
        };
        FromDict(ship.Cargo, s.Cargo);
        if (s.DockX is int dx && s.DockY is int dy) ship.DockTile = new GridPoint(dx, dy);
        if (s.Role == ShipRole.Merchant) ship.Brain = new MerchantBrain(ship.Tile);
        else if (s.Role == ShipRole.Hunter) ship.Brain = new HunterBrain(ship.Tile);
        _ships.Add(ship);
        _nextId = Math.Max(_nextId, s.Id + 1);
        return ship;
    }
}
