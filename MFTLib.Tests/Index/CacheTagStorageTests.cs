using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CacheTagStorageTests : IDisposable
{
    TempDirectory _directoryOwner = null!;
    string _directory = null!;
    string BlockPath => Path.Combine(_directory, CacheDirectory.BlockFileName(new VolumeKey('T'), 123));

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-tag");
        _directory = _directoryOwner.Path;
    }

    [TestCleanup]
    public void Cleanup() => _directoryOwner.Dispose();

    public void Dispose() => Cleanup();

    void WriteBlock(CacheTag tag)
    {
        using var block = TestBlocks.Create(BlockPath, slotCapacity: 8, namePoolCapacity: 1024, volumeSerial: 123, cacheTag: tag);
        Assert.IsFalse(block.Header.IsComplete);
        Assert.AreEqual(tag, block.Header.CacheTag);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, _directory,
            new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0)));
        writer.Complete(new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc), null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreationRoundTripsAndInspectionReportsTheTag(bool unspecified)
    {
        var tag = unspecified ? default : new CacheTag("GITW", 7);
        WriteBlock(tag);
        var bytes = File.ReadAllBytes(BlockPath);
        Assert.AreEqual(BlockLayout.FormatVersion, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.AreEqual(168, Marshal.SizeOf<BlockHeader>());
        Assert.AreEqual(104, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagFourCc)));
        Assert.AreEqual(108, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagVersion)));
        Assert.AreEqual(unspecified ? 0u : 0x57544947u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104, 4)));
        Assert.AreEqual(tag.Version, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(108, 4)));
        using (var reopened = BlockFile.Open(BlockPath, 123, out var validation))
        {
            Assert.IsNull(validation);
            Assert.IsNotNull(reopened);
            Assert.AreEqual(tag, reopened.Header.CacheTag);
        }
        var inspected = CacheDirectory.InspectCached(_directory).Single();
        Assert.AreEqual(CachedBlockAvailability.Available, inspected.Availability);
        Assert.AreEqual(tag, inspected.CacheTag);
    }

    [TestMethod]
    public void MalformedStoredFourCcIsRejectedNotSurfacedAsATag()
    {
        // A byte above 0x7f cannot have come from CacheTag's own constructor (it enforces four
        // ASCII characters), so this simulates on-disk corruption rather than anything the
        // library itself could have written.
        WriteBlock(new CacheTag("GITW", 7));
        using (var block = BlockFile.Open(BlockPath, 123, out _))
        {
            Assert.IsNotNull(block);
            block.Header.CacheTagFourCc = 0x80000000;
            block.Flush(null);
        }

        using (var reopened = BlockFile.Open(BlockPath, 123, out var validation))
        {
            Assert.IsNull(reopened);
            Assert.AreEqual(BlockValidationResult.WrongCacheTag, validation);
        }

        var inspected = CacheDirectory.InspectCached(_directory).Single();
        Assert.AreEqual(CachedBlockAvailability.Invalid, inspected.Availability);
        Assert.AreEqual(BlockValidationResult.WrongCacheTag, inspected.Validation);
        Assert.IsNull(inspected.CacheTag);
    }

    [TestMethod]
    public void InspectionDoesNotInventTagsForLockedOrOldBlocks()
    {
        WriteBlock(new CacheTag("FILE", 1));
        using (var owner = BlockOwnerLock.TryAcquire(BlockPath))
        {
            Assert.IsNotNull(owner);
            var busy = CacheDirectory.InspectCached(_directory).Single();
            Assert.AreEqual(CachedBlockAvailability.InUse, busy.Availability);
            Assert.IsNull(busy.CacheTag);
        }
        using (var block = BlockFile.Open(BlockPath, 123, out _))
        {
            Assert.IsNotNull(block);
            block.Header.FormatVersion = 2;
            block.Flush(null);
        }
        var old = CacheDirectory.InspectCached(_directory).Single();
        Assert.AreEqual(CachedBlockAvailability.Invalid, old.Availability);
        Assert.AreEqual(BlockValidationResult.WrongFormatVersion, old.Validation);
        Assert.IsNull(old.CacheTag);
    }
}
