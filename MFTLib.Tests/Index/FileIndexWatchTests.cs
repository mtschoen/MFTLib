using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexWatchTests : IAsyncDisposable
{
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    IndexTreeFixture _fixture = null!;
    TempDirectory _enumerationDir = null!;
    string TreeRoot => _fixture.TreeRoot;
    string EnumerationRoot => _enumerationDir.Path;
    string CacheDirectoryPath => _fixture.CacheDirectory;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _fixture = new IndexTreeFixture();
        _enumerationDir = new TempDirectory("mftlib-enumeration");

        // Seed a warm-start MFT block for drive 'T' so journal mutations are supported on it.
        Directory.CreateDirectory(CacheDirectoryPath);
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        using (var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = blockPath,
            VolumeSerial = 0x0BADF00D,
            ProducerKind = ProducerKind.Mft,
            SlotCapacity = 256,
            NamePoolCapacity = 4096
        }))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, ChangeMoment.Ticks, 0));
            writer.TryWriteRow(1, "Documents", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 16, 0, ChangeMoment.Ticks, 0));
            writer.TryWriteRow(2, "readme.md", new RowColumns(1, RowFlags.InUse, 32, 100, ChangeMoment.Ticks, 0));
            writer.Complete(ChangeMoment, null);
        }

        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives =
            [
                new IndexedDrive('T', TreeRoot, 0x0BADF00D),
                new IndexedDrive('E', EnumerationRoot, 0x0E0E0E0E, ProducerKind.Enumeration),
                new IndexedDrive('Z', Path.Combine(TreeRoot, "does-not-exist"), 0xDEAD0000, ProducerKind.Enumeration)
            ],
            CacheDirectory = CacheDirectoryPath,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ =>
            [
                new SyntheticRow(5, ".", 5) { IsDirectory = true },
                new SyntheticRow(6, "Documents", 5) { IsDirectory = true },
                new SyntheticRow(7, "readme.md", 6)
            ])
            { ScanTimestamp = ChangeMoment })
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _index.DisposeAsync();
        }
        finally
        {
            try
            {
                _enumerationDir.Dispose();
            }
            finally
            {
                _fixture.Dispose();
            }
        }
    }

    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName,
        UsnReason reason)
    {
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            Usn = 1,
            TimestampUtc = ChangeMoment,
            Reason = reason,
            FileAttributes = FileAttributes.Archive,
            FileName = fileName
        });
    }

    [TestMethod]
    public async Task StartWatchingAsync_OnAMixedIndexWithoutASource_ReportsNotApplicable()
    {
        Assert.IsFalse(_index.Drives[1].Watch.IsSupported);
        Assert.IsFalse(_index.Drives[0].Watch.IsSupported, "no source offers a watch source");
        await DriveResultAssertions.NotApplicableAsync(
            () => _index.StartWatchingAsync(['T'], CancellationToken.None));
    }

    [TestMethod]
    public async Task StartWatchingAsync_OnAnEnumerationDrive_ReportsNotApplicableWithoutCallingSource()
    {
        var source = new FailIfCalledWatchSource();

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('E', TreeRoot, 0x0E0E0E0E, ProducerKind.Enumeration)],
            CacheDirectory = Path.Combine(CacheDirectoryPath, "enumeration-only"),
            MftSource = TestMftSources.WatchOnly(source)
        }, CancellationToken.None);

        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['E'], CancellationToken.None));

        Assert.IsFalse(source.WasCalled);
    }

    [TestMethod]
    public void ApplyJournalEntries_OnEnumerationDrive_ThrowsInvalidOperation()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => _index.ApplyJournalEntries('E', [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)], 5, 100));

        Assert.Contains("enumeration producer", exception.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyJournalEntries_CreatesARowAndRaisesChanged(bool throughTestExtensions)
    {
        var observed = new List<FileChange>();
        _index.Changed += observed.Add;

        UsnJournalEntry[] entries = [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)];
        SyntheticJournalRecord[] synthetic =
        [
            new()
            {
                RecordNumber = 20,
                ParentRecordNumber = 0,
                Usn = 1,
                Timestamp = ChangeMoment,
                Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close,
                FileAttributes = FileAttributes.Archive,
                FileName = "injected.txt"
            }
        ];
        var applied = throughTestExtensions
            ? FileIndexTestAccess.ApplyJournalEntries(_index, 'T', synthetic,
                new SyntheticJournalCursor(5, 100))
            : _index.ApplyJournalEntries('T', entries, journalIdentifier: 5, nextUsn: 100);

        Assert.HasCount(1, applied);
        Assert.HasCount(1, observed);
        Assert.AreEqual(FileChangeKind.Created, observed[0].Kind);
        Assert.AreEqual("injected.txt", observed[0].Entry.Name);
        Assert.AreEqual(observed[0], applied[0]);
        var root = _index.Root('T', TestContext.CancellationToken);
        Assert.AreEqual(5UL, root.DriveBlock.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual(100L, root.DriveBlock.Block.Header.UsnNextUsn);
        Assert.HasCount(1, _index.Search(new SearchQuery("injected"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void ApplyJournalEntries_TestExtensionRejectsNullIndex()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            FileIndexTestAccess.ApplyJournalEntries(null!, 'T', [], new SyntheticJournalCursor(5, 100)));
    }

    [TestMethod]
    public void ApplyJournalEntries_MakesTheNewRowVisibleToSearch()
    {
        _index.ApplyJournalEntries('T',
            [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 100);

        Assert.HasCount(1, _index.Search(new SearchQuery("injected"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void ApplyJournalEntries_DeleteMakesTheEntryReadAsDeletedButKeepsItsName()
    {
        var target = _index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        var recordNumber = target.RecordKey.RecordNumber;

        var applied = _index.ApplyJournalEntries('T',
            [Entry(recordNumber, 0, "readme.md", UsnReason.FileDelete | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 200);

        Assert.AreEqual(FileChangeKind.Deleted, applied[0].Kind);
        Assert.IsTrue(target.IsDeleted);
        Assert.AreEqual("readme.md", target.Name);
    }

    sealed class ConcurrentReaderState
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Finished;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplyJournalEntries_ConcurrentHeldHandleReaderSeesOnlyCompleteNames(bool cancelBeforeStartup = false)
    {
        var target = _index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        var recordNumber = target.RecordKey.RecordNumber;
        const string shortName = "x";
        const string longName = "journal-renamed-document.txt";
        var state = new ConcurrentReaderState();
        var unexpectedNames = new ConcurrentQueue<string>();

        using var startupCancellation = cancelBeforeStartup
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        if (cancelBeforeStartup)
        {
            startupCancellation.Cancel();
        }

        var reader = Task.Run(() =>
        {
            state.Started.TrySetResult();
            while (!state.Finished)
            {
                var name = target.Name;
                if (name is not "readme.md" and not shortName and not longName)
                {
                    unexpectedNames.Enqueue(name);
                }

                Thread.Yield();
            }
        }, startupCancellation.Token);

        try
        {
            var completed = await Task.WhenAny(state.Started.Task, reader).WaitAsync(HangGuard, startupCancellation.Token);
            if (completed == state.Started.Task)
            {
                await state.Started.Task;
                for (var iteration = 0; iteration < 64; iteration++)
                {
                    var name = iteration % 2 == 0 ? shortName : longName;
                    _index.ApplyJournalEntries('T',
                        [Entry(recordNumber, 1, name, UsnReason.RenameNewName | UsnReason.Close)],
                        journalIdentifier: 5, nextUsn: iteration + 1);
                    Thread.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (cancelBeforeStartup)
        {
        }
        finally
        {
            state.Finished = true;
            try
            {
                await reader;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (!cancelBeforeStartup)
        {
            Assert.IsEmpty(unexpectedNames,
                $"Reader observed torn names: {string.Join(", ", unexpectedNames)}");
            Assert.AreEqual(longName, target.Name);
        }
    }

    [TestMethod]
    public async Task ApplyJournalEntries_ExhaustingCapacityReportsReadyWithCapacityExhausted()
    {
        _index.ApplyJournalEntries('T',
            [Entry(50_000_000, 0, "far.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 300);

        Assert.AreEqual(DriveState.Ready, _index.Drives[0].State);
        Assert.IsTrue(_index.Drives[0].Block!.IsCapacityExhausted);
        Assert.IsNotNull(_index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));

        await _index.RescanAsync(['T'], CancellationToken.None);

        var replaced = _index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Ready, replaced.State);
        Assert.IsFalse(replaced.Block!.IsCapacityExhausted);
        Assert.IsNotNull(_index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void ApplyJournalEntries_UnknownDriveThrows()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _index.ApplyJournalEntries('Q', [], 0, 0));
    }

    [TestMethod]
    public void ApplyJournalEntries_ConfiguredButOfflineDriveThrowsInvalidOperation()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => _index.ApplyJournalEntries('Z', [], 0, 0));

        Assert.Contains("offline", exception.Message);
    }

    [TestMethod]
    public void ApplyJournalEntries_HandlerException_StillDeliversEveryChangeToOtherSubscribersThenSurfaces()
    {
        var firstSubscriberCallCount = 0;
        _index.Changed += _ =>
        {
            firstSubscriberCallCount++;
            if (firstSubscriberCallCount == 1)
            {
                throw new InvalidOperationException("boom");
            }
        };

        var secondSubscriberChanges = new List<FileChange>();
        _index.Changed += secondSubscriberChanges.Add;

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _index.ApplyJournalEntries('T',
            [
                Entry(20, 0, "first.txt", UsnReason.FileCreate | UsnReason.Close),
                Entry(21, 0, "second.txt", UsnReason.FileCreate | UsnReason.Close)
            ],
            journalIdentifier: 5, nextUsn: 100));

        Assert.AreEqual("boom", thrown.Message);
        Assert.HasCount(2, secondSubscriberChanges);
        Assert.AreSequenceEqual(["first.txt", "second.txt"], [.. secondSubscriberChanges.Select(change => change.Entry.Name)], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ApplyJournalEntries_MultipleHandlerExceptions_ThrowsAggregateException()
    {
        _index.Changed += _ => throw new InvalidOperationException("first boom");
        _index.Changed += _ => throw new InvalidOperationException("second boom");

        var thrown = Assert.ThrowsExactly<AggregateException>(() => _index.ApplyJournalEntries('T',
            [Entry(20, 0, "first.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 100));

        Assert.HasCount(2, thrown.InnerExceptions);
        Assert.AreSequenceEqual(["first boom", "second boom"], [.. thrown.InnerExceptions.Select(exception => exception.Message)], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ApplyJournalEntries_SingleHandlerException_KeepsTheStackTraceOfTheHandlerThatThrew()
    {
        _index.Changed += ThrowingSubscriber;

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _index.ApplyJournalEntries('T',
            [Entry(20, 0, "first.txt", UsnReason.FileCreate | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 100));

        // Rethrowing the caught instance directly would replace this frame with the throw site
        // inside MFTLib, leaving a consumer no way to find the handler that actually failed.
        Assert.Contains(nameof(ThrowingSubscriber), thrown.StackTrace ?? string.Empty);
    }

    [TestMethod]
    public void ApplyJournalEntries_CreateClosePairAcrossBatches_RaisesExactlyOneCreated()
    {
        var observed = new List<FileChange>();
        _index.Changed += observed.Add;

        var first = _index.ApplyJournalEntries('T',
            [Entry(20, 0, "injected.txt", UsnReason.FileCreate)], journalIdentifier: 5, nextUsn: 100);
        var second = _index.ApplyJournalEntries('T',
            [Entry(20, 0, "injected.txt", UsnReason.FileCreate | UsnReason.Close)], journalIdentifier: 5, nextUsn: 101);

        Assert.HasCount(1, first);
        Assert.IsEmpty(second);
        Assert.HasCount(1, observed);
        Assert.AreEqual(FileChangeKind.Created, observed[0].Kind);
    }

    [TestMethod]
    public void ApplyJournalEntries_CloseCarryingAnUnreportedReason_StillRaisesOneChange()
    {
        var target = _index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        var recordNumber = target.RecordKey.RecordNumber;
        _index.ApplyJournalEntries('T',
            [Entry(recordNumber, 1, "readme.md", UsnReason.DataExtend)], journalIdentifier: 5, nextUsn: 100);

        var second = _index.ApplyJournalEntries('T',
            [Entry(recordNumber, 1, "readme.md", UsnReason.DataExtend | UsnReason.DataTruncation | UsnReason.Close)],
            journalIdentifier: 5, nextUsn: 101);

        Assert.HasCount(1, second);
        Assert.AreEqual(FileChangeKind.Modified, second[0].Kind);
    }

    static void ThrowingSubscriber(FileChange change)
    {
        throw new InvalidOperationException("boom");
    }

    /// <summary>Every member fails the test, so an index with nothing watchable proves it.</summary>
    sealed class FailIfCalledWatchSource : IIndexWatchSource
    {
        public bool WasCalled { get; private set; }

        public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new AssertFailedException(
                $"Source was asked to start drive {target.Drive} with cancellation {cancellationToken.CanBeCanceled}.");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
