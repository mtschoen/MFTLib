using MFTLib.Index;
using MFTLib.Tests.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

[TestClass]
public class DirectDuplicateNameSieveTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    const int UniqueNameCount = 20_000;

    static async Task<FileIndex> OpenIndexAsync(params SyntheticBlockBuilder[] builders)
    {
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = builders.Select(builder => new IndexedDrive(builder.DriveLetter, builder.DirectoryPath,
                builder.VolumeSerial)).ToArray(),
            CacheDirectory = builders[0].DirectoryPath,
            InitialOpenCacheOnly = true,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(DriveState.Ready, drive.State, drive.FailureMessage);
        }

        return index;
    }

    static uint AddRoot(SyntheticBlockBuilder builder) => builder.AddRoot(builder.DirectoryPath);

    static void AddFile(SyntheticBlockBuilder builder, uint root, string name, long size,
        RowFlags flags = RowFlags.InUse)
    {
        builder.AddRow(name, root, flags, size, Moment, sequenceNumber: 0);
    }

    [TestMethod]
    public async Task Find_RepeatedNamesIncludeEveryMemberAndIgnoreCase()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = AddRoot(builder);
        AddFile(builder, root, "readme.txt", 1);
        AddFile(builder, root, "README.TXT", 2);
        AddFile(builder, root, "readme.txt", 3);
        AddFile(builder, root, "unique.txt", 4);
        builder.Complete(Moment);
        await using var index = await OpenIndexAsync(builder);
        var groups = DuplicateNameSieve.Find(index, CancellationToken.None);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("readme.txt", groups[0].Name);
        CollectionAssert.AreEquivalent(new long[] { 1, 2, 3 }, groups[0].Entries.Select(entry => entry.Size).ToArray());
    }

    [TestMethod]
    public async Task Find_DirectoriesDeletedFreedAndEmptyNamesNeverGroup()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = AddRoot(builder);
        foreach (var flags in new[] { RowFlags.InUse | RowFlags.Directory, RowFlags.InUse | RowFlags.Tombstone, RowFlags.None })
        {
            AddFile(builder, root, flags.ToString(), 1, flags);
            AddFile(builder, root, flags.ToString(), 2, flags);
        }

        AddFile(builder, root, string.Empty, 1);
        AddFile(builder, root, string.Empty, 2);
        AddFile(builder, root, "live.txt", 1);
        AddFile(builder, root, "live.txt", 2, RowFlags.InUse | RowFlags.Tombstone);
        builder.Complete(Moment);
        await using var index = await OpenIndexAsync(builder);
        Assert.AreEqual(0, DuplicateNameSieve.Find(index, CancellationToken.None).Count);
    }

    [TestMethod]
    public async Task Find_TwentyThousandUniqueNamesProduceNoGroups()
    {
        using var builder = BuildLargeBlock(includeDuplicates: false);
        await using var index = await OpenIndexAsync(builder);
        Assert.AreEqual(0, DuplicateNameSieve.Find(index, CancellationToken.None, SieveBitmap.MinimumBucketCount).Count);
    }

    [DataTestMethod]
    [DataRow(64)]
    [DataRow(1 << 16)]
    public async Task Find_TrueDuplicatesSurviveEveryRefinementPassUnderCollisions(int bucketCount)
    {
        using var builder = BuildLargeBlock(includeDuplicates: true);
        await using var index = await OpenIndexAsync(builder);
        var groups = DuplicateNameSieve.Find(index, CancellationToken.None, bucketCount);
        Assert.AreEqual(2, groups.Count);
        var byName = groups.ToDictionary(group => group.Name);
        CollectionAssert.AreEquivalent(new long[] { 1, 2 }, byName["dup-a.bin"].Entries.Select(entry => entry.Size).ToArray());
        CollectionAssert.AreEquivalent(new long[] { 3, 4, 5 }, byName["dup-b.bin"].Entries.Select(entry => entry.Size).ToArray());
    }

    static SyntheticBlockBuilder BuildLargeBlock(bool includeDuplicates)
    {
        var builder = new SyntheticBlockBuilder('U', slotCapacity: UniqueNameCount + 20,
            namePoolCapacity: (uint)UniqueNameCount * 40);
        var root = AddRoot(builder);
        for (var row = 0; row < UniqueNameCount; row++)
        {
            AddFile(builder, root, $"unique-{row}.bin", row);
        }

        if (includeDuplicates)
        {
            AddFile(builder, root, "dup-a.bin", 1);
            AddFile(builder, root, "dup-a.bin", 2);
            AddFile(builder, root, "dup-b.bin", 3);
            AddFile(builder, root, "dup-b.bin", 4);
            AddFile(builder, root, "dup-b.bin", 5);
        }

        builder.Complete(Moment);
        return builder;
    }

    [TestMethod]
    public async Task Find_NameAcrossTwoDrivesIsOneGroupInEnumerationOrder()
    {
        using var first = new SyntheticBlockBuilder('Z');
        using var second = new SyntheticBlockBuilder('A');
        AddFile(first, AddRoot(first), "shared.txt", 1);
        AddFile(second, AddRoot(second), "SHARED.TXT", 2);
        first.Complete(Moment);
        second.Complete(Moment);
        // Cache-only opens use one directory for all drives.
        File.Copy(second.BlockPath, Path.Combine(first.DirectoryPath, Path.GetFileName(second.BlockPath)));
        await using var index = await OpenIndexAsync(first, second);
        var groups = DuplicateNameSieve.Find(index, CancellationToken.None, 64);
        Assert.AreEqual(1, groups.Count);
        var expected = index.Enumerate(new SearchQuery(null, Directories: false)).Select(entry => entry.RecordKey).ToArray();
        CollectionAssert.AreEqual(expected, groups[0].Entries.Select(entry => entry.RecordKey).ToArray());
    }

    [TestMethod]
    public async Task Find_CancelledTokenThrows()
    {
        using var builder = new SyntheticBlockBuilder();
        AddRoot(builder);
        builder.Complete(Moment);
        await using var index = await OpenIndexAsync(builder);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        AssertCancelled(index, token);
    }

    static void AssertCancelled(FileIndex index, CancellationToken token)
    {
        Assert.ThrowsException<OperationCanceledException>(() => DuplicateNameSieve.Find(index, token));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Find_CancellationDuringResultAssembly_StopsBeforeMoreWorkOrReturn(int cancelAfter)
    {
        using var builder = new SyntheticBlockBuilder();
        var root = AddRoot(builder);
        foreach (var name in new[] { "first.txt", "second.txt" })
        {
            AddFile(builder, root, name, 1);
            AddFile(builder, root, name, 2);
        }

        builder.Complete(Moment);
        await using var index = await OpenIndexAsync(builder);
        AssertAssemblyCancelled(index, cancelAfter);
    }

    static void AssertAssemblyCancelled(FileIndex index, int cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action cancel = cancellation.Cancel;
        var assembled = 0;
        Assert.ThrowsException<OperationCanceledException>(() =>
            DuplicateNameSieve.Find(index, token, resultAssembledForTest: completed =>
            {
                assembled = completed;
                if (completed == cancelAfter)
                {
                    cancel();
                }
            }));
        Assert.AreEqual(cancelAfter, assembled);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    [TestMethod]
    public void MayRepeat_IsFalseBeforeRecordingAndAfterOneAndTrueAfterTwo()
    {
        var bitmap = new SieveBitmap(SieveBitmap.ComputeBucketCount(1000), 0);
        Assert.IsFalse(bitmap.MayRepeat(12345));
        bitmap.Record(12345);
        Assert.IsFalse(bitmap.MayRepeat(12345));
        bitmap.Record(12345);
        Assert.IsTrue(bitmap.MayRepeat(12345));
        bitmap.CompletePass();
        Assert.IsTrue(bitmap.MayRepeat(12345));
    }

    [TestMethod]
    public void MayRepeat_ReturnsFalseForAHashNeverRecorded()
    {
        Assert.IsFalse(new SieveBitmap(SieveBitmap.ComputeBucketCount(8), 0).MayRepeat(999));
    }

    [TestMethod]
    public void MayRepeat_TreatsTwoDistinctHashesInTheSameBucketAsCandidates()
    {
        var bitmap = new SieveBitmap(SieveBitmap.MinimumBucketCount, 0);
        var secondHash = Enumerable.Range(2, 4_999_998)
            .First(hash => bitmap.BucketIndexFor(hash) == bitmap.BucketIndexFor(1));
        bitmap.Record(1);
        bitmap.Record(secondHash);
        Assert.IsTrue(bitmap.MayRepeat(1));
        Assert.IsTrue(bitmap.MayRepeat(secondHash));
    }

    [TestMethod]
    public void BucketIndexFor_DifferentSeedsScatterCollidingPairsIndependently()
    {
        var seedZero = new SieveBitmap(SieveBitmap.MinimumBucketCount, 0);
        var seedOne = new SieveBitmap(SieveBitmap.MinimumBucketCount, 1);
        var firstHashByBucket = new Dictionary<int, int>();
        var pairs = new List<(int First, int Second)>();
        for (var candidate = 0; candidate < 400_000 && pairs.Count < 500; candidate++)
        {
            var bucket = seedZero.BucketIndexFor(candidate);
            if (firstHashByBucket.TryGetValue(bucket, out var first))
            {
                pairs.Add((first, candidate));
            }
            else
            {
                firstHashByBucket[bucket] = candidate;
            }
        }

        Assert.IsTrue(pairs.Count >= 100);
        Assert.IsTrue(pairs.Count(pair => seedOne.BucketIndexFor(pair.First) == seedOne.BucketIndexFor(pair.Second)) < pairs.Count / 4);
    }

    [DataTestMethod]
    [DataRow(0L, 1 << 24)]
    [DataRow(-5L, 1 << 24)]
    [DataRow(1L, 1 << 16)]
    [DataRow(16384L, 1 << 16)]
    [DataRow(16385L, 1 << 17)]
    [DataRow(21_000_000L, 1 << 27)]
    [DataRow(33_554_432L, 1 << 27)]
    [DataRow(long.MaxValue, 1 << 27)]
    public void ComputeBucketCount_ClampsAndRoundsUp(long rows, int expected)
    {
        Assert.AreEqual(expected, SieveBitmap.ComputeBucketCount(rows));
    }

    [DataTestMethod]
    [DataRow(1 << 27, 32L * 1024 * 1024)]
    [DataRow(1 << 24, 4L * 1024 * 1024)]
    [DataRow(1 << 16, 16384L)]
    public void CompletePass_HalvesTheBitArrayByteCount(int bucketCount, long expectedBytes)
    {
        var bitmap = new SieveBitmap(bucketCount, 0);
        Assert.AreEqual(expectedBytes, bitmap.ByteCount);
        bitmap.CompletePass();
        Assert.AreEqual(expectedBytes / 2, bitmap.ByteCount);
    }

    [TestMethod]
    public void PeakChainMemory_AtMaximumIsEightyMebibytes()
    {
        long peakBytes = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            var bitmap = new SieveBitmap(SieveBitmap.MaximumBucketCount, pass);
            bitmap.CompletePass();
            peakBytes += bitmap.ByteCount;
        }

        peakBytes += new SieveBitmap(SieveBitmap.MaximumBucketCount, 3).ByteCount;
        Assert.AreEqual(80L * 1024 * 1024, peakBytes);
    }

    [DataTestMethod]
    [DataRow(1000)]
    [DataRow(0)]
    [DataRow(32)]
    public void Constructor_RejectsInvalidBucketCounts(int bucketCount)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SieveBitmap(bucketCount, 0));
    }

    [DataTestMethod]
    [DataRow(100L, 10_000L, 1 << 16, 1, true)]
    [DataRow(6000L, 10_000L, 1 << 20, 1, true)]
    [DataRow(40_000L, 100_000L, 1 << 20, 3, true)]
    [DataRow(10_000L, 100_000L, 1 << 20, 1, false)]
    public void ShouldStopRefining_PreservesEveryStopCondition(long candidates, long previous, int buckets,
        int pass, bool expected)
    {
        Assert.AreEqual(expected, DuplicateNameSieve.ShouldStopRefining(candidates, previous, buckets, pass));
    }
}
