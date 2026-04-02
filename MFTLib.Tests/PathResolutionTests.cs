using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class PathResolutionTests
{
    [TestMethod]
    public void ResolvePath_SimpleHierarchy_ReturnsCorrectPath()
    {
        // Record 5 is the NTFS root directory (.)
        // Build: C:\Users\test\file.txt
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),           // root
            new(100, 5, 0x0003, "Users", null),      // Users under root
            new(200, 100, 0x0003, "test", null),     // test under Users
            new(300, 200, 0x0001, "file.txt", null), // file.txt under test
        };

        var path = ResolveTestPath(records, 300, "C");
        Assert.AreEqual(@"C:\Users\test\file.txt", path);
    }

    [TestMethod]
    public void ResolvePath_RootDirectory_ReturnsJustDrive()
    {
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),
            new(100, 5, 0x0003, "folder", null),
        };

        var path = ResolveTestPath(records, 100, "D");
        Assert.AreEqual(@"D:\folder", path);
    }

    [TestMethod]
    public void ResolvePath_CircularReference_DoesNotLoop()
    {
        // Simulate a circular parent reference (shouldn't happen but be safe)
        var records = new MftRecord[]
        {
            new(10, 20, 0x0003, "a", null),
            new(20, 10, 0x0003, "b", null),
        };

        // Should terminate without infinite loop
        var path = ResolveTestPath(records, 10, "C");
        Assert.IsNotNull(path);
    }

    [TestMethod]
    public void ResolvePath_RecordNotInLookup_ReturnsDriveWithEmpty()
    {
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),
        };

        // Record 999 doesn't exist in the lookup
        var path = ResolveTestPath(records, 999, "C");
        Assert.AreEqual(@"C:\", path);
    }

    [TestMethod]
    public void ResolvePath_DirectChildOfRoot_ReturnsOneLevel()
    {
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),
            new(50, 5, 0x0001, "boot.ini", null),
        };

        var path = ResolveTestPath(records, 50, "C");
        Assert.AreEqual(@"C:\boot.ini", path);
    }

    [TestMethod]
    public void ResolvePath_DeeplyNested_ReturnsFullChain()
    {
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),
            new(10, 5, 0x0003, "a", null),
            new(20, 10, 0x0003, "b", null),
            new(30, 20, 0x0003, "c", null),
            new(40, 30, 0x0003, "d", null),
            new(50, 40, 0x0001, "file.txt", null),
        };

        var path = ResolveTestPath(records, 50, "X");
        Assert.AreEqual(@"X:\a\b\c\d\file.txt", path);
    }

    [TestMethod]
    public void ResolvePath_SelfReference_DoesNotLoop()
    {
        // A record whose parent is itself (other than root record 5)
        var records = new MftRecord[]
        {
            new(10, 10, 0x0003, "loop", null),
        };

        var path = ResolveTestPath(records, 10, "C");
        Assert.AreEqual(@"C:\loop", path);
    }

    [TestMethod]
    public void ResolvePath_DifferentDriveLetters()
    {
        var records = new MftRecord[]
        {
            new(5, 5, 0x0003, ".", null),
            new(10, 5, 0x0001, "data.db", null),
        };

        Assert.AreEqual(@"D:\data.db", ResolveTestPath(records, 10, "D"));
        Assert.AreEqual(@"Z:\data.db", ResolveTestPath(records, 10, "Z"));
    }

    private static string ResolveTestPath(MftRecord[] records, ulong recordNumber, string driveLetter)
    {
        var lookup = new Dictionary<ulong, MftRecord>();
        foreach (var r in records)
            lookup[r.RecordNumber] = r;
        return MftPathUtilities.ResolvePath(recordNumber, lookup, driveLetter);
    }
}
