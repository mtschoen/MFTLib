using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     In-process <see cref="FileIndex" /> instances for roots <c>T</c> and <c>U</c> served by a real
///     <see cref="BrokerProcess" /> and an in-process <see cref="JournalBrokerHost" />, with a
///     <see cref="FakeTimeProvider" /> on the host (<see cref="HostClock" />) and another on the
///     client (<see cref="ClientClock" />). Nothing is mocked: each root scans and watches through
///     its own in-memory pipes. A test that needs time to pass calls
///     <see cref="AdvanceIntervalAsync" />, which moves both clocks one heartbeat interval and
///     proves the client consumed every heartbeat the host wrote before it moves the client's
///     clock, so a pipe's stall limit measures only what the host really did not send.
/// </summary>
sealed class CrossRootScenario : IAsyncDisposable
{
    const string ControlPipeName = "control";
    static readonly int HeartbeatLength = MeasureHeartbeat();

    readonly TempDirectory _cache = new("broker-cross-drive-cache", create: false);
    readonly TempDirectory _roots = new("broker-cross-drive-roots", create: false);
    readonly ConcurrentDictionary<char, int> _scans = new();
    readonly ConcurrentDictionary<char, bool> _frozen = new();
    readonly ConcurrentDictionary<string, char> _pipeDrives = new(StringComparer.Ordinal);
    readonly TaskCompletionSource _thaw = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<bool> _visits = Channel.CreateUnbounded<bool>();
    readonly Lock _gate = new();
    readonly List<(string Name, ReadCounter Reads)> _pipes = [];
    readonly List<WatchFault> _faults = [];
    readonly FaultStatusLog _faultStatuses = new();
    readonly List<(WatchFaultKind Kind, char Drive, int Occurrence, TaskCompletionSource<WatchFault> Signal)> _waiters = [];
    readonly Dictionary<string, Cadence> _tracked = new(StringComparer.Ordinal);
    readonly Dictionary<char, FileIndex> _indices = [];

    CrossRootScenario(Func<VolumeKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal)
    {
        Broker = new ScriptedWatchBrokerHarness(scanVolume: Scan, seams: new ScriptedBrokerSeams
        {
            ReadJournal = readJournal,
            HostClock = HostClock,
            HeartbeatVisited = () => _visits.Writer.TryWrite(true),
            ClientOptions = new BrokerTestHarnessOptions { TimeProvider = ClientClock, HoldWrites = HoldWrites },
            WrapClientStream = CountReads
        });
    }

    public FakeTimeProvider HostClock { get; } = new();

    public FakeTimeProvider ClientClock { get; } = new();

    public ScriptedWatchBrokerHarness Broker { get; }

    public FileIndex IndexOf(char driveLetter) =>
        _indices.TryGetValue(char.ToUpperInvariant(driveLetter), out var index)
            ? index
            : throw new InvalidOperationException($"The index for root {driveLetter} is not open yet.");

    public FileIndex IndexFor(char driveLetter) => IndexOf(driveLetter);

    public IReadOnlyCollection<FileIndex> Indices => _indices.Values;

    public CancellationToken Token => Broker.CancellationToken;

    /// <summary>Every fault the indices have raised, in order.</summary>
    public IReadOnlyList<WatchFault> Faults
    {
        get
        {
            lock (_gate)
            {
                return _faults.ToArray();
            }
        }
    }

    /// <summary>
    ///     Opens the indices: each root's first scan lists <c>scan-{root}-1.txt</c>, and every later
    ///     scan of a root names its own number, so a test can tell which scan produced a block.
    /// </summary>
    /// <param name="readJournal">The host's catch-up source; null reads nothing and holds at the armed cursor.</param>
    public static async Task<CrossRootScenario> OpenAsync(
        Func<VolumeKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal = null)
    {
        var scenario = new CrossRootScenario(readJournal);
        try
        {
            await scenario.OpenIndicesAsync();
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }

        return scenario;
    }

    /// <summary>How many scans the host has run for <paramref name="driveLetter" />, the open's included.</summary>
    public int ScansOf(char driveLetter) => _scans.GetValueOrDefault(driveLetter);

    /// <summary>How many volume pipes the client has opened for <paramref name="driveLetter" />, scans and watches alike.</summary>
    public int ChannelsOpenedFor(char driveLetter)
    {
        lock (_gate)
        {
            return _pipes.Count(pipe => pipe.Name != ControlPipeName && DriveOfPipe(pipe.Name) == driveLetter);
        }
    }

    public RootStatus RootOf(char driveLetter) => IndexOf(driveLetter).Status;

    /// <summary>
    ///     Holds every write the host makes to <paramref name="driveLetter" />'s pipes from now on,
    ///     before any byte of it, until the scenario is disposed: a host whose write to that
    ///     pipe is stuck.
    /// </summary>
    public void FreezeHostWrites(char driveLetter) => _frozen[driveLetter] = true;

    /// <summary>The <paramref name="occurrence" />th fault of <paramref name="kind" /> for <paramref name="driveLetter" />.</summary>
    public Task<WatchFault> FaultAsync(WatchFaultKind kind, char driveLetter, int occurrence = 1)
    {
        lock (_gate)
        {
            var matching = _faults.Where(fault => fault.Kind == kind && fault.Volume == driveLetter).ToList();
            if (matching.Count >= occurrence)
            {
                return Task.FromResult(matching[occurrence - 1]);
            }

            var signal = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((kind, driveLetter, occurrence, signal));
            return signal.Task.WaitAsync(HangGuard, Token);
        }
    }

    public IReadOnlyList<WatchFault> FaultsOf(char driveLetter) =>
        Faults.Where(fault => fault.Volume == driveLetter).ToArray();

    /// <summary>
    ///     Starts measuring heartbeats on the control pipe and on each named volume's newest pipe,
    ///     from the bytes the client has read so far. Call it once every frame the host has written
    ///     so far has been consumed, for instance after a caught-up wait.
    /// </summary>
    public void BeginCadence(params char[] driveLetters)
    {
        _tracked.Clear();
        Track(ControlPipeName);
        foreach (var driveLetter in driveLetters)
        {
            Track(TrackKey(driveLetter));
        }
    }

    /// <summary>Stops expecting heartbeats on <paramref name="driveLetter" />'s pipe, whose channel has ended.</summary>
    public void StopMeasuring(char driveLetter) => _tracked.Remove(TrackKey(driveLetter));

    /// <summary>
    ///     Moves the host's clock one heartbeat interval, waits for the heartbeat sender's visit, waits
    ///     until the client has read every heartbeat that visit wrote to a measured pipe, and only
    ///     then moves the client's clock the same interval.
    /// </summary>
    public async Task AdvanceIntervalAsync(params char[] silentDrives)
    {
        var awaited = new List<(string Key, Cadence Pipe)>();
        foreach (var (key, pipe) in _tracked)
        {
            var wrote = pipe.Reads.BytesRead != pipe.Settled;
            var silent = key != ControlPipeName && silentDrives.Contains(key[0]);
            if (silent)
            {
                pipe.Settled = pipe.Reads.BytesRead;
                continue;
            }

            pipe.Settled = wrote ? pipe.Reads.BytesRead : pipe.Settled + HeartbeatLength;
            awaited.Add((key, pipe));
        }

        HostClock.Advance(BrokerLiveness.HeartbeatInterval);
        await _visits.Reader.ReadAsync(Token).AsTask().WaitAsync(HangGuard, Token);
        foreach (var (key, pipe) in awaited)
        {
            var timedOut = false;
            try
            {
                await pipe.Reads.WhenReadPendingAfter(pipe.Settled).WaitAsync(HangGuard, Token);
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }

            Assert.IsFalse(timedOut, $"The client never read the {key} pipe's expected heartbeat: " +
                                     $"{pipe.Reads.BytesRead} of {pipe.Settled} bytes.");
        }

        ClientClock.Advance(BrokerLiveness.HeartbeatInterval);
    }

    public async ValueTask DisposeAsync()
    {
        _thaw.TrySetResult();
        try
        {
            foreach (var index in _indices.Values)
            {
                await index.DisposeAsync().AsTask().WaitAsync(HangGuard, CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                await Broker.DisposeAsync().AsTask().WaitAsync(HangGuard, CancellationToken.None);
            }
            finally
            {
                try
                {
                    _cache.Dispose();
                }
                finally
                {
                    _roots.Dispose();
                }
            }
        }
    }

    async Task OpenIndicesAsync()
    {
        Directory.CreateDirectory(_roots.Path);
        var producer = new BrokerMftBlockProducer(Broker.ConnectAsync);
        var source = producer.CreateIndexSource();
        foreach (var (letter, serial) in new[] { ('T', 123u), ('U', 456u) })
        {
            var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Root = SyntheticIndexedRoot.Create(new VolumeKey(letter), _roots.Path, serial),
                CacheDirectory = _cache.Path,
                CacheMode = CacheMode.Disabled,
                MftSource = source
            }, Token);
            index.WatchFaulted += fault => RecordFault(index, fault);
            _indices[letter] = index;
        }
    }

    IEnumerable<IReadOnlyList<MftRecord>> Scan(VolumeKey volume, ParseThreadAllowance parseThreads,
        BlockWriteReporting reporting,
        MftScanOptions scanOptions, Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        var scanNumber = _scans.AddOrUpdate(volume.Letter, 1, (_, count) => count + 1);
        yield return [new MftRecord(5, 5, new MftRecordFields(3), "."),
            new MftRecord(20, 5, new MftRecordFields(1), $"scan-{volume.Letter}-{scanNumber}.txt")];
    }

    Task HoldWrites(string pipeName) =>
        pipeName != ControlPipeName && _frozen.ContainsKey(DriveOfPipe(pipeName)) ? _thaw.Task : Task.CompletedTask;

    Stream CountReads(string pipeName, Stream client)
    {
        var reads = new ReadCounter();
        lock (_gate)
        {
            _pipes.Add((pipeName, reads));
        }

        var wrapped = reads.Wrap(client);
        return pipeName == ControlPipeName ? new OpenChannelObserverStream(wrapped, _pipeDrives) : wrapped;
    }

    void Track(string key)
    {
        ReadCounter reads;
        lock (_gate)
        {
            reads = _pipes.Last(pipe => key == ControlPipeName
                ? pipe.Name == ControlPipeName
                : pipe.Name != ControlPipeName && DriveOfPipe(pipe.Name) == key[0]).Reads;
        }

        _tracked[key] = new Cadence(reads);
    }

    /// <summary>The status of <paramref name="driveLetter" /> at each of its lost-catch-up faults, in raise order.</summary>
    public RootStatus[] CatchUpLossStatuses(char driveLetter) => _faultStatuses.CatchUpLossStatuses(driveLetter);

    void RecordFault(FileIndex index, WatchFault fault)
    {
        _faultStatuses.Record(index, fault);
        lock (_gate)
        {
            _faults.Add(fault);
            var occurrence = _faults.Count(other => other.Kind == fault.Kind && other.Volume == fault.Volume);
            foreach (var waiter in _waiters.Where(waiter => waiter.Kind == fault.Kind &&
                                                            waiter.Drive == fault.Volume &&
                                                            waiter.Occurrence == occurrence))
            {
                waiter.Signal.TrySetResult(fault);
            }
        }
    }

    char DriveOfPipe(string pipeName) => _pipeDrives[pipeName];

    sealed class OpenChannelObserverStream(Stream inner, ConcurrentDictionary<string, char> pipeDrives)
        : DelegatingStream(inner)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 5 && buffer.Span[4] == (byte)BrokerFrameKind.OpenChannel)
            {
                var frame = BrokerProtocol.ReadFrame(buffer.Span, out _);
                pipeDrives[frame.RequirePipeName()] = frame.RequireVolume().Letter;
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    static string TrackKey(char driveLetter) => driveLetter.ToString();

    static int MeasureHeartbeat()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(buffer, BrokerFrame.Heartbeat());
        return buffer.WrittenCount;
    }

    sealed class Cadence(ReadCounter reads)
    {
        public ReadCounter Reads { get; } = reads;

        /// <summary>The bytes the client had consumed as of the last interval, heartbeats included.</summary>
        public long Settled { get; set; }
    }
}
