using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Allocation ordinals of a dump parse, counted from the parse alone because the countdown is armed
// after the open: 1 the result, 2 and 3 the two read buffers, 4 the entry array, 5 the string pool,
// then each growth of the entry array or the string pool as slices merge, the entry array first.
public partial class DumpParseNativeTests
{
    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void Parse_ReadBufferAllocationFails_ReturnsTheError(int allocationToFail)
    {
        var path = WriteSynthetic(10, 256);

        var result = ParseHeader(path, 256, allocationToFail);

        Assert.AreEqual("Failed to allocate I/O buffers", result.ErrorMessage);
        Assert.AreEqual(0UL, result.UsedRecords);
    }

    [TestMethod]
    public void Parse_EntryArrayAllocationFails_ReturnsTheError()
    {
        var path = WriteSynthetic(10, 256);

        var result = ParseHeader(path, 256, 4);

        Assert.AreEqual("Failed to allocate entry array", result.ErrorMessage);
    }

    [TestMethod]
    public void Parse_StringPoolAllocationFails_ReturnsTheError()
    {
        var path = WriteSynthetic(10, 256);

        var result = ParseHeader(path, 256, 5);

        Assert.AreEqual("Failed to allocate string pool", result.ErrorMessage);
    }

    // 4000 in-use records outgrow the initial 1024 entries while their names stay inside the initial
    // 32768-unit string pool, and one 8192-record chunk holds them all, so the first growth is the
    // entry array's and is the sixth allocation however many workers produced the slices.
    [TestMethod]
    public void Parse_FirstEntryArrayGrowthFails_ReturnsTheError()
    {
        var path = WriteSynthetic(4000, 8192);

        var result = ParseHeader(path, 8192, 6);

        Assert.AreEqual("Failed to grow entry array", result.ErrorMessage);
    }

    // The first growth (1024 to 2048, ordinal 6) succeeds and the second (2048 to 4096, ordinal 7)
    // fails; the rows merged before it stay in the result beside the error.
    [TestMethod]
    public void Parse_SecondEntryArrayGrowthFails_KeepsThePartialResult()
    {
        var path = WriteSynthetic(4000, 8192);

        var result = ParseHeader(path, 8192, 7);

        Assert.AreEqual("Failed to grow entry array", result.ErrorMessage);
        Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
    }

    // One thread and 256-record chunks: each chunk merges once, and the merge that passes 1024
    // entries makes the sixth allocation.
    [TestMethod]
    public void Parse_OneThreadEntryArrayGrowthFails_KeepsThePartialResult()
    {
        var path = WriteSynthetic(5000, 256);
        NativeTestHooks.NativeSetMaxThreads(1);

        var result = ParseHeader(path, 256, 6);

        Assert.AreEqual("Failed to grow entry array", result.ErrorMessage);
        Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
        Assert.IsTrue(result.UsedRecords < 5000, "Should not have all records");
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void GenerateSyntheticMFT_AllocationFails_ReturnsFalse(int allocationToFail)
    {
        var path = Path.Combine(_directory, "generated.mft");
        NativeTestHooks.NativeSetAllocFailCountdown(allocationToFail);

        Assert.IsFalse(MFTLibNative._generateSyntheticMftSized(path, 10, 256, 1024));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_NullPath_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(() => MftVolume.GenerateSyntheticMFT(null!, 10, 256));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_MissingDirectory_ReturnsFalse()
    {
        var path = Path.Combine(_directory, "missing", "fixture.mft");

        Assert.IsFalse(MFTLibNative._generateSyntheticMftSized(path, 10, 256, 1024));
    }
}
