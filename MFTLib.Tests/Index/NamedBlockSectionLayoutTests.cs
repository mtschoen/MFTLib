using System.IO.MemoryMappedFiles;
using System.Text;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The broker opens a section the client created, and the client (or anything running as the
///     same user) can rewrite its header at any time. These tests play that process through the
///     creator's own mapping: a header rewritten before the open is refused, and one rewritten
///     while the broker writes moves no write.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class NamedBlockSectionLayoutTests : IDisposable
{
    // 128 rows fill exactly one page, so the slot just past capacity is the first byte of the
    // sequence region: a write there that a rewritten capacity let through is visible in it.
    const uint SlotCapacity = 128;
    const uint NamePoolCapacity = 4096;
    const uint EvilRow = 6;

    static readonly long SequenceRegionOffset = BlockLayout.SequenceRegionOffset(SlotCapacity);
    static readonly long NamePoolOffset = BlockLayout.NamePoolOffset(SlotCapacity, SlotCapacity);

    TempDirectory _directory = null!;
    string _sectionName = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-section-layout");
        _sectionName = NamedBlockSection.BuildSectionName('T');
    }

    public void Dispose() => _directory.Dispose();

    [TestMethod]
    [DataRow("Magic", "WrongMagic")]
    [DataRow("FormatVersion", "WrongFormatVersion")]
    [DataRow("SlotCapacity", "InconsistentRegions")]
    [DataRow("SlotCapacityWithMatchingOffsets", "InconsistentRegions")]
    [DataRow("NamePoolCapacity", "InconsistentRegions")]
    [DataRow("RowRegionOffset", "InconsistentRegions")]
    [DataRow("SequenceRegionOffset", "InconsistentRegions")]
    [DataRow("NamePoolOffset", "InconsistentRegions")]
    [DataRow("RowCount", "InconsistentRegions")]
    [DataRow("NamePoolUsed", "InvalidNameDescriptor")]
    [DataRow("NameEntryCapacity", "InconsistentRegions")]
    [DataRow("BucketCount", "InvalidNameDescriptor")]
    [DataRow("NameEntryRegionOffset", "InconsistentRegions")]
    [DataRow("BucketRegionOffset", "InconsistentRegions")]
    [DataRow("NameEntryCount", "InvalidNameDescriptor")]
    public void OpenExisting_HeaderRewrittenBeforeOpen_RefusesTheSection(string field, string reason)
    {
        var (creator, lifetime) = CreateSection();
        using (lifetime)
        using (creator)
        {
            RewriteBeforeOpen(ref creator.Header, field);

            var exception = Assert.ThrowsExactly<InvalidDataException>(() => NamedBlockSection.OpenExisting(_sectionName));

            Assert.Contains(reason, exception.Message);
        }
    }

    [TestMethod]
    public void OpenExisting_UntouchedSection_TakesItsGeometryFromTheHeader()
    {
        var (creator, lifetime) = CreateSection();
        using (lifetime)
        using (creator)
        {
            using var opened = NamedBlockSection.OpenExisting(_sectionName);

            Assert.AreEqual(creator.Length, opened.Length);
            Assert.AreEqual(SlotCapacity, opened.SlotCapacity);
            Assert.AreEqual(NamePoolCapacity, opened.NamePoolCapacity);
            Assert.AreEqual(0u, opened.RowCount);
            Assert.AreEqual(0u, opened.NamePoolUsed);
        }
    }

    [TestMethod]
    public void Create_SizesTheSectionWithTheEntryCapacity()
    {
        var options = TestBlocks.Options(Path.Combine(_directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft,
            SlotCapacity, NamePoolCapacity, rootRow: 5, deleteOnClose: true) with
        {
            NameEntryCapacity = 3 * SlotCapacity
        };
        var (creator, lifetime) = NamedBlockSection.Create(options, _sectionName);
        using (lifetime)
        using (creator)
        {
            using var opened = NamedBlockSection.OpenExisting(_sectionName);
            Assert.AreEqual(BlockLayout.TotalBlockBytes(SlotCapacity, 3 * SlotCapacity, NamePoolCapacity), creator.Length);
            Assert.AreEqual(creator.Length, opened.Length);
            Assert.AreEqual(3 * SlotCapacity, opened.NameEntryCapacity);
        }
    }

    [TestMethod]
    public void Write_HeaderRewrittenDuringTheWrite_KeepsEveryWriteInItsRegion()
    {
        var (creator, lifetime) = CreateSection();
        using (lifetime)
        using (creator)
        {
            var result = new RealBlockSectionWriter(TimeProvider.System).Write(_sectionName,
                new UsnJournalCursor(7, 100), BatchesRewritingBetween(creator),
                new MftScanOptions(), default, CancellationToken.None);

            using var raw = MemoryMappedFile.OpenExisting(_sectionName, MemoryMappedFileRights.Read);
            using var view = raw.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            Assert.AreEqual(1L, result.SkippedRecordCount, "the record past the real slot capacity is refused");
            AssertZero(view, BlockLayout.RowRegionOffset, 5 * BlockLayout.RowBytes, "rows 0 to 4 were never written");
            AssertZero(view, SequenceRegionOffset, 5 * sizeof(ushort), "sequence slots 0 to 4 were never written");
            AssertZero(view, SequenceRegionOffset + (EvilRow + 1) * sizeof(ushort),
                (int)(SlotCapacity - EvilRow - 1) * sizeof(ushort), "sequence slots past row 6 were never written");
            Assert.AreEqual("evil", ReadRowName(view, EvilRow));

            view.Read(0, out BlockHeader header);
            Assert.AreEqual(EvilRow + 1, header.RowCount, "the broker publishes its own row count");
            Assert.AreEqual((uint)(".".Length + "evil".Length), header.NamePoolUsed,
                "the broker publishes its own name-pool cursor");
        }
    }

    (BlockFile Block, IDisposable Lifetime) CreateSection()
    {
        return NamedBlockSection.Create(TestBlocks.Options(Path.Combine(_directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft, SlotCapacity, NamePoolCapacity, rootRow: 5, deleteOnClose: true), _sectionName);
    }

    static void RewriteBeforeOpen(ref BlockHeader header, string field)
    {
        switch (field)
        {
            case "Magic": header.Magic++; break;
            case "FormatVersion": header.FormatVersion++; break;
            case "SlotCapacity": header.SlotCapacity *= 64; break;
            case "SlotCapacityWithMatchingOffsets":
                header.SlotCapacity *= 2;
                header.SequenceRegionOffset = (ulong)BlockLayout.SequenceRegionOffset(header.SlotCapacity);
                header.NamePoolOffset = (ulong)BlockLayout.NamePoolOffset(header.SlotCapacity, header.NameEntryCapacity);
                break;
            case "NameEntryCapacity": header.NameEntryCapacity *= 64; break;
            case "BucketCount": header.BucketCount++; break;
            case "NameEntryRegionOffset": header.NameEntryRegionOffset = 0; break;
            case "BucketRegionOffset": header.BucketRegionOffset = 0; break;
            case "NameEntryCount": header.NameEntryCount = header.NameEntryCapacity + 1; break;
            case "NamePoolCapacity": header.NamePoolCapacity += BlockLayout.PageSize; break;
            case "RowRegionOffset": header.RowRegionOffset = 0; break;
            case "SequenceRegionOffset": header.SequenceRegionOffset = BlockLayout.RowRegionOffset; break;
            case "NamePoolOffset": header.NamePoolOffset = 0; break;
            case "RowCount": header.RowCount = header.SlotCapacity + 1; break;
            case "NamePoolUsed": header.NamePoolUsed = header.NamePoolCapacity + 2; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    // The second batch is pulled only after the broker wrote the first, so the rewrite lands
    // between two writes of one open block.
    static IEnumerable<IReadOnlyList<MftRecord>> BatchesRewritingBetween(BlockFile creator)
    {
        yield return [new MftRecord(5, 5, new MftRecordFields(3), ".")];
        RewriteDuringWrite(ref creator.Header);
        yield return
        [
            new MftRecord(EvilRow, 5, new MftRecordFields(1), "evil"),
            new MftRecord(SlotCapacity, 5, new MftRecordFields(1), "beyond")
        ];
    }

    // Every geometry field the writer could follow now points somewhere it must not write.
    static void RewriteDuringWrite(ref BlockHeader header)
    {
        header.SlotCapacity = SlotCapacity * 8;
        header.SequenceRegionOffset = BlockLayout.RowRegionOffset;
        header.NamePoolOffset = BlockLayout.RowRegionOffset;
        header.NamePoolUsed = 0;
        header.RowCount = 0;
    }

    static void AssertZero(MemoryMappedViewAccessor view, long offset, int count, string message)
    {
        var bytes = new byte[count];
        view.ReadArray(offset, bytes, 0, count);
        Assert.IsTrue(bytes.All(value => value == 0), message);
    }

    static string ReadRowName(MemoryMappedViewAccessor view, uint rowIndex)
    {
        view.Read(BlockLayout.RowRegionOffset + rowIndex * BlockLayout.RowBytes, out FileRow row);
        var descriptor = FileRow.ReadDescriptorWord(in row);
        var nameId = FileRow.DescriptorNameId(descriptor);
        view.Read(BlockLayout.NameEntryRegionOffset(SlotCapacity) + nameId * 12, out NameEntry entry);
        var length = entry.LengthAndEncoding & 0x7fff;
        var utf16 = (entry.LengthAndEncoding & 0x8000) != 0;
        var bytes = new byte[length * (utf16 ? sizeof(char) : 1)];
        view.ReadArray(NamePoolOffset + entry.PoolOffsetBytes, bytes, 0, bytes.Length);
        return (utf16 ? Encoding.Unicode : Encoding.ASCII).GetString(bytes);
    }
}
