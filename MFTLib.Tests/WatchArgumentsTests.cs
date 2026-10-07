using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

[TestClass]
public class WatchArgumentsTests
{
    [TestMethod]
    public void TryParse_NoArguments_ScansTheDefaultDrive()
    {
        Assert.IsTrue(WatchArguments.TryParse([], out var parsed, out var error));

        Assert.IsNull(error);
        Assert.AreEqual(ProgramMode.ScanDrive, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_OnlyDriveLetters_ScansEachDrive()
    {
        Assert.IsTrue(WatchArguments.TryParse(["C", "D:"], out var parsed, out _));

        Assert.AreEqual(ProgramMode.ScanDrive, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "C", "D:" }, parsed.Drives.ToArray());
    }

    [DataTestMethod]
    [DataRow("scan-drive")]
    [DataRow("SCAN-Drive")]
    public void TryParse_ModeName_SelectsTheModeAndKeepsTheDrivesAfterIt(string name)
    {
        Assert.IsTrue(WatchArguments.TryParse([name, "C", "E"], out var parsed, out _));

        Assert.AreEqual(ProgramMode.ScanDrive, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "C", "E" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_ModeWithoutDrives_UsesTheDefaultDrive()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan-drive"], out var parsed, out _));

        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_UnknownOption_IsRefusedAndNamed()
    {
        Assert.IsFalse(WatchArguments.TryParse(["scan-drive", "C", "--verbose"], out var parsed, out var error));

        Assert.AreEqual("Unknown option --verbose.", error);
        Assert.AreEqual(ProgramMode.ScanDrive, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void RequiresElevation_ScanDrive_IsFalseBecauseTheBrokerIsTheElevatedProcess()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan-drive", "C"], out var parsed, out _));

        Assert.IsFalse(parsed.RequiresElevation);
    }

    [TestMethod]
    public void Usage_NamesEveryMode()
    {
        foreach (var name in ProgramModes.Names.Keys)
        {
            StringAssert.Contains(WatchArguments.Usage, name);
        }
    }
}
