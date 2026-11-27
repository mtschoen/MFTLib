using System.Buffers.Binary;
using System.Text;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Decoding the native record reader's file name answer, which is untrusted bytes like every record read.</summary>
[TestClass]
public sealed class FileNameResolutionTests
{
    [TestMethod]
    public void FromLookup_ReturnedAnswer_TakesTheParentRecordNumberWithoutItsSequence()
    {
        var content = new byte[8 + 4];
        BinaryPrimitives.WriteUInt64LittleEndian(content, 17251907UL | (25UL << 48));
        Encoding.Unicode.GetBytes("ab").CopyTo(content, 8);

        var resolution = FileNameResolution.FromLookup(11484, 6, InlineContentOutcome.Returned, content);

        Assert.AreEqual(new FileNameResolution(11484, 6, FileNameLookupOutcome.Resolved, 17251907, "ab"), resolution);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "no bytes")]
    [DataRow(8, DisplayName = "a parent and no name")]
    [DataRow(9, DisplayName = "half a name unit")]
    public void FromLookup_ReturnedAnswerOfAnImpossibleLength_IsUnreadable(int length)
    {
        var resolution = FileNameResolution.FromLookup(1, 1, InlineContentOutcome.Returned, new byte[length]);

        Assert.AreEqual(FileNameResolution.Unreadable(1, 1), resolution);
    }

    [TestMethod]
    [DataRow(InlineContentOutcome.Superseded, (int)FileNameLookupOutcome.Superseded)]
    [DataRow(InlineContentOutcome.Unreadable, (int)FileNameLookupOutcome.Unreadable)]
    [DataRow(InlineContentOutcome.NotInline, (int)FileNameLookupOutcome.Unreadable)]
    public void FromLookup_OtherOutcomes_CarryNoNameOrParent(InlineContentOutcome outcome, int expected)
    {
        var resolution = FileNameResolution.FromLookup(1, 1, outcome, []);

        Assert.AreEqual(new FileNameResolution(1, 1, (FileNameLookupOutcome)expected, 0, ""), resolution);
    }
}
