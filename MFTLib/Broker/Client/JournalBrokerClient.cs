namespace MFTLib;

/// <summary>
///     Callback invoked per batch of <see cref="ScanRecord" />s as they are streamed
///     from the broker's shared memory map.
/// </summary>
public delegate ValueTask ScanRecordBatchConsumer(
    IReadOnlyList<ScanRecord> records,
    CancellationToken cancellationToken);

/// <summary>
///     UI-side client for the elevated journal broker. Owns the pipe (server end:
///     the non-elevated caller creates it and passes the name to the broker) and the
///     per-drive page-file-backed MMFs (caller pre-creates; broker opens and writes).
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
/// <param name="mmfReader">Seam for reading the cold-scan MMF after the broker writes it.</param>
/// <param name="createDriveMmf">
///     Seam for pre-creating a per-drive page-file-backed MMF before sending
///     <c>ArmAndScan</c>. Receives the drive letter and capacity; returns the map
///     name and a lifetime handle.
/// </param>
/// <remarks>
///     The pipe must already be connected. Production code builds the pipe, launches
///     the elevated broker, and waits for the connection via
///     <see cref="SpawnAndConnectAsync" />; tests pass a connected in-memory duplex stream.
/// </remarks>
public sealed partial class JournalBrokerClient(
    Stream pipe,
    IMmfReader mmfReader,
    Func<string, long, (string Name, IDisposable Lifetime)> createDriveMmf) : IAsyncDisposable
{
    /// <summary>
    ///     Default capacity for a per-drive MMF: generous enough for tens of millions
    ///     of records (~2 GiB). The broker writes only the exact bytes it needs; the
    ///     caller reads back exactly that many via the <c>ScanReady</c> byte-length field.
    /// </summary>
    public const long DefaultMmfCapacity = 2L * 1024 * 1024 * 1024; // 2 GiB

    // Lifetimes of MMFs pre-created per ArmScanAndCatchUpAsync call, keyed by map name.
    readonly Dictionary<string, IDisposable> _mmfLifetimes = new(StringComparer.Ordinal);
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
}
