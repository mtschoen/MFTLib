using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Removing a deleted row's link costs one sibling examination however many deleted siblings share its
///     parent, so reusing slots never scales with the number of retained deleted siblings.
/// </summary>
[TestClass]
public class DeletedChildLinksTests
{
    const uint ParentRow = 6;
    const uint FirstSiblingRow = 100;
    const int SiblingCount = 300;

    [TestMethod]
    public void ReusingEverySiblingInReverseSlotOrder_ExaminesOneEntryPerReuse()
    {
        var links = LinkSiblings(SiblingCount);

        for (var offset = SiblingCount - 1; offset >= 0; offset--)
        {
            links.RemoveDeletedRow(FirstSiblingRow + (uint)offset, ParentRow);
        }

        Assert.AreEqual(SiblingCount * 1L, links._siblingsExaminedForTest);
    }

    [TestMethod]
    public void RepeatedDeleteAndReuseOfOneSlot_ExaminesOneEntryPerCycleWhileOtherSiblingsRemain()
    {
        var links = LinkSiblings(SiblingCount);
        const uint cycledRow = FirstSiblingRow + SiblingCount;
        const int cycleCount = 250;

        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            links.AddDeletedRow(cycledRow, ParentRow);
            links.RemoveDeletedRow(cycledRow, ParentRow);
        }

        Assert.AreEqual(cycleCount * 1L, links._siblingsExaminedForTest);
    }

    [TestMethod]
    public void RemovingARowThatWasNeverLinked_IsToleratedAndLeavesTheSiblingsLinked()
    {
        var links = LinkSiblings(3);

        links.RemoveDeletedRow(FirstSiblingRow + 50, ParentRow);
        links.RemoveDeletedRow(FirstSiblingRow, 999);

        Assert.AreEqual(1L, links._siblingsExaminedForTest);
        for (var offset = 0; offset < 3; offset++)
        {
            links.RemoveDeletedRow(FirstSiblingRow + (uint)offset, ParentRow);
        }

        Assert.AreEqual(4L, links._siblingsExaminedForTest);
    }

    static DeletedChildLinks LinkSiblings(int count)
    {
        var links = new DeletedChildLinks();
        for (var offset = 0; offset < count; offset++)
        {
            links.AddDeletedRow(FirstSiblingRow + (uint)offset, ParentRow);
        }

        return links;
    }
}
