using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CacheDirectoryInspectionTests
{
    const uint VolumeSerial = 0x0BADF00D;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_cacheDirectory, recursive: true);
    }

    string CreateBlock(char letter, ProducerKind producer = ProducerKind.Enumeration,
        Action<BlockFile>? mutate = null)
    {
        var path = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName(letter, VolumeSerial));
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = VolumeSerial,
            ProducerKind = producer,
            RootRow = producer == ProducerKind.Mft ? 5u : 0u,
            SlotCapacity = 8,
            NamePoolCapacity = 4096
        });
        var writer = new BlockWriter(block);
        var columns = new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0);
        Assert.IsTrue(writer.TryWriteRow(0, _cacheDirectory, columns));
        if (producer == ProducerKind.Mft)
        {
            Assert.IsTrue(writer.TryWriteRow(5, "not-the-drive-root", columns));
        }
        writer.Complete(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        mutate?.Invoke(block);
        block.Flush();
        return path;
    }

    static void AssertReleased(string path)
    {
        using var owner = BlockOwnerLock.TryAcquire(path);
        Assert.IsNotNull(owner, "inspection must release the slot");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0, "inspection must release the mapped block handle");
    }

    [TestMethod]
    public void InspectCached_HeldCorruptBlockIsInUseUntilReleased()
    {
        var path = CreateBlock('T', mutate: block => block.Header.NamePoolUsed = 0);
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
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(path)));
    }

    [TestMethod]
    public void InspectCached_FilterPrecedesLockCreation()
    {
        var included = CreateBlock('C');
        var excluded = CreateBlock('D');
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(included)));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));

        var result = CacheDirectory.InspectCached(_cacheDirectory, new HashSet<char> { 'C' });

        Assert.AreEqual(included, result.Single().File.Path);
        Assert.IsTrue(File.Exists(BlockOwnerLock.LockPathFor(included)));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        Assert.AreEqual(0, CacheDirectory.InspectCached(_cacheDirectory, new HashSet<char>()).Count);
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(excluded)));
        AssertReleased(included);
    }

    [TestMethod]
    public void InspectCached_AvailableRootsAreMaterializedAndMappingsReleased()
    {
        var enumeration = CreateBlock('C');
        var mft = CreateBlock('D', ProducerKind.Mft);
        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.DriveLetter).ToArray();

        Assert.AreEqual(2, statuses.Length);
        Assert.AreEqual(_cacheDirectory, statuses[0].RootDirectory);
        Assert.AreEqual("D:\\", statuses[1].RootDirectory);
        Assert.AreEqual(ProducerKind.Enumeration, statuses[0].ProducerKind);
        Assert.AreEqual(ProducerKind.Mft, statuses[1].ProducerKind);
        foreach (var status in statuses)
        {
            Assert.AreEqual(CachedBlockAvailability.Available, status.Availability);
            Assert.AreEqual(BlockValidationResult.Valid, status.Validation);
            Assert.AreEqual(VolumeSerial, status.File.VolumeSerial);
            Assert.AreEqual(new FileInfo(status.File.Path).Length, status.File.SizeBytes);
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
        var unavailable = CreateBlock('C');
        Directory.CreateDirectory(BlockOwnerLock.LockPathFor(unavailable));
        var available = CreateBlock('D');

        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.DriveLetter).ToArray();

        Assert.AreEqual(2, statuses.Length);
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
        CreateBlock('C', mutate: block => block.Header.VolumeSerial++);
        CreateBlock('D', mutate: block => block.Header.RowCount = 0);
        var shortPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('E', VolumeSerial));
        File.WriteAllText(shortPath, "short");
        CreateBlock('F');

        var statuses = CacheDirectory.InspectCached(_cacheDirectory)
            .OrderBy(status => status.File.DriveLetter).ToArray();

        Assert.AreEqual(4, statuses.Length);
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
        var path = CreateBlock('C', (ProducerKind)99);
        var result = CacheDirectory.InspectCached(_cacheDirectory).Single();
        Assert.AreEqual(CachedBlockAvailability.Available, result.Availability);
        Assert.AreEqual(BlockValidationResult.Valid, result.Validation);
        Assert.AreEqual((ProducerKind)99, result.ProducerKind);
        Assert.IsNull(result.RootDirectory);
        AssertReleased(path);
    }

    [TestMethod]
    public void InspectCached_MissingEmptyAndUnrecognizedListingsAreEmpty()
    {
        Assert.AreEqual(0, CacheDirectory.InspectCached(Path.Combine(_cacheDirectory, "missing")).Count);
        Assert.AreEqual(0, CacheDirectory.InspectCached(_cacheDirectory).Count);
        var unrelated = Path.Combine(_cacheDirectory, "notes.mlix");
        File.WriteAllText(unrelated, "not a cache name");
        Assert.AreEqual(0, CacheDirectory.InspectCached(_cacheDirectory).Count);
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(unrelated)));
    }

    [TestMethod]
    public void InspectCached_RejectsNullAndEmptyPaths()
    {
        Assert.ThrowsException<ArgumentNullException>(() => CacheDirectory.InspectCached(null!));
        Assert.ThrowsException<ArgumentException>(() => CacheDirectory.InspectCached(string.Empty));
    }
}
