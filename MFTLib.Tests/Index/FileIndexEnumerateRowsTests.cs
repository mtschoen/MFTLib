using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Row-view semantics of <see cref="FileIndex.EnumerateRows" />: row-view field parity with
///     <see cref="FileEntry" />, zero borrows without iteration, and enumerator reuse or copy disposal.
/// </summary>
[TestClass]
public sealed class FileIndexEnumerateRowsTests : IDisposable
{
    TwoDriveQueryFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new TwoDriveQueryFixture();

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public async Task EnumerateRows_RowViewAgreesWithItsEntry()
    {
        await using var index = await _fixture.OpenAsync();
        AssertRowViews(index);
    }

    static void AssertRowViews(FileIndex index)
    {
        var seen = 0;
        foreach (var row in index.EnumerateRows(new SearchQuery(null)))
        {
            var entry = row.ToEntry();
            Assert.AreEqual(entry.Name, row.Name.ToString());
            Assert.AreEqual(entry.RecordKey.Drive, row.Drive);
            Assert.AreEqual(entry.Size, row.Size);
            Assert.AreEqual(entry.IsSizeKnown, row.IsSizeKnown);
            Assert.AreEqual(entry.IsDirectory, row.IsDirectory);
            Assert.AreEqual(entry.IsDeleted, row.IsDeleted);
            seen++;
        }

        Assert.IsGreaterThan(0, seen, "The two-drive fixture has rows.");
    }

    [TestMethod]
    public async Task EnumerateRows_WithoutIteration_TakesNoBorrow()
    {
        await using var index = await _fixture.OpenAsync();
        CreateWithoutIteration(index);
        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }

    static void CreateWithoutIteration(FileIndex index)
    {
        index.EnumerateRows(new SearchQuery(null));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task EnumerateRows_AccessAfterSharedBorrowReturned_Throws(bool throughCopy, bool readCurrent)
    {
        await using var index = await _fixture.OpenAsync();
        AssertDisposedAccess(index, throughCopy, readCurrent);
    }

    static void AssertDisposedAccess(FileIndex index, bool throughCopy, bool readCurrent)
    {
        using var enumerator = index.EnumerateRows(new SearchQuery(null)).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        using var copy = enumerator;
        enumerator.Dispose();
        try
        {
            if (readCurrent)
            {
                _ = throughCopy ? copy.Current : enumerator.Current;
            }
            else
            {
                _ = throughCopy ? copy.MoveNext() : enumerator.MoveNext();
            }

            Assert.Fail("A returned shared borrow must reject access before reading mapped memory.");
        }
        catch (ObjectDisposedException exception)
        {
            Assert.AreEqual(nameof(IndexRowEnumerator), exception.ObjectName);
        }
    }

    [TestMethod]
    public async Task EnumerateRows_RepeatedAndCopiedDispose_ReturnsExactlyOneBorrow()
    {
        await using var index = await _fixture.OpenAsync();
        AssertRepeatedDispose(index);
        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }

    static void AssertRepeatedDispose(FileIndex index)
    {
        using var source = new CancellationTokenSource();
        using var enumerator = index.EnumerateRows(new SearchQuery(null), source.Token).GetEnumerator();
        using var copy = enumerator;
        Assert.AreEqual(1, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        copy.Dispose();
        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        enumerator.Dispose();
        enumerator.Dispose();
        copy.Dispose();
        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);

        using var later = index.EnumerateRows(new SearchQuery(null)).GetEnumerator();
        Assert.AreEqual(1, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        Assert.IsTrue(later.MoveNext());
    }
}
