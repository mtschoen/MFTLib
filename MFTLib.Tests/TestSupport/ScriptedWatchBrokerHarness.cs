using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A real <see cref="BrokerProcess" /> served by an in-process <see cref="JournalBrokerHost" />
///     whose watch source is scripted per drive: each host-side watch is a
///     <see cref="ScriptedWatchRun" /> the test pushes batches into, fails, ends or holds. Nothing
///     here mocks the client, and a host fault reaches a test only through what the process and its
///     channels surface.
/// </summary>
internal sealed class ScriptedWatchBrokerHarness : IAsyncDisposable
{
    /// <summary>The journal tip every watch is measured against; a watch from it starts caught up.</summary>
    public static readonly UsnJournalCursor DefaultTip = new(WatchHarness.JournalId, WatchHarness.NextUsn);

    readonly ConcurrentDictionary<char, ScriptedHostWatch> _watches = new();
    readonly ConcurrentDictionary<char, UsnJournalCursor> _tips = new();
    readonly CancellationTokenSource _hangGuard = new(HostChannelHarness.HangGuard);
    readonly InProcessBroker _broker;
    readonly ArmableControlCorruption _corruption = new();
    int _connectionCount;

    /// <summary>Serves a broker process whose host has no scans unless <paramref name="scanDrive" /> supplies them.</summary>
    /// <param name="tip">The journal tip the host answers to a watch's arm query and to a scan's arming.</param>
    /// <param name="scanDrive">The host's scan source; a test that scans supplies it, and any other scan fails.</param>
    /// <param name="seams">The host's catch-up source and clock, the client's options and the observation hooks.</param>
    public ScriptedWatchBrokerHarness(UsnJournalCursor? tip = null, MftRecordBatchSource? scanDrive = null,
        ScriptedBrokerSeams? seams = null)
    {
        seams ??= new ScriptedBrokerSeams();
        var journalTip = tip ?? DefaultTip;
        var host = new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(drive => _tips.GetValueOrDefault(drive[0], journalTip),
                scanDrive ?? ((_, _, _, _, _) => throw new InvalidOperationException("A scan is not expected.")),
                seams.ReadJournal ?? ((_, since, _) => ([], since)),
                (drive, since, operation, token) => Watch(drive[0]).RunAsync(since, operation, token),
                QueryVolumeInformation: _ => new NtfsVolumeInformation(1024 * 100, 1024)),
            processorCount: 4,
            timeProvider: seams.HostClock)
        {
            HeartbeatVisitedForTest = seams.HeartbeatVisited
        };
        _broker = new InProcessBroker(host, seams.ClientOptions,
            wrapClientStream: (name, stream) =>
            {
                var wrapped = name == "control" ? _corruption.Wrap(stream) : stream;
                return seams.WrapClientStream?.Invoke(name, wrapped) ?? wrapped;
            });
    }

    public BrokerProcess Process => _broker.Process;

    /// <summary>Cancelled if a test outlives <see cref="HostChannelHarness.HangGuard" />.</summary>
    public CancellationToken CancellationToken => _hangGuard.Token;

    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>The connect callback a source under test is given.</summary>
    public Task<BrokerProcess> ConnectAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectionCount);
        return Task.FromResult(Process);
    }

    /// <summary>
    ///     Moves the journal tip the host answers for <paramref name="driveLetter" />: a watch armed
    ///     from a block cursor below it has a backlog to deliver before the drive is caught up.
    /// </summary>
    public void SetTip(char driveLetter, UsnJournalCursor tip) => _tips[char.ToUpperInvariant(driveLetter)] = tip;

    /// <summary>The host-side script of one drive's watches.</summary>
    public ScriptedHostWatch Watch(char driveLetter) =>
        _watches.GetOrAdd(char.ToUpperInvariant(driveLetter), _ => new ScriptedHostWatch());

    /// <summary>
    ///     Ends the host the way a broker process that hits a fatal error ends: the next control
    ///     frame the client writes is one the host cannot read, so its session fails, and its pipes
    ///     close on every channel at once. Returns once the client has seen the control pipe go.
    /// </summary>
    public Task EndHostAsync()
    {
        _corruption.CorruptNextWrite();
        return Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HostChannelHarness.HangGuard));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var watch in _watches.Values)
        {
            watch.ReleaseAll();
        }

        await _broker.DisposeAsync();
        _hangGuard.Dispose();
    }

    sealed class ArmableControlCorruption
    {
        int _armed;

        public Stream Wrap(Stream inner) => new CorruptingStream(inner, this);

        public void CorruptNextWrite() => Volatile.Write(ref _armed, 1);

        bool TakeArmed() => Interlocked.Exchange(ref _armed, 0) == 1;

        sealed class CorruptingStream(Stream inner, ArmableControlCorruption owner) : DelegatingStream(inner)
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!owner.TakeArmed())
                {
                    return Inner.WriteAsync(buffer, cancellationToken);
                }

                var corrupted = buffer.ToArray();
                corrupted[4] = 200;
                return Inner.WriteAsync(corrupted, cancellationToken);
            }
        }
    }
}

/// <summary>The seams of a <see cref="ScriptedWatchBrokerHarness" /> beyond its scan source; every one is optional.</summary>
internal sealed record ScriptedBrokerSeams
{
    /// <summary>The host's journal catch-up source; the default reads nothing and stays at the cursor it is given.</summary>
    public UsnJournalCatchUpSource? ReadJournal { get; init; }

    /// <summary>The host's clock; null reads the system clock.</summary>
    public TimeProvider? HostClock { get; init; }

    /// <summary>The client's clock, held writes and connection failures; null takes the harness defaults.</summary>
    public BrokerTestHarnessOptions? ClientOptions { get; init; }

    /// <summary>Runs on the host's heartbeat sender thread after each visit.</summary>
    public Action? HeartbeatVisited { get; init; }

    /// <summary>Wraps the client end of each pipe, given its name (<c>"control"</c> or a drive pipe's name).</summary>
    public Func<string, Stream, Stream>? WrapClientStream { get; init; }
}

/// <summary>
///     The host-side watches of one drive, one <see cref="ScriptedWatchRun" /> per watch the host
///     starts, numbered from one in the order the host starts them.
/// </summary>
internal sealed class ScriptedHostWatch
{
    readonly ConcurrentDictionary<int, TaskCompletionSource<ScriptedWatchRun>> _runs = new();
    readonly ConcurrentQueue<ScriptedWatchRun> _started = new();
    int _count;
    int _ignoreCancellationInNextRun;
    (string StepName, TestGate Gate)? _wedgeInNextRun;

    /// <summary>Makes the next watch the host starts keep running after its pipe closes, like a wedged native read, until released.</summary>
    public void IgnoreCancellationInNextRun() => Volatile.Write(ref _ignoreCancellationInNextRun, 1);

    /// <summary>
    ///     Makes the next watch the host starts publish <paramref name="stepName" /> as its processing
    ///     step and stay there, without yielding, until <paramref name="gate" /> is released: a
    ///     source loop that is alive but makes no progress.
    /// </summary>
    public void WedgeNextRunInProcessing(string stepName, TestGate gate) => _wedgeInNextRun = (stepName, gate);

    /// <summary>Completes with the <paramref name="number" />th watch the host starts on this drive.</summary>
    public Task<ScriptedWatchRun> RunAsync(int number) =>
        Slot(number).Task.WaitAsync(HostChannelHarness.HangGuard);

    public int StartedCount => Volatile.Read(ref _count);

    internal IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> RunAsync(UsnJournalCursor since,
        IBrokerOperationReporter operation, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref _count);
        var wedge = _wedgeInNextRun;
        _wedgeInNextRun = null;
        var run = new ScriptedWatchRun(since, Interlocked.Exchange(ref _ignoreCancellationInNextRun, 0) == 1,
            operation, wedge);
        _started.Enqueue(run);
        Slot(number).TrySetResult(run);
        return run.ReadAsync(cancellationToken);
    }

    internal void ReleaseAll()
    {
        foreach (var run in _started)
        {
            run.Release();
        }
    }

    TaskCompletionSource<ScriptedWatchRun> Slot(int number) =>
        _runs.GetOrAdd(number, _ => new TaskCompletionSource<ScriptedWatchRun>(
            TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
///     One watch the host runs on a drive, driven by hand. It yields each pushed batch, fails or
///     ends when told to, and reports when it entered, when the host cancelled it (its pipe closed
///     or its session ended) and when it returned.
/// </summary>
internal sealed class ScriptedWatchRun
{
    readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> _items =
        Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();

    readonly bool _ignoreCancellation;
    readonly IBrokerOperationReporter _operation;
    readonly (string StepName, TestGate Gate)? _wedge;

    internal ScriptedWatchRun(UsnJournalCursor since, bool ignoreCancellation, IBrokerOperationReporter operation,
        (string StepName, TestGate Gate)? wedge)
    {
        Since = since;
        _ignoreCancellation = ignoreCancellation;
        _operation = operation;
        _wedge = wedge;
    }

    /// <summary>The cursor the client asked this watch to start from.</summary>
    public UsnJournalCursor Since { get; }

    public Task Entered => _entered.Task;

    /// <summary>Completes when the host cancels the watch.</summary>
    public Task Cancelled => _cancelled.Task;

    /// <summary>Completes when the host's watch source returns or throws.</summary>
    public Task Finished => _finished.Task;

    /// <summary>Delivers one batch of one created file, ending at <paramref name="nextUsn" />.</summary>
    public void Push(uint recordNumber, string fileName, long nextUsn) =>
        _items.Writer.TryWrite(([WatchHarness.Create(recordNumber, fileName)],
            new UsnJournalCursor(WatchHarness.JournalId, nextUsn)));

    /// <summary>The host's watch fails: the client reads an <c>Error</c> frame carrying the exception's message.</summary>
    public void Fail(Exception exception) => _items.Writer.TryComplete(exception);

    /// <summary>The host's watch source ends normally, which closes the drive's pipe.</summary>
    public void End() => _items.Writer.TryComplete();

    /// <summary>Lets a watch that ignores cancellation finish, after it has yielded what is queued.</summary>
    public void Release() => _released.TrySetResult();

    internal async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());
        _entered.TrySetResult();
        try
        {
            if (_wedge is { } wedge)
            {
                _operation.Processing(wedge.StepName);
                wedge.Gate.MarkEntered();
                await wedge.Gate.WaitForReleaseAsync(cancellationToken);
            }

            if (_ignoreCancellation)
            {
                await _released.Task;
                _items.Writer.TryComplete();
            }

            await foreach (var item in _items.Reader.ReadAllAsync(_ignoreCancellation
                               ? CancellationToken.None
                               : cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            // Registered callbacks run last-in first-out, so the read's own wake-up can end this
            // iteration and dispose the registration before the callback above has run.
            if (cancellationToken.IsCancellationRequested)
            {
                _cancelled.TrySetResult();
            }

            _finished.TrySetResult();
        }
    }
}

/// <summary>Completes when a <see cref="FileIndex" /> raises <see cref="FileIndex.Changed" /> for a named file.</summary>
internal static class ChangeSignal
{
    public static Task WhenApplied(FileIndex index, string fileName)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.Changed += change =>
        {
            if (change.Entry.Name == fileName)
            {
                applied.TrySetResult();
            }
        };
        return applied.Task.WaitAsync(HostChannelHarness.HangGuard);
    }
}

/// <summary>Bounded reads of a drive's watch handle, so a hang fails a test instead of wedging it.</summary>
internal static class WatchReads
{
    public static async Task<WatchStreamItem> NextAsync(IAsyncEnumerator<WatchStreamItem> reader)
    {
        Assert.IsTrue(await reader.MoveNextAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard),
            "The watch ended where an item was expected.");
        return reader.Current;
    }

    public static async Task<JournalBatch> NextBatchAsync(IAsyncEnumerator<WatchStreamItem> reader)
    {
        var item = await NextAsync(reader);
        Assert.IsInstanceOfType<JournalBatch>(item);
        return (JournalBatch)item;
    }

    /// <summary>The read that throws <typeparamref name="TException" />, or a type derived from it.</summary>
    public static Task<TException> ThrowsNextAsync<TException>(IAsyncEnumerator<WatchStreamItem> reader)
        where TException : Exception =>
        WatchDeduplicationTestSupport.ThrowsAsync<TException>(
            async () => await reader.MoveNextAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard));
}
