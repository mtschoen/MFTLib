using System.IO.Pipes;
using System.Security.Principal;

namespace MFTLib;

/// <summary>
///     Production <see cref="IElevatedEntryRunner" />. The broker mode does its work and
///     then terminates the elevated child via <see cref="System.Environment.Exit" />,
///     since an elevated child exists only to perform one mode.
/// </summary>
sealed class DefaultElevatedEntryRunner : IElevatedEntryRunner
{
    // How long an exiting broker waits for its queued diagnostics lines to reach the log.
    static readonly TimeSpan DiagnosticsFlushTimeout = TimeSpan.FromSeconds(2);

    // How long the broker waits for the control pipe, and for each drive pipe, before it gives up.
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    readonly TimeProvider _timeProvider;
    readonly Action<int> _exitProcess;
    readonly TimeSpan _connectTimeout;

    /// <summary>Uses the system clock and terminates the production elevated child on completion.</summary>
    public DefaultElevatedEntryRunner()
        : this(TimeProvider.System, Environment.Exit, ConnectTimeout)
    {
    }

    /// <summary>Captures the clock and exit behavior for this runner's complete lifetime.</summary>
    /// <param name="timeProvider">The clock for block completion and the diagnostics flush bound.</param>
    /// <param name="exitProcess">Called with 1 for a missing or unreachable pipe and 0 after normal session completion.</param>
    /// <param name="connectTimeout">How long to wait, on <paramref name="timeProvider" />, for the control pipe and each drive pipe to connect.</param>
    internal DefaultElevatedEntryRunner(TimeProvider timeProvider, Action<int> exitProcess,
        TimeSpan connectTimeout)
    {
        _timeProvider = timeProvider;
        _exitProcess = exitProcess;
        _connectTimeout = connectTimeout;
    }

    /// <summary>
    ///     Connects to the caller-created control pipe and serves one elevated journal-broker
    ///     session. This method terminates the current process after the session ends.
    /// </summary>
    /// <param name="controlPipeName">
    ///     Name of the caller-created control pipe. A null name terminates the process with a
    ///     failure exit code.
    /// </param>
    /// <exception cref="IOException">The control pipe cannot be connected or used.</exception>
    public void RunBroker(string? controlPipeName)
    {
        if (controlPipeName == null)
        {
            _exitProcess(1);
            return;
        }

        // The non-elevated caller created every pipe server; the elevated broker is the client
        // end of each (high integrity connecting to medium integrity - the only safe
        // cross-integrity direction). It also opens the caller-created block sections.
        using var control = new NamedPipeClientStream(
            ".", controlPipeName, PipeDirection.InOut, BrokerPipeAccess.ClientOptions,
            TokenImpersonationLevel.Identification);
        if (!TryConnectControl(control))
        {
            _exitProcess(1);
            return;
        }

        // Block the elevated child's entry thread for the whole broker session: this
        // process exists solely to serve the broker, so there is no other work to
        // yield to. What makes the blocking wait safe is the awaited chain, not the
        // shape of the entry point - a consumer's elevated child can enter here on a
        // thread that does carry a SynchronizationContext (a consumer can dispatch
        // ElevatedEntryPoint.RunBrokerIfRequested from a UI application's startup code, on the UI
        // thread's synchronization context). Every await from
        // ServeAsync down to the native journal wait uses ConfigureAwait(false), so
        // the first incomplete await already leaves the caller's context and no
        // continuation is ever posted back to this thread for GetResult() to block on.
        // A session that fails still leaves through the flush, so its last diagnostics lines, the
        // ones that explain the failure, reach the log before the exception ends the process.
        try
        {
            JournalBrokerHost.CreateDefault()
                .ServeAsync(control, ConnectDrivePipeAsync, new RealBlockSectionWriter(_timeProvider), CancellationToken.None)
                // aislop-ignore-next-line csharp-sync-over-async -- every await in the ServeAsync chain uses ConfigureAwait(false), so no continuation needs the calling thread's SynchronizationContext and GetResult() cannot deadlock even when the entry thread has one
                .GetAwaiter().GetResult();
        }
        finally
        {
            FlushDiagnostics();
        }

        _exitProcess(0);
    }

    /// <summary>The production <see cref="BrokerChannelConnector" />: the client end of the named drive pipe, bounded by this runner's connect timeout.</summary>
    Task<Stream> ConnectDrivePipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        return ConnectDrivePipeAsync(pipeName, _connectTimeout, _timeProvider, cancellationToken);
    }

    /// <summary>Connects the named drive pipe, verifies its DACL, and fails with <see cref="TimeoutException" /> when no server answers within <paramref name="timeout" />.</summary>
    internal static async Task<Stream> ConnectDrivePipeAsync(string pipeName, TimeSpan timeout,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, BrokerPipeAccess.ClientOptions, TokenImpersonationLevel.Identification);
        try
        {
            using var bound = new CancellationTokenSource(timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(bound.Token, cancellationToken);
            try
            {
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(FormattableString.Invariant(
                    $"No server answered drive pipe '{pipeName}' within {timeout.TotalSeconds:G} seconds."));
            }

            BrokerPipeAccess.VerifyServerRestrictedToCurrentUser(pipe);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // No server within the bound means the launching client is gone or never listened; the elevated
    // process leaves instead of hanging. The wait is synchronous because the entry thread has no
    // other work, and the timer is the runner's own clock.
    bool TryConnectControl(NamedPipeClientStream control)
    {
        using var bound = new CancellationTokenSource(_connectTimeout, _timeProvider);
        try
        {
            // aislop-ignore-next-line csharp-sync-over-async -- the connect awaits only the pipe and a timer, neither of which posts to the calling thread's SynchronizationContext
            control.ConnectAsync(bound.Token).GetAwaiter().GetResult();
            BrokerPipeAccess.VerifyServerRestrictedToCurrentUser(control);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException ||
                                          (exception is OperationCanceledException && bound.IsCancellationRequested))
        {
            return false;
        }
    }

    // The session is over and the process is about to exit, so the diagnostics lines still
    // queued get a bounded wait, on the runner's clock, to reach the log. Lines the wait does not
    // cover are lost with the process; there is nowhere left to report that, so whichever of the
    // flush and the bound finishes first ends the wait.
    void FlushDiagnostics()
    {
        using var stopBound = new CancellationTokenSource();
        var flush = BrokerDiagnostics.FlushAsync(CancellationToken.None);
        var bound = Task.Delay(DiagnosticsFlushTimeout, _timeProvider, stopBound.Token);
        // aislop-ignore-next-line csharp-sync-over-async -- the wait covers only the diagnostics writer's drain and a timer, neither of which posts to the calling thread's SynchronizationContext
        Task.WhenAny(flush, bound).GetAwaiter().GetResult();
        stopBound.Cancel();
    }
}
