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
    // Exiting the process cannot be exercised from an in-process unit test (it would
    // kill the test host), so tests inject a fake. Production always uses Environment.Exit.
    internal static Action<int> _exitProcess = Environment.Exit;

    [SupportedOSPlatform("windows")]
    public void RunBroker(string? pipeName, bool oneShot)
    {
        if (pipeName == null)
        {
            _exitProcess(1);
            return;
        }

        // The non-elevated caller created the named-pipe server; the elevated broker is
        // the client end (high integrity connecting to medium integrity - the only
        // safe cross-integrity direction). It also opens the caller-created MMFs.
        using var stream = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        stream.Connect();

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
        JournalBrokerHost.CreateDefault()
            .ServeAsync(stream, new RealBlockSectionWriter(), oneShot, CancellationToken.None)
            // aislop-ignore-next-line csharp-sync-over-async -- every await in the ServeAsync chain uses ConfigureAwait(false), so no continuation needs the calling thread's SynchronizationContext and GetResult() cannot deadlock even when the entry thread has one
            .GetAwaiter().GetResult();

        _exitProcess(0);
    }

    internal static void ResetToDefaults()
    {
        _exitProcess = Environment.Exit;
    }
}
