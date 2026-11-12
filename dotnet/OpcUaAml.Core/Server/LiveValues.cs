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

    /// <summary>
    /// Bindings whose node this server refused to follow, mostly because it
    /// does not have it (BadNodeIdUnknown); not in <see cref="Count"/>, each
    /// named in <see cref="Problems"/> with the server's status.
    /// </summary>
    public int NotOnServer { get; }

    /// <summary>Bindings that could not be read (a malformed NodeId, for instance).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Values written into the document so far.</summary>
    public int Updates => Volatile.Read(ref _counter.Value);

    private LiveValues(IAsyncDisposable watch, Counter counter, int count, int skipped, int notOnServer, IReadOnlyList<string> problems)
    {
        _watch = watch;
        _counter = counter;
        Count = count;
        Skipped = skipped;
        NotOnServer = notOnServer;
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
        if (byAddress.Count == 0) return new LiveValues(new Nothing(), counter, 0, skipped, 0, problems);
        // A server sends many values at once (a hundred every half second for the
        // MPS 500). Each one a call of its own on the editor's UI thread, through its
        // services one by one, made the editor hardly answer. They are gathered, the
        // newest per node, and written together in one call.
        var pending = new Dictionary<Addressing.UaNodeAddress, UaReadResult>();
        var flushQueued = 0;
        void Flush()
        {
            Interlocked.Exchange(ref flushQueued, 0);
            List<UaReadResult> batch;
            lock (pending)
            {
                batch = pending.Values.ToList();
                pending.Clear();
            }
            if (batch.Count == 0) return;
            var written = new List<(string What, string? Value, int Count)>();
            Import.EditorServices.WithoutUndo(doc, () =>
            {
                foreach (var result in batch)
                {
                    var bound = byAddress[result.Address];
                    foreach (var target in bound) target.Write(result);
                    written.Add((bound[0].What, result.ValueText, bound.Count));
                }
            });
            foreach (var (what, value, count) in written)
            {
                // Counted whether or not anyone listens.
                var updates = Interlocked.Add(ref counter.Value, count);
                changed?.Invoke(new LiveUpdate(what, value, updates));
            }
        }
        var (watch, refused) = await client.WatchCoreAsync(byAddress.Keys.ToList(), result =>
        {
            if (!result.Good || !byAddress.ContainsKey(result.Address)) return;
            lock (pending) pending[result.Address] = result;
            if (Interlocked.Exchange(ref flushQueued, 1) == 0) dispatch(Flush);
        }, publishingIntervalMs, ct);
        // A node the server refused is never heard of again: named here, not counted as followed.
        var notOnServer = 0;
        foreach (var r in refused)
        {
            if (!byAddress.TryGetValue(r.Address, out var bound)) continue;
            notOnServer += bound.Count;
            problems.AddRange(bound.Select(t => $"{t.What}: not followed, {r.Status}"));
        }
        return new LiveValues(watch, counter, targets.Count - notOnServer, skipped, notOnServer, problems);
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
