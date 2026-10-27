using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class FileIndexCacheTagTests : IDisposable
{
    static readonly CacheTag OriginalTag = new("GITW", 1);
    static readonly DateTime Moment = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
    TempDirectory _directoryOwner = null!;
    string _directory = null!;
    IDisposable? _journalScope;
    string Root => Path.Combine(_directory, "root");
    string Cache => Path.Combine(_directory, "cache");
    string BlockPath => Path.Combine(Cache, CacheDirectory.BlockFileName(new VolumeKey('T'), 123));

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-index-tag");
        _directory = _directoryOwner.Path;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        _journalScope = JournalIsolation.OverrideJournalWindow(
            _ => new SyntheticJournalWindow(7, 100, 4096, 4096, 65536));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _journalScope?.Dispose();
        _journalScope = null;
        _directoryOwner.Dispose();
    }

    public void Dispose() => Cleanup();

    FileIndexOptions Options(CacheTag tag, bool cacheOnly = false) => new()
    {
        Drives = [SyntheticIndexedDrive.Create(new VolumeKey('T'), Root, 123)],
        CacheDirectory = Cache,
        CacheTag = tag,
        CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent,
        MftSource = new MftIndexSource(Produce)
    };

    static BlockFile Build(MftBlockProduceRequest request)
    {
        var block = TestBlocks.Create(request.BlockPath, ProducerKind.Mft, 16, 1024,
            volumeSerial: request.VolumeSerial, rootRow: 5,
            deleteOnClose: request.DeleteOnClose, cacheTag: request.CacheTag,
            scanShape: request.ScanShape);
        try
        {
            var writer = new BlockWriter(block);
            Assert.IsTrue(writer.TryWriteRow(5, ".",
                new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1)));
            writer.SetJournalCursor(7, 4096);
            writer.Complete(Moment, null);
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
        return Task.FromResult(new MftBlockProduceResult(Build(request), 0));
    }

    void Seed(CacheTag tag)
    {
        using var block = Build(new MftBlockProduceRequest
        {
            Drive = 'T',
            VolumeSerial = 123,
            BlockPath = BlockPath,
            CacheTag = tag,
            ScanShape = ScanShape.ForMft(null, includeFreed: false)
        });
    }

    [TestMethod]
    [DataRow(ProducerKind.Enumeration, false)]
    [DataRow(ProducerKind.Mft, false)]
    [DataRow(ProducerKind.Enumeration, true)]
    [DataRow(ProducerKind.Mft, true)]
    public async Task CreationAndRescanKeepTheRequestedTag(ProducerKind producerKind, bool cacheDisabled)
    {
        var options = Options(OriginalTag) with
        {
            Drives = [SyntheticIndexedDrive.Create(new VolumeKey('T'), Root, 123, producerKind)],
            CacheMode = cacheDisabled ? CacheMode.Disabled : CacheMode.Persistent
        };
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
            Assert.AreEqual(OriginalTag, index.CurrentSnapshot.Single().Block.Header.CacheTag);
            await index.RescanAsync([new VolumeKey('T')], CancellationToken.None);
            Assert.AreEqual(OriginalTag, index.CurrentSnapshot.Single().Block.Header.CacheTag);
        }
        if (cacheDisabled)
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
            MftSource = new MftIndexSource((request, token) => Produce(request with { CacheTag = default }, token))
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.Contains("cache tag", status.Failure?.Message!);
        Assert.IsFalse(File.Exists(BlockPath));
    }

    [TestMethod]
    public void InvalidTagIsRejectedWhileConstructingOptions()
    {
        var cache = Path.Combine(_directory, "invalid-options-cache");
        Assert.ThrowsExactly<ArgumentException>(() => new FileIndexOptions
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
            MftSource = new MftIndexSource((_, _) => throw new AssertFailedException("A matching cache must not scan."))
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, index.Drives.Single().Block!.Origin);
        Assert.IsNull(index.Drives.Single().Watch.CheckpointLoss);
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
            Assert.Contains(line => line.Contains("cache validation failed: WrongCacheTag"), diagnostics,
                "the rejected block's deletion or refusal names the reason");
            Assert.Contains(line => line.Contains("Cache tag mismatch") &&
                line.Contains(stored.ToString()) && line.Contains(requested.ToString()), diagnostics);
            if (cacheOnly)
            {
                Assert.AreEqual(DriveState.Failed, status.State);
                Assert.AreEqual(DriveFailureKind.CacheIdentityMismatch, status.Failure?.Kind);
                Assert.IsNull(status.Block);
                using (var left = BlockFile.Open(BlockPath, 123, out _))
                {
                    Assert.AreEqual(stored, left?.Header.CacheTag, "a cache-only open leaves the rejected block in place");
                }
                await index.RescanAsync([new VolumeKey('T')], CancellationToken.None);
                Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
                Assert.IsNull(index.Drives.Single().Failure);
            }
            else
            {
                Assert.AreEqual(DriveState.Ready, status.State);
                Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
            }
        }
        Assert.AreEqual(requested, CacheDirectory.InspectCached(Cache).Single().CacheTag);
        await using var reopened = await FileIndex.OpenAsync(Options(requested, true), CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block!.Origin);
    }

    [TestMethod]
    public async Task VersionTwoColdScansOnceAndThenWarmStarts()
    {
        Seed(OriginalTag);
        using (var block = BlockFile.Open(BlockPath, 123, out _))
        {
            Assert.IsNotNull(block);
            block.Header.FormatVersion = 2;
            block.Flush(null);
        }
        var diagnostics = new List<string>();
        await using (var index = await FileIndex.OpenAsync(
            Options(OriginalTag) with { Diagnostics = diagnostics.Add }, CancellationToken.None))
        {
            Assert.Contains(line => line.Contains("cache validation failed: WrongFormatVersion"), diagnostics,
                "the rejected block's deletion names the reason");
            Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives.Single().Block!.Origin);
        }
        await using var reopened = await FileIndex.OpenAsync(Options(OriginalTag), CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives.Single().Block!.Origin);
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
            Assert.AreEqual(DriveFailureKind.InUse, declined.Drives.Single().Failure?.Kind);
        }
        await using (var privateIndex = await FileIndex.OpenAsync(
            Options(requested) with { Diagnostics = diagnostics.Add }, CancellationToken.None))
        {
            Assert.AreEqual(BlockOrigin.ProducedByScan, privateIndex.Drives.Single().Block!.Origin);
            Assert.AreEqual(requested, privateIndex.CurrentSnapshot.Single().Block.Header.CacheTag);
        }
        Assert.DoesNotContain(line => line.Contains("Cache tag mismatch"), diagnostics);
        using var original = BlockFile.Open(BlockPath, 123, out _);
        Assert.IsNotNull(original);
        Assert.AreEqual(OriginalTag, original.Header.CacheTag);
    }
}
