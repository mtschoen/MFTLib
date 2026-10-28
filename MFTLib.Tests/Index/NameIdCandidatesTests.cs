using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NameIdCandidatesTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(20)]
    [DataRow(100)]
    public void Add_KeepsEveryIdentifierAcrossTheInlineSlotsAndTheRentedArray(int count)
    {
        var candidates = default(NameIdCandidates);
        try
        {
            for (var index = 0; index < count; index++)
            {
                candidates.Add((uint)(index * 3 + 1));
            }

            Assert.AreEqual(count, candidates.Count);
            for (var index = 0; index < count; index++)
            {
                Assert.IsTrue(candidates.Contains((uint)(index * 3 + 1)));
                Assert.IsFalse(candidates.Contains((uint)(index * 3 + 2)));
            }
        }
        finally
        {
            candidates.Dispose();
        }
    }

    [TestMethod]
    public void Seal_SortsTheOverflowOnceAndKeepsMembershipExact()
    {
        var candidates = default(NameIdCandidates);
        try
        {
            uint[] ids = [900, 5, 77, 3, 41, 1000, 12, 640, 8, 2, 333, 99];
            foreach (var id in ids)
            {
                candidates.Add(id);
            }

            candidates.Seal();
            candidates.Seal();

            foreach (var id in ids)
            {
                Assert.IsTrue(candidates.Contains(id));
            }

            foreach (var missing in new uint[] { 0, 1, 4, 6, 100, 641, 1001 })
            {
                Assert.IsFalse(candidates.Contains(missing));
            }

            candidates.Add(7);
            Assert.IsTrue(candidates.Contains(7));
            Assert.IsTrue(candidates.Contains(900));
            candidates.Seal();
            Assert.IsTrue(candidates.Contains(7));
        }
        finally
        {
            candidates.Dispose();
        }
    }

    [TestMethod]
    public void Dispose_EmptiesTheSetAndMayRepeat()
    {
        var candidates = default(NameIdCandidates);
        for (uint id = 1; id <= 10; id++)
        {
            candidates.Add(id);
        }

        candidates.Dispose();
        candidates.Dispose();

        Assert.AreEqual(0, candidates.Count);
        Assert.IsFalse(candidates.Contains(1));
    }

    static bool ThrowsInvalidOperation(NameView view, bool asAscii)
    {
        try
        {
            _ = asAscii ? view.AsciiBytes.Length : view.Utf16Characters.Length;
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    static bool CopyToShortDestinationThrows(NameView view)
    {
        try
        {
            view.CopyTo(new char[view.Length - 1]);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [TestMethod]
    public void NameView_RejectsTheWrongWidthAccessorAndAShortCopyDestination()
    {
        var ascii = new NameView("abc"u8.ToArray(), isUtf16: false);
        var wide = new NameView(MemoryMarshal.AsBytes("abé".AsSpan()), isUtf16: true);

        Assert.IsTrue(ThrowsInvalidOperation(wide, asAscii: true));
        Assert.IsTrue(ThrowsInvalidOperation(ascii, asAscii: false));
        Assert.IsTrue(CopyToShortDestinationThrows(ascii));
        Assert.AreEqual('c', ascii[2]);
        Assert.AreEqual('é', wide[2]);
        Assert.IsFalse(ascii.IsEmpty);
        Assert.IsTrue(default(NameView).IsEmpty);
        Assert.IsFalse(ascii.EqualsOrdinal("ab"));
    }
}
