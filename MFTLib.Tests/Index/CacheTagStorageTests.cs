using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CacheTagStorageTests
{
    string _directory = null!;
    string BlockPath => Path.Combine(_directory, CacheDirectory.BlockFileName('T', 123));

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-tag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    void WriteBlock(CacheTag tag)
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = BlockPath,
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = 8,
            NamePoolCapacity = 1024,
            CacheTag = tag
        });
        Assert.IsFalse(block.Header.IsComplete);
        Assert.AreEqual(tag, block.Header.CacheTag);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, _directory,
            new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 0)));
        writer.Complete(new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreationRoundTripsAndInspectionReportsTheTag(bool unspecified)
    {
        var tag = unspecified ? default : new CacheTag("GITW", 7);
        WriteBlock(tag);
        var bytes = File.ReadAllBytes(BlockPath);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Assert.AreEqual(112, Marshal.SizeOf<BlockHeader>());
        Assert.AreEqual(104, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagFourCc)));
        Assert.AreEqual(108, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.CacheTagVersion)));
        Assert.AreEqual(unspecified ? 0u : 0x57544947u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104, 4)));
        Assert.AreEqual(tag.Version, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(108, 4)));
        using (var reopened = BlockFile.Open(BlockPath, 123, out var validation))
        {
            Assert.AreEqual(BlockValidationResult.Valid, validation);
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
            block.Flush();
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
            block.Flush();
        }
        var old = CacheDirectory.InspectCached(_directory).Single();
        Assert.AreEqual(CachedBlockAvailability.Invalid, old.Availability);
        Assert.AreEqual(BlockValidationResult.WrongFormatVersion, old.Validation);
        Assert.IsNull(old.CacheTag);
    }
}
