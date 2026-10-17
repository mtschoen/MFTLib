using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class EnumerationProducerTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string _treeRoot = null!;
    string _blockDirectory => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture();
        _treeRoot = _fixture.TreeRoot;
        Directory.CreateDirectory(_blockDirectory);

        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents", "Projects"));
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Pictures"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf"), new string('x', 100));
        File.WriteAllText(Path.Combine(_treeRoot, "Pictures", "holiday.jpg"), new string('y', 50));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    (BlockFile Block, BlockWriter Writer) CreateBlock(uint slotCapacity = 256, uint namePoolCapacity = 4096)
    {
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(_blockDirectory, "T-00000001.mlix"),
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        });

        return (block, new BlockWriter(block));
    }

    DriveBlock[] Produce(out BlockFile block, out EnumerationResult result)
    {
        var created = CreateBlock();
        block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        result = producer.Produce(created.Writer, progress: null, CancellationToken.None);
        created.Writer.Complete(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), null);
        return [new DriveBlock('T', 0, block, rootDirectoryPath: _treeRoot)];
    }

    [TestMethod]
    public async Task Produce_WritesARootPlusEveryDirectoryAndFile()
    {
        var snapshot = Produce(out var block, out var result);
        try
        {
            // Root, Documents, Projects, Pictures, readme.md, report.pdf, holiday.jpg.
            Assert.AreEqual(7u, result.RowCount);
            Assert.AreEqual(7u, block.Header.RowCount);
            Assert.AreEqual(0, result.AccessDeniedSubtreeCount);
            Assert.IsFalse(result.CapacityExhausted);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void CreateDirectoryEnumerationOptions_CarriesTheMeasuredFetchBufferSize()
    {
        var options = EnumerationProducer.CreateDirectoryEnumerationOptions();

        Assert.AreEqual(64 * 1024, options.BufferSize);
    }

    [TestMethod]
    public async Task Produce_BuildsPathsThatMatchTheRealTree()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf"));
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual(100L, entry.Value.Size);
            Assert.IsFalse(entry.Value.IsDirectory);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_MarksDirectoriesAsDirectoriesWithZeroSize()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var documents = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Documents"));
            Assert.IsTrue(documents.HasValue);
            Assert.IsTrue(documents.Value.IsDirectory);
            Assert.AreEqual(0L, documents.Value.Size);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_RootRowIsRowZeroAndParentsItself()
    {
        var snapshot = Produce(out var block, out _);
        try
        {
            Assert.AreEqual(0u, block.Header.RootRow);
            Assert.AreEqual(0u, block.Rows[0].ParentRow);
            Assert.IsTrue(block.Rows[0].IsDirectory);
            Assert.AreEqual(_treeRoot.Length, block.Rows[0].NameLengthUnits);
            Assert.AreEqual(_treeRoot, new string(NamePool.ReadRowName(block, 0)));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_RootRowCarriesTheRootDirectoriesRealLastWriteTimeAndAttributes()
    {
        var rootMoment = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(_treeRoot, rootMoment);
        var snapshot = Produce(out var block, out _);
        try
        {
            ref readonly var root = ref block.Rows[0];
            Assert.IsTrue(root.LastWriteTimeKnown);
            Assert.AreEqual(rootMoment, root.ModifiedUtc, "The root row must not be stamped with the scan clock.");
            Assert.AreEqual((uint)(new DirectoryInfo(_treeRoot).Attributes | FileAttributes.Directory), root.Attributes);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_UnreadableRoot_MarksTheRootLastWriteTimeUnknownInsteadOfStampingTheClock()
    {
        var existingRoot = _treeRoot;
        _treeRoot = Path.Combine(existingRoot, "missing");
        var snapshot = Produce(out var block, out _);
        try
        {
            ref readonly var root = ref block.Rows[0];
            Assert.IsFalse(root.LastWriteTimeKnown);
            Assert.AreEqual(0L, root.ModifiedTicks);
            Assert.IsTrue(root.IsDirectory);
        }
        finally
        {
            _treeRoot = existingRoot;
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_ProducerKindOnTheBlockIsEnumerationSoIdsCarryThatKind()
    {
        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "Pictures", "holiday.jpg"));
            Assert.AreEqual(ProducerKind.Enumeration, entry!.Value.RecordKey.ProducerKind);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Produce_ReportsProgress()
    {
        var created = CreateBlock();
        using var block = created.Block;

        // A direct IProgress<T> implementation, not System.Progress<T>: Progress<T> posts
        // through a SynchronizationContext and delivers asynchronously, which would make an
        // assertion on the collected reports racy. Reporting synchronously on the calling
        // thread is deterministic and lets every report be asserted on.
        var observed = new List<IndexScanProgress>();
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        producer.Produce(created.Writer, new SynchronousProgress<IndexScanProgress>(observed.Add),
            CancellationToken.None);

        Assert.AreEqual(7u, created.Writer.RowCount);
        Assert.IsTrue(observed.Count > 0, "the walk visited at least one directory");
        Assert.AreEqual(IndexScanPhase.Enumerating, observed[0].Phase);
        Assert.AreEqual('T', observed[0].Drive);
        CollectionAssert.AllItemsAreUnique(observed.Select(report => report.CurrentDirectory!).ToArray());
        Assert.IsTrue(observed[^1].WrittenRowCount > observed[0].WrittenRowCount,
            "later reports reflect more rows written as the walk progresses");
        Assert.AreEqual(7u, observed[^1].WrittenRowCount);
    }

    [TestMethod]
    public void Produce_ExhaustedSlotCapacity_SetsCapacityExhaustedAndDoesNotThrow()
    {
        var created = CreateBlock(slotCapacity: 3);
        using var block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });
        var progress = new RecordingProgress();

        var result = producer.Produce(created.Writer, progress, CancellationToken.None);

        Assert.IsTrue(result.CapacityExhausted);
        Assert.IsTrue(created.Writer.CapacityExhausted);

        // Root (Documents, Pictures both fit in the 3-row capacity) is always walked, then
        // exactly one of Documents or Pictures fails to write its first entry and trips
        // capacity exhausted. The walk must stop there instead of also opening the other sibling
        // directory, so at most two directories are ever visited.
        Assert.AreEqual(2, progress.Directories.Count);
    }

    [TestMethod]
    public async Task Produce_DirectorySymbolicLink_RecordsRowButDoesNotFollowIt()
    {
        var linkPath = Path.Combine(_treeRoot, "RootLink");
        try
        {
            Directory.CreateSymbolicLink(linkPath, _treeRoot);
        }
        catch (Exception exception)
        {
            Assert.Inconclusive($"Could not create a directory symbolic link: {exception.Message}");
        }

        var snapshot = Produce(out var block, out var result);
        try
        {
            // Root, Documents, Projects, Pictures, readme.md, report.pdf, holiday.jpg, RootLink.
            Assert.AreEqual(8u, result.RowCount);
            Assert.AreEqual(8u, block.Header.RowCount);
            Assert.IsFalse(result.CapacityExhausted);

            var link = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, "RootLink"));
            Assert.IsTrue(link.HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Produce_IndexesHiddenAndSystemEntries()
    {
        // Hidden on Windows is an attribute bit; on Linux and macOS it is a leading-dot naming
        // convention. Giving the file both makes the assertion meaningful on every platform.
        const string hiddenName = ".hidden.txt";
        var hiddenPath = Path.Combine(_treeRoot, hiddenName);
        await File.WriteAllTextAsync(hiddenPath, "secret");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
        }

        var snapshot = Produce(out _, out _);
        try
        {
            var entry = EnumerationLookupTestAccess.FindByPath(snapshot, Path.Combine(_treeRoot, hiddenName));
            Assert.IsTrue(entry.HasValue);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Produce_MissingRootDirectory_CountsAnAccessDeniedSubtreeAndDoesNotThrow()
    {
        var created = CreateBlock();
        using var block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = Path.Combine(_treeRoot, "does-not-exist"),
            Drive = 'T'
        });

        var result = producer.Produce(created.Writer, progress: null, CancellationToken.None);

        Assert.AreEqual(1u, result.RowCount);
        Assert.AreEqual(1, result.AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public void Produce_HonoursCancellation()
    {
        var created = CreateBlock();
        using var block = created.Block;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = _treeRoot,
            Drive = 'T'
        });

        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsException<OperationCanceledException>(
            () => producer.Produce(created.Writer, progress: null, cancellation.Token));
    }

    [TestMethod]
    public void EstimateRowCount_IsAtLeastTheMinimumAndDoesNotThrowOnAMissingDirectory()
    {
        Assert.IsTrue(EnumerationProducer.EstimateRowCount(_treeRoot) > 0);
        Assert.IsTrue(EnumerationProducer.EstimateRowCount(Path.Combine(_treeRoot, "nope")) > 0);
    }

    [TestMethod]
    public void EstimateNamePoolBytes_ScalesWithRowCount()
    {
        Assert.IsTrue(EnumerationProducer.EstimateNamePoolBytes(1000) >
            EnumerationProducer.EstimateNamePoolBytes(100));
    }
}

/// <summary>
///     A synchronous <see cref="IProgress{T}" /> that records every report immediately, unlike
///     <see cref="Progress{T}" />, which posts through a synchronization context and so cannot
///     be asserted on deterministically right after the call that triggered it.
/// </summary>
sealed class RecordingProgress : IProgress<IndexScanProgress>
{
    public List<string?> Directories { get; } = [];

    public void Report(IndexScanProgress value)
    {
        Directories.Add(value.CurrentDirectory);
    }
}

/// <summary>
///     Resolves an entry by full path without touching the lookup engine, which another task in
///     this same wave creates. Path building comes from an earlier wave, so this test file
///     depends only on committed work.
/// </summary>
static class EnumerationLookupTestAccess
{
    public static FileEntry? FindByPath(DriveBlock[] snapshot, string fullPath)
    {
        var block = snapshot[0].Block;
        for (var rowIndex = 0u; rowIndex < block.Header.RowCount; rowIndex++)
        {
            var entry = FileEntry.Create(snapshot[0], rowIndex);
            if (string.Equals(entry.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
