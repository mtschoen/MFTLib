using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every public query takes a cancellation token, observes it, and holds a borrow on the
///     snapshot it reads for as long as it reads it. A token already cancelled when a query is
///     called stops that query before it touches a row.
/// </summary>
[TestClass]
public sealed class FileIndexQueryCancellationTests : IAsyncDisposable
{
    IndexTreeFixture _fixture = null!;
    string _treeRoot => _fixture.TreeRoot;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _fixture = new IndexTreeFixture();
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "report.pdf"), new string('x', 100));

        _index = await FileIndex.OpenAsync(_fixture.Options(volumeSerial: TestVolumeSerial.GetNext()), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _index.DisposeAsync();
        }
        finally
        {
            _fixture.Dispose();
        }
    }

    /// <summary>
    ///     The live-token half of the contract: every entry point accepts a token and answers
    ///     normally while it is uncancelled, so passing one is never a behaviour change.
    /// </summary>
    [TestMethod]
    public void EveryQuery_WithALiveToken_AnswersNormally()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        var readme = _index.Find(Path.Combine(_treeRoot, "Documents", "readme.md"), token);

        Assert.IsTrue(readme.HasValue);
        Assert.AreEqual(1, _index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), token).Count);
        Assert.AreEqual(1, _index.Search(new SearchQuery("report"), token).Count);
        Assert.IsTrue(_index.Root('T', token).IsDirectory);
        Assert.AreEqual("Documents", _index.Root('T', token).Children(token).Single().Name);
    }

    [TestMethod]
    public void Find_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => _index.Find(Path.Combine(_treeRoot, "Documents", "readme.md"), token));
    }

    [TestMethod]
    public void Search_ExactMode_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => _index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), token));
        Assert.AreEqual(0, _index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public void Search_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => _index.Search(new SearchQuery("readme"), token));
    }

    [TestMethod]
    public void Search_Under_WithACancelledToken_Throws()
    {
        var root = _index.Root('T');
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => _index.Search(new SearchQuery("readme", Subtree: root), token));
    }

    [TestMethod]
    public void EnumerateRows_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => ConsumeRows(_index, new SearchQuery(null), token));
    }

    [TestMethod]
    public void EnumerateRows_Under_WithACancelledToken_Throws()
    {
        var root = _index.Root('T');
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => ConsumeRows(_index, new SearchQuery(null, Subtree: root), token));
    }

    static void ConsumeRows(FileIndex index, SearchQuery query, CancellationToken cancellationToken)
    {
        foreach (var row in index.EnumerateRows(query, cancellationToken))
        {
            _ = row.Size;
        }
    }

    [TestMethod]
    public void Root_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() => _index.Root('T', token));
    }

    /// <summary>
    ///     Listing a directory's children scans the drive's whole row count, so it is a query in
    ///     every sense that matters here and takes the same token as the rest.
    /// </summary>
    [TestMethod]
    public void Children_WithACancelledToken_Throws()
    {
        var root = _index.Root('T');
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() => root.Children(token));
    }

    /// <summary>
    ///     That scan holds a borrow for its duration, the same as the index's own queries, so a
    ///     release cannot unmap the rows it is walking. It gives the borrow back when it returns.
    /// </summary>
    [TestMethod]
    public void Children_LeavesNoBorrowOutstanding()
    {
        _ = _index.Root('T').Children();

        Assert.AreEqual(0, _index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }

    /// <summary>
    ///     A query gives its borrow back when it returns, so a disposal that follows a completed
    ///     query has nothing to wait for.
    /// </summary>
    [TestMethod]
    public void AQueryThatReturned_LeavesNoBorrowOutstanding()
    {
        _ = _index.Search(new SearchQuery("readme.md", NameMatchMode.Exact));

        Assert.AreEqual(0, _index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }


    static CancellationTokenSource Cancelled()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        return cancellation;
    }
}
