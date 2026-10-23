using System.Numerics;
using PirateEngine.Core.AI;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Simulation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Combat;

/// <summary>
/// All fighting: ship broadsides, musket volleys and melee.
/// <list type="bullet">
/// <item><b>Ships</b> fire a volley (one ball per gun) when reloaded, with a hostile target in
/// range and within the broadside arc. Targets can be ships or troops ashore (balls do
/// <see cref="Data.GameConfig.CannonVsTroopsMultiplier"/>× damage to troops).</item>
/// <item><b>Ranged squads</b> fire one musket ball per standing soldier at troops or ships in
/// range. Muskets have no firing arc — soldiers turn to face.</item>
/// <item><b>Melee squads</b> strike troops in contact; damage scales with soldiers standing.</item>
/// <item>Balls fly in real time with angular and range error; a ball hits the first hostile
/// ship or squad it passes over.</item>
/// </list>
/// The player fires at its explicit target, otherwise at the nearest hostile that bears.
/// Squads (player and AI) likewise shoot or strike at anything hostile in reach.
/// </summary>
public sealed class CombatSystem
{
    public const float HitRadius = 0.45f;
    public const float MusketSpeed = 22f;
    private readonly GameWorld _w;

    public CombatSystem(GameWorld world) => _w = world;

    public void Update(float dt)
    {
        UpdateShips(dt);
        UpdateUnits(dt);
        UpdateProjectiles(dt);
    }

    // ---- ships ------------------------------------------------------------------------

    private void UpdateShips(float dt)
    {
        int ammo = _w.Defs.ResourceIndex(_w.Config.AmmoResource);
        foreach (var s in _w.Ships)
        {
            if (s.IsSunk) continue;
            if (s.ReloadTimer > 0) s.ReloadTimer -= dt;
            if (s.ReloadTimer > 0) continue;

            var target = ChooseShipTarget(s);
            if (target == null) continue;

            int guns = s.Class.Guns;
            if (s.Faction == Faction.Player && _w.Config.PlayerUsesAmmo)
            {
                guns = Math.Min(guns, s.Cargo[ammo]);
                if (guns <= 0)
                {
                    if (s.Target != null) _w.Events.Post("Out of cannonballs! Build a Foundry to make more.", MessageKind.Danger);
                    s.ReloadTimer = 6f; // re-check (and re-warn) only occasionally
                    continue;
                }
                s.Cargo.Remove(ammo, guns);
            }
            FireVolley(s, target, guns);
        }
    }

    private MobileEntity? ChooseShipTarget(Ship s)
    {
        float range = s.Class.Range;
        if (s.Target is { IsDead: false } t)
        {
            if (s.DistanceTo(t) <= range && InArc(s, t.Position)) return t;
            if (s.Faction == Faction.Player) return NearestInArc(s, range, includeShips: true); // fire on what bears meanwhile
            return null; // AI holds fire for its target
        }
        // Player: anything hostile that bears. AI ships with no target defend against troops on the shore.
        return NearestInArc(s, range, includeShips: s.Faction == Faction.Player);
    }

    private MobileEntity? NearestInArc(Ship s, float range, bool includeShips)
    {
        MobileEntity? best = null;
        float bestD = range;
        if (includeShips)
        {
            foreach (var o in _w.Ships)
            {
                if (o.IsSunk || o == s || !GameWorld.IsHostile(s, o)) continue;
                float d = s.DistanceTo(o);
                if (d <= bestD && InArc(s, o.Position)) { bestD = d; best = o; }
            }
        }
        foreach (var u in _w.Units)
        {
            if (u.IsDead || !GameWorld.IsHostile(s, u)) continue;
            float d = s.DistanceTo(u);
            if (d <= bestD && InArc(s, u.Position)) { bestD = d; best = u; }
        }
        return best;
    }

    /// <summary>True if <paramref name="point"/> is within the broadside arc of <paramref name="s"/>.</summary>
    public bool InArc(Ship s, Vector2 point)
    {
        var to = point - s.Position;
        if (to.LengthSquared() < 1e-6f) return true;
        float bearing = MathF.Atan2(to.Y, to.X);
        float rel = MathF.Abs(MathUtil.AngleDelta(s.Heading, bearing)); // 0 = dead ahead, π = astern
        float offBeam = MathF.Abs(rel - MathF.PI / 2);
        return offBeam <= _w.Config.BroadsideArcDegrees * MathF.PI / 180f;
    }

    private void FireVolley(Ship s, MobileEntity target, int guns)
    {
        s.ReloadTimer = s.Class.Reload * (0.9f + 0.2f * _w.Rng.NextFloat());
        Provoke(s, target);
        var fwd = s.Forward;
        int vsUnits = (int)MathF.Round(s.Class.Damage * _w.Config.CannonVsTroopsMultiplier);
        for (int g = 0; g < guns; g++)
        {
            // Spread the guns along the hull.
            float along = guns == 1 ? 0 : (g / (float)(guns - 1) - 0.5f) * 0.6f;
            Launch(ProjectileKind.Cannonball, s, s.Position + fwd * along, target, _w.Config.CannonballSpeed,
                s.Class.Accuracy, s.Class.Damage, vsUnits);
        }
        _w.AddEffect(EffectKind.Muzzle, s.Position);
        _w.Events.RaiseVolley(s);
    }

    // ---- land units ---------------------------------------------------------------------

    private void UpdateUnits(float dt)
    {
        foreach (var u in _w.Units)
        {
            if (u.IsDead) continue;
            if (u.ReloadTimer > 0) u.ReloadTimer -= dt;
            if (u.ReloadTimer > 0 || u.PendingEmbark != null) continue;

            var target = ChooseUnitTarget(u);
            if (target == null) continue;
            if (u.Def.IsRanged) FireMuskets(u, target);
            else Strike(u, target);
        }
    }

    private bool CanHit(LandUnit u, MobileEntity t) =>
        !t.IsDead && t is not LandUnit { IsEmbarked: true } && (u.Def.IsRanged || t is LandUnit) &&
        u.DistanceTo(t) <= u.Def.Range;

    private MobileEntity? ChooseUnitTarget(LandUnit u)
    {
        if (u.Target != null && CanHit(u, u.Target)) return u.Target;
        // Otherwise anything hostile in reach: troops first, then (musketeers only) ships.
        var troops = UnitSteering.NearestHostileUnit(_w, u, u.Def.Range);
        if (troops != null) return troops;
        if (!u.Def.IsRanged) return null;
        Ship? best = null;
        float bestD = u.Def.Range;
        foreach (var s in _w.Ships)
        {
            if (s.IsSunk || !GameWorld.IsHostile(u, s)) continue;
            float d = u.DistanceTo(s);
            if (d <= bestD) { bestD = d; best = s; }
        }
        return best;
    }

    private void Strike(LandUnit u, MobileEntity target)
    {
        u.ReloadTimer = u.Def.Reload * (0.9f + 0.2f * _w.Rng.NextFloat());
        UnitSteering.FaceTowards(u, target.Position);
        int dmg = (int)MathF.Round(u.Members * u.Def.Damage * _w.Rng.Range(0.8f, 1.2f));
        _w.AddEffect(EffectKind.Clash, Vector2.Lerp(u.Position, target.Position, 0.5f));
        ApplyDamage(target, u, Math.Max(1, dmg));
    }

    private void FireMuskets(LandUnit u, MobileEntity target)
    {
        u.ReloadTimer = u.Def.Reload * (0.9f + 0.2f * _w.Rng.NextFloat());
        UnitSteering.FaceTowards(u, target.Position);
        Provoke(u, target);
        int shots = u.Members;
        var side = new Vector2(-MathF.Sin(u.Heading), MathF.Cos(u.Heading));
        for (int i = 0; i < shots; i++)
        {
            // Soldiers stand in a line abreast; each fires from his own place in it.
            float offset = shots == 1 ? 0 : (i / (float)(shots - 1) - 0.5f) * 0.5f;
            Launch(ProjectileKind.Musket, u, u.Position + side * offset, target, MusketSpeed,
                u.Def.Accuracy, u.Def.DamageVsShips, u.Def.Damage);
        }
        _w.AddEffect(EffectKind.MusketSmoke, u.Position + u.Forward * 0.3f);
        _w.Events.RaiseVolley(u);
    }

    // ---- shared ballistics ----------------------------------------------------------------

    private void Provoke(MobileEntity shooter, MobileEntity target)
    {
        if (shooter.Faction == Faction.Player && target.Faction == Faction.Merchant) target.Provoked = true;
    }

    /// <summary>
    /// Fires one ball at a (moving) target: leads it, then applies angular error (left/right)
    /// and range error (short/long). A ball that falls short lands harmlessly; one that goes long
    /// can still strike what it passes over.
    /// </summary>
    private void Launch(ProjectileKind kind, MobileEntity owner, Vector2 origin, MobileEntity target, float speed,
        float accuracy, int vsShips, int vsUnits)
    {
        float dist = Vector2.Distance(origin, target.Position);
        var predicted = target.Position + target.Velocity * (dist / speed);
        var toAim = predicted - origin;
        float len = toAim.Length();
        if (len < 1e-3f) return;
        float inacc = 1f - accuracy;
        float maxAngle = 0.05f + inacc * 0.3f;
        float ang = _w.Rng.Range(-maxAngle, maxAngle);
        var dir = toAim / len;
        dir = new Vector2(dir.X * MathF.Cos(ang) - dir.Y * MathF.Sin(ang), dir.X * MathF.Sin(ang) + dir.Y * MathF.Cos(ang));
        float rangeFactor = 1f + _w.Rng.Range(-0.6f, 0.4f) * inacc;
        _w.AddProjectile(new Projectile
        {
            Kind = kind,
            Position = origin,
            PreviousPosition = origin,
            Velocity = dir * speed,
            RemainingDistance = len * rangeFactor,
            DamageVsShips = vsShips,
            DamageVsUnits = vsUnits,
            Owner = owner,
        });
    }

    private void UpdateProjectiles(float dt)
    {
        foreach (var p in _w.ProjectileList)
        {
            if (p.Dead) continue;
            var start = p.Position;
            p.PreviousPosition = start;
            float stepLen = Math.Min(p.Velocity.Length() * dt, p.RemainingDistance);
            var end = start + Vector2.Normalize(p.Velocity) * stepLen;
            p.RemainingDistance -= stepLen;
            p.Position = end;

            MobileEntity? hit = null;
            float bestT = float.MaxValue;
            foreach (var s in _w.Ships)
            {
                if (s.IsSunk || s == p.Owner || !GameWorld.IsHostile(p.Owner, s)) continue;
                if (SegmentHits(start, end, s.Position, s.HitRadius, out float t) && t < bestT) { bestT = t; hit = s; }
            }
            foreach (var u in _w.Units)
            {
                if (u.IsDead || u == p.Owner || !GameWorld.IsHostile(p.Owner, u)) continue;
                if (SegmentHits(start, end, u.Position, u.HitRadius, out float t) && t < bestT) { bestT = t; hit = u; }
            }

            if (hit != null)
            {
                p.Dead = true;
                p.Position = Vector2.Lerp(start, end, bestT);
                int dmg = hit is Ship ? p.DamageVsShips : p.DamageVsUnits;
                if (dmg > 0) ApplyDamage(hit, p.Owner, dmg);
                _w.AddEffect(hit is Ship ? EffectKind.Hit : EffectKind.UnitHit, p.Position);
            }
            else if (p.RemainingDistance <= 0)
            {
                p.Dead = true;
                if (p.Kind == ProjectileKind.Cannonball)
                    _w.AddEffect(_w.Map.IsWater(GridPoint.FromWorld(p.Position)) ? EffectKind.Splash : EffectKind.Dust, p.Position);
            }
        }
    }

    /// <summary>Applies damage to a ship (hull) or a squad (hit points), handling sinking and death.</summary>
    public void ApplyDamage(MobileEntity victim, MobileEntity attacker, int damage)
    {
        victim.LastAttacker = attacker;
        victim.TimeSinceDamaged = 0;
        Provoke(attacker, victim);
        switch (victim)
        {
            case Ship ship:
                ship.Hull -= damage;
                _w.Events.RaiseDamaged(ship, attacker, damage);
                if (ship.Hull <= 0) _w.SinkShip(ship, attacker);
                break;
            case LandUnit unit:
                unit.Hp -= damage;
                _w.Events.RaiseUnitDamaged(unit, attacker, damage);
                if (unit.Hp <= 0) _w.KillUnit(unit, attacker);
                break;
        }
    }

    /// <summary>Segment–circle intersection; <paramref name="t"/> is the first contact along the segment (0..1).</summary>
    public static bool SegmentHits(Vector2 a, Vector2 b, Vector2 c, float r, out float t)
    {
        var d = b - a;
        var f = a - c;
        float dd = Vector2.Dot(d, d);
        t = 0;
        if (dd < 1e-8f) return f.LengthSquared() <= r * r;
        float tc = Math.Clamp(-Vector2.Dot(f, d) / dd, 0f, 1f);
        var closest = a + d * tc;
        if (Vector2.DistanceSquared(closest, c) > r * r) return false;
        // Back off to the entry point for correct ordering between multiple targets.
        float bq = Vector2.Dot(f, d);
        float disc = bq * bq - dd * (Vector2.Dot(f, f) - r * r);
        t = disc > 0 ? Math.Clamp((-bq - MathF.Sqrt(disc)) / dd, 0f, 1f) : tc;
        return true;
    }
}
