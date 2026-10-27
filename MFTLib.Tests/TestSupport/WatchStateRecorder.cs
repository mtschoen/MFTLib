using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Records every <see cref="FileIndex.WatchStateChanged" /> state and every
///     <see cref="FileIndex.WatchFaulted" /> fault of one or more indices in the single order their
///     handlers ran, so a test can assert both what changed and how states interleave with faults.
///     Waits are settled by this recorder's own handlers, so a completed wait proves the recorder
///     has seen the event, never by polling.
/// </summary>
sealed class WatchStateRecorder
{
    readonly Lock _gate = new();
    readonly List<object> _events = [];
    readonly List<(Func<object, bool> Match, TaskCompletionSource<object> Completion)> _waiters = [];

    public WatchStateRecorder(params FileIndex[] indices)
        : this((IEnumerable<FileIndex>)indices)
    {
    }

    public WatchStateRecorder(IEnumerable<FileIndex> indices)
    {
        foreach (var index in indices)
        {
            index.WatchStateChanged += Record;
            index.WatchFaulted += Record;
        }
    }

    /// <summary>Every recorded <see cref="WatchStateChange" /> and <see cref="WatchFault" />, in order.</summary>
    public IReadOnlyList<object> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The root's recorded states and faults, in order.</summary>
    public IReadOnlyList<object> EventsFor(char driveLetter) => [.. Events.Where(item => DriveOf(item) == driveLetter)];

    /// <summary>The root's recorded states, in order.</summary>
    public IReadOnlyList<WatchStateChange> StatesFor(char driveLetter) =>
        [.. EventsFor(driveLetter).OfType<WatchStateChange>()];

    /// <summary>Completes with the first state of the root, recorded or future, that <paramref name="match" /> accepts.</summary>
    public async Task<WatchStateChange> WaitForStateAsync(char driveLetter, Func<WatchStateChange, bool> match) =>
        (WatchStateChange)await WaitForAsync(item =>
            item is WatchStateChange state && state.Volume == driveLetter && match(state));

    /// <summary>Completes with the first fault of <paramref name="kind" /> on the root that this recorder has seen.</summary>
    public async Task<WatchFault> WaitForFaultAsync(WatchFaultKind kind, char driveLetter) =>
        (WatchFault)await WaitForAsync(item =>
            item is WatchFault fault && fault.Kind == kind && fault.Volume == driveLetter);

    static char DriveOf(object item) =>
        item is WatchStateChange state ? state.Volume.Letter : ((WatchFault)item).Volume.Letter;

    Task<object> WaitForAsync(Func<object, bool> match)
    {
        lock (_gate)
        {
            if (_events.FirstOrDefault(match) is { } recorded)
            {
                return Task.FromResult(recorded);
            }

            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, completion));
            return completion.Task.WaitAsync(HangGuard);
        }
    }

    void Record(object item)
    {
        List<TaskCompletionSource<object>> matched = [];
        lock (_gate)
        {
            _events.Add(item);
            for (var index = _waiters.Count - 1; index >= 0; index--)
            {
                if (_waiters[index].Match(item))
                {
                    matched.Add(_waiters[index].Completion);
                    _waiters.RemoveAt(index);
                }
            }
        }

        foreach (var completion in matched)
        {
            completion.TrySetResult(item);
        }
    }

    void Record(WatchStateChange state) => Record((object)state);

    void Record(WatchFault fault) => Record((object)fault);
}
