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
public sealed partial class JournalBrokerClient : IAsyncDisposable
{
    /// <summary>
    ///     Default capacity for a per-drive MMF: generous enough for tens of millions
    ///     of records (~2 GiB). The broker writes only the exact bytes it needs; the
    ///     caller reads back exactly that many via the <c>ScanReady</c> byte-length field.
    /// </summary>
    public const long DefaultMmfCapacity = 2L * 1024 * 1024 * 1024; // 2 GiB

    readonly Func<string, long, (string Name, IDisposable Lifetime)> _createDriveMmf;

    // Lifetimes of MMFs pre-created per ArmScanAndCatchUpAsync call, keyed by map name.
    readonly Dictionary<string, IDisposable> _mmfLifetimes = new(StringComparer.Ordinal);
    readonly object _mmfLifetimesLock = new();
    readonly IMmfReader _mmfReader;

    readonly Stream _pipe;

    // Pipe write mutex: only ArmScanAndCatchUpAsync and DisposeAsync write to the pipe,
    // and DisposeAsync waits for ArmScanAndCatchUpAsync to finish before writing Shutdown.
    readonly SemaphoreSlim _writeLock = new(1, 1);

    // Guards single-fire BrokerDied: 0 = not yet fired, 1 = fired. Swapped with
    // Interlocked.Exchange so only the first caller fires the event.
    int _brokerDeathSignaled;

    /// <summary>
    ///     Construct a client over an already-connected pipe and its supporting seams.
    /// </summary>
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
    public JournalBrokerClient(
        Stream pipe,
        IMmfReader mmfReader,
        Func<string, long, (string Name, IDisposable Lifetime)> createDriveMmf)
    {
        _pipe = pipe;
        _mmfReader = mmfReader;
        _createDriveMmf = createDriveMmf;
    }

    /// <summary>
    ///     Fired when the pipe EOF or IO error is detected (broker died or was killed).
    ///     Fires at most once per client lifetime regardless of how many concurrent
    ///     readers detect the same death.
    /// </summary>
    public event Action<string>? BrokerDied;

    /// <summary>
    ///     Arms, scans, and catches up each drive with optional scan profile, consumer, marker files, and progress callback.
    /// </summary>
    public Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, null, cancellationToken);
    }

    internal Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        return ArmScanAndCatchUpCoreAsync(drives, options, transmissionStarted, cancellationToken);
    }

    async Task<BrokerScanResult> ArmScanAndCatchUpCoreAsync(
        IReadOnlyList<string> drives,
        BrokerScanOptions? options,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        var profile = options?.Profile ?? BrokerScanProfile.Full;
        var keepFileNames = options?.KeepFileNames;

        var mmfNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var drivesSpec = PrepareDriveScan(drives, profile, mmfNamesByDrive);

        // Send the ArmAndScan frame.
        await WriteFrameAsync(
            writer => BrokerProtocol.WriteArmAndScan(writer, drivesSpec, keepFileNames),
            transmissionStarted, cancellationToken).ConfigureAwait(false);

        // Fold each broker response into the collector until every drive reports in.
        var collector = new ScanCollector(
            _mmfReader, mmfNamesByDrive, drives.Select(NormalizeDriveLetter),
            options, TakeMmfLifetime, cancellationToken);

        while (!collector.IsComplete)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                // Pipe closed: broker died before all drives responded.
                // SignalBrokerDeath was already called inside ReadFrameAsync.
                break;
            }

            await collector.ApplyAsync(frame.Value).ConfigureAwait(false);
        }

        return collector.ToResult();
    }

    IDisposable? TakeMmfLifetime(string mmfName)
    {
        lock (_mmfLifetimesLock)
        {
            return _mmfLifetimes.Remove(mmfName, out var lifetime) ? lifetime : null;
        }
    }

    // Pre-create a page-file-backed MMF per drive (registering each lifetime for
    // disposal), record each map's name in mmfNamesByDrive, and return the
    // comma-joined drivesSpec (letter:journalId:nextUsn:mmfName tokens) for the
    // ArmAndScan frame. Cold arm: journalId and nextUsn are 0, so the broker
    // queries the real cursor.
    string PrepareDriveScan(
        IReadOnlyList<string> drives, BrokerScanProfile profile,
        Dictionary<string, string> mmfNamesByDrive)
    {
        var specTokens = new List<string>(drives.Count);
        foreach (var drive in drives)
        {
            var letter = NormalizeDriveLetter(drive);
            var (mmfName, lifetime) = _createDriveMmf(letter, DefaultMmfCapacity);
            lock (_mmfLifetimesLock)
            {
                _mmfLifetimes[mmfName] = lifetime;
            }

            mmfNamesByDrive[letter] = mmfName;
            specTokens.Add(FormattableString.Invariant($"{letter}:0:0:{mmfName}:{(int)profile}"));
        }

        return string.Join(",", specTokens);
    }

    // Accumulates one ArmScanAndCatchUpAsync pass. Apply folds each broker response
    // frame into the right per-drive bucket; a drive is retired from the remaining
    // set once its JournalBatch or Error frame arrives. Build the aggregate with
    // ToResult once IsComplete reports true.
    sealed class ScanCollector
    {
        readonly Dictionary<string, UsnJournalCursor> _advancedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalCursor> _armedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalEntry[]> _catchUpEntries = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
        readonly IReadOnlyDictionary<string, string> _mmfNamesByDrive;
        readonly IMmfReader _mmfReader;
        readonly ScanRecordBatchConsumer _consumeRecords;
        readonly IProgress<BrokerScanProgress>? _progress;
        readonly Func<string, IDisposable?> _takeMmfLifetime;
        readonly CancellationToken _cancellationToken;
        readonly HashSet<string> _remaining;

        public ScanCollector(
            IMmfReader mmfReader,
            IReadOnlyDictionary<string, string> mmfNamesByDrive,
            IEnumerable<string> drives,
            BrokerScanOptions? options,
            Func<string, IDisposable?> takeMmfLifetime,
            CancellationToken cancellationToken)
        {
            _mmfReader = mmfReader;
            _mmfNamesByDrive = mmfNamesByDrive;
            _consumeRecords = options?.ConsumeRecords ?? (static (_, _) => ValueTask.CompletedTask);
            _progress = options?.Progress;
            _takeMmfLifetime = takeMmfLifetime;
            _cancellationToken = cancellationToken;
            _remaining = new HashSet<string>(drives, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsComplete => _remaining.Count == 0;

        public async ValueTask ApplyAsync(BrokerFrame frame)
        {
            switch (frame.Kind)
            {
                case BrokerFrameKind.ScanProgress:
                    if (frame.Progress is { } p)
                    {
                        _progress?.Report(p);
                    }
                    break;

                case BrokerFrameKind.Cursor:
                    _armedCursors[frame.RequireDrive()] = frame.Cursor;
                    break;

                case BrokerFrameKind.ScanReady:
                    {
                        // The ScanReady frame does not carry a Drive field; correlate by mmfName.
                        var mmfName = frame.RequireMmfName();
                        var matchedDrive = _mmfNamesByDrive
                            .FirstOrDefault(pair => string.Equals(pair.Value, mmfName, StringComparison.Ordinal))
                            .Key;
                        if (matchedDrive != null)
                        {
                            try
                            {
                                if (_mmfReader is IStreamingMmfReader streamingReader)
                                {
                                    foreach (var batch in streamingReader.ReadBatches(mmfName, frame.ByteLength, 4096, _cancellationToken))
                                    {
                                        await _consumeRecords(batch, _cancellationToken).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    var records = _mmfReader.Read(mmfName, frame.ByteLength);
                                    if (records.Length > 0)
                                    {
                                        await _consumeRecords(records, _cancellationToken).ConfigureAwait(false);
                                    }
                                }
                            }
                            finally
                            {
                                _takeMmfLifetime(mmfName)?.Dispose();
                            }
                        }

                        break;
                    }

                case BrokerFrameKind.JournalBatch:
                    {
                        var drive = frame.RequireDrive();
                        _advancedCursors[drive] = frame.Cursor;
                        _catchUpEntries[drive] = frame.Entries;
                        // A drive is complete after Cursor + ScanReady + JournalBatch.
                        // (Error also completes a drive; handled below.)
                        _remaining.Remove(drive);
                        break;
                    }

                case BrokerFrameKind.Error:
                    {
                        var drive = frame.RequireDrive();
                        _errors[drive] = frame.RequireMessage();
                        if (_mmfNamesByDrive.TryGetValue(drive, out var mmfName))
                        {
                            _takeMmfLifetime(mmfName)?.Dispose();
                        }

                        _remaining.Remove(drive);
                        break;
                    }

                    // Heartbeat and other frames are ignored during the scan phase.
            }
        }

        public BrokerScanResult ToResult()
        {
            return new BrokerScanResult(_armedCursors, _advancedCursors,
                _catchUpEntries, _errors);
        }
    }
}
