using System.Collections.Concurrent;

namespace OpcUaAml.Tests;

/// <summary>
/// A thread with a synchronization context of its own, as the editor's UI
/// thread has one: continuations posted to it run on it, one after another.
/// </summary>
public sealed class UiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "UI" };
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Runs <paramref name="work"/> on the thread and waits for it.</summary>
    public Task<T> Run<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((async _ =>
        {
            try { done.SetResult(await work()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null));
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private sealed class Context(UiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner._queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;
    }
}
