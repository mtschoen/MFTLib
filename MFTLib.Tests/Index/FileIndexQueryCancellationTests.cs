using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every public query takes a cancellation token, observes it, and holds a borrow on the
///     snapshot it reads for as long as it reads it. A token already cancelled when a query is
///     called stops that query before it touches a row.
/// </summary>
[TestClass]
public class FileIndexQueryCancellationTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "report.pdf"), new string('x', 100));

        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, TestVolumeSerial.GetNext())],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _index.DisposeAsync();
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
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
        Assert.AreEqual(1, _index.FindByName("readme.md", token).Count);
        Assert.AreEqual(1, _index.Search(new SearchQuery("report"), token).Count);
        Assert.AreEqual(1, _index.Largest(1, under: null, token).Count);
        Assert.AreEqual(0, _index.DuplicateNames(token).Count);
        Assert.IsTrue(_index.Root('T', token).IsDirectory);
        Assert.AreEqual("Documents", _index.Root('T', token).Children(token).Single().Name);
    }

    [TestMethod]
    public void Find_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.Find(Path.Combine(_treeRoot, "Documents", "readme.md"), token));
    }

    [TestMethod]
    public void FindByName_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.FindByName("readme.md", token));
    }

    [TestMethod]
    public void Search_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.Search(new SearchQuery("readme"), token));
    }

    [TestMethod]
    public void Search_Under_WithACancelledToken_Throws()
    {
        var root = _index.Root('T');
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.Search(new SearchQuery("readme", Under: root), token));
    }

    [TestMethod]
    public void Largest_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.Largest(5, under: null, token));
    }

    [TestMethod]
    public void Largest_Under_WithACancelledToken_Throws()
    {
        var root = _index.Root('T');
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.Largest(5, under: root, token));
    }

    [TestMethod]
    public void DuplicateNames_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.DuplicateNames(token));
    }

    [TestMethod]
    public void Root_WithACancelledToken_Throws()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() => _index.Root('T', token));
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

        Assert.ThrowsException<OperationCanceledException>(() => root.Children(token));
    }

    /// <summary>
    ///     That scan holds a borrow for its duration, the same as the index's own queries, so a
    ///     release cannot unmap the rows it is walking. It gives the borrow back when it returns.
    /// </summary>
    [TestMethod]
    public void Children_LeavesNoBorrowOutstanding()
    {
        _ = _index.Root('T').Children();

        Assert.AreEqual(0, _index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    /// <summary>
    ///     A query gives its borrow back when it returns, so a disposal that follows a completed
    ///     query has nothing to wait for.
    /// </summary>
    [TestMethod]
    public void AQueryThatReturned_LeavesNoBorrowOutstanding()
    {
        _ = _index.FindByName("readme.md");

        Assert.AreEqual(0, _index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    /// <summary>
    ///     A query that throws gives its borrow back too: the release is in a finally, not on the
    ///     success path, or one cancelled query would stall every later disposal.
    /// </summary>
    [TestMethod]
    public void AQueryThatWasCancelled_LeavesNoBorrowOutstanding()
    {
        using var cancellation = Cancelled();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => _index.FindByName("readme.md", token));

        Assert.AreEqual(0, _index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    static CancellationTokenSource Cancelled()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        return cancellation;
    }
}
