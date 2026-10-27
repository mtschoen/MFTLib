using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProcessTests
{
    sealed class UnavailableHostClock(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }

    static SyntheticJournalCursor ArmedSynthetic => Armed.ToSynthetic();

    static SyntheticScanRecord SyntheticRecord(ulong recordNumber, string name, ushort flags = 1) =>
        new()
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = 5,
            FileName = name,
            InUse = (flags & 1) != 0,
            IsDirectory = (flags & 2) != 0
        };

    static ScriptedBrokerVolumes ScriptedVolumes(
        Func<ScriptedScan, IEnumerable<IReadOnlyList<SyntheticScanRecord>>>? scanDrive = null) => new()
        {
            QueryJournalCursor = _ => ArmedSynthetic,
            ScanDrive = scanDrive
        };

    public enum HandleDisposalEdge
    {
        Sequential,
        OverlappingHandle,
        ProcessFirst
    }

    static IEnumerable<IReadOnlyList<SyntheticScanRecord>> BatchesWithHeldCleanup(TestGate scanStarted,
        TestGate cleanupGate, CancellationToken cancellationToken)
    {
        scanStarted.MarkEntered();
        try
        {
            cancellationToken.WaitHandle.WaitOne();
        }
        finally
        {
            cleanupGate.MarkEntered();
            cleanupGate.WaitForRelease();
        }

        yield break;
    }

    [TestMethod]
    [DataRow(HandleDisposalEdge.Sequential)]
    [DataRow(HandleDisposalEdge.OverlappingHandle)]
    [DataRow(HandleDisposalEdge.ProcessFirst)]
    public async Task ScriptedVolumes_AnswerTheDefaultSmallVolumeAndDisposeTwice(HandleDisposalEdge edge)
    {
        if (edge == HandleDisposalEdge.Sequential)
        {
            var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());
            var process = handle.Process;
            try
            {
                var volume = await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
                Assert.AreEqual(256 * 1024, volume.MftValidDataLength);
                Assert.AreEqual(1024u, volume.BytesPerFileRecordSegment);
                Assert.AreEqual(256, volume.MftRecordCount);
                await handle.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
                await process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
            finally
            {
                await handle.DisposeAsync();
            }

            return;
        }

        var scanStarted = new TestGate();
        var cleanupGate = new TestGate();
        var handleWithScan = BrokerTestHarness.StartInProcess(ScriptedVolumes(scan =>
            BatchesWithHeldCleanup(scanStarted, cleanupGate, scan.CancellationToken)));
        var activeProcess = handleWithScan.Process;
        try
        {
            var scanTask = activeProcess.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
                null, CancellationToken.None);
            await scanStarted.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            var scan = handleWithScan.Scans.Single();
            Assert.AreEqual(new DriveKey('C'), scan.Drive);

            var first = edge == HandleDisposalEdge.ProcessFirst
                ? activeProcess.DisposeAsync().AsTask()
                : handleWithScan.DisposeAsync().AsTask();
            await cleanupGate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            var second = handleWithScan.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);

            cleanupGate.Release();
            await first.WaitAsync(HangGuard, TestContext.CancellationToken);
            await second.WaitAsync(HangGuard, TestContext.CancellationToken);
            await activeProcess.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
            await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scanTask);
        }
        finally
        {
            cleanupGate.Release();
            await handleWithScan.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ScriptedVolumes_QueryVolumeOverridesTheDefault()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes() with { QueryVolume = _ => Volume });
        await using var other = BrokerTestHarness.StartInProcess(ScriptedVolumes());
        using var cancellation = new CancellationTokenSource();
        handle.Process.Hooks.AfterControlWriteLockAcquired = cancellation.Cancel;
        await AssertCancelledAsync(handle.Process.QueryVolumeAsync('C', cancellation.Token));
        Assert.AreEqual(0, handle.Process.Hooks.PendingRequestCount);
        using var independentCancellation = new CancellationTokenSource();
        handle.Process.Hooks.AfterControlWriteLockAcquired = independentCancellation.Cancel;
        var independent = await other.Process.QueryVolumeAsync('D', independentCancellation.Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(256L, independent.MftRecordCount);
        handle.Process.Hooks.AfterControlWriteLockAcquired = null;
        var volume = await handle.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
    }

    [TestMethod]
    public void ScriptedVolumes_RejectNullVolumesBeforeStarting()
    {
        var failure = Assert.ThrowsExactly<ArgumentNullException>(() => BrokerTestHarness.StartInProcess(null!));

        Assert.AreEqual("volumes", failure.ParamName);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostStartupFailure_EndsProcessAndDisposalDoesNotThrow(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException("host cancelled")
            : new InvalidOperationException("host unavailable");
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to InProcessBrokerHandle on Start
        var resources = ScriptedBrokerResources.Create();
        var handle = BrokerTestHarness.Start(
            BrokerHostFactory.Create(new() { TimeProvider = new UnavailableHostClock(failure) }), resources,
            new BrokerTestHarnessOptions(), null, null);
        var process = handle.Process;
        try
        {
            Assert.IsEmpty(handle.Scans, "Host startup failed before any scan began.");
            await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
                process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
            await process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
            await process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
        }
        finally
        {
            await handle.DisposeAsync();
        }

        BlockFileAssertions.IsDisposed(resources.Writer.Block);
    }

    [TestMethod]
    public async Task Crash_EndsTheClientAsABrokerDeathDoes()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());
        var process = handle.Process;
        await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        handle.Crash();
        handle.Crash();

        var reason = await process.Ended.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreNotEqual("The broker process was disposed.", reason);
        Assert.IsTrue(process.Ended.IsCompleted);
        await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() =>
            process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ScriptedVolumes_WithoutScanDriveRefuseAScanWithAClearError()
    {
        using var directory = new TempDirectory("control-only");
        var target = new BlockScanTarget(Path.Combine(directory.Path, "scan.mlix"), 1, true);
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            handle.Process.ScanDriveAsync('C', target, new MftScanOptions(), null, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("no scan source", failure.Message);
        Assert.AreEqual(new DriveKey('C'), handle.Scans.Single().Drive);
    }

    public enum ScanRecordingCase
    {
        Success,
        SynchronousSourceFailure,
        CursorQueryFailure,
        FirstCallbackHeld
    }

    [TestMethod]
    [DataRow(ScanRecordingCase.Success)]
    [DataRow(ScanRecordingCase.SynchronousSourceFailure)]
    [DataRow(ScanRecordingCase.CursorQueryFailure)]
    [DataRow(ScanRecordingCase.FirstCallbackHeld)]
    public async Task ScriptedScan_WritesTheBatchesThroughTheRealWriterAndRecordsTheRequest(ScanRecordingCase recordingCase)
    {
        var options = new MftScanOptions();
        var target = TestBlockSections.Target() with { RetainedFileNames = [".git"] };

        if (recordingCase == ScanRecordingCase.FirstCallbackHeld)
        {
            var firstCallbackEntered = new TestGate();
            var firstCallbackRelease = new TestGate();
            await using var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
            {
                QueryJournalCursor = _ => ArmedSynthetic,
                ScanDrive = scan =>
                {
                    if (scan.Drive == 'C')
                    {
                        firstCallbackEntered.MarkEntered();
                        firstCallbackRelease.WaitForRelease();
                    }

                    return [[SyntheticRecord(5, ".", 3)]];
                }
            });

            var scanC = handle.Process.ScanDriveAsync('c', target, options, null, CancellationToken.None);
            await firstCallbackEntered.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

            var resultD = await handle.Process.ScanDriveAsync('d', TestBlockSections.Target(), options, null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(new DriveKey('D'), resultD.Drive);

            Assert.HasCount(2, handle.Scans);
            Assert.AreEqual('C', handle.Scans[0].Drive);
            Assert.AreEqual('D', handle.Scans[1].Drive);

            firstCallbackRelease.Release();
            var resultC = await scanC.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(new DriveKey('C'), resultC.Drive);
            Assert.HasCount(2, handle.Scans);
            Assert.AreEqual('C', handle.Scans[0].Drive);
            Assert.AreEqual('D', handle.Scans[1].Drive);
            return;
        }

        Func<DriveKey, SyntheticJournalCursor> queryCursor = recordingCase == ScanRecordingCase.CursorQueryFailure
            ? _ => throw new InvalidOperationException("scripted cursor failure")
            : _ => ArmedSynthetic;

        Func<ScriptedScan, IEnumerable<IReadOnlyList<SyntheticScanRecord>>> scanDrive = recordingCase switch
        {
            ScanRecordingCase.SynchronousSourceFailure => _ => throw new InvalidOperationException("scripted scan failure"),
            _ => _ => [[SyntheticRecord(5, ".", 3)], [SyntheticRecord(20, "file.txt")]]
        };

        var volumes = new ScriptedBrokerVolumes
        {
            QueryJournalCursor = queryCursor,
            ScanDrive = scanDrive
        };

        await using var singleHandle = BrokerTestHarness.StartInProcess(volumes);

        if (recordingCase is ScanRecordingCase.SynchronousSourceFailure or ScanRecordingCase.CursorQueryFailure)
        {
            var expectedMessage = recordingCase == ScanRecordingCase.SynchronousSourceFailure
                ? "scripted scan failure"
                : "scripted cursor failure";
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                singleHandle.Process.ScanDriveAsync('c', target, options, null, CancellationToken.None)
                    .WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.Contains(expectedMessage, failure.Message);

            var recorded = singleHandle.Scans.Single();
            Assert.AreEqual('C', recorded.Drive);
            Assert.AreSequenceEqual([".git"], [.. recorded.RetainedFileNames!]);
            return;
        }

        var result = await singleHandle.Process.ScanDriveAsync('c', target, options,
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(new DriveKey('C'), result.Drive);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.IsNull(result.CatchUpLoss);
        Assert.IsGreaterThan(0u, result.Block.Block.Header.RowCount);
        var scan = singleHandle.Scans.Single();
        Assert.AreEqual('C', scan.Drive);
        Assert.AreSequenceEqual([".git"], [.. scan.RetainedFileNames!]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow([])]
    public async Task ScriptedScan_RecordsNullVersusEmptyRetention(string[]? names)
    {
        await using var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => ArmedSynthetic,
            ScanDrive = _ => [[SyntheticRecord(5, ".", 3)]]
        });
        await handle.Process.ScanDriveAsync('C', TestBlockSections.Target() with { RetainedFileNames = names },
            new MftScanOptions(), null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        var scan = handle.Scans.Single();
        if (names is null)
        {
            Assert.IsNull(scan.RetainedFileNames);
        }
        else
        {
            Assert.IsNotNull(scan.RetainedFileNames);
            Assert.IsEmpty(scan.RetainedFileNames);
        }
    }

    [TestMethod]
    public async Task ScriptedScan_ReportParsedReachesTheClientAsAParsingProgressFrame()
    {
        // The host throttles progress, so the scan waits until the client has seen the parse frame; the first
        // frame the host emits is never throttled, and the parse report is the first it is given.
        var parsedSeen = new TaskCompletionSource<BlockWriteProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<(DriveKey Drive, int ParseThreads, bool Cancelled)>();
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes(scan =>
        {
            seen.Add((scan.Drive, scan.ParseThreads.Count, scan.CancellationToken.IsCancellationRequested));
            scan.ReportParsed(2, 2);
            parsedSeen.Task.Wait(HangGuard, TestContext.CancellationToken);
            return [[SyntheticRecord(5, ".", 3)], [SyntheticRecord(20, "file.txt")]];
        }));
        var progressSink = new SynchronousProgress<BlockWriteProgress>(report =>
        {
            if (report.Phase == BlockScanPhase.Parsing)
            {
                parsedSeen.TrySetResult(report);
            }
        });

        await handle.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(), progressSink, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(1, seen);
        Assert.AreEqual('C', seen[0].Drive);
        Assert.IsGreaterThanOrEqualTo(1, seen[0].ParseThreads);
        Assert.IsFalse(seen[0].Cancelled);
        Assert.IsTrue(parsedSeen.Task.IsCompletedSuccessfully);
        Assert.AreEqual(2L, (await parsedSeen.Task).TotalRecords);
    }

    sealed class CountingOperationReporter : ISourceOperationReporter
    {
        public int ProcessingSteps { get; private set; }

        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName) => ProcessingSteps++;
    }

    [TestMethod]
    [DataRow(true, DisplayName = "operation reporter marks processing")]
    [DataRow(false, DisplayName = "#598-scripted-scan-without-operation-reporter")]
    public void ScriptedScan_ReportParsedWithoutAProgressSinkStillMarksProcessing(bool hasReporter)
    {
        var operation = hasReporter ? new CountingOperationReporter() : null;
        var scan = new ScriptedScan('C', new ParseThreadAllowance(1), new BlockWriteReporting(null, operation),
            CancellationToken.None);

        scan.ReportParsed(1, null);

        Assert.AreEqual(hasReporter ? 1 : 0, operation?.ProcessingSteps ?? 0);
    }

    [TestMethod]
    public void TestBlockSections_ResolveAnUnknownSectionNameToNull()
    {
        using var sections = new TestBlockSections();

        Assert.IsNull(sections.Resolve("section-unknown"));
    }

    [TestMethod]
    public async Task ScriptedVolumes_ReadJournalAndGrowAreServed()
    {
        var readCursors = new System.Collections.Concurrent.ConcurrentQueue<SyntheticJournalCursor>();
        var advanced = new SyntheticJournalCursor(7, 1500);
        var volumes = ScriptedVolumes(_ => [[SyntheticRecord(5, ".", 3)]]) with
        {
            ReadJournal = (_, since, _) =>
            {
                readCursors.Enqueue(since);
                return since == advanced ? ([], since) : ([], advanced);
            },
            GrowUsnJournal = (_, target) => target
        };
        await using var handle = BrokerTestHarness.StartInProcess(volumes);

        var scan = await handle.Process.ScanDriveAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        var settings = await handle.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSequenceEqual([ArmedSynthetic, advanced], [.. readCursors]);
        Assert.AreEqual(Armed, scan.ArmedCursor);
        Assert.IsNull(scan.CatchUpLoss);
        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
    }

    [TestMethod]
    public async Task ScriptedVolumes_WithoutGrowRefuseTheRequest()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            handle.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    static async IAsyncEnumerable<(IReadOnlyList<SyntheticJournalRecord> Entries, SyntheticJournalCursor Cursor)> OneBatchThenEnd(
        SyntheticJournalRecord[] entries, SyntheticJournalCursor cursor, TaskCompletionSource ended)
    {
        yield return (entries, cursor);
        await Task.Yield();
        ended.SetResult();
    }

    [TestMethod]
    public async Task ScriptedVolumes_WatchDriveStreamsItsBatchesToTheClient()
    {
        var entry = new SyntheticJournalRecord
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            Usn = 1200,
            FileName = "file.txt"
        };
        var cursor = new SyntheticJournalCursor(7, 1500);
        var volumes = ScriptedVolumes() with
        {
            WatchDrive = (_, _, cancellationToken) => ScriptedJournalStreams.OneBatchThenQuiet([entry], cursor, cancellationToken)
        };
        await using var handle = BrokerTestHarness.StartInProcess(volumes);
        using var deadline = new CancellationTokenSource(HangGuard);

        await using var channel = await handle.Process.OpenWatchChannelAsync(new IndexWatchTarget('C', 7, 1000),
            deadline.Token);
        var reader = channel.ReadAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        await using var readerLifetime = reader;
        JournalBatch? batch = null;
        while (batch is null && await reader.MoveNextAsync())
        {
            batch = reader.Current as JournalBatch;
        }

        Assert.IsNotNull(batch);
        Assert.HasCount(1, batch.Entries);
        Assert.AreEqual(cursor.NextUsn, batch.NextUsn);
    }

    [TestMethod]
    public async Task ScriptedVolumes_WatchDriveThatEndsAfterItsBatchIsEnumeratedToTheEnd()
    {
        var entry = new SyntheticJournalRecord
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            Usn = 1200,
            FileName = "file.txt"
        };
        var cursor = new SyntheticJournalCursor(7, 1500);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumes = ScriptedVolumes() with
        {
            WatchDrive = (_, _, _) => OneBatchThenEnd([entry], cursor, ended)
        };
        await using var handle = BrokerTestHarness.StartInProcess(volumes);
        using var deadline = new CancellationTokenSource(HangGuard);

        await using var channel = await handle.Process.OpenWatchChannelAsync(new IndexWatchTarget('C', 7, 1000),
            deadline.Token);

        await ended.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task ScriptedVolumes_WithoutWatchDriveRefuseTheWatch()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());
        using var deadline = new CancellationTokenSource(HangGuard);

        await using var channel = await handle.Process.OpenWatchChannelAsync(new IndexWatchTarget('C', 7, 1000),
            deadline.Token);
        var reader = channel.ReadAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        await using var readerLifetime = reader;

        var failure = await Assert.ThrowsExactlyAsync<DriveWatchFaultException>(async () =>
            await reader.MoveNextAsync());
        Assert.Contains("no watch source", failure.Message);
    }
}
