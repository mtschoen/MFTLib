using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
/// An owned scan-to-watch session over one elevated journal broker. Wraps a single
/// connected <see cref="JournalBrokerClient"/> and its most recent
/// <see cref="BrokerScanResult"/> so that discovery and live watching share one elevated
/// process (one UAC prompt) and one pipe. The session is the sole owner and sole disposer
/// of the underlying client; the client is never exposed, which prevents the discovery
/// layer and the watch layer from both disposing it.
/// </summary>
public sealed partial class JournalBrokerScanSession : IAsyncDisposable
{
    static readonly BrokerScanResult EmptyScanResult = new(
        records: Array.Empty<ScanRecord>(),
        armedCursors: new Dictionary<string, UsnJournalCursor>(),
        advancedCursors: new Dictionary<string, UsnJournalCursor>(),
        catchUpEntries: new Dictionary<string, UsnJournalEntry[]>(),
        errors: new Dictionary<string, string>());

    readonly JournalBrokerClient _client;
    readonly object _stateLock = new();
    readonly IReadOnlyList<string> _drives;
    readonly BrokerScanProfile _profile;
    readonly IReadOnlyCollection<string>? _keepFileNames;

    JournalBrokerSessionState _state;
    BrokerScanResult _latestScan;
    bool _isFaulted;
    string? _faultReason;
    Action<string>? _faultedHandlers;
    int _disposed;
    // Cached by StartWatchAsync (Task 3), consumed by WatchDriveAsync, cleared by
    // StopWatchAsync. Null whenever the session has never started a watch.
    JournalBatchSource? _batchSource;

    JournalBrokerScanSession(JournalBrokerClient client, IReadOnlyList<string> drives, BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames)
    {
        _client = client;
        _drives = drives;
        _profile = profile;
        _keepFileNames = keepFileNames;
        // Placeholder until the internal StartAsync seam replaces it with the real
        // scan result (it either sets this and Parked, or disposes and throws) -
        // never observed by a caller since the session escapes only on that path.
        _latestScan = EmptyScanResult;
        _client.BrokerDied += OnBrokerDied;
    }

    /// <summary>Current lifecycle state. Safe to read at any time, including after fault or disposal.</summary>
    public JournalBrokerSessionState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary>
    /// The most recent scan result. Set by the initial scan and replaced by each
    /// rescan. Immutable between rescans; exposes per-drive records, armed and
    /// advanced cursors, catch-up entries, and per-drive errors.
    /// </summary>
    public BrokerScanResult LatestScan
    {
        get { lock (_stateLock) return _latestScan; }
    }

    /// <summary>True once the broker has died. Never reverts.</summary>
    public bool IsFaulted
    {
        get { lock (_stateLock) return _isFaulted; }
    }

    /// <summary>The reason the broker died, or null while <see cref="IsFaulted"/> is false.</summary>
    public string? FaultReason
    {
        get { lock (_stateLock) return _faultReason; }
    }

    /// <summary>
    /// Raised once when the broker dies. If a handler is added after death already
    /// occurred, it is invoked immediately with <see cref="FaultReason"/>, so a consumer
    /// that attaches after discovery cannot miss a death that happened while parked.
    /// </summary>
    public event Action<string>? Faulted
    {
        add
        {
            string? lateReason = null;
            lock (_stateLock)
            {
                if (_isFaulted)
                    lateReason = _faultReason;
                else
                    _faultedHandlers += value;
            }
            if (lateReason != null)
                value?.Invoke(lateReason);
        }
        remove
        {
            lock (_stateLock)
                _faultedHandlers -= value;
        }
    }

    /// <summary>Drives the session was started or last rescanned with.</summary>
    internal IReadOnlyList<string> Drives => _drives;

    /// <summary>Scan profile the session was started or last rescanned with.</summary>
    internal BrokerScanProfile Profile => _profile;

    /// <summary>
    /// Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker"/>),
    /// arm and scan <paramref name="drives"/> with <see cref="BrokerScanProfile.Full"/>,
    /// and return a session parked on the result. Throws
    /// <see cref="InvalidOperationException"/> if the broker declines to launch or
    /// dies before the scan completes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        CancellationToken cancellationToken = default) =>
        StartAsync(launchBroker, drives, BrokerScanProfile.Full, cancellationToken: cancellationToken);

    /// <summary>
    /// As <see cref="StartAsync(Func{string,bool},IReadOnlyList{string},CancellationToken)"/>
    /// but with an explicit <paramref name="profile"/> and, under
    /// <see cref="BrokerScanProfile.DirectoryIndex"/>, an optional set of non-directory
    /// <paramref name="keepFileNames"/> to keep alongside every directory record.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default) =>
        StartAsync(
            ct => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, ct),
            drives, profile, keepFileNames, cancellationToken);

    // The public overloads above delegate here with connectAsync = ct =>
    // JournalBrokerClient.SpawnAndConnectAsync(launchBroker, ct). Tests inject a fake
    // client built over an in-memory duplex stream.
    internal static async Task<JournalBrokerScanSession> StartAsync(
        Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default)
    {
        var client = await connectAsync(cancellationToken).ConfigureAwait(false);
        var session = new JournalBrokerScanSession(client, drives, profile, keepFileNames);
        try
        {
            var result = await client.ArmScanAndCatchUpAsync(drives, profile, keepFileNames, cancellationToken)
                .ConfigureAwait(false);
            lock (session._stateLock)
            {
                if (!session._isFaulted)
                {
                    session._latestScan = result;
                    session._state = JournalBrokerSessionState.Parked;
                }
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (session.TryGetFaultReason(out var reason))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(reason);
        }

        return session;
    }

    bool TryGetFaultReason(out string? reason)
    {
        lock (_stateLock)
        {
            reason = _faultReason;
            return _isFaulted;
        }
    }

    /// <summary>
    /// Dispose the session: stop any live watch, send the broker <c>Shutdown</c>, close
    /// the pipe, and release memory maps. Idempotent - the underlying client is disposed
    /// exactly once no matter how many times this is called.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_stateLock)
            _state = JournalBrokerSessionState.Disposed;

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    // Throws if the session cannot currently accept a state-changing operation:
    // ObjectDisposedException once disposed, InvalidOperationException (carrying
    // FaultReason) once faulted. The queries above never call this - they stay safe to
    // read in every state. Task 3/4 operations call this before they run.
    internal void EnsureOperable()
    {
        lock (_stateLock)
            EnsureOperableLocked();
    }

    // Same check as EnsureOperable, for a caller that already holds _stateLock (lock
    // re-entry on the same thread is safe but this avoids the confusing double-enter).
    // Watch.cs uses this to recheck for a terminal state that raced ahead of it during
    // an awaited client call, in the same critical section that would otherwise commit
    // a live-state transition - so Disposed/Faulted is never resurrected back to
    // Watching/Parked (mirrors OnBrokerDied never overwriting Disposed below).
    void EnsureOperableLocked()
    {
        ObjectDisposedException.ThrowIf(_state == JournalBrokerSessionState.Disposed, this);
        if (_state == JournalBrokerSessionState.Faulted)
            throw new InvalidOperationException(_faultReason);
    }

    void OnBrokerDied(string reason)
    {
        Action<string>? handlers;
        lock (_stateLock)
        {
            if (_isFaulted)
                return;
            _isFaulted = true;
            _faultReason = reason;
            if (_state != JournalBrokerSessionState.Disposed)
                _state = JournalBrokerSessionState.Faulted;
            handlers = _faultedHandlers;
        }
        handlers?.Invoke(reason);
    }
}
