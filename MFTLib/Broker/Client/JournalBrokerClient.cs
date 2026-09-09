using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     UI-side client for the elevated journal broker. Owns the pipe (server end:
///     the non-elevated caller creates it and passes the name to the broker) and the
///     per-drive MMF lifetimes (caller pre-creates; broker opens and writes). Finished
///     packed blocks pass to the caller through BrokerScanResult.BlockOutcomes.
///     All external seams are injected so the class is fully testable without a
///     real child process, real named pipe, or real named MMF.
/// </summary>
/// <remarks>
///     Construct a client over an already-connected pipe and its supporting seams.
/// </remarks>
/// <param name="pipe">
///     The connected pipe stream. In production a <c>NamedPipeServerStream</c> that
///     the caller created and the broker connected to. Tests pass an in-memory
///     duplex stream.
/// </param>
/// <param name="createDriveBlockSection">
///     Creates a named section, its block view, and its section lifetime for a drive. The
///     production implementation returns a lifetime that aliases the block's own memory-mapped
///     file handle, so disposing the lifetime early does not invalidate the block; any other
///     implementation of this seam must preserve that property, since callers dispose the
///     lifetime independently of the block.
/// </param>
/// <remarks>
///     The pipe must already be connected. Production code builds the pipe, launches
///     the elevated broker, and waits for the connection via
///     <see cref="SpawnAndConnectAsync(Func{string,bool},CancellationToken)" />; tests pass a connected in-memory duplex stream.
/// </remarks>
public sealed partial class JournalBrokerClient(
    Stream pipe,
    Func<string, BlockFileCreateOptions, (string SectionName, BlockFile Block, IDisposable Lifetime)> createDriveBlockSection) : IAsyncDisposable
{
    // Lifetimes of MMFs pre-created per ArmScanAndCatchUpAsync call, keyed by map name.
    readonly Dictionary<string, IDisposable> _mmfLifetimes = new(StringComparer.Ordinal);
    readonly Dictionary<string, BlockFile> _pendingBlocks = new(StringComparer.Ordinal);
    readonly object _mmfLifetimesLock = new();
    // Pipe write mutex: only ArmScanAndCatchUpAsync and DisposeAsync write to the pipe,
    // and DisposeAsync waits for ArmScanAndCatchUpAsync to finish before writing Shutdown.
    readonly SemaphoreSlim _writeLock = new(1, 1);

    // Guards single-fire BrokerDied: 0 = not yet fired, 1 = fired. Swapped with
    // Interlocked.Exchange so only the first caller fires the event.
    int _brokerDeathSignaled;

    /// <summary>
    ///     Fired when the pipe EOF or IO error is detected (broker died or was killed).
    ///     Fires at most once per client lifetime regardless of how many concurrent
    ///     readers detect the same death.
    /// </summary>
    public event Action<string>? BrokerDied;

    /// <summary>
    ///     Fired when a non-fatal <see cref="BrokerFrameKind.Warning" /> frame arrives from the broker.
    ///     Carries the drive letter and warning message.
    /// </summary>
    public event Action<string, string>? WarningReceived;
}
