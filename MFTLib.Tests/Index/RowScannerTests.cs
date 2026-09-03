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
        _builder.AddRow("alpha.txt", root, RowFlags.InUse, 1, Moment);
        _builder.AddRow("beta.txt", root, RowFlags.InUse, 2, Moment);
        _builder.AddRow("gamma.txt", root, RowFlags.InUse, 3, Moment);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block, deleteFileOnRelease: false)]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
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
