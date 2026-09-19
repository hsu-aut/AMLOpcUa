// The document's bound values kept current: the same bindings a snapshot reads
// (NodeId with a Value attribute, BPR DataVariables, aml-opcua-variable), but
// subscribed, so every change the server reports is written into the document.
//
// Aml.Engine documents are not thread safe and the stack reports changes on
// its own threads, so every write goes through the caller's dispatcher (the
// editor's UI thread in the plugin).

using Aml.Engine.CAEX;

namespace OpcUaAml.Server;

/// <summary>What a live update changed, for a status line.</summary>
public sealed record LiveUpdate(string What, string? Value, int Updates);

public sealed class LiveValues : IAsyncDisposable
{
    private readonly IAsyncDisposable _watch;
    private readonly Counter _counter;

    /// <summary>How many bound values are followed.</summary>
    public int Count { get; }

    /// <summary>Bindings to another server, left out.</summary>
    public int Skipped { get; }

    /// <summary>Bindings that could not be read (a malformed NodeId, for instance).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Values written into the document so far.</summary>
    public int Updates => Volatile.Read(ref _counter.Value);

    private LiveValues(IAsyncDisposable watch, Counter counter, int count, int skipped, IReadOnlyList<string> problems)
    {
        _watch = watch;
        _counter = counter;
        Count = count;
        Skipped = skipped;
        Problems = problems;
    }

    /// <summary>
    /// Subscribes to every bound value the client's server serves.
    /// <paramref name="dispatch"/> runs a write where the document may be
    /// changed; <paramref name="changed"/> hears of each write, after it.
    /// </summary>
    public static async Task<LiveValues> FollowAsync(CAEXDocument doc, UaClient client, Action<Action> dispatch,
        Action<LiveUpdate>? changed = null, int publishingIntervalMs = 1000, CancellationToken ct = default)
    {
        var (targets, problems, skipped) = await ValueSnapshot.ResolvedTargetsAsync(doc, client, ct);
        // One monitored item per node, even when several elements are bound to it.
        var byAddress = targets.GroupBy(t => t.Address).ToDictionary(g => g.Key, g => g.ToList());
        // The first values arrive while the subscription is created, before this object exists.
        var counter = new Counter();
        var watch = byAddress.Count == 0
            ? new Nothing()
            : await client.WatchAsync(byAddress.Keys.ToList(), result =>
            {
                if (!result.Good || !byAddress.TryGetValue(result.Address, out var bound)) return;
                dispatch(() =>
                {
                    foreach (var target in bound) target.Write(result);
                    var updates = Interlocked.Add(ref counter.Value, bound.Count);
                    changed?.Invoke(new LiveUpdate(bound[0].What, result.ValueText, updates));
                });
            }, publishingIntervalMs, ct);
        return new LiveValues(watch, counter, targets.Count, skipped, problems);
    }

    public ValueTask DisposeAsync() => _watch.DisposeAsync();

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
