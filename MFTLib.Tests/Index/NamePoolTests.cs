using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NamePoolTests
{
    [TestMethod]
    public void TryAppend_WritesAtTheCurrentOffsetAndAdvancesUsed()
    {
        var pool = new char[64];
        var used = 0u;

        Assert.IsTrue(NamePool.TryAppend(pool, ref used, 128, "alpha", out var firstOffset));
        Assert.AreEqual(0u, firstOffset);
        Assert.AreEqual(10u, used);

        Assert.IsTrue(NamePool.TryAppend(pool, ref used, 128, "beta", out var secondOffset));
        Assert.AreEqual(10u, secondOffset);
        Assert.AreEqual(18u, used);

        Assert.AreEqual("alpha", new string(NamePool.Read(pool, firstOffset, 5)));
        Assert.AreEqual("beta", new string(NamePool.Read(pool, secondOffset, 4)));
    }

    [TestMethod]
    public void TryAppend_EmptyName_SucceedsWithoutAdvancing()
    {
        var pool = new char[8];
        var used = 4u;
        Assert.IsTrue(NamePool.TryAppend(pool, ref used, 16, ReadOnlySpan<char>.Empty, out var offset));
        Assert.AreEqual(4u, offset);
        Assert.AreEqual(4u, used);
    }

    [TestMethod]
    public void TryAppend_PastCapacity_ReturnsFalseAndLeavesUsedUnchanged()
    {
        var pool = new char[8];
        var used = 12u;
        Assert.IsFalse(NamePool.TryAppend(pool, ref used, 16, "toolong", out var offset));
        Assert.AreEqual(12u, used);
        Assert.AreEqual(0u, offset);
    }

    [TestMethod]
    public void TryAppend_ExactlyFillingCapacity_Succeeds()
    {
        var pool = new char[8];
        var used = 0u;
        Assert.IsTrue(NamePool.TryAppend(pool, ref used, 16, "abcdefgh", out _));
        Assert.AreEqual(16u, used);
    }

    [TestMethod]
    public void TryAppend_NameLongerThanTheLengthField_IsRejected()
    {
        var pool = new char[100_000];
        var used = 0u;
        var oversized = new string('x', NamePool.MaximumNameLengthUnits + 1);
        Assert.IsFalse(NamePool.TryAppend(pool, ref used, 200_000, oversized, out _));
        Assert.AreEqual(0u, used);
    }

    [TestMethod]
    public void Read_ReturnsExactlyTheRequestedUnits()
    {
        var pool = "alphabeta".ToCharArray();
        Assert.AreEqual("beta", new string(NamePool.Read(pool, offsetBytes: 10, lengthUnits: 4)));
    }

    [TestMethod]
    public void ReadRowName_ReadsThroughTheBlock()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        var rowIndex = builder.AddRow("notes.txt", 0, RowFlags.InUse, 12,
            new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        builder.Complete(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));

        using var block = builder.OpenForReading(out _);
        Assert.AreEqual("notes.txt", new string(NamePool.ReadRowName(block!, rowIndex)));
    }

    [TestMethod]
    public void RenameShape_AppendsThenSwapsSoTheOldNameStaysReadable()
    {
        var pool = new char[64];
        var used = 0u;
        NamePool.TryAppend(pool, ref used, 128, "before.txt", out var oldOffset);
        NamePool.TryAppend(pool, ref used, 128, "after.txt", out var newOffset);

        Assert.AreEqual("before.txt", new string(NamePool.Read(pool, oldOffset, 10)));
        Assert.AreEqual("after.txt", new string(NamePool.Read(pool, newOffset, 9)));
    }

    /// <summary>
    ///     A synthetic used-bytes value near the top of the 32-bit range, with no pool of that
    ///     size actually allocated. Past two gigabytes the character index no longer fits an
    ///     unsigned 32-bit byte count comfortably, and this method's whole contract is to report
    ///     "does not fit" by returning false rather than throwing its way out.
    /// </summary>
    [DataTestMethod]
    [DataRow(uint.MaxValue)]
    [DataRow(uint.MaxValue - 1)]
    [DataRow(uint.MaxValue - 2)]
    [DataRow(uint.MaxValue - 3)]
    [DataRow(2147483648u)]
    [DataRow(3221225472u)]
    public void TryAppend_UsedBytesNearTheTopOfTheRange_ReturnsFalseWithoutThrowing(uint usedBytes)
    {
        var pool = new char[16];
        var used = usedBytes;

        Assert.IsFalse(NamePool.TryAppend(pool, ref used, uint.MaxValue, "a", out var offsetBytes));
        Assert.AreEqual(0u, offsetBytes);
        Assert.AreEqual(usedBytes, used, "a rejected append must leave the used count untouched");
    }
}
