using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CacheDirectoryDirectoryOwnershipTests : IDisposable
{
    const uint Serial = 0x0BADF00D;
    readonly TempDirectory _directoryOwner = new("mftlib-dir-ownership");

    public void Dispose() => _directoryOwner.Dispose();

    string CacheDirectoryPath => _directoryOwner.Path;

    static RootKey Folder(string name) =>
        RootKey.FromDirectory(Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "Held", name));

    string Seed(RootKey root) =>
        SyntheticBlock.WriteCached(CacheDirectoryPath, root, Serial,
            new SyntheticBlockOptions { ProducerKind = ProducerKind.Enumeration, RootRow = 0 },
            [new SyntheticRow(0, root.ToString(), 0) { IsDirectory = true }]);

    string WriteRaw(RootKey root, byte[] bytes)
    {
        var path = SyntheticBlock.CachedPath(CacheDirectoryPath, root, Serial);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public enum KeyStorage { Valid, Unreadable, NotYetInitialized }

    string Prepare(RootKey root, KeyStorage storage) => storage switch
    {
        KeyStorage.Valid => Seed(root),
        KeyStorage.Unreadable => WriteRaw(root, new byte[BlockLayout.HeaderRegionBytes]),
        _ => WriteRaw(root, [])
    };

    [TestMethod]
    [DataRow(KeyStorage.Valid)]
    [DataRow(KeyStorage.Unreadable)]
    [DataRow(KeyStorage.NotYetInitialized)]
    public void InspectCached_HeldSelectedDirectorySlot_IsInUseWhateverTheKeyStorageHolds(KeyStorage storage)
    {
        var wanted = Folder("Wanted");
        var path = Prepare(wanted, storage);
        var before = File.ReadAllBytes(path);
        using var held = SyntheticCacheSlot.Acquire(CacheDirectoryPath, wanted, Serial);
        var rejected = new List<CachedBlockRejection>();

        var status = CacheDirectory.InspectCached(CacheDirectoryPath, [wanted], rejected.Add).Single();

        Assert.AreEqual(CachedBlockAvailability.InUse, status.Availability);
        Assert.AreEqual(wanted, status.File.Drive);
        Assert.IsEmpty(rejected);
        Assert.AreSequenceEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(KeyStorage.Valid)]
    [DataRow(KeyStorage.Unreadable)]
    [DataRow(KeyStorage.NotYetInitialized)]
    public void DeleteCached_HeldSelectedDirectorySlot_IsInUseAndLeavesTheBlock(KeyStorage storage)
    {
        var wanted = Folder("Wanted");
        var path = Prepare(wanted, storage);
        using var held = SyntheticCacheSlot.Acquire(CacheDirectoryPath, wanted, Serial);
        var rejected = new List<CachedBlockRejection>();

        var result = CacheDirectory.DeleteCached(CacheDirectoryPath, [wanted], rejected.Add).Single();

        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, result.Outcome);
        Assert.AreEqual(wanted, result.File.Drive);
        Assert.IsEmpty(rejected);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void InspectAndDeleteCached_HeldUnselectedDirectorySlotWithValidKey_ReportInUseUnderTheStoredKey()
    {
        var key = Folder("Unselected");
        var path = Seed(key);
        using var held = SyntheticCacheSlot.Acquire(CacheDirectoryPath, key, Serial);

        var status = CacheDirectory.InspectCached(CacheDirectoryPath).Single();
        var result = CacheDirectory.DeleteCached(CacheDirectoryPath).Single();

        Assert.AreEqual(CachedBlockAvailability.InUse, status.Availability);
        Assert.AreEqual(key, status.File.Drive);
        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, result.Outcome);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void InspectAndDeleteCached_HeldUnselectedDirectorySlotWithUnreadableKey_AreReportedRejectedNotDeleted()
    {
        var key = Folder("Unselected");
        var path = WriteRaw(key, new byte[BlockLayout.HeaderRegionBytes]);
        using var held = SyntheticCacheSlot.Acquire(CacheDirectoryPath, key, Serial);
        var rejected = new List<CachedBlockRejection>();

        Assert.IsEmpty(CacheDirectory.InspectCached(CacheDirectoryPath, null, rejected.Add));
        Assert.IsEmpty(CacheDirectory.DeleteCached(CacheDirectoryPath, null, rejected.Add));

        Assert.HasCount(2, rejected);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void InspectCached_UnheldSelectedDirectorySlotWithUnreadableKey_IsInvalid()
    {
        var wanted = Folder("Wanted");
        WriteRaw(wanted, new byte[BlockLayout.HeaderRegionBytes]);

        var status = CacheDirectory.InspectCached(CacheDirectoryPath, [wanted]).Single();

        Assert.AreEqual(CachedBlockAvailability.Invalid, status.Availability);
        Assert.AreEqual(wanted, status.File.Drive);
    }

    [TestMethod]
    public void InspectAndDeleteCached_SelectedSlotWhoseBlockAnotherWriterHoldsExclusively_AreInUse()
    {
        var wanted = Folder("Wanted");
        var path = Seed(wanted);
        using var held = SyntheticCacheSlot.Acquire(CacheDirectoryPath, wanted, Serial);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var status = CacheDirectory.InspectCached(CacheDirectoryPath, [wanted]).Single();
        var result = CacheDirectory.DeleteCached(CacheDirectoryPath, [wanted]).Single();

        Assert.AreEqual(CachedBlockAvailability.InUse, status.Availability);
        Assert.AreEqual(CachedBlockDeletionOutcome.InUse, result.Outcome);
    }
}
