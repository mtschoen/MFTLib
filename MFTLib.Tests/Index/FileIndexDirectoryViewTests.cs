using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A directory root on an NTFS volume is a view of the whole-volume block rooted at the row its path resolves to.
///     Volume T is mapped to the tree root, which holds real directories <c>src</c> and <c>other</c>; the block holds
///     rows 5 (root), 30 <c>src</c>, 31 <c>src\a.txt</c>, 32 <c>other</c> and 33 <c>other\b.txt</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexDirectoryViewTests : IndexTreeTestBase
{
    const ulong JournalIdentifier = 7;
    const uint SourceRow = 30;
    static readonly VolumeKey VolumeT = new('T');
    static readonly DateTime Moment = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string SourcePath => Path.Combine(TreeRoot, "src");

    string OtherPath => Path.Combine(TreeRoot, "other");

    [TestInitialize]
    public void CreateDirectories()
    {
        Directory.CreateDirectory(SourcePath);
        Directory.CreateDirectory(OtherPath);
    }

    [TestCleanup]
    public void ResetJournalSettings() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    static IEnumerable<SyntheticRow> Rows(bool includeSource = true)
    {
        yield return new SyntheticRow(5, ".", 5) { IsDirectory = true };
        if (includeSource)
        {
            yield return new SyntheticRow(SourceRow, "src", 5) { IsDirectory = true };
            yield return new SyntheticRow(31, "a.txt", SourceRow) { Size = 10 };
        }

        yield return new SyntheticRow(32, "other", 5) { IsDirectory = true };
        yield return new SyntheticRow(33, "b.txt", 32) { Size = 20 };
    }

    FileIndexOptions Options(RootKey root, bool includeSource = true) =>
        SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = root,
            Producer = ProducerSelection.Automatic,
            CacheDirectory = CacheDirectoryPath,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => Rows(includeSource))
            {
                JournalCursor = new SyntheticJournalCursor(JournalIdentifier, 4096),
                ScanTimestamp = Moment
            })
        }, UniqueVolumeSerial, volume: VolumeT, volumeRootDirectory: TreeRoot);

    Task<FileIndex> OpenViewAsync(bool includeSource = true) =>
        FileIndex.OpenAsync(Options(RootKey.FromDirectory(SourcePath), includeSource), TestContext.CancellationToken);

    static UsnJournalEntry Entry(ulong record, ulong parent, string name, UsnReason reason, long usn,
        FileAttributes attributes = FileAttributes.Normal) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            Usn = usn,
            TimestampUtc = Moment,
            FileAttributes = attributes
        });

    static Task<IReadOnlyList<FileChange>> ApplyAsync(FileIndex index, params UsnJournalEntry[] entries) =>
        index.ApplyJournalEntriesAsync(index.Status.Root, entries, JournalIdentifier, entries[^1].Usn + 1);

    [TestMethod]
    public async Task Open_ADirectoryOnAnEligibleVolume_IsAnMftViewOfTheVolumeBlock()
    {
        await using var index = await OpenViewAsync();

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(RootKey.FromDirectory(SourcePath), status.Root);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.AreEqual(UniqueVolumeSerial, status.VolumeSerial);
        var header = index.CurrentBlock!.Block.Header;
        Assert.AreEqual(5u, header.RootRow);
        Assert.AreEqual(ProducerKind.Mft, header.ProducerKind);
        var cacheFile = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(VolumeT, UniqueVolumeSerial));
        Assert.AreSequenceEqual([cacheFile], Directory.GetFiles(CacheDirectoryPath, "*.mlix"));
    }

    [TestMethod]
    public async Task Find_AcceptsOnlyPathsUnderTheViewRoot()
    {
        await using var index = await OpenViewAsync();

        var inside = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(SourcePath, "a.txt"), inside?.Path);
        Assert.IsNull(index.Find(Path.Combine(OtherPath, "b.txt"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(TreeRoot, TestContext.CancellationToken));
        Assert.AreEqual(SourcePath, index.Find(SourcePath, TestContext.CancellationToken)?.Path);
    }

    [TestMethod]
    public async Task Root_IsTheViewRootAndItsParentIsNull()
    {
        await using var index = await OpenViewAsync();

        var root = index.Root(TestContext.CancellationToken);

        Assert.AreEqual(SourcePath, root.Path);
        Assert.AreEqual(SourceRow, root.RecordKey.RecordNumber);
        Assert.IsNull(root.Parent);
        Assert.AreEqual(SourceRow, index.Find(Path.Combine(SourcePath, "a.txt"))!.Value.Parent!.Value.RecordKey.RecordNumber);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnscopedQueries_SeeOnlyTheView(bool withSizeBound)
    {
        await using var index = await OpenViewAsync();
        var query = withSizeBound
            ? new SearchQuery(null, IsDirectory: false, SizeMinimum: 1)
            : new SearchQuery(null, IsDirectory: false);
        var expected = Path.Combine(SourcePath, "a.txt");

        Assert.AreSequenceEqual([expected], index.Search(query, TestContext.CancellationToken).Select(entry => entry.Path));
        Assert.AreSequenceEqual([expected], index.Enumerate(query, TestContext.CancellationToken).Select(entry => entry.Path));
        var rowNames = new List<string>();
        foreach (var row in index.EnumerateRows(query, TestContext.CancellationToken))
        {
            rowNames.Add(row.Name.ToString());
        }

        Assert.AreSequenceEqual(["a.txt"], rowNames);
    }

    [TestMethod]
    public async Task ExplicitSubtreeInsideTheView_Narrows()
    {
        await using var index = await OpenViewAsync();
        var root = index.Root(TestContext.CancellationToken);

        var results = index.Search(new SearchQuery(null, IsDirectory: false, Subtree: root), TestContext.CancellationToken);

        Assert.AreSequenceEqual([Path.Combine(SourcePath, "a.txt")], results.Select(entry => entry.Path));
    }

    [TestMethod]
    public async Task Children_OfTheViewRootAreItsDirectChildren()
    {
        await using var index = await OpenViewAsync();

        var children = index.Root(TestContext.CancellationToken).Children(TestContext.CancellationToken);

        Assert.AreSequenceEqual(["a.txt"], [.. children.Select(child => child.Name)]);
    }

    [TestMethod]
    public async Task Watch_RenamingTheViewRootAwayAndBack_GoesOfflineThenReady()
    {
        await using var index = await OpenViewAsync();

        await ApplyAsync(index, Entry(SourceRow, 5, "src2", UsnReason.RenameNewName | UsnReason.Close, 5000,
            FileAttributes.Directory));

        var away = index.Status;
        Assert.AreEqual(RootState.Offline, away.State);
        Assert.AreEqual(UniqueVolumeSerial, away.VolumeSerial);
        Assert.IsNull(away.Block);
        Assert.AreEqual(ProducerKind.Mft, away.Producer);
        Assert.IsNull(index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken));
        Assert.IsEmpty(index.Search(new SearchQuery(null), TestContext.CancellationToken));
        Assert.IsEmpty(index.Enumerate(new SearchQuery(null), TestContext.CancellationToken).ToList());
        var enumeration = index.EnumerateRows(new SearchQuery(null), TestContext.CancellationToken);
        var rows = 0;
        foreach (var _ in enumeration)
        {
            rows++;
        }

        Assert.AreEqual(0, rows);
        Assert.ThrowsExactly<InvalidOperationException>(() => index.Root(TestContext.CancellationToken));

        await ApplyAsync(index, Entry(SourceRow, 5, "src", UsnReason.RenameNewName | UsnReason.Close, 5001,
            FileAttributes.Directory));

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.IsNotNull(index.Status.Block);
        Assert.AreEqual(Path.Combine(SourcePath, "a.txt"),
            index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken)?.Path);
        Assert.HasCount(1, index.Search(new SearchQuery("a.txt", NameMatchMode.Exact), TestContext.CancellationToken));
    }

    // Renames the view's directory once, after a query was admitted and before it reads: the schedule a journal batch
    // can interleave with any query.
    static void RenameViewRootAfterAdmission(FileIndex index)
    {
        var renamed = false;
        index.Hooks.ReaderAdmitted = () =>
        {
            if (renamed)
            {
                return;
            }

            renamed = true;
            ApplyAsync(index, Entry(SourceRow, 5, "src2", UsnReason.RenameNewName | UsnReason.Close, 5000,
                FileAttributes.Directory)).GetAwaiter().GetResult();
        };
    }

    [TestMethod]
    [DataRow("Search")]
    [DataRow("Enumerate")]
    [DataRow("EnumerateRows")]
    public async Task QueryAdmittedBeforeTheViewRootIsRenamed_StaysRestrictedToThatSubtree(string api)
    {
        await using var index = await OpenViewAsync();
        RenameViewRootAfterAdmission(index);
        var query = new SearchQuery(null);
        var names = new List<string>();

        switch (api)
        {
            case "Search":
                names.AddRange(index.Search(query, TestContext.CancellationToken).Select(entry => entry.Name));
                break;
            case "Enumerate":
                names.AddRange(index.Enumerate(query, TestContext.CancellationToken).Select(entry => entry.Name));
                break;
            default:
                foreach (var row in index.EnumerateRows(query, TestContext.CancellationToken))
                {
                    names.Add(row.Name.ToString());
                }

                break;
        }

        Assert.DoesNotContain("b.txt", names);
        Assert.DoesNotContain("other", names);
    }

    [TestMethod]
    public async Task RootAdmittedBeforeTheViewRootIsRenamed_IsStillTheAdmittedRow()
    {
        await using var index = await OpenViewAsync();
        RenameViewRootAfterAdmission(index);

        var root = index.Root(TestContext.CancellationToken);

        Assert.AreEqual(SourceRow, root.RecordKey.RecordNumber);
    }

    [TestMethod]
    [DataRow("Search")]
    [DataRow("Enumerate")]
    [DataRow("EnumerateRows")]
    public async Task ExplicitAnchorMovedOutOfTheView_MatchesNothing(string api)
    {
        await using var index = await OpenViewAsync();
        var anchor = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken)!.Value;
        await ApplyAsync(index, Entry(31, 32, "a.txt", UsnReason.RenameNewName | UsnReason.Close, 5000));
        var query = new SearchQuery(null, Subtree: anchor);
        var count = 0;

        switch (api)
        {
            case "Search":
                count = index.Search(query, TestContext.CancellationToken).Count;
                break;
            case "Enumerate":
                count = index.Enumerate(query, TestContext.CancellationToken).Count();
                break;
            default:
                foreach (var _ in index.EnumerateRows(query, TestContext.CancellationToken))
                {
                    count++;
                }

                break;
        }

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task UnrelatedBatchesWhileTheViewIsAbsent_CostNoResolutionAndTheReturnCostsOne()
    {
        await using var index = await OpenViewAsync();
        var view = index.CurrentBlock!.View;
        await ApplyAsync(index, Entry(SourceRow, 5, "src2", UsnReason.RenameNewName | UsnReason.Close, 5000,
            FileAttributes.Directory));
        Assert.AreEqual(RootState.Offline, index.Status.State);
        var afterRename = view.ResolutionDescents;

        // Each batch is larger than the last and the block keeps growing; none names a component of the path.
        long usn = 5001;
        for (var batch = 1; batch <= 4; batch++)
        {
            var entries = Enumerable.Range(0, batch * 25)
                .Select(index2 => Entry((ulong)(1000 + batch * 100 + index2), 5, $"unrelated-{batch}-{index2}.tmp",
                    UsnReason.FileCreate | UsnReason.Close, usn++))
                .ToArray();
            await ApplyAsync(index, entries);
            Assert.IsEmpty(index.Search(new SearchQuery(null), TestContext.CancellationToken));
        }

        Assert.AreEqual(afterRename, view.ResolutionDescents);

        // Entries that share the missing component's name but sit under another parent, and records for other names
        // under the right parent, cannot bring the path back.
        await ApplyAsync(index,
            Entry(1900, 32, "src", UsnReason.FileCreate | UsnReason.Close, usn++, FileAttributes.Directory),
            Entry(1901, 32, "src", UsnReason.RenameNewName | UsnReason.Close, usn++, FileAttributes.Directory),
            Entry(1902, 32, "src", UsnReason.BasicInfoChange | UsnReason.Close, usn++),
            Entry(1903, 5, "srcx", UsnReason.FileCreate | UsnReason.Close, usn++),
            Entry(1904, 5, "other2", UsnReason.DataExtend | UsnReason.Close, usn++));
        Assert.AreEqual(afterRename, view.ResolutionDescents);

        await ApplyAsync(index, Entry(SourceRow, 5, "src", UsnReason.RenameNewName | UsnReason.Close, usn,
            FileAttributes.Directory));

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(afterRename + 1, view.ResolutionDescents);
    }

    // The view is src\child. Row 30 is SRC (no child); the exact-spelling src that holds child (row 41) is row 40,
    // named tmp or not yet written, so the walk settles on SRC and child is missing under it.
    [TestMethod]
    [DataRow((uint)UsnReason.RenameNewName)]
    [DataRow((uint)UsnReason.FileCreate)]
    public async Task AbsentView_ComesBackWhenAnExactSpellingAncestorReplacesThePreferredOne(uint arrivalBits)
    {
        var arrival = (UsnReason)arrivalBits;
        Directory.CreateDirectory(Path.Combine(SourcePath, "child"));
        IEnumerable<SyntheticRow> AncestorRows()
        {
            yield return new SyntheticRow(5, ".", 5) { IsDirectory = true };
            yield return new SyntheticRow(30, "SRC", 5) { IsDirectory = true };
            if (arrival == UsnReason.RenameNewName)
            {
                yield return new SyntheticRow(40, "tmp", 5) { IsDirectory = true };
            }

            yield return new SyntheticRow(41, "child", 40) { IsDirectory = true };
        }

        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(Path.Combine(SourcePath, "child")),
            Producer = ProducerSelection.Automatic,
            CacheDirectory = CacheDirectoryPath,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => AncestorRows())
            {
                JournalCursor = new SyntheticJournalCursor(JournalIdentifier, 4096),
                ScanTimestamp = Moment
            })
        }, UniqueVolumeSerial, volume: VolumeT, volumeRootDirectory: TreeRoot);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        Assert.AreEqual(RootState.Offline, index.Status.State);
        var view = index.CurrentBlock!.View;
        var before = view.ResolutionDescents;

        await ApplyAsync(index, Entry(40, 5, "src", arrival | UsnReason.Close, 5000, FileAttributes.Directory));

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(before + 1, view.ResolutionDescents);
    }

    [TestMethod]
    public async Task AJournalBatchAddressedByVolume_ReachesAViewOfThatVolume()
    {
        await using var index = await OpenViewAsync();

        await index.ApplyJournalEntriesAsync(VolumeT,
            [Entry(SourceRow, 5, "src2", UsnReason.RenameNewName | UsnReason.Close, 5000, FileAttributes.Directory)],
            JournalIdentifier, 5001);

        Assert.AreEqual(RootState.Offline, index.Status.State);
    }

    [TestMethod]
    [DataRow((uint)UsnReason.FileCreate)]
    [DataRow((uint)UsnReason.BasicInfoChange)]
    [DataRow((uint)UsnReason.DataExtend)]
    [DataRow((uint)UsnReason.SecurityChange)]
    public async Task Open_DirectoryAbsentFromTheBlock_IsOfflineUntilTheJournalCreatesOrHydratesIt(uint reasonBits)
    {
        await using var index = await OpenViewAsync(includeSource: false);

        var absent = index.Status;
        Assert.AreEqual(RootState.Offline, absent.State);
        Assert.IsNull(absent.Block);
        Assert.AreEqual(ProducerKind.Mft, absent.Producer);
        Assert.IsEmpty(index.Search(new SearchQuery(null), TestContext.CancellationToken));
        var view = index.CurrentBlock!.View;
        var before = view.ResolutionDescents;

        // A create installs the row; a modification record for a row never written hydrates it with its name and parent.
        await ApplyAsync(index, Entry(SourceRow, 5, "src", (UsnReason)reasonBits | UsnReason.Close, 5000,
            FileAttributes.Directory));

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(SourcePath, index.Root(TestContext.CancellationToken).Path);
        Assert.AreEqual(before + 1, view.ResolutionDescents);
    }

    [TestMethod]
    public async Task RootTextWithOtherSeparatorsAndCase_NamesTheSameView()
    {
        var spelled = OperatingSystem.IsWindows()
            ? (TreeRoot + @"\SRC\").Replace('\\', '/').ToUpperInvariant()
            : TreeRoot + "/src/";
        Assert.IsTrue(RootKey.TryParse(spelled, out var alternate));
        Assert.AreEqual(RootKey.FromDirectory(SourcePath), alternate, "the key names the same directory");

        await using var index = await FileIndex.OpenAsync(Options(alternate), TestContext.CancellationToken);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(ProducerKind.Mft, index.Status.Producer);
        var viaStored = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken);
        Assert.AreEqual(Path.Combine(SourcePath, "a.txt"), viaStored?.Path);
        var viaAlternate = index.Find(spelled + (OperatingSystem.IsWindows() ? "A.TXT" : "a.txt"), TestContext.CancellationToken);
        Assert.AreEqual(viaStored, viaAlternate);
    }

    [TestMethod]
    public async Task QueryUsnJournalSettings_ServesADirectoryView()
    {
        var expected = new UsnJournalSettings { MaximumSize = 64L * 1024 * 1024, AllocationDelta = 8L * 1024 * 1024 };
        UsnJournalSettingsQuery.Dependencies._queryOverride =
            volume => volume == 'T' ? expected : throw new IOException($"Unexpected volume {volume}");
        await using var index = await OpenViewAsync();

        var settings = index.ReadUsnJournalSettings();

        Assert.AreEqual(expected.MaximumSize, settings.MaximumSize);
        Assert.AreEqual(expected.AllocationDelta, settings.AllocationDelta);
    }
}
