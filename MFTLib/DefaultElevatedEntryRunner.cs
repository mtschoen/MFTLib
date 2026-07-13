using System.IO.Pipes;
using System.Runtime.Versioning;

namespace MFTLib;

/// <summary>
/// Production <see cref="IElevatedEntryRunner"/>. The broker mode does its work and
/// then terminates the elevated child via <see cref="System.Environment.Exit"/>,
/// since an elevated child exists only to perform one mode.
/// </summary>
public sealed class DefaultElevatedEntryRunner : IElevatedEntryRunner
{
    // Exiting the process cannot be exercised from an in-process unit test (it would
    // kill the test host), so tests inject a fake. Production always uses Environment.Exit.
    internal static Action<int> ExitProcess = Environment.Exit;

    internal static void ResetToDefaults() => ExitProcess = Environment.Exit;

    [SupportedOSPlatform("windows")]
    public void RunBroker(string? pipeName, bool oneShot)
    {
        if (pipeName == null)
        {
            ExitProcess(1);
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
        // yield to. Safe from the usual sync-over-async deadlock risk because a
        // console entry point has no SynchronizationContext to resume onto - every
        // ServeAsync continuation runs on a thread-pool thread, not this one.
        JournalBrokerHost.CreateDefault()
            .ServeAsync(stream, new RealMmfWriter(), oneShot, CancellationToken.None)
            // aislop-ignore-next-line csharp-sync-over-async
            .GetAwaiter().GetResult();

        ExitProcess(0);
    }
}
