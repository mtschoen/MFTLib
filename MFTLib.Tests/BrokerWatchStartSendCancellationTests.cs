using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     MFTLib issue 250: the broker source never cancels its StartWatch send, so a start whose send
///     is stuck on the pipe must still end promptly when its token is cancelled, or when the watch
///     is stopped or the index disposed, and the send it left behind must be torn down without
///     disturbing the next watch. The StartWatch frame's pipe write is held by a gate for the whole
///     interruption, and every gate is released in a finally so a failed assertion cannot wedge
///     the in-process broker.
/// </summary>
[TestClass]
public sealed class BrokerWatchStartSendCancellationTests
{
    [TestMethod]
    public async Task StartWatchingAsync_CancelledWhileTheStartWatchSendIsBlocked_ThrowsAndTheNextStartWatches()
    {
        await using var scenario = new BlockedStartWatchScenario();
        var token = scenario.CancellationToken;
        await using var index = await scenario.OpenIndexAsync(token);
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var start = index.StartWatchingAsync(startCancellation.Token);
            await scenario.StartWatchGate.Entered.WaitAsync(token);
            await startCancellation.CancelAsync();

            await AssertCancelledWhileTheSendIsHeldAsync(start);
        }
        finally
        {
            scenario.StartWatchGate.Release();
        }

        await scenario.StartWatchAndRescanAsync(index, token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_WhileTheStartWatchSendIsBlocked_CompletesAndTheNextStartWatches()
    {
        await using var scenario = new BlockedStartWatchScenario();
        var token = scenario.CancellationToken;
        await using var index = await scenario.OpenIndexAsync(token);
        try
        {
            var start = index.StartWatchingAsync(token);
            await scenario.StartWatchGate.Entered.WaitAsync(token);

            await index.StopWatchingAsync(token).WaitAsync(FakeIndexWatchSource.HangGuard, token);
            await AssertCancelledWhileTheSendIsHeldAsync(start);
        }
        finally
        {
            scenario.StartWatchGate.Release();
        }

        await scenario.StartWatchAndRescanAsync(index, token);
    }

    [TestMethod]
    public async Task DisposeAsync_WhileTheStartWatchSendIsBlocked_CompletesAndTheSourceWatchesForTheNextIndex()
    {
        await using var scenario = new BlockedStartWatchScenario();
        var token = scenario.CancellationToken;
        var disposedIndex = await scenario.OpenIndexAsync(token);
        try
        {
            var start = disposedIndex.StartWatchingAsync(token);
            await scenario.StartWatchGate.Entered.WaitAsync(token);

            await disposedIndex.DisposeAsync().AsTask().WaitAsync(FakeIndexWatchSource.HangGuard, token);
            await AssertCancelledWhileTheSendIsHeldAsync(start);
        }
        finally
        {
            scenario.StartWatchGate.Release();
            await disposedIndex.DisposeAsync();
        }

        await using var index = await scenario.OpenIndexAsync(token);
        await scenario.StartWatchAndRescanAsync(index, token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_IssuedWhileAnAbandonedSendIsBlocked_WaitsForItsTeardownAndThenWatches()
    {
        await using var scenario = new BlockedStartWatchScenario();
        var token = scenario.CancellationToken;
        await using var index = await scenario.OpenIndexAsync(token);
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task nextStart;
        try
        {
            var start = index.StartWatchingAsync(startCancellation.Token);
            await scenario.StartWatchGate.Entered.WaitAsync(token);
            await startCancellation.CancelAsync();
            await AssertCancelledWhileTheSendIsHeldAsync(start);

            nextStart = index.StartWatchingAsync(token);
            Assert.IsFalse(nextStart.IsCompleted,
                "A start completed while the previous start's StartWatch send was still on the pipe.");
        }
        finally
        {
            scenario.StartWatchGate.Release();
        }

        await nextStart.WaitAsync(token);
        await scenario.WatchAndRescanAsync(index, token);
    }

    /// <summary>
    ///     The interrupted start must have ended while the gate still holds its send, which is the
    ///     whole point: bounded by the hang guard, so the pre-fix hang reports a
    ///     <see cref="TimeoutException" /> rather than stalling the run.
    /// </summary>
    static async Task AssertCancelledWhileTheSendIsHeldAsync(Task start)
    {
        try
        {
            await start.WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("The start completed instead of being cancelled.");
    }

    /// <summary>
    ///     Two drives on one in-process broker whose client pipe gates the first StartWatch frame
    ///     mid-write. Journal batches are fed per drive through channels, and every applied change
    ///     is announced by name.
    /// </summary>
    sealed class BlockedStartWatchScenario : IAsyncDisposable
    {
        readonly Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> _batchesC =
            Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();

        readonly Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> _batchesD =
            Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();

        readonly Dictionary<string, TaskCompletionSource> _appliedByName = new();
        readonly Lock _appliedLock = new();
        readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"broker-start-send-{Guid.NewGuid():N}");
        readonly InProcessBlockBrokerHarness _harness;
        readonly BrokerMftBlockProducer _producer;
        readonly BrokerIndexWatchSource _source;
        GateFrameWriteStream? _startWatchGate;

        public BlockedStartWatchScenario()
        {
            _harness = new InProcessBlockBrokerHarness(new InProcessBlockBrokerHarness.Options
            {
                RecordBatches = (_, _, token) => Scan(token),
                CatchUp = (_, cursor) => ([], cursor),
                WatchDrive = Watch,
                WrapClientTransport = transport =>
                    _startWatchGate = new GateFrameWriteStream(transport, BrokerFrameKind.StartWatch)
            });
            _producer = new BrokerMftBlockProducer(_harness.ConnectAsync);
            _source = new BrokerIndexWatchSource(_harness.ConnectAsync);
        }

        public CancellationToken CancellationToken => _harness.CancellationToken;

        public GateFrameWriteStream StartWatchGate =>
            _startWatchGate ?? throw new InvalidOperationException("The client transport was never wrapped.");

        public async ValueTask DisposeAsync()
        {
            StartWatchGate.Release();
            await _harness.DisposeAsync();
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }

        public async Task<FileIndex> OpenIndexAsync(CancellationToken token)
        {
            var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('C', Path.GetTempPath(), 123),
                    new IndexedDrive('D', Path.GetTempPath(), 456)],
                CacheDirectory = _cacheDirectory,
                NoCache = true,
                MftProducer = _producer.CreateProducer(),
                WatchSource = _source
            }, token);
            index.Changed += change => Applied(change.Entry.Name).TrySetResult();
            return index;
        }

        public async Task StartWatchAndRescanAsync(FileIndex index, CancellationToken token)
        {
            await index.StartWatchingAsync(token);
            await WatchAndRescanAsync(index, token);
        }

        /// <summary>
        ///     Proves the watch running now is whole: the abandoned StartWatch reached the broker and
        ///     was ended before this watch's StartWatch was sent, so its EndWatchAck was read by the
        ///     teardown and not by this watch. Both drives deliver, a rescan of one re-arms it, and it
        ///     delivers again, with no watch fault recorded anywhere.
        /// </summary>
        public async Task WatchAndRescanAsync(FileIndex index, CancellationToken token)
        {
            var faults = new List<WatchFault>();
            index.WatchFaulted += faults.Add;
            await DeliverAsync(_batchesC, 12600, "watched-c.txt", token);
            await DeliverAsync(_batchesD, 12600, "watched-d.txt", token);

            var watchFrames = StartWatchGate.ForwardedFrameKinds
                .Where(kind => kind is BrokerFrameKind.StartWatch or BrokerFrameKind.EndWatch)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { BrokerFrameKind.StartWatch, BrokerFrameKind.EndWatch, BrokerFrameKind.StartWatch },
                watchFrames, string.Join(", ", watchFrames));

            await index.RescanAsync('C', token);
            await DeliverAsync(_batchesC, 12700, "rescanned-c.txt", token);

            Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(fault => fault.Exception.Message)));
            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            await index.StopWatchingAsync(token);
        }

        Task DeliverAsync(Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> batches, long usn,
            string name, CancellationToken token)
        {
            batches.Writer.TryWrite(([JournalEntryFactory.Create((ulong)(usn / 100), usn, name, UsnReason.FileCreate)],
                new UsnJournalCursor(71, usn + 1)));
            return Applied(name).Task.WaitAsync(token);
        }

        TaskCompletionSource Applied(string name)
        {
            lock (_appliedLock)
            {
                if (!_appliedByName.TryGetValue(name, out var applied))
                {
                    applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _appliedByName.Add(name, applied);
                }

                return applied;
            }
        }

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> Watch(
            string drive, UsnJournalCursor cursor, [EnumeratorCancellation] CancellationToken token)
        {
            var channel = drive == "C" ? _batchesC : _batchesD;
            await foreach (var batch in channel.Reader.ReadAllAsync(token))
            {
                yield return batch;
            }
        }

        static IEnumerable<IReadOnlyList<MftRecord>> Scan(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            yield return [new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                new MftRecord(20, 5, new MftRecordFields(1), "scanned.txt", null)];
        }
    }
}
