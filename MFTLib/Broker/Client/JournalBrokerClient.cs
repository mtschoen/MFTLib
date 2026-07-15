namespace MFTLib;

/// <summary>
/// UI-side client for the elevated journal broker. Owns the pipe (server end:
/// the non-elevated caller creates it and passes the name to the broker) and the
/// per-drive page-file-backed MMFs (caller pre-creates; broker opens and writes).
/// All external seams are injected so the class is fully testable without a
/// real child process, real named pipe, or real named MMF.
/// </summary>
public sealed partial class JournalBrokerClient : IAsyncDisposable
{
    /// <summary>
    /// Default capacity for a per-drive MMF: generous enough for tens of millions
    /// of records (~2 GiB). The broker writes only the exact bytes it needs; the
    /// caller reads back exactly that many via the <c>ScanReady</c> byte-length field.
    /// </summary>
    public const long DefaultMmfCapacity = 2L * 1024 * 1024 * 1024; // 2 GiB

    readonly Stream _pipe;
    readonly IMmfReader _mmfReader;
    readonly Func<string, long, (string Name, IDisposable Lifetime)> _createDriveMmf;

    // Lifetimes of MMFs pre-created per ArmScanAndCatchUpAsync call. Disposed on DisposeAsync.
    readonly List<IDisposable> _mmfLifetimes = new();
    readonly object _mmfLifetimesLock = new();

    // Pipe write mutex: only ArmScanAndCatchUpAsync and DisposeAsync write to the pipe,
    // and DisposeAsync waits for ArmScanAndCatchUpAsync to finish before writing Shutdown.
    readonly SemaphoreSlim _writeLock = new(1, 1);

    // Guards single-fire BrokerDied: 0 = not yet fired, 1 = fired. Swapped with
    // Interlocked.Exchange so only the first caller fires the event.
    int _brokerDeathSignaled;

    /// <summary>
    /// Fired when the pipe EOF or IO error is detected (broker died or was killed).
    /// Fires at most once per client lifetime regardless of how many concurrent
    /// readers detect the same death.
    /// </summary>
    public event Action<string>? BrokerDied;

    /// <summary>
    /// Construct a client over an already-connected pipe and its supporting seams.
    /// </summary>
    /// <param name="pipe">
    /// The connected pipe stream. In production a <c>NamedPipeServerStream</c> that
    /// the caller created and the broker connected to. Tests pass an in-memory
    /// duplex stream.
    /// </param>
    /// <param name="mmfReader">Seam for reading the cold-scan MMF after the broker writes it.</param>
    /// <param name="createDriveMmf">
    /// Seam for pre-creating a per-drive page-file-backed MMF before sending
    /// <c>ArmAndScan</c>. Receives the drive letter and capacity; returns the map
    /// name and a lifetime handle (disposed on <see cref="DisposeAsync"/>).
    /// </param>
    /// <remarks>
    /// The pipe must already be connected. Production code builds the pipe, launches
    /// the elevated broker, and waits for the connection via
    /// <see cref="SpawnAndConnectAsync"/>; tests pass a connected in-memory duplex stream.
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
    /// For each drive: pre-create its MMF, request a full cold scan, then consume response
    /// frames until every requested drive has delivered either a complete scan (Cursor +
    /// ScanReady + JournalBatch) or an Error.
    /// </summary>
    public Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives, CancellationToken cancellationToken = default) =>
        ArmScanAndCatchUpAsync(drives, BrokerScanProfile.Full, cancellationToken);

    /// <summary>
    /// Arms, scans, and catches up each drive using the requested cold-scan record profile.
    /// </summary>
    public async Task<BrokerScanResult> ArmScanAndCatchUpAsync(
        IReadOnlyList<string> drives, BrokerScanProfile profile,
        CancellationToken cancellationToken = default)
    {
        var mmfNamesByDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var drivesSpec = PrepareDriveScan(drives, profile, mmfNamesByDrive);

        // Send the ArmAndScan frame.
        await WriteFrameAsync(
            writer => BrokerProtocol.WriteArmAndScan(writer, drivesSpec),
            cancellationToken).ConfigureAwait(false);

        // Fold each broker response into the collector until every drive reports in.
        var collector = new ScanCollector(
            _mmfReader, mmfNamesByDrive, drives.Select(NormalizeDriveLetter));

        while (!collector.IsComplete)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                // Pipe closed: broker died before all drives responded.
                // SignalBrokerDeath was already called inside ReadFrameAsync.
                break;
            }

            collector.Apply(frame.Value);
        }

        return collector.ToResult();
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
                _mmfLifetimes.Add(lifetime);
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
        readonly IMmfReader _mmfReader;
        readonly IReadOnlyDictionary<string, string> _mmfNamesByDrive;
        readonly HashSet<string> _remaining;
        readonly List<ScanRecord> _records = new();
        readonly Dictionary<string, UsnJournalCursor> _armedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalCursor> _advancedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalEntry[]> _catchUpEntries = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

        public ScanCollector(IMmfReader mmfReader, IReadOnlyDictionary<string, string> mmfNamesByDrive,
            IEnumerable<string> drives)
        {
            _mmfReader = mmfReader;
            _mmfNamesByDrive = mmfNamesByDrive;
            _remaining = new HashSet<string>(drives, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsComplete => _remaining.Count == 0;

        public void Apply(BrokerFrame frame)
        {
            switch (frame.Kind)
            {
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
                            _records.AddRange(_mmfReader.Read(mmfName, frame.ByteLength));
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
                        _remaining.Remove(drive);
                        break;
                    }

                    // Heartbeat and other frames are ignored during the scan phase.
            }
        }

        public BrokerScanResult ToResult() =>
            new(records: _records, armedCursors: _armedCursors, advancedCursors: _advancedCursors,
                catchUpEntries: _catchUpEntries, errors: _errors);
    }

}
