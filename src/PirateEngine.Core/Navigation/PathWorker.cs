using System.Collections.Concurrent;
using PirateEngine.Core.Util;
using PirateEngine.Core.World;

namespace PirateEngine.Core.Navigation;

/// <summary>
/// Plans long paths on a background thread so a click across a 1000×1000 map never stalls
/// a frame. The map's water layout is immutable after generation, so the worker's private
/// <see cref="Pathfinder"/> can read it without locking. Results are collected on the
/// simulation thread via <see cref="Drain"/>.
/// </summary>
public sealed class PathWorker : IDisposable
{
    public readonly record struct Request(int Ticket, GridPoint Start, GridPoint Goal, int Budget);
    public readonly record struct Result(int Ticket, PathStatus Status, GridPoint[] Path);

    private readonly BlockingCollection<Request> _requests = new();
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly Thread _thread;
    private readonly Pathfinder _pathfinder;
    private int _nextTicket;

    public PathWorker(TileMap map, NavDomain domain = NavDomain.Water)
    {
        _pathfinder = new Pathfinder(map, domain);
        _thread = new Thread(Run) { IsBackground = true, Name = $"PathWorker-{domain}", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int Enqueue(GridPoint start, GridPoint goal, int budget)
    {
        int ticket = Interlocked.Increment(ref _nextTicket);
        _requests.Add(new Request(ticket, start, goal, budget));
        return ticket;
    }

    public bool TryDequeue(out Result result) => _results.TryDequeue(out result);

    private void Run()
    {
        var path = new List<GridPoint>();
        try
        {
            foreach (var r in _requests.GetConsumingEnumerable())
            {
                var status = _pathfinder.FindPath(r.Start, r.Goal, path, r.Budget);
                _results.Enqueue(new Result(r.Ticket, status, path.ToArray()));
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // collection completed
    }

    public void Dispose()
    {
        _requests.CompleteAdding();
        _thread.Join(500);
        _requests.Dispose();
    }
}
