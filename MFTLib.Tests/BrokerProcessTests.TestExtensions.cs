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

    static ScriptedBrokerVolumes ScriptedVolumes(
        Func<ScriptedScan, IEnumerable<IReadOnlyList<MftRecord>>>? scanDrive = null) => new()
        {
            QueryJournalCursor = _ => Armed,
            ScanDrive = scanDrive
        };

    public enum HandleDisposalEdge
    {
        Sequential,
        OverlappingHandle,
        ProcessFirst
    }

    static IEnumerable<IReadOnlyList<MftRecord>> BatchesWithHeldCleanup(TestGate scanStarted,
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

    [DataTestMethod]
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
                var volume = await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);
                Assert.AreEqual(256 * 1024, volume.MftValidDataLength);
                Assert.AreEqual(1024u, volume.BytesPerFileRecordSegment);
                Assert.AreEqual(256, volume.MftRecordCount);
                await handle.DisposeAsync().AsTask().WaitAsync(HangGuard);
                await process.Ended.WaitAsync(HangGuard);
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
            var scanTask = activeProcess.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
                CancellationToken.None);
            await scanStarted.Entered.WaitAsync(HangGuard);

            var first = edge == HandleDisposalEdge.ProcessFirst
                ? activeProcess.DisposeAsync().AsTask()
                : handleWithScan.DisposeAsync().AsTask();
            await cleanupGate.Entered.WaitAsync(HangGuard);

            var second = handleWithScan.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);

            cleanupGate.Release();
            await first.WaitAsync(HangGuard);
            await second.WaitAsync(HangGuard);
            await activeProcess.Ended.WaitAsync(HangGuard);
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scanTask);
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

        var volume = await handle.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
    }

    [TestMethod]
    public void ScriptedVolumes_RejectNullVolumesBeforeStarting()
    {
        var failure = Assert.ThrowsException<ArgumentNullException>(() => BrokerTestHarness.StartInProcess(null!));

        Assert.AreEqual("volumes", failure.ParamName);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostStartupFailure_EndsProcessAndDisposalDoesNotThrow(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException("host cancelled")
            : new InvalidOperationException("host unavailable");
        using var sections = new TestBlockSections();
        using var writer = new RecordingBlockSectionWriter(sections.Resolve);
        var handle = BrokerTestHarness.Start(
            CreateHost(timeProvider: new UnavailableHostClock(failure)), writer, sections.Create,
            new BrokerTestHarnessOptions(), null, null);
        var process = handle.Process;
        try
        {
            Assert.AreEqual(0, handle.Scans.Count, "A handle MFTLib's own tests started has no scan log.");
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
                process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
            await process.DisposeAsync().AsTask().WaitAsync(HangGuard);
            await process.Ended.WaitAsync(HangGuard);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    static BlockScanTarget ScanTarget() =>
        new(Path.Combine(Path.GetTempPath(), "control-only-" + Guid.NewGuid().ToString("N") + ".mlix"), 1, true);

    [TestMethod]
    public async Task Crash_EndsTheClientAsABrokerDeathDoes()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());
        var process = handle.Process;
        await process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard);

        handle.Crash();
        handle.Crash();

        var reason = await process.Ended.WaitAsync(HangGuard);
        Assert.AreNotEqual("The broker process was disposed.", reason);
        Assert.IsTrue(process.Ended.IsCompleted);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task ScriptedVolumes_WithoutScanDriveRefuseAScanWithAClearError()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            handle.Process.ScanDriveAsync('C', ScanTarget(), new BrokerScanOptions(), CancellationToken.None)
                .WaitAsync(HangGuard));

        StringAssert.Contains(failure.Message, "no scan source");
        Assert.AreEqual(0, handle.Scans.Count);
    }

    public enum ScanRecordingCase
    {
        Success,
        SynchronousSourceFailure,
        CursorQueryFailure,
        FirstCallbackHeld
    }

    [DataTestMethod]
    [DataRow(ScanRecordingCase.Success)]
    [DataRow(ScanRecordingCase.SynchronousSourceFailure)]
    [DataRow(ScanRecordingCase.CursorQueryFailure)]
    [DataRow(ScanRecordingCase.FirstCallbackHeld)]
    public async Task ScriptedScan_WritesTheBatchesThroughTheRealWriterAndRecordsTheRequest(ScanRecordingCase recordingCase)
    {
        var options = new BrokerScanOptions
        {
            Profile = BrokerScanProfile.DirectoryIndex,
            KeepFileNames = [".git"]
        };

        if (recordingCase == ScanRecordingCase.FirstCallbackHeld)
        {
            var firstCallbackEntered = new TestGate();
            var firstCallbackRelease = new TestGate();
            await using var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
            {
                QueryJournalCursor = _ => Armed,
                ScanDrive = scan =>
                {
                    if (scan.DriveLetter == "C")
                    {
                        firstCallbackEntered.MarkEntered();
                        firstCallbackRelease.WaitForRelease();
                    }

                    return [[Record(5, ".", 3)]];
                }
            });

            var scanC = handle.Process.ScanDriveAsync('c', TestBlockSections.Target(), options, CancellationToken.None);
            await firstCallbackEntered.Entered.WaitAsync(HangGuard);

            var resultD = await handle.Process.ScanDriveAsync('d', TestBlockSections.Target(), options, CancellationToken.None).WaitAsync(HangGuard);
            Assert.AreEqual('D', resultD.DriveLetter);

            Assert.AreEqual(2, handle.Scans.Count);
            Assert.AreEqual("C", handle.Scans[0].DriveLetter);
            Assert.AreEqual("D", handle.Scans[1].DriveLetter);

            firstCallbackRelease.Release();
            var resultC = await scanC.WaitAsync(HangGuard);
            Assert.AreEqual('C', resultC.DriveLetter);
            Assert.AreEqual(2, handle.Scans.Count);
            Assert.AreEqual("C", handle.Scans[0].DriveLetter);
            Assert.AreEqual("D", handle.Scans[1].DriveLetter);
            return;
        }

        Func<string, UsnJournalCursor> queryCursor = recordingCase == ScanRecordingCase.CursorQueryFailure
            ? _ => throw new InvalidOperationException("scripted cursor failure")
            : _ => Armed;

        Func<ScriptedScan, IEnumerable<IReadOnlyList<MftRecord>>> scanDrive = recordingCase switch
        {
            ScanRecordingCase.SynchronousSourceFailure => _ => throw new InvalidOperationException("scripted scan failure"),
            _ => _ => [[Record(5, ".", 3)], [Record(20, "file.txt")]]
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
            var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                singleHandle.Process.ScanDriveAsync('c', TestBlockSections.Target(), options, CancellationToken.None)
                    .WaitAsync(HangGuard));
            StringAssert.Contains(failure.Message, expectedMessage);

            var recorded = singleHandle.Scans.Single();
            Assert.AreEqual("C", recorded.DriveLetter);
            Assert.AreEqual(BrokerScanProfile.DirectoryIndex, recorded.Profile);
            CollectionAssert.AreEqual(new[] { ".git" }, recorded.KeepFileNames!.ToArray());
            return;
        }

        var result = await singleHandle.Process.ScanDriveAsync('c', TestBlockSections.Target(), options,
            CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual('C', result.DriveLetter);
        Assert.AreEqual(Armed, result.ArmedCursor);
        Assert.AreEqual(Armed, result.AdvancedCursor, "A null ReadJournal answers nothing new from every cursor.");
        Assert.IsTrue(result.Block.Block.Header.RowCount > 0);
        var scan = singleHandle.Scans.Single();
        Assert.AreEqual("C", scan.DriveLetter);
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, scan.Profile);
        CollectionAssert.AreEqual(new[] { ".git" }, scan.KeepFileNames!.ToArray());
    }

    [TestMethod]
    public async Task ScriptedScan_ReportParsedReachesTheClientAsAParsingProgressFrame()
    {
        // The host throttles progress, so the scan waits until the client has seen the parse frame; the first
        // frame the host emits is never throttled, and the parse report is the first it is given.
        var parsedSeen = new TaskCompletionSource<BrokerScanProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<(string DriveLetter, int ParseThreads, bool Cancelled)>();
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes(scan =>
        {
            seen.Add((scan.DriveLetter, scan.ParseThreads.Count, scan.CancellationToken.IsCancellationRequested));
            scan.ReportParsed(2, 2);
            parsedSeen.Task.Wait(HangGuard);
            return [[Record(5, ".", 3)], [Record(20, "file.txt")]];
        }));
        var options = new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(report =>
            {
                if (report.Phase == BrokerScanPhase.Parsing)
                {
                    parsedSeen.TrySetResult(report);
                }
            })
        };

        await handle.Process.ScanDriveAsync('C', TestBlockSections.Target(), options, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(1, seen.Count);
        Assert.AreEqual("C", seen[0].DriveLetter);
        Assert.IsTrue(seen[0].ParseThreads >= 1);
        Assert.IsFalse(seen[0].Cancelled);
        Assert.IsTrue(parsedSeen.Task.IsCompletedSuccessfully);
        Assert.AreEqual(2, (await parsedSeen.Task).TotalRecords);
    }

    sealed class CountingOperationReporter : IBrokerOperationReporter
    {
        public int ProcessingSteps { get; private set; }

        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName) => ProcessingSteps++;
    }

    [TestMethod]
    public void ScriptedScan_ReportParsedWithoutAProgressSinkStillMarksProcessing()
    {
        var operation = new CountingOperationReporter();
        var scan = new ScriptedScan("C", new ParseThreadAllowance(1), operation, null, CancellationToken.None);

        scan.ReportParsed(1, null);

        Assert.AreEqual(1, operation.ProcessingSteps);
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
        var advanced = new UsnJournalCursor(7, 1500);
        var volumes = ScriptedVolumes(_ => [[Record(5, ".", 3)]]) with
        {
            ReadJournal = (_, since, _) => since == advanced ? ([], since) : ([], advanced),
            GrowUsnJournal = (_, maximumSize, allocationDelta) => new UsnJournalSettings
            {
                MaximumSize = maximumSize,
                AllocationDelta = allocationDelta
            }
        };
        await using var handle = BrokerTestHarness.StartInProcess(volumes);

        var scan = await handle.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None).WaitAsync(HangGuard);
        var settings = await handle.Process.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(advanced, scan.AdvancedCursor);
        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
    }

    [TestMethod]
    public async Task ScriptedVolumes_WithoutGrowRefuseTheRequest()
    {
        await using var handle = BrokerTestHarness.StartInProcess(ScriptedVolumes());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            handle.Process.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None)
                .WaitAsync(HangGuard));
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> OneBatchThenQuiet(
        UsnJournalEntry[] entries, UsnJournalCursor cursor, [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return (entries, cursor);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [TestMethod]
    public async Task ScriptedVolumes_WatchDriveStreamsItsBatchesToTheClient()
    {
        var entry = SyntheticJournalEntry.Create(new SyntheticJournalEntryOptions
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            Usn = 1200,
            FileName = "file.txt"
        });
        var cursor = new UsnJournalCursor(7, 1500);
        var volumes = ScriptedVolumes() with
        {
            WatchDrive = (_, _, cancellationToken) => OneBatchThenQuiet([entry], cursor, cancellationToken)
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
        Assert.AreEqual(1, batch.Entries.Count);
        Assert.AreEqual(cursor.NextUsn, batch.NextUsn);
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

        var failure = await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(async () =>
            await reader.MoveNextAsync());
        StringAssert.Contains(failure.Message, "no watch source");
    }
}
