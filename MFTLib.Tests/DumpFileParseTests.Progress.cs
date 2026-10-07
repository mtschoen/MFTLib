using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A dump parse has the same progress, thread allowance and cancellation behaviour as a volume
// scan: both parse through the one native core. These cases run the real native parser over the
// 1000-record synthetic image, read in 256-record chunks so several chunks run.
public partial class DumpFileParseTests
{
    [TestMethod]
    public void Parse_Progress_ReportsEveryChunkAndEndsAtTheTotal()
    {
        var reports = new List<MftScanProgress>();

        using var result = StreamFile(new SynchronousProgress<MftScanProgress>(reports.Add));

        Assert.IsTrue(reports.Count > 1, "A 1000-record image read 256 records at a time reports more than once.");
        Assert.IsTrue(reports.All(report => report.TotalRecords == 1000));
        Assert.AreEqual(1000L, reports[^1].RecordsScanned);
        CollectionAssert.AreEqual(reports.Select(report => report.RecordsScanned).OrderBy(scanned => scanned).ToArray(),
            reports.Select(report => report.RecordsScanned).ToArray(), "Records scanned never goes backwards.");
    }


    [TestMethod]
    public void Parse_ThrowingProgress_DoesNotAbortTheParse()
    {
        using var result = StreamFile(new SynchronousProgress<MftScanProgress>(
            _ => throw new InvalidOperationException("consumer failure")));

        Assert.AreEqual(1000UL, result.TotalRecords);
    }

    [TestMethod]
    public void Parse_TokenCancelledBeforeTheParse_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // The assertion runs the lambda synchronously.
        // ReSharper disable once AccessToDisposedClosure
        var exception = Assert.ThrowsException<OperationCanceledException>(
            () => StreamFile(cancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public void Parse_TokenCancelledFromProgress_StopsAfterThatChunk()
    {
        var uncancelledReports = 0;
        using (StreamFile(new SynchronousProgress<MftScanProgress>(_ => uncancelledReports++)))
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
        Assert.ThrowsException<OperationCanceledException>(
            () => StreamFile(progress, cancellationToken: cancellation.Token));

        Assert.AreEqual(1, reports, "The chunk after the cancelling report is never reported.");
        Assert.IsTrue(uncancelledReports > reports);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Parse_Allowance_LimitsEveryChunk(int allowedThreads)
    {
        var allowance = new ParseThreadAllowance(allowedThreads);

        using (StreamFile(parseThreads: allowance))
        {
        }

        var chunkThreadCounts = ParseControlBlock.ChunkThreadCounts();
        Assert.IsTrue(chunkThreadCounts.Length > 1);
        Assert.IsTrue(chunkThreadCounts.All(count => count <= (uint)allowedThreads),
            "No chunk runs more threads than the allowance.");
    }

    [TestMethod]
    public void Parse_Allowance_DetachesWhenTheParseReturns()
    {
        var allowance = new ParseThreadAllowance(2);

        using (StreamFile(parseThreads: allowance))
        {
        }

        using (StreamFile(parseThreads: allowance))
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

            innerFailure = Assert.ThrowsException<InvalidOperationException>(() =>
            {
                using var unexpected = StreamFile(parseThreads: allowance);
            });
        });

        using (StreamFile(progress, allowance))
        {
        }

        Assert.IsNotNull(innerFailure, "A second parse cannot share an allowance that is still attached.");
    }



    [TestMethod]
    public void Parse_ReturnsEveryAllocatedRecordOfTheFile()
    {
        using var result = StreamFile();

        Assert.AreEqual(1000UL, result.TotalRecords);
        Assert.AreEqual(result.UsedRecords, (ulong)result.ToArray().Length);
    }
}
