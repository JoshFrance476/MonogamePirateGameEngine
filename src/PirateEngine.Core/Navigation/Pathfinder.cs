using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Navigation;

public enum PathStatus { Found, AlreadyThere, StartBlocked, GoalBlocked, Unreachable, BudgetExceeded }

/// <summary>What a pathfinder can traverse: ships sail on water, land units walk on land.</summary>
public enum NavDomain { Water, Land }

/// <summary>
/// A* over one navigation domain (water for ships, land for troops), 8-directional without
/// corner-cutting, octile heuristic. On land each tile also has a terrain cost (forest, hills,
/// swamp, mountains are slower), so routes go around rough ground when that's quicker.
/// <para>
/// Built for large maps: all per-node buffers are allocated once and invalidated with a
/// generation stamp instead of being cleared, so a search costs only what it touches.
/// Unreachable goals are rejected in O(1) using the map's precomputed region ids, so a
/// click on a landlocked lake never floods the whole ocean.
/// </para>
/// Not thread-safe: use one instance per thread.
/// </summary>
public sealed class Pathfinder
{
    private const float Diag = 1.41421356f;

    private readonly TileMap _map;
    public NavDomain Domain { get; }

    // All search buffers use a *padded* grid ((W+2)×(H+2)) whose border is permanently
    // impassable, so the inner loop needs no bounds checks. Water never changes at runtime
    // (buildings go on land), so passability is snapshotted once; call Refresh() if a mod
    // ever edits terrain.
    private readonly int _pw;
    private readonly bool[] _pass;
    private readonly float[]? _cost;   // per-tile step cost (land only; water is uniform)
    private readonly float[] _g;
    private readonly int[] _parent;
    private readonly int[] _state;     // == _gen → seen this search, == _gen + 1 → closed
    private readonly int[] _offset;    // neighbour index deltas, same order as GridPoint.Neighbours8
    private int _gen;
    private readonly MinHeap _open = new(4096);

    /// <summary>Optional extra traversal cost per map tile index (e.g. slower shallows). Return ≥ 0.</summary>
    public Func<int, float>? ExtraCost { get; set; }

    public int LastExpansions { get; private set; }

    /// <summary>
    /// Heuristic weight used for long trips (octile distance above <see cref="LongTripDistance"/>).
    /// Weighted A* explores far fewer nodes on a big map; the result is at most this factor
    /// longer than optimal and in practice within a few percent after smoothing.
    /// </summary>
    public float LongTripWeight { get; set; } = 1.35f;
    public float LongTripDistance { get; set; } = 48f;

    public Pathfinder(TileMap map, NavDomain domain = NavDomain.Water)
    {
        _map = map;
        Domain = domain;
        _pw = map.Width + 2;
        int n = _pw * (map.Height + 2);
        _pass = new bool[n];
        if (domain == NavDomain.Land) _cost = new float[n];
        _g = new float[n];
        _parent = new int[n];
        _state = new int[n];
        _offset = GridPoint.Neighbours8.Select(o => o.Y * _pw + o.X).ToArray();
        Refresh();
    }

    /// <summary>Re-reads passability (and terrain costs) from the map.</summary>
    public void Refresh()
    {
        for (int y = 0; y < _map.Height; y++)
            for (int x = 0; x < _map.Width; x++)
            {
                var b = _map.Biome[_map.Index(x, y)];
                int i = (y + 1) * _pw + x + 1;
                _pass[i] = Biomes.IsWater(b) == (Domain == NavDomain.Water);
                if (_cost != null) _cost[i] = Biomes.LandCost(b);
            }
    }

    /// <summary>Whether a tile can be entered in this pathfinder's domain.</summary>
    public bool Passable(int x, int y) => (uint)x < (uint)_map.Width && (uint)y < (uint)_map.Height && _pass[(y + 1) * _pw + x + 1];
    public bool Passable(GridPoint p) => Passable(p.X, p.Y);

    /// <summary>Terrain cost of a tile (1 for water).</summary>
    public float CostAt(GridPoint p) => _cost != null && Passable(p) ? _cost[(p.Y + 1) * _pw + p.X + 1] : 1f;

    private int Pad(GridPoint p) => (p.Y + 1) * _pw + p.X + 1;
    private GridPoint Unpad(int i) => new(i % _pw - 1, i / _pw - 1);

    /// <summary>
    /// Finds a path from <paramref name="start"/> to <paramref name="goal"/>. On success
    /// <paramref name="result"/> holds the tile sequence (excluding start, including goal),
    /// smoothed into straight legs when <paramref name="smooth"/> is set.
    /// </summary>
    public PathStatus FindPath(GridPoint start, GridPoint goal, List<GridPoint> result,
        int maxExpansions = 250_000, bool smooth = true)
    {
        result.Clear();
        if (!Passable(start)) return PathStatus.StartBlocked;
        if (!Passable(goal)) return PathStatus.GoalBlocked;
        if (start == goal) return PathStatus.AlreadyThere;
        if (_map.RegionAt(start) != _map.RegionAt(goal)) return PathStatus.Unreachable;

        NewGeneration();
        int gen = _gen, closed = _gen + 1, pw = _pw;
        int s = Pad(start), g = Pad(goal);
        int gx = goal.X + 1, gy = goal.Y + 1;
        _g[s] = 0; _parent[s] = -1; _state[s] = gen;
        _open.Clear();
        float h0 = Heuristic(start.X, start.Y, goal.X, goal.Y);
        _open.Push(s, h0);

        // Even for short trips the heuristic is inflated by 0.1%, which breaks ties toward
        // the goal and avoids flooding open water with equal-cost nodes.
        float weight = h0 > LongTripDistance ? LongTripWeight : 1.001f;
        var pass = _pass; var state = _state; var gArr = _g; var off = _offset;
        var extra = ExtraCost; var cost = _cost;
        int expansions = 0;
        bool found = false;
        while (_open.Count > 0)
        {
            int cur = _open.Pop();
            if (state[cur] == closed) continue; // stale duplicate entry
            state[cur] = closed;
            if (cur == g) { found = true; break; }
            if (++expansions > maxExpansions) { LastExpansions = expansions; return PathStatus.BudgetExceeded; }

            int cx = cur % pw, cy = cur / pw;
            float gc = gArr[cur];
            for (int k = 0; k < 8; k++)
            {
                int ni = cur + off[k];
                if (!pass[ni] || state[ni] == closed) continue;
                float step = 1f;
                if (k >= 4)
                {
                    // No squeezing diagonally between two blocked tiles.
                    var o = GridPoint.Neighbours8[k];
                    if (!pass[cur + o.X] || !pass[cur + o.Y * pw]) continue;
                    step = Diag;
                }
                if (cost != null) step *= 0.5f * (cost[cur] + cost[ni]); // average of both tiles
                if (extra != null) step += extra(_map.Index(Unpad(ni)));

                float ng = gc + step;
                if (state[ni] == gen && ng >= gArr[ni]) continue;
                state[ni] = gen;
                gArr[ni] = ng;
                _parent[ni] = cur;
                var d = GridPoint.Neighbours8[k];
                _open.Push(ni, ng + Heuristic(cx + d.X, cy + d.Y, gx, gy) * weight);
            }
        }
        LastExpansions = expansions;
        if (!found) return PathStatus.Unreachable;

        for (int i = g; i != s; i = _parent[i]) result.Add(Unpad(i));
        result.Reverse();
        if (smooth) Smooth(start, result);
        return PathStatus.Found;
    }

    private static float Heuristic(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x0 - x1), dy = Math.Abs(y0 - y1);
        return Math.Max(dx, dy) + (Diag - 1f) * Math.Min(dx, dy);
    }

    private void NewGeneration()
    {
        _gen += 2;
        if (_gen >= int.MaxValue - 2)
        {
            Array.Clear(_state);
            _gen = 2;
        }
    }

    /// <summary>String-pulling: drop waypoints that have clear line of sight past them.</summary>
    private void Smooth(GridPoint start, List<GridPoint> path)
    {
        if (path.Count < 3) return;
        var output = new List<GridPoint>(path.Count);
        var anchor = start;
        for (int i = 0; i < path.Count; i++)
        {
            bool last = i == path.Count - 1;
            if (last || !HasLineOfSight(anchor, path[i + 1], SmoothCap(anchor, path[i + 1])))
            {
                output.Add(path[i]);
                anchor = path[i];
            }
        }
        path.Clear();
        path.AddRange(output);
    }

    /// <summary>
    /// On land, smoothing may only cut across tiles no rougher than the two endpoints, so a
    /// path that A* routed around a mountain isn't pulled straight back over it.
    /// </summary>
    private float SmoothCap(GridPoint a, GridPoint b) =>
        _cost == null ? float.MaxValue : Math.Max(CostAt(a), CostAt(b)) + 1e-3f;

    private bool Ok(int x, int y, float maxCost) =>
        Passable(x, y) && (_cost == null || _cost[(y + 1) * _pw + x + 1] <= maxCost);

    /// <summary>
    /// True if a straight segment between tile centres crosses only passable tiles (no more
    /// costly than <paramref name="maxCost"/>). Supercover traversal, so it can't slip through
    /// a diagonal corner.
    /// </summary>
    public bool HasLineOfSight(GridPoint a, GridPoint b, float maxCost = float.MaxValue)
    {
        int x = a.X, y = a.Y;
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        int sx = Math.Sign(b.X - a.X), sy = Math.Sign(b.Y - a.Y);
        int n = dx + dy;
        int err = dx - dy;
        dx *= 2; dy *= 2;
        for (; n > 0; n--)
        {
            if (!Ok(x, y, maxCost)) return false;
            if (err > 0) { x += sx; err -= dy; }
            else if (err < 0) { y += sy; err += dx; }
            else
            {
                // Passing exactly through a corner: both side tiles must be passable.
                if (!Ok(x + sx, y, maxCost) || !Ok(x, y + sy, maxCost)) return false;
                x += sx; y += sy; err += dx - dy; n--;
            }
        }
        return Ok(x, y, maxCost);
    }

    /// <summary>
    /// True if the straight segment between two arbitrary world positions passes only over
    /// passable tiles (Amanatides–Woo grid traversal). Used when an entity that is between
    /// tiles receives a new path, so its first leg can't clip a blocked corner.
    /// </summary>
    public bool SegmentClear(System.Numerics.Vector2 a, System.Numerics.Vector2 b)
    {
        int x = (int)MathF.Floor(a.X), y = (int)MathF.Floor(a.Y);
        int ex = (int)MathF.Floor(b.X), ey = (int)MathF.Floor(b.Y);
        float dx = b.X - a.X, dy = b.Y - a.Y;
        int stepX = Math.Sign(dx), stepY = Math.Sign(dy);
        float tDeltaX = stepX != 0 ? 1f / MathF.Abs(dx) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? 1f / MathF.Abs(dy) : float.PositiveInfinity;
        float tMaxX = stepX > 0 ? (x + 1 - a.X) * tDeltaX : stepX < 0 ? (a.X - x) * tDeltaX : float.PositiveInfinity;
        float tMaxY = stepY > 0 ? (y + 1 - a.Y) * tDeltaY : stepY < 0 ? (a.Y - y) * tDeltaY : float.PositiveInfinity;

        int guard = Math.Abs(ex - x) + Math.Abs(ey - y) + 2;
        while (guard-- > 0)
        {
            if (!Passable(x, y)) return false;
            if (x == ex && y == ey) return true;
            const float eps = 1e-6f;
            if (tMaxX < tMaxY - eps) { x += stepX; tMaxX += tDeltaX; }
            else if (tMaxY < tMaxX - eps) { y += stepY; tMaxY += tDeltaY; }
            else
            {
                if (!Passable(x + stepX, y) || !Passable(x, y + stepY)) return false;
                x += stepX; y += stepY;
                tMaxX += tDeltaX; tMaxY += tDeltaY;
            }
        }
        return Passable(ex, ey);
    }

    /// <summary>Binary min-heap of (node, priority); duplicates allowed (lazy deletion).</summary>
    private sealed class MinHeap
    {
        private int[] _nodes;
        private float[] _prio;
        public int Count { get; private set; }

        public MinHeap(int capacity)
        {
            _nodes = new int[capacity];
            _prio = new float[capacity];
        }

        public void Clear() => Count = 0;

        public void Push(int node, float priority)
        {
            if (Count == _nodes.Length)
            {
                Array.Resize(ref _nodes, Count * 2);
                Array.Resize(ref _prio, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_prio[parent] <= priority) break;
                _nodes[i] = _nodes[parent];
                _prio[i] = _prio[parent];
                i = parent;
            }
            _nodes[i] = node;
            _prio[i] = priority;
        }

        public int Pop()
        {
            int result = _nodes[0];
            int lastNode = _nodes[--Count];
            float lastPrio = _prio[Count];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= Count) break;
                if (c + 1 < Count && _prio[c + 1] < _prio[c]) c++;
                if (_prio[c] >= lastPrio) break;
                _nodes[i] = _nodes[c];
                _prio[i] = _prio[c];
                i = c;
            }
            _nodes[i] = lastNode;
            _prio[i] = lastPrio;
            return result;
        }
    }
}
