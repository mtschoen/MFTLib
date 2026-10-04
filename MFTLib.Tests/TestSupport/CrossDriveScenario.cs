using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     An in-process <see cref="FileIndex" /> over drives <c>T</c> and <c>U</c> served by a real
///     <see cref="BrokerProcess" /> and an in-process <see cref="JournalBrokerHost" />, with a
///     <see cref="FakeTimeProvider" /> on the host (<see cref="HostClock" />) and another on the
///     client (<see cref="ClientClock" />). Nothing is mocked: each drive scans and watches through
///     its own in-memory pipes. A test that needs time to pass calls
///     <see cref="AdvanceIntervalAsync" />, which moves both clocks one heartbeat interval and
///     proves the client consumed every heartbeat the host wrote before it moves the client's
///     clock, so a pipe's stall limit measures only what the host really did not send.
/// </summary>
internal sealed class CrossDriveScenario : IAsyncDisposable
{
    const string ControlPipeName = "control";
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;
    static readonly int HeartbeatLength = MeasureHeartbeat();

    readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"broker-cross-drive-{Guid.NewGuid():N}");
    readonly ConcurrentDictionary<char, int> _scans = new();
    readonly ConcurrentDictionary<char, bool> _frozen = new();
    readonly TaskCompletionSource _thaw = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<bool> _visits = Channel.CreateUnbounded<bool>();
    readonly Lock _gate = new();
    readonly List<(string Name, ReadCounter Reads)> _pipes = [];
    readonly List<WatchFault> _faults = [];
    readonly List<(WatchFaultKind Kind, char Drive, int Occurrence, TaskCompletionSource<WatchFault> Signal)> _waiters = [];
    readonly Dictionary<string, Cadence> _tracked = new(StringComparer.Ordinal);

    FileIndex? _index;

    CrossDriveScenario(UsnJournalCatchUpSource? readJournal)
    {
        Broker = new ScriptedWatchBrokerHarness(scanDrive: Scan, seams: new ScriptedBrokerSeams
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

    public FileIndex Index => _index ?? throw new InvalidOperationException("The index is not open yet.");

    public CancellationToken Token => Broker.CancellationToken;

    /// <summary>Every fault the index has raised, in order.</summary>
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
    ///     Opens the index: each drive's first scan lists <c>scan-{drive}-1.txt</c>, and every later
    ///     scan of a drive names its own number, so a test can tell which scan produced a block.
    /// </summary>
    /// <param name="readJournal">The host's catch-up source; null reads nothing and holds at the armed cursor.</param>
    public static async Task<CrossDriveScenario> OpenAsync(UsnJournalCatchUpSource? readJournal = null)
    {
        var scenario = new CrossDriveScenario(readJournal);
        try
        {
            await scenario.OpenIndexAsync();
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

    /// <summary>How many drive pipes the client has opened for <paramref name="driveLetter" />, scans and watches alike.</summary>
    public int ChannelsOpenedFor(char driveLetter)
    {
        lock (_gate)
        {
            return _pipes.Count(pipe => pipe.Name != ControlPipeName && DriveOfPipe(pipe.Name) == driveLetter);
        }
    }

    public DriveStatus DriveOf(char driveLetter) => Index.Drives.Single(drive => drive.DriveLetter == driveLetter);

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
            var matching = _faults.Where(fault => fault.Kind == kind && fault.DriveLetter == driveLetter).ToList();
            if (matching.Count >= occurrence)
            {
                return Task.FromResult(matching[occurrence - 1]);
            }

            var signal = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((kind, driveLetter, occurrence, signal));
            return signal.Task.WaitAsync(HangGuard);
        }
    }

    public IReadOnlyList<WatchFault> FaultsOf(char driveLetter) =>
        Faults.Where(fault => fault.DriveLetter == driveLetter).ToArray();

    /// <summary>
    ///     Starts measuring heartbeats on the control pipe and on each named drive's newest pipe,
    ///     from the bytes the client has read so far. Call it once every frame the host has written
    ///     so far has been consumed, for instance after a caught-up wait.
    /// </summary>
    public void BeginCadence(params char[] driveLetters)
    {
        _tracked.Clear();
        Track(ControlPipeName);
        foreach (var driveLetter in driveLetters)
        {
            Track(DriveKey(driveLetter));
        }
    }

    /// <summary>Stops expecting heartbeats on <paramref name="driveLetter" />'s pipe, whose channel has ended.</summary>
    public void StopMeasuring(char driveLetter) => _tracked.Remove(DriveKey(driveLetter));

    /// <summary>
    ///     Moves the host's clock one heartbeat interval, waits for the heartbeat sender's visit, waits
    ///     until the client has read every heartbeat that visit wrote to a measured pipe, and only
    ///     then moves the client's clock the same interval. The sender heartbeats a pipe that wrote no
    ///     frame since its last visit, so a pipe that carried frames in the meantime is expected to
    ///     skip this one. The pipes of <paramref name="silentDrives" /> are expected to carry nothing.
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
        await _visits.Reader.ReadAsync().AsTask().WaitAsync(HangGuard);
        foreach (var (key, pipe) in awaited)
        {
            try
            {
                await pipe.Reads.WhenReadPendingAfter(pipe.Settled).WaitAsync(HangGuard);
            }
            catch (TimeoutException)
            {
                throw new AssertFailedException($"The client never read the {key} pipe's expected heartbeat: " +
                                                $"{pipe.Reads.BytesRead} of {pipe.Settled} bytes.");
            }
        }

        ClientClock.Advance(BrokerLiveness.HeartbeatInterval);
    }

    public async ValueTask DisposeAsync()
    {
        _thaw.TrySetResult();
        try
        {
            if (_index is not null)
            {
                await _index.DisposeAsync().AsTask().WaitAsync(HangGuard);
            }
        }
        finally
        {
            try
            {
                await Broker.DisposeAsync().AsTask().WaitAsync(HangGuard);
            }
            finally
            {
                if (Directory.Exists(_cacheDirectory))
                {
                    Directory.Delete(_cacheDirectory, recursive: true);
                }
            }
        }
    }

    async Task OpenIndexAsync()
    {
        var producer = new BrokerMftBlockProducer(Broker.ConnectAsync);
        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', Path.GetTempPath(), 123), new IndexedDrive('U', Path.GetTempPath(), 456)],
            CacheDirectory = _cacheDirectory,
            NoCache = true,
            MftSource = producer.CreateIndexSource()
        }, Token);
        _index.WatchFaulted += RecordFault;
    }

    IEnumerable<IReadOnlyList<MftRecord>> Scan(string drive, ParseThreadAllowance parseThreads,
        IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var scanNumber = _scans.AddOrUpdate(drive[0], 1, (_, count) => count + 1);
        yield return [new MftRecord(5, 5, new MftRecordFields(3), ".", null),
            new MftRecord(20, 5, new MftRecordFields(1), $"scan-{drive}-{scanNumber}.txt", null)];
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

        return reads.Wrap(client);
    }

    // Measures from zero: no visit has happened yet on this pipe, so the sender's first one skips a
    // pipe that has carried any frame.
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

    void RecordFault(WatchFault fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
            var occurrence = _faults.Count(other => other.Kind == fault.Kind && other.DriveLetter == fault.DriveLetter);
            foreach (var waiter in _waiters.Where(waiter => waiter.Kind == fault.Kind &&
                                                            waiter.Drive == fault.DriveLetter &&
                                                            waiter.Occurrence == occurrence))
            {
                waiter.Signal.TrySetResult(fault);
            }
        }
    }

    // A drive pipe is named "control-{drive}-{sequence}".
    static char DriveOfPipe(string pipeName) => pipeName.Split('-')[^2][0];

    static string DriveKey(char driveLetter) => driveLetter.ToString();

    static int MeasureHeartbeat()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(buffer);
        return buffer.WrittenCount;
    }

    sealed class Cadence(ReadCounter reads)
    {
        public ReadCounter Reads { get; } = reads;

        /// <summary>The bytes the client had consumed as of the last interval, heartbeats included.</summary>
        public long Settled { get; set; }
    }
}
