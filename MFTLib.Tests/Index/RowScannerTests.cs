using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class RowScannerTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder();
        var root = _builder.AddRoot();
        _builder.AddRow("alpha.txt", root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        _builder.AddRow("beta.txt", root, RowFlags.InUse, 2, Moment, sequenceNumber: 0);
        _builder.AddRow("gamma.txt", root, RowFlags.InUse, 3, Moment, sequenceNumber: 0);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block)]);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }

    [TestMethod]
    public void Scanner_VisitsEveryUsedRowInOrder()
    {
        var names = new List<string>();
        var scanner = new RowScanner(_snapshot, 0);
        while (scanner.MoveNext())
        {
            names.Add(new string(scanner.CurrentName));
        }

        CollectionAssert.AreEqual(new[] { "", "alpha.txt", "beta.txt", "gamma.txt" }, names);
    }

    [TestMethod]
    public void Scanner_StopsAtRowCountNotSlotCapacity()
    {
        var visited = 0;
        var scanner = new RowScanner(_snapshot, 0);
        while (scanner.MoveNext())
        {
            visited++;
        }

        Assert.AreEqual(4, visited);
    }

    [TestMethod]
    public void Scanner_ExposesTheRowByReference()
    {
        var scanner = new RowScanner(_snapshot, 0);
        Assert.IsTrue(scanner.MoveNext());
        Assert.IsTrue(scanner.MoveNext());

        Assert.AreEqual(1u, scanner.CurrentRowIndex);
        Assert.AreEqual(1L, scanner.Current.Size);
    }

    [TestMethod]
    public void RangeScanner_VisitsOnlyItsPartition()
    {
        var names = new List<string>();
        var scanner = new RowScanner(_snapshot, 0, startRow: 2, endRowExclusive: 4);
        while (scanner.MoveNext())
        {
            names.Add(new string(scanner.CurrentName));
        }

        CollectionAssert.AreEqual(new[] { "beta.txt", "gamma.txt" }, names);
    }

    [TestMethod]
    public void RangeScanner_ClampsAnEndPastRowCount()
    {
        var visited = 0;
        var scanner = new RowScanner(_snapshot, 0, startRow: 3, endRowExclusive: 9999);
        while (scanner.MoveNext())
        {
            visited++;
        }

        Assert.AreEqual(1, visited);
    }

    /// <summary>
    ///     A token already cancelled when the scan starts stops it before any row is read, so a
    ///     caller who cancelled before the query reached the rows pays for nothing.
    /// </summary>
    [TestMethod]
    public void Scanner_WithAnAlreadyCancelledToken_ThrowsOnTheFirstMoveNext()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
        {
            var scanner = new RowScanner(_snapshot, 0, token);
            while (scanner.MoveNext())
            {
                // The first MoveNext is expected to throw, so the body is never reached.
            }
        });
    }

    /// <summary>
    ///     A scanner whose range holds no rows still reads the token once. That is what makes the
    ///     per-block half of the contract true for an empty block: an engine that opens one
    ///     scanner per drive block observes cancellation for every block, not only the ones with
    ///     rows in them.
    /// </summary>
    [TestMethod]
    public void RangeScanner_WithAnEmptyRangeAndACancelledToken_StillObservesTheToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
        {
            var scanner = new RowScanner(_snapshot, 0, startRow: 0, endRowExclusive: 0, token);
            while (scanner.MoveNext())
            {
                // The range is empty, so the only thing that can end this loop is the token.
            }
        });
    }

    /// <summary>
    ///     Cancellation mid-scan is observed at the next checkpoint rather than at the end of the
    ///     block: the scanner checks the token on its first row and then every
    ///     <see cref="RowScanner.CancellationCheckIntervalRows" /> rows, which is what bounds how
    ///     long a cancelled query keeps reading a mapping its caller has finished with.
    /// </summary>
    [TestMethod]
    public async Task Scanner_WithATokenCancelledMidScan_StopsAtTheNextCheckpoint()
    {
        const uint rowCount = RowScanner.CancellationCheckIntervalRows + 512;
        using var builder = new SyntheticBlockBuilder('U', 0x0BADCAFE, slotCapacity: rowCount + 8,
            namePoolCapacity: (rowCount + 8) * 32);
        var root = builder.AddRoot();
        for (var index = 0u; index < rowCount; index++)
        {
            builder.AddRow($"row{index}", root, RowFlags.InUse, index, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        var snapshot = Snapshot.Create([new DriveBlock('U', 0, builder.OpenForReading(out _)!)]);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var visited = 0;
            OperationCanceledException? thrown = null;

            // Scanned inline rather than inside an assertion lambda: the loop body cancels the
            // token source, and a lambda that captures a source the enclosing scope disposes is
            // what the quality gate refuses.
            try
            {
                var scanner = new RowScanner(snapshot, 0, cancellation.Token);
                while (scanner.MoveNext())
                {
                    visited++;
                    if (visited == 10)
                    {
                        cancellation.Cancel();
                    }
                }
            }
            catch (OperationCanceledException exception)
            {
                thrown = exception;
            }

            Assert.IsNotNull(thrown, "the scan ran to the end of the block after its token was cancelled");
            Assert.AreEqual((int)RowScanner.CancellationCheckIntervalRows, visited,
                "the scan ran past the checkpoint that follows the row the token was cancelled on");
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Scanner_WorksInAForeachThroughGetEnumerator()
    {
        var visited = 0;
        foreach (var _ in new RowScanner(_snapshot, 0))
        {
            visited++;
        }

        Assert.AreEqual(4, visited);
    }
}
