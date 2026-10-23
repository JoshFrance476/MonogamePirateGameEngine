using System.Numerics;
using PirateEngine.Core.Entities;
using PirateEngine.Core.Navigation;
using PirateEngine.Core.Util;

namespace PirateEngine.Core.Simulation;

/// <summary>
/// Real-time movement for ships and land units alike. An entity always travels in a straight
/// line toward its current waypoint (the smoothed path guarantees those legs stay in its
/// domain), so a ship can never be pushed onto land and a squad can never walk into the sea.
/// Heading turns at the entity's turn rate and it slows while facing far off course — which
/// reads as a sailing ship coming about. Land units additionally move at 1/terrain-cost speed,
/// so they visibly slow down in forest, swamp and mountains.
/// </summary>
public static class MovementSystem
{
    private const float Acceleration = 2.0f;   // fraction of top speed per second
    private const float Deceleration = 3.0f;

    public static void Move(GameWorld world, MobileEntity s, float dt)
    {
        var before = s.Position;
        s.PreviousPosition = s.Position;
        s.PreviousHeading = s.Heading;
        float top = s.TopSpeed;
        if (s.Domain == NavDomain.Land) top /= world.LandPathfinder.CostAt(s.Tile);

        if (s.IsMoving)
        {
            var target = s.Path[s.PathIndex].Center;
            var to = target - s.Position;
            float dist = to.Length();
            if (dist > 1e-4f)
            {
                float desired = MathF.Atan2(to.Y, to.X);
                s.Heading = MathUtil.MoveTowardsAngle(s.Heading, desired, s.TurnRate * dt);
                float align = MathF.Cos(MathUtil.AngleDelta(s.Heading, desired));
                float wanted = top * Math.Clamp(align, 0.25f, 1f);
                // Ease off when arriving at the final waypoint.
                if (s.PathIndex == s.Path.Count - 1) wanted = Math.Min(wanted, MathF.Max(0.8f, dist * 2.5f));
                s.Speed = Approach(s.Speed, wanted, (wanted > s.Speed ? Acceleration : Deceleration) * top * dt);
            }

            float step = s.Speed * dt;
            // Consume as many waypoints as this step covers.
            while (s.IsMoving)
            {
                target = s.Path[s.PathIndex].Center;
                to = target - s.Position;
                dist = to.Length();
                if (step >= dist)
                {
                    s.Position = target;
                    step -= dist;
                    s.PathIndex++;
                    if (step <= 0) break;
                }
                else
                {
                    s.Position += to / dist * step;
                    break;
                }
            }
            if (!s.IsMoving) s.ClearPath();
        }
        else
        {
            s.Speed = Approach(s.Speed, 0, Deceleration * Math.Max(top, 1f) * dt);
        }

        s.Velocity = (s.Position - before) / dt;
    }

    /// <summary>
    /// Gently pushes overlapping entities of the same domain apart. Only idle ones are displaced,
    /// and only onto tiles of their own domain, so moving entities keep their guaranteed-clear legs.
    /// </summary>
    public static void Separate<T>(GameWorld world, IReadOnlyList<T> list, float minDist) where T : MobileEntity
    {
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.IsDead) continue;
            for (int j = i + 1; j < list.Count; j++)
            {
                var b = list[j];
                if (b.IsDead) continue;
                var d = b.Position - a.Position;
                float len = d.Length();
                if (len >= minDist) continue;
                var dir = len > 1e-4f ? d / len : new Vector2(1, 0);
                float push = (minDist - len) * 0.5f;
                TryNudge(world, a, -dir * push);
                TryNudge(world, b, dir * push);
            }
        }
    }

    private static void TryNudge(GameWorld world, MobileEntity s, Vector2 delta)
    {
        if (s.IsMoving || s is Ship { IsDocked: true }) return;
        var p = s.Position + delta;
        var pf = world.PathfinderFor(s.Domain);
        if (pf.Passable(GridPoint.FromWorld(p))) s.Position = p;
    }

    private static float Approach(float v, float target, float maxDelta) =>
        v < target ? Math.Min(v + maxDelta, target) : Math.Max(v - maxDelta, target);
}
