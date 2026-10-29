using System.Runtime.InteropServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>The block-level trim behind <c>FileIndex.ReleaseResidentPages</c>, observed through the per-block seam.</summary>
[TestClass]
public class BlockFileResidentPagesTests
{
    static readonly DateTime Moment = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void TryReleaseResidentPages_TrimsTheWholePageAlignedView()
    {
        using var directory = new TempDirectory("mftlib-trim-whole");
        using var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        var ranges = new List<(long Start, long End)>();
        block.Hooks._trimViewRange = (start, end) =>
        {
            ranges.Add((start, end));
            return 0;
        };

        Assert.IsTrue(block.TryReleaseResidentPages());

        var pageBytes = (long)Environment.SystemPageSize;
        Assert.AreSequenceEqual([(0L, block.Length / pageBytes * pageBytes)], ranges);
    }

    [TestMethod]
    public void TryReleaseResidentPages_DisposedBlock_DoesNotTrim()
    {
        using var directory = new TempDirectory("mftlib-trim-disposed");
        var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        var calls = 0;
        block.Hooks._trimViewRange = (_, _) => ++calls;
        block.Dispose();

        Assert.IsFalse(block.TryReleaseResidentPages());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void TryReleaseResidentPages_NativeFailure_ReportsFalse()
    {
        using var directory = new TempDirectory("mftlib-trim-failure");
        using var block = TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), slotCapacity: 512);
        block.Hooks._trimViewRange = (_, _) => 87;

        Assert.IsFalse(block.TryReleaseResidentPages());
    }

    [TestMethod]
    public void TryReleaseResidentPages_WrittenRowsStayIntactAndPersist()
    {
        using var directory = new TempDirectory("mftlib-trim-dirty");
        var path = Path.Combine(directory.Path, "T-0BADF00D.mlix");
        const uint rows = 4096;
        using (var block = TestBlocks.Create(path, slotCapacity: rows, namePoolCapacity: rows * 16))
        {
            var writer = new BlockWriter(block);
            for (var row = 0u; row < rows; row++)
            {
                Assert.IsTrue(writer.TryWriteRow(row, $"r{row}",
                    new RowColumns(0, RowFlags.InUse, 0, row + 1, Moment.Ticks, 0)));
                if (row % 512 == 511)
                {
                    Assert.IsTrue(block.TryReleaseResidentPages(), "Real trim over freshly dirtied pages.");
                }
            }

            for (var row = 0; row < rows; row++)
            {
                Assert.AreEqual(row + 1, block.Rows[row].Size);
            }

            block.Flush(null);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        for (var row = 0; row < rows; row++)
        {
            var offset = (int)(BlockLayout.RowRegionOffset + (long)row * BlockLayout.RowBytes);
            Assert.AreEqual(row + 1, MemoryMarshal.Read<FileRow>(bytes.AsSpan(offset)).Size);
        }
    }
}
