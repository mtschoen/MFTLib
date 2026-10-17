using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A <see cref="SearchQuery.Subtree" /> anchor belongs to the queried index's current block for the
///     anchor's drive. All three query forms validate it against the snapshot they borrowed.
/// </summary>
[TestClass]
public class SubtreeAnchorTests
{
    // Real index work on a loaded runner outlasts the shared guard.
    static readonly TimeSpan SlowHangGuard = HangGuard * 3;

    public enum QueryForm
    {
        Search,
        Enumerate,
        EnumerateRows
    }

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static readonly string[] DocumentsSubtree = ["documents", "notes.txt"];

    static List<string> Run(FileIndex index, QueryForm form, SearchQuery query)
    {
        switch (form)
        {
            case QueryForm.Search:
                return index.Search(query).Select(entry => entry.Name).ToList();
            case QueryForm.Enumerate:
                return index.Enumerate(query).Select(entry => entry.Name).ToList();
            default:
                var names = new List<string>();
                foreach (var row in index.EnumerateRows(query))
                {
                    names.Add(row.Name.ToString());
                }

                return names;
        }
    }

    /// <summary>Makes the call and nothing else: a lazy result is never enumerated.</summary>
    static ArgumentException CallRejected(FileIndex index, QueryForm form, SearchQuery query) =>
        Assert.ThrowsException<ArgumentException>(() =>
        {
            switch (form)
            {
                case QueryForm.Search:
                    _ = index.Search(query);
                    break;
                case QueryForm.Enumerate:
                    _ = index.Enumerate(query);
                    break;
                default:
                    _ = index.EnumerateRows(query);
                    break;
            }
        });

    static void Rejected<TException>(FileIndex index, QueryForm form, SearchQuery query, string message = "")
        where TException : Exception =>
        Assert.ThrowsException<TException>(() => Run(index, form, query), message);

    static Task<List<string>> RunOnThreadPool(FileIndex index, QueryForm form, SearchQuery query,
        CancellationToken cancellationToken) =>
        Task.Run(() => Run(index, form, query), cancellationToken);

    static FileEntry Documents(FileIndex index) => index.Search(new SearchQuery("documents", NameMatchMode.Exact)).Single();

    static SearchQuery Under(FileEntry anchor) => new(null, Subtree: anchor);

    static UsnJournalEntry Journal(UsnReason reason, string name, ushort sequence) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 6,
            ParentRecordNumber = 5,
            FileName = name,
            Reason = reason | UsnReason.Close,
            FileAttributes = FileAttributes.Directory,
            SequenceNumber = sequence,
            Usn = 1000,
            TimestampUtc = new DateTime(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc)
        });

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public void ValidAnchor_RestrictsToItsSubtree(QueryForm form)
    {
        using var harness = new WatchHarness('T');

        CollectionAssert.AreEqual(DocumentsSubtree, Run(harness.Index, form, Under(Documents(harness.Index))));
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public void DefaultAnchor_IsRejectedOnCallEntry(QueryForm form)
    {
        using var harness = new WatchHarness('T');
        var query = new SearchQuery(null, Subtree: default(FileEntry));

        var admissions = new int[1];
        var snapshot = harness.Index.CurrentSnapshot;
        snapshot.ReleaseState._readerAdmittedForTest = () => Interlocked.Increment(ref admissions[0]);
        ArgumentException exception;
        try
        {
            exception = CallRejected(harness.Index, form, query);
        }
        finally
        {
            snapshot.ReleaseState._readerAdmittedForTest = null;
        }

        StringAssert.Contains(exception.Message, "default FileEntry");
        Assert.AreEqual(0, admissions[0], "The call was refused before any query was admitted.");
        Assert.AreEqual(0, snapshot.ReleaseState.OutstandingBorrowCount);
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search, 'T', 'U', DisplayName = "Search, foreign index with equal ordinals")]
    [DataRow(QueryForm.Enumerate, 'T', 'U', DisplayName = "Enumerate, foreign index with equal ordinals")]
    [DataRow(QueryForm.EnumerateRows, 'T', 'U', DisplayName = "EnumerateRows, foreign index with equal ordinals")]
    [DataRow(QueryForm.Search, 'T', 'T', DisplayName = "Search, foreign index with the same drive key")]
    [DataRow(QueryForm.Enumerate, 'T', 'T', DisplayName = "Enumerate, foreign index with the same drive key")]
    [DataRow(QueryForm.EnumerateRows, 'T', 'T', DisplayName = "EnumerateRows, foreign index with the same drive key")]
    public void ForeignAnchor_IsRejected(QueryForm form, char anchorDrive, char queriedDrive)
    {
        using var anchorOwner = new WatchHarness(anchorDrive);
        using var queried = new WatchHarness(queriedDrive);
        var anchor = anchorOwner.Index.Root(anchorDrive);

        Rejected<ArgumentException>(queried.Index, form, Under(anchor));

        Assert.AreEqual(0, queried.Index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        Assert.IsTrue(Run(queried.Index, form, new SearchQuery(null)).Count > 0, "The queried index still answers.");
    }

    [TestMethod]
    public void ForeignAnchor_IsRejectedWhileOwnRootIncludesItself()
    {
        using var first = new WatchHarness('T');
        using var second = new WatchHarness('T');
        var firstIndex = first.Index;
        var ownRoot = firstIndex.Root('T');
        var foreignRoot = second.Index.Root('T');

        Assert.ThrowsException<ArgumentException>(() =>
            firstIndex.Search(new SearchQuery(null, Subtree: foreignRoot)));
        CollectionAssert.Contains(
            firstIndex.Search(new SearchQuery(null, Subtree: ownRoot)).ToArray(), ownRoot);
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public void DisposedAnchor_IsRejectedWithObjectDisposed(QueryForm form)
    {
        using var queried = new WatchHarness('T');
        FileEntry anchor;
        using (var anchorOwner = new WatchHarness('T'))
        {
            anchor = anchorOwner.Index.Root('T');
        }

        Assert.IsTrue(anchor.IsDisposed);
        Rejected<ObjectDisposedException>(queried.Index, form, Under(anchor));
        Assert.AreEqual(0, queried.Index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public async Task AnchorOfAReplacedDriveBlock_IsRejectedButStaysReadable(QueryForm form)
    {
        using var harness = new WatchHarness('T');
        var anchor = Documents(harness.Index);

        await harness.Index.RescanAsync(['T'], Token).WaitAsync(SlowHangGuard);

        Rejected<ArgumentException>(harness.Index, form, Under(anchor));
        Assert.AreEqual(0, harness.Index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        Assert.AreEqual("documents", anchor.Name, "A retained handle stays readable against its original block.");
        CollectionAssert.AreEqual(DocumentsSubtree, Run(harness.Index, form, Under(Documents(harness.Index))));
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public async Task RescanningAnotherDrive_KeepsTheAnchorValid(QueryForm form)
    {
        using var harness = new WatchHarness('T', 'U');
        var anchor = harness.Index.Search(new SearchQuery("documents", NameMatchMode.Exact))
            .Single(entry => entry.Drive == 'T');

        await harness.Index.RescanAsync(['U'], Token).WaitAsync(SlowHangGuard);

        CollectionAssert.AreEqual(DocumentsSubtree, Run(harness.Index, form, Under(anchor)));
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public void AnchorSupersededByRowReuse_IsRejected(QueryForm form)
    {
        using var harness = new WatchHarness('T');
        var anchor = Documents(harness.Index);

        harness.Index.ApplyJournalEntries('T',
            [Journal(UsnReason.FileDelete, "documents", 0), Journal(UsnReason.FileCreate, "replacement", 5)],
            WatchHarness.JournalIdentifier, 200);

        Assert.IsTrue(anchor.IsDeleted);
        Rejected<ArgumentException>(harness.Index, form, Under(anchor));
        Assert.AreEqual(0, harness.Index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        CollectionAssert.AreEqual(new[] { "replacement", "notes.txt" },
            Run(harness.Index, form, Under(Documents(harness.Index, "replacement"))));
    }

    [DataTestMethod]
    [DataRow(QueryForm.Search)]
    [DataRow(QueryForm.Enumerate)]
    [DataRow(QueryForm.EnumerateRows)]
    public async Task RescanAfterAdmission_DoesNotInvalidateTheBorrowedQuery(QueryForm form)
    {
        using var harness = new WatchHarness('T');
        var anchor = Documents(harness.Index);
        var snapshot = harness.Index.CurrentSnapshot;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.ReleaseState._readerAdmittedForTest = () =>
        {
            admitted.TrySetResult();
            proceed.Task.GetAwaiter().GetResult();
        };
        List<string> names;
        try
        {
            var query = RunOnThreadPool(harness.Index, form, Under(anchor), Token);
            await admitted.Task.WaitAsync(SlowHangGuard);
            Assert.AreEqual(1, snapshot.ReleaseState.OutstandingBorrowCount);

            await harness.Index.RescanAsync(['T'], Token).WaitAsync(SlowHangGuard);
            Assert.AreNotSame(snapshot, harness.Index.CurrentSnapshot, "The rescan published a new snapshot.");
            proceed.TrySetResult();
            names = await query.WaitAsync(SlowHangGuard);
        }
        finally
        {
            proceed.TrySetResult();
            snapshot.ReleaseState._readerAdmittedForTest = null;
        }

        CollectionAssert.AreEqual(DocumentsSubtree, names,
            "The anchor is validated against the snapshot the query borrowed, where its block is still current.");
        Rejected<ArgumentException>(harness.Index, form, Under(anchor),
            "A query admitted after the rescan sees the replacement block and rejects the old anchor.");
    }

    static FileEntry Documents(FileIndex index, string name) =>
        index.Search(new SearchQuery(name, NameMatchMode.Exact)).Single();
}
