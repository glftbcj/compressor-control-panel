namespace Hvacr.App;

/// <summary>Wake all live pages on a state change without queuing old telemetry.</summary>
public sealed class StatusUpdates
{
    private readonly object _sync = new();
    private long _version;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Version { get { lock (_sync) return _version; } }
    public void Notify()
    {
        TaskCompletionSource previous;
        lock (_sync) { _version++; previous = _changed; _changed = NewSignal(); }
        previous.TrySetResult();
    }
    public async Task WaitAsync(long version, CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync) { if (_version != version) return; task = _changed.Task; }
        try { await task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
        catch (TimeoutException) { /* Also check command timeouts and connection health while idle. */ }
    }
}
