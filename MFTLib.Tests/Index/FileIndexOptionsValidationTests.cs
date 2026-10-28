using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What <see cref="FileIndex.OpenAsync" /> refuses before any side effect: a missing or default root, an undefined
///     producer selection, a dump asked to walk. Every rejection leaves the cache directory uncreated and calls no producer.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexOptionsValidationTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    [TestMethod]
    public async Task NullRoot_IsRejected()
    {
        var failure = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(options => options with { Root = null });

        Assert.AreEqual("options", failure.ParamName);
        Assert.Contains("FileIndexOptions.Root", failure.Message);
    }

    [TestMethod]
    public async Task DefaultRootKey_IsRejected()
    {
        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(options => options with { Root = default(RootKey) });
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(4u)]
    [DataRow(uint.MaxValue)]
    public async Task UndefinedProducerSelection_IsRejected(uint value)
    {
        Directory.CreateDirectory(TreeRoot);

        await AssertInvalidOpenCreatesNothingAsync<ArgumentOutOfRangeException>(
            options => options with { Root = new VolumeKey('T'), Producer = (ProducerSelection)value });
    }

    [TestMethod]
    public async Task Dump_WithWalkOnly_IsRejected()
    {
        var dumpFile = MftDumpFixture.WriteFile(TreeRoot, MftDumpFixture.Standard());
        var options = new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(dumpFile, 'D'),
            Producer = ProducerSelection.WalkOnly
        };

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileIndex.OpenAsync(options, TestContext.CancellationToken));

        Assert.Contains("Producer", failure.Message);
        Assert.Contains("WalkOnly", failure.Message);
    }

    [TestMethod]
    [DataRow(ProducerSelection.Automatic)]
    [DataRow(ProducerSelection.MftOnly)]
    public async Task Dump_WithAnMftSelection_Opens(ProducerSelection selection)
    {
        var dumpFile = MftDumpFixture.WriteFile(TreeRoot, MftDumpFixture.Standard());
        var options = new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(dumpFile, 'D'),
            Producer = selection
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(RootState.Ready, index.Status.State, index.Status.Failure?.Message);
        Assert.AreEqual(ProducerKind.Mft, index.Status.Producer);
        Assert.IsNull(index.Status.MftUnavailable);
    }

    [TestMethod]
    public void Apply_RejectsAVolumeWithoutItsRootDirectory()
    {
        var options = new FileIndexOptions();

        Assert.ThrowsExactly<ArgumentException>(() => SyntheticVolumeFacts.Apply(options, 1, volume: new VolumeKey('T')));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticVolumeFacts.Apply(options, 1, volume: default(VolumeKey), volumeRootDirectory: TreeRoot));
        Assert.ThrowsExactly<ArgumentException>(() => SyntheticVolumeFacts.Apply(options, 1, volumeRootDirectory: TreeRoot));
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntheticVolumeFacts.Apply(null!, 1));
    }

    [TestMethod]
    public async Task InvalidConfiguration_LeavesAnExistingOwnedCacheDirectoryUntouched()
    {
        Directory.CreateDirectory(CacheDirectoryPath);
        var existingFile = Path.Combine(CacheDirectoryPath, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "keep", TestContext.CancellationToken);
        var contentsBefore = SnapshotDirectory(CacheDirectoryPath);
        var producerCalls = 0;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = CacheDirectoryPath,
            Root = null,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(new InvalidOperationException("producer must not run"));
            })
        }, CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreSequenceEqual(contentsBefore, SnapshotDirectory(CacheDirectoryPath), "no block slot, lock file or other entry may appear or vanish");
        Assert.AreEqual("keep", await File.ReadAllTextAsync(existingFile, TestContext.CancellationToken));
    }

    static string[] SnapshotDirectory(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => $"{Path.GetRelativePath(directory, entry)}|{(File.Exists(entry) ? new FileInfo(entry).Length : -1)}")
            .Order(StringComparer.Ordinal)];

    async Task<TException> AssertInvalidOpenCreatesNothingAsync<TException>(
        Func<FileIndexOptions, FileIndexOptions> configure)
        where TException : Exception
    {
        var producerCalls = 0;
        var scanProgressCalls = 0;
        var baseOptions = new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(
                    new InvalidOperationException("producer must not run"));
            }),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(_ => Interlocked.Increment(ref scanProgressCalls))
        };

        var exception = await Assert.ThrowsExactlyAsync<TException>(
            () => FileIndex.OpenAsync(configure(baseOptions), CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreEqual(0, scanProgressCalls);
        Assert.IsFalse(Directory.Exists(CacheDirectoryPath));
        return exception;
    }
}
