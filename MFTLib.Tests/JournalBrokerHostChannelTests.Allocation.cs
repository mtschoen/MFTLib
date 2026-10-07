using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostChannelTests
{
    public enum ScanEnding
    {
        Completes,
        Cancelled,
        SourceThrows
    }

    [TestMethod]
    public async Task ParseThreadAllocator_TwoScans_DivideProcessors()
    {
        var two = new ParseThreadAllocator(8);
        using var first = await two.AdmitAsync(CancellationToken.None);
        using var second = await two.AdmitAsync(CancellationToken.None);
        Assert.AreEqual(4, first.Allowance.Count);
        Assert.AreEqual(4, second.Allowance.Count);

        var three = new ParseThreadAllocator(8);
        using var a = await three.AdmitAsync(CancellationToken.None);
        using var b = await three.AdmitAsync(CancellationToken.None);
        using var c = await three.AdmitAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 3, 3, 2 },
            new[] { a.Allowance.Count, b.Allowance.Count, c.Allowance.Count },
            "The remainder goes to the earliest admissions.");
    }

    [TestMethod]
    public async Task ParseThreadAllocator_ScanStarts_RunningScanIsReducedWithoutItsCooperation()
    {
        var scans = new HeldScans();
        var host = CreateHost(processorCount: 4, scanDrive: scans.Source);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipeC = await harness.OpenScanChannelAsync('C');
        await scans["C"].Gate.Entered.WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(4, scans["C"].Allowance!.Count);

        var pipeD = await harness.OpenScanChannelAsync('D');
        await scans["D"].Gate.Entered.WaitAsync(HostChannelHarness.HangGuard);

        Assert.AreEqual(2, scans["C"].Allowance!.Count, "The running scan's share shrinks while it does nothing.");
        Assert.AreEqual(2, scans["D"].Allowance!.Count);
        scans.ReleaseAll();
        await HostChannelHarness.ReadToEndAsync(pipeC);
        await HostChannelHarness.ReadToEndAsync(pipeD);
    }

    [DataTestMethod]
    [DataRow(ScanEnding.Completes)]
    [DataRow(ScanEnding.Cancelled)]
    [DataRow(ScanEnding.SourceThrows)]
    public async Task ParseThreadAllocator_ScanEndsCancelsOrFails_RemainingScansAreRaised(ScanEnding ending)
    {
        var scans = new HeldScans();
        var host = CreateHost(processorCount: 4, scanDrive: scans.Source);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipeC = await harness.OpenScanChannelAsync('C');
        var pipeD = await harness.OpenScanChannelAsync('D');
        await Task.WhenAll(scans["C"].Gate.Entered, scans["D"].Gate.Entered).WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(2, scans["D"].Allowance!.Count);

        switch (ending)
        {
            case ScanEnding.Completes:
                scans["C"].Gate.Release();
                Assert.AreEqual(BrokerFrameKind.ScanCompleted, (await HostChannelHarness.ReadToEndAsync(pipeC))[^1].Kind);
                break;
            case ScanEnding.Cancelled:
                await pipeC.DisposeAsync();
                await scans["C"].Returned.WaitAsync(HostChannelHarness.HangGuard);
                break;
            case ScanEnding.SourceThrows:
                scans["C"].Fail = true;
                scans["C"].Gate.Release();
                Assert.AreEqual(BrokerFrameKind.Error, (await HostChannelHarness.ReadToEndAsync(pipeC))[^1].Kind);
                break;
        }

        await WaitUntilAsync(() => scans["D"].Allowance!.Count == 4);
        scans["D"].Gate.Release();
        await HostChannelHarness.ReadToEndAsync(pipeD);
    }

    [TestMethod]
    public async Task ParseThreadAllocator_NeverOversubscribedAtRest()
    {
        for (var processorCount = 1; processorCount <= 8; processorCount++)
        {
            for (var scanCount = 1; scanCount <= 12; scanCount++)
            {
                var allocator = new ParseThreadAllocator(processorCount);
                var outstanding = new List<Task<ParseThreadRegistration>>();
                for (var scan = 0; scan < scanCount; scan++)
                {
                    outstanding.Add(allocator.AdmitAsync(CancellationToken.None).AsTask());
                    await AssertAtRestAsync(allocator, processorCount, outstanding);
                }

                // A fixed disposal order: alternately the oldest and the newest running scan.
                for (var step = 0; step < scanCount; step++)
                {
                    var running = await RunningAsync(outstanding, processorCount);
                    var victim = step % 2 == 0 ? 0 : running.Count - 1;
                    outstanding.RemoveAt(victim);
                    running[victim].Dispose();
                    await AssertAtRestAsync(allocator, processorCount, outstanding);
                }

                Assert.AreEqual(0, allocator.RunningScanCount);
            }
        }
    }

    [TestMethod]
    public async Task ParseThreadAllocator_MoreScansThanProcessors_ExtrasQueueInArrivalOrder()
    {
        var allocator = new ParseThreadAllocator(2);
        var first = await allocator.AdmitAsync(CancellationToken.None);
        var second = await allocator.AdmitAsync(CancellationToken.None);
        var third = allocator.AdmitAsync(CancellationToken.None).AsTask();
        var fourth = allocator.AdmitAsync(CancellationToken.None).AsTask();

        Assert.IsFalse(third.IsCompleted);
        Assert.AreEqual(2, allocator.QueuedScanCount);
        Assert.AreEqual(1, first.Allowance.Count);
        Assert.AreEqual(1, second.Allowance.Count);

        first.Dispose();
        using var admittedThird = await third.WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(1, admittedThird.Allowance.Count);
        Assert.IsFalse(fourth.IsCompleted, "The fourth scan arrived after the third and still waits.");

        second.Dispose();
        using var admittedFourth = await fourth.WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(1, admittedFourth.Allowance.Count);
    }

    [TestMethod]
    public async Task QueuedScan_PipeClosed_LeavesQueueAndSourceNeverRuns()
    {
        var scans = new HeldScans();
        var host = CreateHost(processorCount: 1, scanDrive: scans.Source);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipeC = await harness.OpenScanChannelAsync('C');
        await scans["C"].Gate.Entered.WaitAsync(HostChannelHarness.HangGuard);

        var pipeD = await harness.OpenScanChannelAsync('D');
        await WaitUntilAsync(() => host.ParseThreads.QueuedScanCount == 1);
        await pipeD.DisposeAsync();
        await WaitUntilAsync(() => host.ParseThreads.QueuedScanCount == 0);

        scans["C"].Gate.Release();
        await HostChannelHarness.ReadToEndAsync(pipeC);
        scans["E"].Gate.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('E'));

        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
        Assert.AreEqual(0, scans.InvocationCount("D"), "A scan that left the queue never reaches its source.");
    }

    [TestMethod]
    public async Task ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns()
    {
        var scans = new HeldScans();
        scans["C"].HonorCancellation = false;
        var host = CreateHost(processorCount: 2, scanDrive: scans.Source);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipeC = await harness.OpenScanChannelAsync('C');
        var pipeD = await harness.OpenScanChannelAsync('D');
        await Task.WhenAll(scans["C"].Gate.Entered, scans["D"].Gate.Entered).WaitAsync(HostChannelHarness.HangGuard);
        var pipeE = await harness.OpenScanChannelAsync('E');
        await WaitUntilAsync(() => host.ParseThreads.QueuedScanCount == 1);

        await pipeC.DisposeAsync();
        await WaitUntilAsync(() => scans["C"].Token.IsCancellationRequested);

        Assert.AreEqual(1, scans["D"].Allowance!.Count, "The closed scan's share is still in use by its source.");
        Assert.AreEqual(1, host.ParseThreads.QueuedScanCount);
        Assert.IsFalse(scans["E"].Gate.Entered.IsCompleted);

        scans["C"].Gate.Release();
        await scans["E"].Gate.Entered.WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(1, scans["E"].Allowance!.Count);
        Assert.AreEqual(1, scans["D"].Allowance!.Count);
        scans.ReleaseAll();
        await HostChannelHarness.ReadToEndAsync(pipeD);
        await HostChannelHarness.ReadToEndAsync(pipeE);
    }

    [TestMethod]
    public async Task ScanChannel_PipeClosed_CancelsOnlyThatScan()
    {
        var scans = new HeldScans();
        var host = CreateHost(processorCount: 4, scanDrive: scans.Source);
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipeC = await harness.OpenScanChannelAsync('C');
        var pipeD = await harness.OpenScanChannelAsync('D');
        await Task.WhenAll(scans["C"].Gate.Entered, scans["D"].Gate.Entered).WaitAsync(HostChannelHarness.HangGuard);

        await pipeC.DisposeAsync();
        await scans["C"].Returned.WaitAsync(HostChannelHarness.HangGuard);
        scans["D"].Gate.Release();
        var frames = await HostChannelHarness.ReadToEndAsync(pipeD);

        Assert.IsFalse(scans["D"].Token.IsCancellationRequested);
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
    }

    static async Task AssertAtRestAsync(ParseThreadAllocator allocator, int processorCount,
        List<Task<ParseThreadRegistration>> outstanding)
    {
        var running = await RunningAsync(outstanding, processorCount);
        Assert.AreEqual(running.Count, allocator.RunningScanCount);
        Assert.IsTrue(running.Count <= processorCount);
        Assert.IsTrue(running.All(registration => registration.Allowance.Count >= 1));
        if (running.Count > 0)
        {
            Assert.AreEqual(processorCount, running.Sum(registration => registration.Allowance.Count));
        }
    }

    // The admissions that must be running: the earliest min(processorCount, outstanding) in arrival
    // order. Awaiting them proves each one was admitted; the rest must still wait.
    static async Task<List<ParseThreadRegistration>> RunningAsync(List<Task<ParseThreadRegistration>> outstanding,
        int processorCount)
    {
        var expected = Math.Min(processorCount, outstanding.Count);
        var running = new List<ParseThreadRegistration>();
        foreach (var admission in outstanding.Take(expected))
        {
            running.Add(await admission.WaitAsync(HostChannelHarness.HangGuard));
        }

        Assert.IsTrue(outstanding.Skip(expected).All(admission => !admission.IsCompleted));
        return running;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(HostChannelHarness.HangGuard);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token);
        }
    }

    // One held scan source per drive: records its allowance and token, reports entry, and parks
    // until released. A source that honors cancellation stops when its pipe closes; one that does
    // not keeps the native-work stand-in running until released.
    sealed class HeldScans
    {
        readonly Dictionary<string, HeldScan> _scans = [];
        readonly Dictionary<string, int> _invocations = [];

        public HeldScan this[string drive]
        {
            get
            {
                lock (_scans)
                {
                    return _scans.TryGetValue(drive, out var scan) ? scan : _scans[drive] = new HeldScan();
                }
            }
        }

        public int InvocationCount(string drive)
        {
            lock (_scans)
            {
                return _invocations.GetValueOrDefault(drive);
            }
        }

        public void ReleaseAll()
        {
            lock (_scans)
            {
                foreach (var scan in _scans.Values)
                {
                    scan.Gate.Release();
                }
            }
        }

        public IEnumerable<IReadOnlyList<MftRecord>> Source(string drive, ParseThreadAllowance parseThreads,
            IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress,
            MftRecordScanOptions scanOptions, CancellationToken cancellationToken)
        {
            lock (_scans)
            {
                _invocations[drive] = _invocations.GetValueOrDefault(drive) + 1;
            }

            return this[drive].Run(parseThreads, cancellationToken);
        }
    }

    sealed class HeldScan
    {
        readonly TaskCompletionSource _returned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestGate Gate { get; } = new();
        public bool HonorCancellation { get; set; } = true;
        public bool Fail { get; set; }
        public ParseThreadAllowance? Allowance { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task Returned => _returned.Task;

        public IEnumerable<IReadOnlyList<MftRecord>> Run(ParseThreadAllowance allowance, CancellationToken token)
        {
            Allowance = allowance;
            Token = token;
            Gate.MarkEntered();
            try
            {
                Gate.WaitForReleaseAsync(HonorCancellation ? token : CancellationToken.None)
                    .WaitAsync(HostChannelHarness.HangGuard, CancellationToken.None).GetAwaiter().GetResult();
                if (Fail)
                {
                    throw new IOException("scan source failed");
                }
            }
            finally
            {
                _returned.TrySetResult();
            }

            yield return [Record(5, ".", 3)];
        }
    }
}
