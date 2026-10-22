using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A dump parse has the same progress, thread allowance and cancellation behaviour as a volume
// scan: both parse through the one native core. These cases run the real native parser over the
// 1000-record synthetic image, read in 256-record chunks so several chunks run.
public partial class DumpFileParseTests
{
    [TestMethod]
    [TestCategory("Smoke")]
    public void Parse_Progress_ReportsEveryChunkAndEndsAtTheTotal()
    {
        var reports = new List<MftScanProgress>();

        using var result = StreamFile(new SynchronousProgress<MftScanProgress>(reports.Add), cancellationToken: TestContext.CancellationToken);

        Assert.IsGreaterThan(1, reports.Count, "A 1000-record image read 256 records at a time reports more than once.");
        Assert.IsTrue(reports.All(report => report.TotalRecords == 1000));
        Assert.AreEqual(1000L, reports[^1].RecordsScanned);
        Assert.AreSequenceEqual([.. reports.Select(report => report.RecordsScanned).Order()], [.. reports.Select(report => report.RecordsScanned)], "Records scanned never goes backwards.");
    }

    [TestMethod]
    public void Parse_OneRecordChunks_ReportsStrictlyIncreasingProgressNeverPastTheTotal()
    {
        var reports = new List<MftScanProgress>();
        using var input = MftDumpInput.Open(_syntheticPath);

        using var result = input.Parse(new MftFileScanOptions(new SynchronousProgress<MftScanProgress>(reports.Add),
            BufferSizeRecords: 1));

        Assert.AreEqual(1000UL, result.TotalRecords);
        Assert.HasCount(1000, reports, "Every one-record chunk reports once.");
        long previous = 0;
        foreach (var report in reports)
        {
            Assert.IsGreaterThan(previous, report.RecordsScanned, $"{report.RecordsScanned} does not pass {previous}");
            Assert.IsLessThanOrEqualTo(report.TotalRecords, report.RecordsScanned);
            Assert.AreEqual(1000L, report.TotalRecords);
            previous = report.RecordsScanned;
        }

        Assert.AreEqual(1000L, previous);
    }


    [TestMethod]
    public void Parse_ThrowingProgress_DoesNotAbortTheParse()
    {
        using var result = StreamFile(new SynchronousProgress<MftScanProgress>(
            _ => throw new InvalidOperationException("consumer failure")), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(1000UL, result.TotalRecords);
    }

    [TestMethod]
    public void Parse_TokenCancelledBeforeTheParse_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // The assertion runs the lambda synchronously.
        // ReSharper disable once AccessToDisposedClosure
        var exception = Assert.ThrowsExactly<OperationCanceledException>(
            () => StreamFile(cancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public void Parse_TokenCancelledFromProgress_StopsAfterThatChunk()
    {
        var uncancelledReports = 0;
        using (StreamFile(new SynchronousProgress<MftScanProgress>(_ => uncancelledReports++), cancellationToken: TestContext.CancellationToken))
        {
        }

        using var cancellation = new CancellationTokenSource();
        var reports = 0;
        var progress = new SynchronousProgress<MftScanProgress>(_ =>
        {
            reports++;
            // ReSharper disable once AccessToDisposedClosure
            cancellation.Cancel();
        });

        // The assertion runs the lambda synchronously.
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsExactly<OperationCanceledException>(
            () => StreamFile(progress, cancellationToken: cancellation.Token));

        Assert.AreEqual(1, reports, "The chunk after the cancelling report is never reported.");
        Assert.IsGreaterThan(reports, uncancelledReports);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Parse_Allowance_LimitsEveryChunk(int allowedThreads)
    {
        var allowance = new ParseThreadAllowance(allowedThreads);

        using (StreamFile(parseThreads: allowance, cancellationToken: TestContext.CancellationToken))
        {
        }

        var chunkThreadCounts = ParseControlBlock.ChunkThreadCounts();
        Assert.IsGreaterThan(1, chunkThreadCounts.Length);
        Assert.IsTrue(chunkThreadCounts.All(count => count <= (uint)allowedThreads),
            "No chunk runs more threads than the allowance.");
    }

    [TestMethod]
    public void Parse_Allowance_DetachesWhenTheParseReturns()
    {
        var allowance = new ParseThreadAllowance(2);

        using (StreamFile(parseThreads: allowance, cancellationToken: TestContext.CancellationToken))
        {
        }

        using (StreamFile(parseThreads: allowance, cancellationToken: TestContext.CancellationToken))
        {
        }
    }

    [TestMethod]
    public void Parse_AllowanceAttachedToAnotherParse_ThrowsInvalidOperation()
    {
        var allowance = new ParseThreadAllowance(2);
        Exception? innerFailure = null;
        var progress = new SynchronousProgress<MftScanProgress>(_ =>
        {
            if (innerFailure != null)
            {
                return;
            }

            innerFailure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using var unexpected = StreamFile(parseThreads: allowance, cancellationToken: TestContext.CancellationToken);
            });
        });

        using (StreamFile(progress, allowance, TestContext.CancellationToken))
        {
        }

        Assert.IsNotNull(innerFailure, "A second parse cannot share an allowance that is still attached.");
    }



    [TestMethod]
    public void Parse_ReturnsEveryAllocatedRecordOfTheFile()
    {
        using var result = StreamFile(cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(1000UL, result.TotalRecords);
        Assert.AreEqual(result.UsedRecords, (ulong)result.ToRecords().Length);
    }

    public TestContext TestContext { get; set; } = null!;
}
