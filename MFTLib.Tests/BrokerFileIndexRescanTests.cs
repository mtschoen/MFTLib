using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class BrokerFileIndexRescanTests
{
    [TestMethod]
    public async Task Rescan_UsesTheSharedBrokerAndRearmsOnlyItsDrive()
    {
        using var scenario = new SharedBrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        await using var index = await scenario.OpenIndexAsync(producer.CreateProducer(),
            producer.CreateWatchSource(), token);

        await index.StartWatchingAsync(token);
        await Task.WhenAll(scenario.FirstArmC, scenario.FirstArmD).WaitAsync(token);
        await scenario.RescanWhileTheSiblingDeliversAsync(index, token);
        await index.StopWatchingAsync(token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WaitsForTheBrokerStreamToBePublished_SoAnImmediateRescanRearmsOnlyItsDrive()
    {
        using var scenario = new SharedBrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var publishGate = new TestGate();
        var source = new BrokerIndexWatchSource(harness.ConnectAsync)
        {
            BeforeStreamPublishedForTest = async gateToken =>
            {
                publishGate.MarkEntered();
                await publishGate.WaitForReleaseAsync(gateToken);
            }
        };
        try
        {
            await using var index = await scenario.OpenIndexAsync(producer.CreateProducer(), source, token);
            var faults = new List<WatchFault>();
            index.WatchFaulted += faults.Add;

            var start = index.StartWatchingAsync(token);
            await publishGate.Entered.WaitAsync(token);
            Assert.IsFalse(start.IsCompleted,
                "StartWatchingAsync completed while the StartWatch frame was sent but the stream was unpublished.");

            publishGate.Release();
            await start.WaitAsync(token);
            await scenario.RescanWhileTheSiblingDeliversAsync(index, token);
            Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(fault => fault.Exception.Message)));
            await index.StopWatchingAsync(token);
        }
        finally
        {
            publishGate.Release();
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_WaitsForTheBrokerConnection_AndARescanNeedsNoDeliveredItem()
    {
        using var scenario = new SharedBrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectGate = new TestGate();
        var source = new BrokerIndexWatchSource(async connectToken =>
        {
            connectGate.MarkEntered();
            await connectGate.WaitForReleaseAsync(connectToken);
            return await connect(connectToken);
        });
        try
        {
            await using var index = await scenario.OpenIndexAsync(producer.CreateProducer(), source, token);

            var start = index.StartWatchingAsync(token);
            await connectGate.Entered.WaitAsync(token);
            Assert.IsFalse(start.IsCompleted, "StartWatchingAsync completed before the broker connected.");

            connectGate.Release();
            await start.WaitAsync(token);
            await index.RescanAsync('D', token);

            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            await index.StopWatchingAsync(token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheBrokerConnectionFails_ThrowsItAndLeavesTheIndexStartable()
    {
        using var scenario = new SharedBrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectionFailure = new IOException("the broker pipe is gone");
        var connectAttempts = 0;
        var source = new BrokerIndexWatchSource(connectToken => Interlocked.Increment(ref connectAttempts) == 1
            ? Task.FromException<JournalBrokerClient>(connectionFailure)
            : connect(connectToken));
        await using var index = await scenario.OpenIndexAsync(producer.CreateProducer(), source, token);

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => index.StartWatchingAsync(token));

        Assert.AreSame(connectionFailure, thrown);
        await index.StopWatchingAsync(token);
        await index.StartWatchingAsync(token);
        await index.RescanAsync('C', token);
        Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
        await index.StopWatchingAsync(token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_CancelledWhileTheBrokerConnects_ThrowsAndLeavesTheIndexStartable()
    {
        using var scenario = new SharedBrokerScenario();
        await using var harness = scenario.CreateHarness();
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var connect = harness.ConnectAsync;
        var connectGate = new TestGate();
        var connectAttempts = 0;
        var source = new BrokerIndexWatchSource(async connectToken =>
        {
            if (Interlocked.Increment(ref connectAttempts) == 1)
            {
                connectGate.MarkEntered();
                await connectGate.WaitForReleaseAsync(connectToken);
            }

            return await connect(connectToken);
        });
        try
        {
            await using var index = await scenario.OpenIndexAsync(producer.CreateProducer(), source, token);
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

            var start = index.StartWatchingAsync(startCancellation.Token);
            await connectGate.Entered.WaitAsync(token);
            await startCancellation.CancelAsync();

            await AssertCancelledAsync(start, token);
            await index.StartWatchingAsync(token);
            await index.RescanAsync('C', token);
            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            await index.StopWatchingAsync(token);
        }
        finally
        {
            connectGate.Release();
        }
    }

    static async Task AssertCancelledAsync(Task task, CancellationToken token)
    {
        try
        {
            await task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return;
        }

        Assert.Fail("The start completed instead of being cancelled.");
    }

    /// <summary>
    ///     Two drives on one in-process broker. Drive C's second scan publishes a batch for drive D
    ///     and waits until the index applies it, so a rescan of C proves D kept delivering while C
    ///     was off the watch.
    /// </summary>
    sealed class SharedBrokerScenario : IDisposable
    {
        readonly Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> _batchesC =
            Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();

        readonly Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> _batchesD =
            Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();

        readonly TaskCompletionSource _firstArmC = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _firstArmD = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _secondArmC = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _appliedD = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _appliedC = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"broker-rescan-{Guid.NewGuid():N}");
        InProcessBlockBrokerHarness? _harness;
        int _armsC;
        int _armsD;
        int _scansC;

        public Task FirstArmC => _firstArmC.Task;

        public void Dispose()
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }

        public Task FirstArmD => _firstArmD.Task;

        public InProcessBlockBrokerHarness CreateHarness()
        {
            _harness = new InProcessBlockBrokerHarness(recordBatches: Scan,
                catchUp: (_, cursor) => ([], cursor), watchDrive: Watch);
            return _harness;
        }

        public async Task<FileIndex> OpenIndexAsync(MftBlockProducer producer, IIndexWatchSource watchSource,
            CancellationToken token)
        {
            var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('C', Path.GetTempPath(), 123),
                    new IndexedDrive('D', Path.GetTempPath(), 456)],
                CacheDirectory = _cacheDirectory,
                NoCache = true,
                MftProducer = producer,
                WatchSource = watchSource
            }, token);
            index.Changed += change =>
            {
                if (change.Entry.Name == "during.txt")
                {
                    _appliedD.TrySetResult();
                }

                if (change.Entry.Name == "after.txt")
                {
                    _appliedC.TrySetResult();
                }
            };
            return index;
        }

        /// <summary>
        ///     Rescans C while D delivers, observing the rescan's own failure directly rather than
        ///     through a timeout on the sibling's batch, then proves C was re-armed on the same
        ///     broker from its fresh block and delivers again.
        /// </summary>
        public async Task RescanWhileTheSiblingDeliversAsync(FileIndex index, CancellationToken token)
        {
            var rescan = index.RescanAsync('C', token);
            var firstFinished = await Task.WhenAny(_appliedD.Task, rescan).WaitAsync(token);
            await firstFinished;
            await rescan;
            await _secondArmC.Task.WaitAsync(token);
            Assert.AreEqual(2, _scansC);
            Assert.AreEqual(2, _armsC);
            Assert.AreEqual(1, _armsD);
            Assert.AreEqual("scan-2.txt", NamePool.ReadRowName(_harness!.CreatedBlock!, 20).ToString());
            Assert.AreEqual(1, index.FindByName("scan-2.txt", token).Count);
            Assert.AreEqual(1, index.FindByName("scan-1.txt", token).Count);
            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            _batchesC.Writer.TryWrite(([JournalEntryFactory.Create(41, 12700, "after.txt", UsnReason.FileCreate)],
                new UsnJournalCursor(71, 12701)));
            await _appliedC.Task.WaitAsync(token);
        }

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> Watch(
            string drive, UsnJournalCursor cursor, [EnumeratorCancellation] CancellationToken token)
        {
            if (drive == "C")
            {
                (Interlocked.Increment(ref _armsC) == 1 ? _firstArmC : _secondArmC).TrySetResult();
            }
            else
            {
                Interlocked.Increment(ref _armsD);
                _firstArmD.TrySetResult();
            }

            var channel = drive == "C" ? _batchesC : _batchesD;
            await foreach (var batch in channel.Reader.ReadAllAsync(token))
            {
                yield return batch;
            }
        }

        IEnumerable<IReadOnlyList<MftRecord>> Scan(string drive,
            IProgress<BlockWriteProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var scanNumber = drive == "C" ? Interlocked.Increment(ref _scansC) : 1;
            if (drive == "C" && scanNumber == 2)
            {
                _batchesD.Writer.TryWrite(([JournalEntryFactory.Create(40, 12600, "during.txt", UsnReason.FileCreate)],
                    new UsnJournalCursor(71, 12601)));
                _appliedD.Task.Wait(token);
            }

            yield return [new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                new MftRecord(20, 5, new MftRecordFields(1), $"scan-{scanNumber}.txt", null)];
        }
    }
}
