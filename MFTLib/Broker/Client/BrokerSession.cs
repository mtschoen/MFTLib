using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Owns one elevated broker process for a consumer session: launches it on first use,
///     reports its end and disposes it. Each scan or watch opens its own drive channel on the process.
/// </summary>
/// <remarks>
///     <para>
///         Launch is lazy and shared. The first use starts one launch; every concurrent use waits for the
///         same one. A caller's cancellation ends only that caller's wait and never aborts the launch, which
///         only disposal cancels. A launch that fails (the UAC prompt declined, a connect timeout) is not
///         cached: the next use launches again and raises <see cref="Connecting" /> again.
///     </para>
///     <para>
///         The launch runs on a thread-pool thread, never on the caller's thread or synchronization context,
///         because the launcher blocks on the UAC prompt. A launch that disposal overtakes before the launcher
///         runs, including disposal started by a <see cref="Connecting" /> subscriber, never runs the launcher
///         and fails its callers with <see cref="OperationCanceledException" />.
///     </para>
///     <para>
///         A process that ends stays ended. <see cref="Ended" /> completes with the reason, and every later
///         use throws <see cref="InvalidOperationException" /> naming it. The session never relaunches behind
///         the indexes that still reference the dead process: the owner closes those indexes, disposes this
///         session and creates a new one. <see cref="Ended" /> reports only a broker that ended: disposing the
///         session never completes it, and every use after disposal throws <see cref="ObjectDisposedException" />.
///     </para>
///     <para>
///         <see cref="Connecting" /> and <see cref="Connected" /> run on the launching thread-pool thread, outside
///         every gate the session holds, so a subscriber may call any member. A subscriber that throws does
///         not corrupt the session: every other subscriber still runs, and the exception reaches the callers
///         waiting on that launch. A throwing <see cref="Connecting" /> subscriber fails the attempt before
///         anything launches. A throwing <see cref="Connected" /> subscriber fails that attempt after the
///         process exists; the session keeps and owns the process, and the next use returns it.
///     </para>
/// </remarks>
public sealed class BrokerSession : IAsyncDisposable
{
    const string DisposedWhileConnectingReason = "The broker session was disposed while connecting.";

    readonly Func<CancellationToken, Task<BrokerProcess>> _launchAsync;
    readonly TaskScheduler _launchScheduler;
    readonly Lock _gate = new();
    readonly CancellationTokenSource _stopping = new();
    readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<BrokerProcess>? _launch;
    Task<BrokerProcess>? _launcherResult;
    BrokerProcess? _process;
    Task _endObservation = Task.CompletedTask;
    Task? _disposal;
    bool _disposed;

    /// <summary>Owns a broker launched through <see cref="BrokerLauncher.Launch" /> with the default connect timeout.</summary>
    [SupportedOSPlatform("windows")]
    public BrokerSession()
        : this(BrokerLauncher.Launch)
    {
    }

    /// <summary>Owns a broker launched through <paramref name="launchBroker" />.</summary>
    /// <param name="launchBroker">
    ///     Receives the broker command line and returns whether the launch started: false when the UAC prompt
    ///     was declined. Production passes <see cref="BrokerLauncher.Launch" />.
    /// </param>
    /// <param name="connectTimeout">
    ///     How long to wait for the launched broker to connect; null waits the default 30 seconds.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="launchBroker" /> is null.</exception>
    [SupportedOSPlatform("windows")]
    public BrokerSession(Func<string, bool> launchBroker, TimeSpan? connectTimeout = null)
        : this(cancellationToken => connectTimeout is { } timeout
            ? BrokerProcess.LaunchAsync(launchBroker, timeout, cancellationToken)
            : BrokerProcess.LaunchAsync(launchBroker, cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(launchBroker);
    }

    // Tests give the session an in-process process through MFTLibTestExtensions.BrokerTestHarness.CreateSession.
    internal BrokerSession(Func<CancellationToken, Task<BrokerProcess>> launchAsync)
        : this(launchAsync, TaskScheduler.Default)
    {
    }

    // Tests pass a scheduler they run by hand, to order a launch against disposal.
    internal BrokerSession(Func<CancellationToken, Task<BrokerProcess>> launchAsync, TaskScheduler launchScheduler)
    {
        ArgumentNullException.ThrowIfNull(launchAsync);
        _launchAsync = launchAsync;
        _launchScheduler = launchScheduler;
    }

    /// <summary>
    ///     Completes once, with the reason, shortly after the launched process ends. Never completes at disposal,
    ///     so a session that never launched, or that is disposed while its process is alive, never completes it.
    ///     Read <c>Ended.IsCompleted</c> for a flag. Never faults.
    /// </summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>
    ///     Raised on the launching thread-pool thread just before each launch attempt, outside every gate the session
    ///     holds. Not raised for a launch that disposal overtook.
    /// </summary>
    public event Action? Connecting;

    /// <summary>Raised on the launching thread-pool thread once the launched process connected, outside every gate the session holds.</summary>
    public event Action? Connected;

    /// <summary>
    ///     The index source over this session, to assign to <see cref="FileIndexOptions.MftSource" />. Nothing
    ///     launches until an index scans or watches a drive.
    /// </summary>
    /// <param name="scanOptions">
    ///     Base options for every scan: retention and freed-record inclusion. Scan progress arrives through
    ///     <see cref="MFTLib.Index.FileIndexOptions.ScanProgress" />.
    /// </param>
    /// <returns>A source whose scans and watches run on this session's process.</returns>
    public MftIndexSource CreateIndexSource(MftScanOptions? scanOptions = null) =>
        new BrokerMftBlockProducer(ConnectAsync, scanOptions).CreateIndexSource();

    /// <summary>
    ///     Grows the drive's USN journal (never shrinks it) and returns the sizing the volume reports afterwards,
    ///     launching the broker first if no use has yet.
    /// </summary>
    /// <param name="drive">The drive whose journal grows.</param>
    /// <param name="target">The requested maximum size and allocation delta in bytes; the broker refuses a maximum at or below the current.</param>
    /// <param name="cancellationToken">Ends this call's wait; it does not abort a launch other callers share.</param>
    /// <exception cref="InvalidOperationException">The process ended, the launch did not start, or the broker refused the change.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    /// <exception cref="IOException">The process ended first. <see cref="Ended" /> says why.</exception>
    /// <exception cref="TimeoutException">The broker never connected, or did not answer within the reply timeout.</exception>
    public async Task<UsnJournalSettings> GrowUsnJournalAsync(DriveKey drive, UsnJournalSettings target,
        CancellationToken cancellationToken)
    {
        var letter = drive.Letter;
        var process = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await process.GrowUsnJournalAsync(letter, target, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Disposes the process and ends the session. An in-flight launch is cancelled and awaited so no
    ///     process leaks, including one that arrives after disposal began. Idempotent.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // A process end that completed but is not yet observed is still published with its own reason.
            // Only the first call looks: after it, the process's end may be the disposal's own.
            var launcherResult = Volatile.Read(ref _launcherResult);
            var process = _process ?? (launcherResult is { IsCompletedSuccessfully: true } ? launcherResult.Result : null);
            if (!_disposed && process is { Ended.IsCompleted: true } ended)
            {
                _ended.TrySetResult(CompletedReason(ended.Ended));
            }

            _disposed = true;

            return new ValueTask(_disposal ??= DisposeCoreAsync(_launch));
        }
    }

    internal Task<BrokerProcess> ConnectAsync(CancellationToken cancellationToken) =>
        ConnectCoreAsync(cancellationToken);

    async Task<BrokerProcess> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        Task<BrokerProcess> launch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfEnded();
            launch = _launch ??= StartLaunch(_stopping.Token);
        }

        var process = await launch.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfEnded();
        }

        return process;
    }

    // Callers hold _gate.
    void ThrowIfEnded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reason = _ended.Task.IsCompleted
            ? CompletedReason(_ended.Task)
            : _process is { Ended.IsCompleted: true } process ? CompletedReason(process.Ended) : null;
        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"The elevated broker process for this session has ended: {reason} Start a new session.");
        }
    }

    static string CompletedReason(Task<string> ended)
    {
        // Callers pass a completed task, and an end task never faults, so this read cannot block or throw.
        return ended.Result;
    }

    // The launcher blocks on the UAC prompt, so the launch body is queued to the launch scheduler (the thread
    // pool in production) instead of continuing on the caller's thread, gate or synchronization context.
    // Callers hold _gate; queueing never runs the body inline.
    Task<BrokerProcess> StartLaunch(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => LaunchAsync(stoppingToken), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, _launchScheduler).Unwrap();

    async Task<BrokerProcess> LaunchAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Disposal marks the session synchronously but cancels stoppingToken later, so the launch checks the
            // mark itself: before Connecting, and again after it because a subscriber may have started disposal.
            ThrowIfDisposedWhileConnecting();
            Raise(Connecting);
            ThrowIfDisposedWhileConnecting();
            var launcherResult = _launchAsync(stoppingToken);
            // Disposal must see a completed launcher even while ownership publication waits for _gate.
            Volatile.Write(ref _launcherResult, launcherResult);
            var process = await launcherResult.ConfigureAwait(false);
            bool abandoned;
            lock (_gate)
            {
                // Publish ownership before observing cancellation, because a launcher can return late.
                // Disposal marks the session synchronously, so a process returned after it began is never
                // handed out; disposal reclaims it.
                _process = process;
                _endObservation = ObserveEndAsync(process);
                abandoned = _disposed;
            }

            if (abandoned)
            {
                throw new OperationCanceledException(DisposedWhileConnectingReason);
            }

            Raise(Connected);
            return process;
        }
        catch
        {
            // A failed attempt is not cached; the process, if one was launched, stays owned through _process.
            lock (_gate)
            {
                _launch = _process is null ? null : Task.FromResult(_process);
            }

            throw;
        }
    }

    void ThrowIfDisposedWhileConnecting()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new OperationCanceledException(DisposedWhileConnectingReason);
            }
        }
    }

    async Task ObserveEndAsync(BrokerProcess process)
    {
        var reason = await process.Ended.ConfigureAwait(false);
        lock (_gate)
        {
            // Disposal ends the process it owns; that end is not a broker end. A real end that completed
            // before disposal began was published by DisposeAsync.
            if (!_disposed)
            {
                _ended.TrySetResult(reason);
            }
        }
    }

    // Every subscriber runs even when an earlier one throws; the first exception then propagates.
    static void Raise(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        ExceptionDispatchInfo? first = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                first ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        first?.Throw();
    }

    async Task DisposeCoreAsync(Task<BrokerProcess>? pending)
    {
        await Task.Yield();
        try
        {
            // Only an in-flight launch is abandoned; an established process's token stays uncancelled so its
            // orderly shutdown below is not mistaken for an abort.
            if (pending is not { IsCompletedSuccessfully: true })
            {
                await _stopping.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await ObserveLaunchAsync(pending).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    BrokerProcess? process;
                    Task observation;
                    lock (_gate)
                    {
                        process = _process;
                        observation = _endObservation;
                    }

                    if (process is not null)
                    {
                        await process.DisposeAsync().ConfigureAwait(false);
                        await observation.ConfigureAwait(false);
                    }
                }
                finally
                {
                    _stopping.Dispose();
                }
            }
        }
    }

    static async Task ObserveLaunchAsync(Task<BrokerProcess>? pending)
    {
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception) when (pending.IsFaulted || pending.IsCanceled)
        {
            // The callers waiting on the launch own its failure; cleanup only waits so a late process is reclaimed.
        }
    }
}
