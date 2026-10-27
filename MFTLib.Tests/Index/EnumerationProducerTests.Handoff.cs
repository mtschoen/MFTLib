using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public sealed partial class EnumerationProducerTests
{
    const int HandoffReaders = 3;
    const int HandoffChildren = 3;
    const int HandoffGrandchildrenPerChild = 3;

    /// <summary>
    ///     A writer held in its progress callback must not idle the walk's permits or stall finished
    ///     readers: the two readers that finish meanwhile return their permits and both completed
    ///     directories wait in the hand-off. A later round that overfills the hand-off makes a
    ///     publisher wait, and the walk still completes, or cancels and drains every reader.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Produce_SlowWriter_ReturnsPermitsBuffersCompletionsAndWaitsWhenTheHandoffIsFull(bool cancel)
    {
        if (Environment.ProcessorCount < HandoffReaders)
        {
            Assert.Inconclusive($"Requires at least {HandoffReaders} processors for three concurrent readers.");
        }

        CreateHandoffTree(HandoffChildren, HandoffGrandchildrenPerChild);
        var firstCommit = new TestGate();
        var secondCommit = new TestGate();
        var committedChildren = 0;
        var childReadsExited = 0;
        var publishedChildren = 0;
        var firstRoundExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRoundPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = new EnumerationReadObservation();
        EnumerationWalkLimit.Hooks._directoryEntered = observation.Enter;
        EnumerationWalkLimit.Hooks._directoryExited = (drive, path) =>
        {
            observation.Exit(drive, path);
            if (path != _treeRoot && Interlocked.Increment(ref childReadsExited) == HandoffReaders)
            {
                firstRoundExited.TrySetResult();
            }
        };

        var progress = new SynchronousProgress<IndexScanProgress>(report =>
        {
            if (report.CurrentDirectory == _treeRoot)
            {
                return;
            }

            var gate = Interlocked.Increment(ref committedChildren) switch
            {
                1 => firstCommit,
                2 => secondCommit,
                _ => null
            };
            gate?.MarkEntered();
            gate?.WaitForRelease();
        });

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        })
        {
            HandoffWaiting = () => handoffWaiting.TrySetResult(),
            HandoffPublished = path =>
            {
                if (path != _treeRoot && Interlocked.Increment(ref publishedChildren) == HandoffReaders)
                {
                    firstRoundPublished.TrySetResult();
                }
            }
        };

        using var block = TestBlocks.Create(
            Path.Combine(CacheDirectoryPath, $"T-{Interlocked.Increment(ref _blockId):D8}.mlix"),
            slotCapacity: 4096, namePoolCapacity: 1048576, volumeSerial: 1);
        var created = (Block: block, Writer: new BlockWriter(block));
        using var cancellation = new CancellationTokenSource();
        Task? produceTask = null;
        try
        {
            using (await EnumerationWalkTestSupport.ReserveSlotsAsync(HandoffReaders, CancellationToken.None)
                       .WaitAsync(HangGuard, TestContext.CancellationToken))
            {
                produceTask = EnumerationWalkTestSupport.ProduceAsync(producer, created.Writer, progress,
                    cancellation.Token, maximumWorkers: HandoffReaders);

                // The writer holds the first child; the other two first-round readers finish meanwhile.
                await firstCommit.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                await firstRoundExited.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                await firstRoundPublished.Task.WaitAsync(HangGuard, TestContext.CancellationToken);

                // Both extra permits come back while the writer is still held, so no reader idles on one.
                // Reserving all but (readers - 1) takes exactly the walk's two extra permits.
                using var returnedPermits = new CancellationTokenSource(HangGuard);
                using (await EnumerationWalkTestSupport.ReserveSlotsAsync(
                           Environment.ProcessorCount - (HandoffReaders - 1), returnedPermits.Token))
                {
                    Assert.IsFalse(handoffWaiting.Task.IsCompleted,
                        "two completed directories must fit in the hand-off without waiting");
                }

                firstCommit.Release();
                await secondCommit.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                await handoffWaiting.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
                if (cancel)
                {
                    await cancellation.CancelAsync();
                }

                secondCommit.Release();
                if (cancel)
                {
                    var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                        () => produceTask.WaitAsync(HangGuard, TestContext.CancellationToken));
                    Assert.AreEqual(cancellation.Token, thrown.CancellationToken);
                }
                else
                {
                    await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                    const uint expectedRowCount = 1u + (2u * HandoffChildren) +
                                           (2u * HandoffChildren * HandoffGrandchildrenPerChild);
                    Assert.AreEqual(expectedRowCount, created.Writer.RowCount);
                    Assert.IsFalse(created.Writer.CapacityExhausted);
                }

                Assert.AreEqual(0, observation.Active);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            firstCommit.Release();
            secondCommit.Release();
            if (produceTask is not null)
            {
                try
                {
                    await produceTask.WaitAsync(HangGuard, TestContext.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected cancellation drain
                }
            }

            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }

        using var verifyReservationCts = new CancellationTokenSource(HangGuard);
        using var verifyAll = await EnumerationWalkTestSupport.ReserveSlotsAsync(0, verifyReservationCts.Token);
    }

    void CreateHandoffTree(int children, int grandchildrenPerChild)
    {
        _treeRoot = Path.Combine(_treeRoot, "drain");
        for (var child = 0; child < children; child++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(_treeRoot, $"child-{child}"));
            File.WriteAllText(Path.Combine(directory.FullName, "file.txt"), "content");
            for (var grandchild = 0; grandchild < grandchildrenPerChild; grandchild++)
            {
                var subDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, $"grandchild-{grandchild}"));
                File.WriteAllText(Path.Combine(subDirectory.FullName, "file.txt"), "content");
            }
        }
    }
}
