using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class MftDumpBlockProducerTests
{
    string _directory = null!;
    string _path = null!;

    public TestContext TestContext { get; set; } = null!;

    static ParseThreadAllocator Allocator => MftBlockProducerPipeline.SharedAllocator;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "input.mft");
        File.WriteAllBytes(_path, MftDumpFixture.Standard());
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    MftBlockProduceRequest Request() => new()
    {
        Drive = 'T',
        VolumeSerial = 0,
        BlockPath = Path.Combine(_directory, "block.mlix"),
        DeleteOnClose = true
    };

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Produce_NativeParseUsesSharedBudget(bool liveScanRunning)
    {
        if (liveScanRunning && Environment.ProcessorCount == 1)
        {
            Assert.Inconclusive("A concurrent admitted scan requires at least two processors.");
        }

        using var live = liveScanRunning
            ? await Allocator.AdmitAsync(TestContext.CancellationTokenSource.Token)
            : null;
        var result = await new MftDumpBlockProducer(_path, TimeProvider.System).ProduceAsync(Request(),
            TestContext.CancellationTokenSource.Token);
        using var block = result.Block;
        var counts = ParseControlBlock.ChunkThreadCounts();
        var expected = liveScanRunning ? Environment.ProcessorCount / 2 : Environment.ProcessorCount;

        Assert.IsTrue(counts.Length > 0, "The real native dump parser must have processed a chunk.");
        Assert.IsTrue(counts.All(count => count == expected),
            $"Expected every dump chunk to use {expected} threads; observed {string.Join(", ", counts)}.");
        Assert.AreEqual(liveScanRunning ? 1 : 0, Allocator.RunningScanCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Produce_CancelledScanReleasesRegistrationAndAdmitsWaiter(bool cancelled)
    {
        var registrations = new List<ParseThreadRegistration>();
        using var cancellation = new CancellationTokenSource();
        Action cancelScan = cancellation.Cancel;
        Task<ParseThreadRegistration>? waiting = null;
        var queuedDuringScan = -1;
        try
        {
            for (var position = 1; position < Environment.ProcessorCount; position++)
            {
                registrations.Add(await Allocator.AdmitAsync(TestContext.CancellationTokenSource.Token));
            }

            var producer = new MftDumpBlockProducer(_path, TimeProvider.System, (_, _, token) =>
            {
                waiting = Allocator.AdmitAsync(TestContext.CancellationTokenSource.Token).AsTask();
                queuedDuringScan = Allocator.QueuedScanCount;
                if (!cancelled)
                {
                    throw new IOException("dump source failed");
                }

                cancelScan();
                token.ThrowIfCancellationRequested();
                return [];
            });

            var request = Request();
            if (cancelled)
            {
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    producer.ProduceAsync(request, cancellation.Token));
            }
            else
            {
                var failure = await Assert.ThrowsExceptionAsync<IOException>(() =>
                    producer.ProduceAsync(request, cancellation.Token));
                Assert.AreEqual("dump source failed", failure.Message);
            }

            using var admitted = await waiting!.WaitAsync(TestContext.CancellationTokenSource.Token);
            Assert.AreEqual(1, queuedDuringScan, "The dump occupies the last running slot until cancellation.");
            Assert.AreEqual(Environment.ProcessorCount, Allocator.RunningScanCount);
            Assert.AreEqual(0, Allocator.QueuedScanCount);
            Assert.IsFalse(File.Exists(request.BlockPath));
        }
        finally
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }

        Assert.AreEqual(0, Allocator.RunningScanCount);
    }
}
