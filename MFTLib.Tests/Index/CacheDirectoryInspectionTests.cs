using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CacheDirectoryInspectionTests : IDisposable
{
    const uint VolumeSerial = 0x0BADF00D;
    TempDirectory _directoryOwner = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-inspect");
        _cacheDirectory = _directoryOwner.Path;
    }

    [TestCleanup]
    public void Cleanup() => _directoryOwner.Dispose();

    public void Dispose() => Cleanup();

    static void AssertReleased(string path)
    {
        using var owner = BlockOwnerLock.TryAcquire(path);
        Assert.IsNotNull(owner, "inspection must release the slot");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0, exclusive.Length, "inspection must release the mapped block handle");
    }

    [TestMethod]
    public void InspectCached_HeldCorruptBlockIsInUseUntilReleased()
    {
        var path = TestBlocks.WriteCache(_cacheDirectory, 'T', mutate: block => block.Header.NamePoolUsed = 0);
        var before = File.ReadAllBytes(path);
        using (new FileStream(BlockOwnerLock.LockPathFor(path),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = CacheDirectory.InspectCached(_cacheDirectory).Single();
            Assert.AreEqual(path, busy.File.Path);
            Assert.AreEqual(CachedBlockAvailability.InUse, busy.Availability);
            Assert.IsNull(busy.Validation);
            Assert.IsNull(busy.ProducerKind);
            Assert.IsNull(busy.RootDirectory);
        }

        AssertReleased(path);
        var invalid = CacheDirectory.InspectCached(_cacheDirectory).Single();
        Assert.AreEqual(CachedBlockAvailability.Invalid, invalid.Availability);
        Assert.AreEqual(BlockValidationResult.InvalidNameDescriptor,
            invalid.Validation);
        Assert.IsNull(invalid.ProducerKind);
        Assert.IsNull(invalid.RootDirectory);
        AssertReleased(path);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(path)));
    }

    [TestMethod]
    public void InspectCached_FilterPrecedesLockCreation()
    {
        var included = TestBlocks.WriteCache(_cacheDirectory, 'C');
        var excluded = TestBlocks.WriteCache(_cacheDirectory, 'D');
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(included)));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));

        var result = CacheDirectory.InspectCached(_cacheDirectory, new HashSet<char> { 'C' }.Select(letter => (RootKey)new VolumeKey(letter)));

        Assert.AreEqual(included, result.Single().File.Path);
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(included)));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        Assert.IsEmpty(CacheDirectory.InspectCached(_cacheDirectory, new HashSet<char>().Select(letter => (RootKey)new VolumeKey(letter))));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        AssertReleased(included);
    }

    [TestMethod]
    public void InspectCached_AvailableRootsAreMaterializedAndMappingsReleased()
    {
        var enumeration = TestBlocks.WriteCache(_cacheDirectory, 'C');
        var mft = TestBlocks.WriteCache(_cacheDirectory, 'D', producer: ProducerKind.Mft);
        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.Drive).ToArray();

        Assert.HasCount(2, statuses);
        Assert.AreEqual(_cacheDirectory, statuses[0].RootDirectory);
        Assert.AreEqual("D:\\", statuses[1].RootDirectory);
        Assert.AreEqual(ProducerKind.Enumeration, statuses[0].ProducerKind);
        Assert.AreEqual(ProducerKind.Mft, statuses[1].ProducerKind);
        foreach (var status in statuses)
        {
            Assert.AreEqual(CachedBlockAvailability.Available, status.Availability);
            Assert.IsNull(status.Validation);
            Assert.AreEqual(VolumeSerial, status.File.VolumeSerial);
            Assert.AreEqual(new FileInfo(status.File.Path).Length, status.File.Size);
            AssertReleased(status.File.Path);
        }
        File.Delete(enumeration);
        File.Delete(mft);
        Assert.AreEqual(_cacheDirectory, statuses[0].RootDirectory);
        Assert.AreEqual("D:\\", statuses[1].RootDirectory);
    }

    [TestMethod]
    public void InspectCached_UnopenableLockIsInUseAndOtherFilesAreInspected()
    {
        var unavailable = TestBlocks.WriteCache(_cacheDirectory, 'C');
        Directory.CreateDirectory(BlockOwnerLock.LockPathFor(unavailable));
        var available = TestBlocks.WriteCache(_cacheDirectory, 'D');

        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.Drive).ToArray();

        Assert.HasCount(2, statuses);
        Assert.AreEqual(CachedBlockAvailability.InUse, statuses[0].Availability);
        Assert.IsNull(statuses[0].Validation);
        Assert.IsNull(statuses[0].ProducerKind);
        Assert.IsNull(statuses[0].RootDirectory);
        Assert.AreEqual(CachedBlockAvailability.Available, statuses[1].Availability);
        AssertReleased(available);
    }

    [TestMethod]
    public void InspectCached_RejectionReasonsArePreservedAndListingContinues()
    {
        TestBlocks.WriteCache(_cacheDirectory, 'C', mutate: block => block.Header.VolumeSerial++);
        TestBlocks.WriteCache(_cacheDirectory, 'D', mutate: block => block.Header.RowCount = 0);
        var shortPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName(new VolumeKey('E'), VolumeSerial));
        File.WriteAllText(shortPath, "short");
        TestBlocks.WriteCache(_cacheDirectory, 'F');

        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.Drive).ToArray();

        Assert.HasCount(4, statuses);
        var reasons = new[] { BlockValidationResult.WrongVolumeSerial,
            BlockValidationResult.InconsistentRegions, BlockValidationResult.WrongMagic };
        for (var index = 0; index < reasons.Length; index++)
        {
            Assert.AreEqual(CachedBlockAvailability.Invalid, statuses[index].Availability);
            Assert.AreEqual(reasons[index], statuses[index].Validation);
            Assert.IsNull(statuses[index].ProducerKind);
            Assert.IsNull(statuses[index].RootDirectory);
            AssertReleased(statuses[index].File.Path);
        }
        Assert.AreEqual(CachedBlockAvailability.Available, statuses[3].Availability);
    }

    [TestMethod]
    public void InspectCached_UnrecognizedProducerHasNoRoot()
    {
        var path = TestBlocks.WriteCache(_cacheDirectory, 'C', producer: (ProducerKind)99);
        var result = CacheDirectory.InspectCached(_cacheDirectory).Single();
        Assert.AreEqual(CachedBlockAvailability.Available, result.Availability);
        Assert.IsNull(result.Validation);
        Assert.AreEqual((ProducerKind)99, result.ProducerKind);
        Assert.IsNull(result.RootDirectory);
        AssertReleased(path);
    }

    [TestMethod]
    public void InspectCached_MissingEmptyAndUnrecognizedListingsAreEmpty()
    {
        Assert.IsEmpty(CacheDirectory.InspectCached(Path.Combine(_cacheDirectory, "missing")));
        Assert.IsEmpty(CacheDirectory.InspectCached(_cacheDirectory));
        var unrelated = Path.Combine(_cacheDirectory, "notes.mlix");
        File.WriteAllText(unrelated, "not a cache name");
        Assert.IsEmpty(CacheDirectory.InspectCached(_cacheDirectory));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(unrelated)));
    }

    [TestMethod]
    public void InspectCached_RejectsNullAndEmptyPaths()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CacheDirectory.InspectCached(null!));
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.InspectCached(string.Empty));
    }
}
