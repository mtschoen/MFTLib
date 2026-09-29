using System.IO.Pipes;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
///     Production <see cref="IElevatedEntryRunner" />. The broker mode does its work and
///     then terminates the elevated child via <see cref="System.Environment.Exit" />,
///     since an elevated child exists only to perform one mode.
/// </summary>
public sealed class DefaultElevatedEntryRunner : IElevatedEntryRunner
{
    static readonly TimeSpan DefaultDiagnosticsFlushTimeout = TimeSpan.FromSeconds(2);

    // How long an exiting broker waits for its queued diagnostics lines to reach the log. A test
    // lifts the bound to prove the wait happens.
    internal static TimeSpan _diagnosticsFlushTimeout = DefaultDiagnosticsFlushTimeout;

    // Exiting the process cannot be exercised from an in-process unit test (it would
    // kill the test host), so tests inject a fake. Production always uses Environment.Exit.
    internal static Action<int> _exitProcess = Environment.Exit;

    [SupportedOSPlatform("windows")]
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
            ".", controlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        control.Connect();

        // Block the elevated child's entry thread for the whole broker session: this
        // process exists solely to serve the broker, so there is no other work to
        // yield to. What makes the blocking wait safe is the awaited chain, not the
        // shape of the entry point - a consumer's elevated child can enter here on a
        // thread that does carry a SynchronizationContext (file-wizard dispatches
        // ElevatedEntryPoint.TryHandle from its WinUI App constructor, on the UI
        // thread's DispatcherQueueSynchronizationContext). Every await from
        // ServeAsync down to the native journal wait uses ConfigureAwait(false), so
        // the first incomplete await already leaves the caller's context and no
        // continuation is ever posted back to this thread for GetResult() to block on.
        // A session that fails still leaves through the flush, so its last diagnostics lines, the
        // ones that explain the failure, reach the log before the exception ends the process.
        try
        {
            JournalBrokerHost.CreateDefault()
                .ServeAsync(control, ConnectDrivePipeAsync, new RealBlockSectionWriter(), CancellationToken.None)
                // aislop-ignore-next-line csharp-sync-over-async -- every await in the ServeAsync chain uses ConfigureAwait(false), so no continuation needs the calling thread's SynchronizationContext and GetResult() cannot deadlock even when the entry thread has one
                .GetAwaiter().GetResult();
        }
        finally
        {
            FlushDiagnostics();
        }

        _exitProcess(0);
    }

    /// <summary>The production <see cref="BrokerChannelConnector" />: the client end of the named drive pipe.</summary>
    internal static async Task<Stream> ConnectDrivePipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The session is over and the process is about to exit, so the diagnostics lines still
    // queued get a bounded wait to reach the log. Lines the wait does not cover are lost with the
    // process; there is nowhere left to report that, so the wait's outcome is not inspected.
    static void FlushDiagnostics()
    {
        // aislop-ignore-next-line csharp-sync-over-async -- the flush awaits only the diagnostics writer's own drain task, which never posts to the calling thread's SynchronizationContext
        _ = BrokerDiagnostics.FlushAsync(CancellationToken.None).Wait(_diagnosticsFlushTimeout);
    }

    internal static void ResetToDefaults()
    {
        _exitProcess = Environment.Exit;
        _diagnosticsFlushTimeout = DefaultDiagnosticsFlushTimeout;
    }
}
