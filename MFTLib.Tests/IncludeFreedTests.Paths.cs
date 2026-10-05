using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class IncludeFreedTests
{
    [TestMethod]
    [DataRow(99ul, (ushort)0, null)]
    [DataRow(1ul, (ushort)0, null)]
    [DataRow(13ul, (ushort)14, null)]
    [DataRow(5ul, (ushort)5, null)]
    [DataRow(5ul, (ushort)6, "deleted-before.txt")]
    public void FreedOrigin_RequiresACompleteTrustedChain(ulong parent, ushort parentSequence, string? expectedPath)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        WriteParent(image, 13, parent, parentSequence);
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", expectedPath);
    }

    [TestMethod]
    [DataRow((ushort)5, null)]
    [DataRow((ushort)6, @"deleted-dir\deleted-before.txt")]
    public void FreedOrigin_ValidatesTheFinalRootHop(ushort rootSequence, string? expectedPath)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        WriteParent(image, 12, 5, rootSequence);
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", expectedPath);
    }

    [TestMethod]
    public void FreedOrigin_AcceptsFreedSequenceWraparound()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        BitConverter.GetBytes((ushort)0).CopyTo(image, 12 * RecordSize + 0x10);
        WriteParent(image, 13, 12, ushort.MaxValue);
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", @"deleted-dir\deleted-before.txt");
    }

    [TestMethod]
    public void FreedOrigin_RootDoesNotRequireAStoredName()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        image[FileNameOffset(image, 5) + 64] = 0;
        BitConverter.GetBytes((ushort)0).CopyTo(image, 5 * RecordSize + 0x10);
        WriteParent(image, 12, 5, 0);
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", @"deleted-dir\deleted-before.txt");
    }

    [TestMethod]
    [DataRow((ushort)0)]
    [DataRow(ushort.MaxValue)]
    public void FreedOrigin_InvalidRootIsNotAFreedSequenceZeroRecord(ushort parentSequence)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        image[5 * RecordSize] = 0;
        WriteParent(image, 12, 5, parentSequence);
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", null);
    }

    [TestMethod]
    public void FreedOrigin_NamelessParentDoesNotYieldAPartialPath()
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        image[FileNameOffset(image, 12) + 64] = 0;
        File.WriteAllBytes(_fixturePath, image);
        AssertFreedPath(13, "deleted-before.txt", null);
    }

    [TestMethod]
    [DataRow(127, true)]
    [DataRow(128, false)]
    public void FreedOrigin_EnforcesTheComponentLimit(int directoryCount, bool resolves)
    {
        if (MftFixtureTests.SkipOnNonWindows())
        {
            return;
        }

        var image = File.ReadAllBytes(_fixturePath);
        Array.Resize(ref image, (21 + directoryCount) * RecordSize);
        for (var index = 0; index < directoryCount; index++)
        {
            var recordNumber = 21 + index;
            Array.Copy(image, 12 * RecordSize, image, recordNumber * RecordSize, RecordSize);
            WriteParent(image, recordNumber, index == 0 ? 5ul : (ulong)recordNumber - 1,
                index == 0 ? (ushort)6 : (ushort)14);
        }

        WriteParent(image, 13, (ulong)(20 + directoryCount), 14);
        File.WriteAllBytes(_fixturePath, image);
        var expectedPath = resolves
            ? string.Join(@"\", Enumerable.Repeat("deleted-dir", directoryCount).Append("deleted-before.txt"))
            : null;
        AssertFreedPath(13, "deleted-before.txt", expectedPath);
    }

    void AssertFreedPath(ulong recordNumber, string name, string? expectedPath)
    {
        using var result = MftVolume.StreamMftFromFile(_fixturePath, null,
            MatchFlags.IncludeFreed | MatchFlags.ResolvePaths);
        var record = result.Single(candidate => candidate.RecordNumber == recordNumber);
        Assert.IsFalse(record.InUse);
        Assert.AreEqual(name, record.FileName);
        Assert.AreEqual(expectedPath, record.FullPath);
        Assert.AreEqual(expectedPath ?? name, record.ToString());
        var materialized = record.Materialize();
        Assert.AreEqual(record.FileName, materialized.FileName);
        Assert.AreEqual(record.FullPath, materialized.FullPath);
    }
}
