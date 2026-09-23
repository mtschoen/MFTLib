using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class FileIndexCacheTagTests
{
    static readonly CacheTag OriginalTag = new("GITW", 1);
    static readonly DateTime Moment = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
    string _directory = null!;
    string Root => Path.Combine(_directory, "root");
    string Cache => Path.Combine(_directory, "cache");
    string BlockPath => Path.Combine(Cache, CacheDirectory.BlockFileName('T', 123));

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-tag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        JournalCheckpointCheck._journalOverride = _ => new JournalWindow(7, 100, 8192, 4096, 65536);
    }

    [TestCleanup]
    public void Cleanup()
    {
        JournalCheckpointCheck._journalOverride = null;
        Directory.Delete(_directory, recursive: true);
    }

    FileIndexOptions Options(CacheTag tag, bool cacheOnly = false) => new()
    {
        Drives = [new IndexedDrive('T', Root, 123)],
        CacheDirectory = Cache,
        CacheTag = tag,
        InitialOpenCacheOnly = cacheOnly,
        ProducerPolicy = ProducerPolicy.Mft,
        MftProducer = Produce
    };

    static BlockFile Build(MftBlockProduceRequest request)
    {
        var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 16,
            NamePoolCapacity = 1024,
            DeleteOnClose = request.DeleteOnClose,
            CacheTag = request.CacheTag
        });
        try
        {
            var writer = new BlockWriter(block);
            Assert.IsTrue(writer.TryWriteRow(5, ".",
                new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1)));
            writer.SetJournalCursor(7, 4096);
            writer.Complete(Moment);
            return block;
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MftBlockProduceResult(Build(request), 7, 4096, 0, false));
    }

    void Seed(CacheTag tag)
    {
        using var block = Build(new MftBlockProduceRequest
        {
            DriveLetter = 'T',
            VolumeSerial = 123,
            BlockPath = BlockPath,
            CacheTag = tag
        });
    }

    [TestMethod]
    [DataRow(ProducerPolicy.Enumeration, false)]
    [DataRow(ProducerPolicy.Mft, false)]
    [DataRow(ProducerPolicy.Enumeration, true)]
    [DataRow(ProducerPolicy.Mft, true)]
    public async Task CreationAndRescanKeepTheRequestedTag(ProducerPolicy policy, bool noCache)
    {
        var options = Options(OriginalTag) with { ProducerPolicy = policy, NoCache = noCache };
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
            Assert.AreEqual(OriginalTag, index.CurrentSnapshot.DriveBlocks.Single().Block.Header.CacheTag);
            await index.RescanAsync('T', CancellationToken.None);
            Assert.AreEqual(OriginalTag, index.CurrentSnapshot.DriveBlocks.Single().Block.Header.CacheTag);
        }
        if (noCache)
        {
            Assert.IsFalse(File.Exists(BlockPath));
        }
        else
        {
            Assert.AreEqual(OriginalTag, CacheDirectory.InspectCached(Cache).Single().CacheTag);
        }
    }

    [TestMethod]
    public async Task ContradictoryProducerTagFailsAndCannotBeWarmStarted()
    {
        var options = Options(OriginalTag) with
        {
            MftProducer = (request, token) => Produce(request with { CacheTag = default }, token)
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
        StringAssert.Contains(status.MftProducerFailureMessage!, "cache tag");
        Assert.IsFalse(File.Exists(BlockPath));
    }

    [TestMethod]
    public void InvalidTagIsRejectedWhileConstructingOptions()
    {
        var cache = Path.Combine(_directory, "invalid-options-cache");
        Assert.ThrowsException<ArgumentException>(() => new FileIndexOptions
        {
            CacheDirectory = cache,
            CacheTag = new CacheTag("GIT", 1)
        });
        Assert.IsFalse(Directory.Exists(cache));
    }

    [TestMethod]
    [DataRow("GITW", 1u)]
    [DataRow("\0\0\0\0", 0u)]
    public async Task MatchingTagWarmStartsWithoutProducing(string code, uint version)
    {
        var tag = new CacheTag(code, version);
        Seed(tag);
        var options = Options(tag) with
        {
            MftProducer = (_, _) => throw new AssertFailedException("A matching cache must not scan.")
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, index.Drives.Single().BlockSource);
        Assert.IsNull(index.Drives.Single().DiscardedBlock);
        Assert.IsNull(index.Drives.Single().CheckpointLoss);
    }

    [TestMethod]
    [DataRow("GITW", 1u, "GITW", 2u, false)]
    [DataRow("GITW", 1u, "FILE", 1u, false)]
    [DataRow("GITW", 1u, "GITW", 2u, true)]
    [DataRow("GITW", 1u, "FILE", 1u, true)]
    [DataRow("\0\0\0\0", 0u, "GITW", 1u, false)]
    [DataRow("GITW", 1u, "\0\0\0\0", 0u, false)]
    [DataRow("\0\0\0\0", 0u, "GITW", 1u, true)]
    [DataRow("GITW", 1u, "\0\0\0\0", 0u, true)]
    public async Task MismatchRescansOrDeclinesAndReportsBothTags(
        string storedCode, uint storedVersion, string requestedCode, uint requestedVersion, bool cacheOnly)
    {
        var stored = new CacheTag(storedCode, storedVersion);
        var requested = new CacheTag(requestedCode, requestedVersion);
        Seed(stored);
        var diagnostics = new List<string>();
        var options = Options(requested, cacheOnly) with { Diagnostics = diagnostics.Add };
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            var status = index.Drives.Single();
            Assert.AreEqual(BlockValidationResult.WrongCacheTag, status.DiscardedBlock);
            Assert.IsTrue(diagnostics.Any(line => line.Contains("Cache tag mismatch") &&
                line.Contains(stored.ToString()) && line.Contains(requested.ToString())));
            if (cacheOnly)
            {
                Assert.AreEqual(DriveState.Failed, status.State);
                Assert.AreEqual(DriveFailureKind.CacheTagMismatch, status.FailureKind);
                Assert.AreEqual(BlockSource.None, status.BlockSource);
                Assert.IsFalse(File.Exists(BlockPath));
                await index.RescanAsync('T', CancellationToken.None);
                Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
                Assert.AreEqual(DriveFailureKind.None, index.Drives.Single().FailureKind);
                Assert.IsNull(index.Drives.Single().DiscardedBlock);
            }
            else
            {
                Assert.AreEqual(DriveState.Ready, status.State);
                Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
            }
        }
        Assert.AreEqual(requested, CacheDirectory.InspectCached(Cache).Single().CacheTag);
        await using var reopened = await FileIndex.OpenAsync(Options(requested, true), CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task VersionTwoColdScansOnceAndThenWarmStarts()
    {
        Seed(OriginalTag);
        using (var block = BlockFile.Open(BlockPath, 123, out _))
        {
            Assert.IsNotNull(block);
            block.Header.FormatVersion = 2;
            block.Flush();
        }
        await using (var index = await FileIndex.OpenAsync(Options(OriginalTag), CancellationToken.None))
        {
            Assert.AreEqual(BlockValidationResult.WrongFormatVersion, index.Drives.Single().DiscardedBlock);
            Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
        }
        await using var reopened = await FileIndex.OpenAsync(Options(OriginalTag), CancellationToken.None);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);
    }

    [TestMethod]
    public async Task ForeignOwnerPreventsTagValidationAndPrivateScanKeepsItsOwnTag()
    {
        Seed(OriginalTag);
        using var owner = BlockOwnerLock.TryAcquire(BlockPath);
        Assert.IsNotNull(owner);
        var requested = new CacheTag("FILE", 1);
        var diagnostics = new List<string>();
        await using (var declined = await FileIndex.OpenAsync(
            Options(requested, true) with { Diagnostics = diagnostics.Add }, CancellationToken.None))
        {
            Assert.AreEqual(DriveFailureKind.InUse, declined.Drives.Single().FailureKind);
            Assert.IsNull(declined.Drives.Single().DiscardedBlock);
        }
        await using (var privateIndex = await FileIndex.OpenAsync(
            Options(requested) with { Diagnostics = diagnostics.Add }, CancellationToken.None))
        {
            Assert.AreEqual(BlockSource.ProducedByScan, privateIndex.Drives.Single().BlockSource);
            Assert.AreEqual(requested, privateIndex.CurrentSnapshot.DriveBlocks.Single().Block.Header.CacheTag);
        }
        Assert.IsFalse(diagnostics.Any(line => line.Contains("Cache tag mismatch")));
        using var original = BlockFile.Open(BlockPath, 123, out _);
        Assert.IsNotNull(original);
        Assert.AreEqual(OriginalTag, original.Header.CacheTag);
    }
}
