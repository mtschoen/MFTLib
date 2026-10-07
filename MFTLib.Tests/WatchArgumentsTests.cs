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
    public void Need_ScanDrive_IsTheBrokerLaunchBecauseTheBrokerIsTheElevatedProcess()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan-drive", "C"], out var parsed, out _));

        Assert.AreEqual(ElevationNeed.BrokerLaunch, parsed.Need);
    }

    [TestMethod]
    public void Usage_NamesEveryMode()
    {
        foreach (var name in ProgramModes.Names.Keys)
        {
            StringAssert.Contains(WatchArguments.Usage, name);
        }
    }

    [DataTestMethod]
    [DataRow("watch", "Watch")]
    [DataRow("rescan", "Rescan")]
    [DataRow("journal", "Journal")]
    public void TryParse_IndexMode_SelectsItAndDefaultsTheDrive(string name, string mode)
    {
        Assert.IsTrue(WatchArguments.TryParse([name], out var parsed, out _));

        Assert.AreEqual(mode, parsed.Mode.ToString());
        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
        Assert.AreEqual(ElevationNeed.BrokerLaunch, parsed.Need);
    }

    [TestMethod]
    public void TryParse_WatchSeconds_AreRead()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--seconds", "45"], out var parsed, out _));

        Assert.AreEqual(45, parsed.Seconds);
        Assert.AreEqual(WatchArguments.DefaultSeconds, WatchArguments.TryParse(["watch"], out var plain, out _) ? plain.Seconds : -1);
    }

    [TestMethod]
    public void TryParse_Journal_ReadsBothSizes()
    {
        Assert.IsTrue(WatchArguments.TryParse(["journal", "C", "--maximum-size", "9000", "--allocation-delta", "4096"], out var parsed, out _));

        Assert.AreEqual((9000L, 4096L), (parsed.MaximumSize, parsed.AllocationDelta));
    }

    [DataTestMethod]
    [DataRow(new[] { "journal", "C", "--maximum-size", "9000" }, "journal needs --maximum-size and --allocation-delta together.")]
    [DataRow(new[] { "journal", "C", "--allocation-delta", "1" }, "journal needs --maximum-size and --allocation-delta together.")]
    [DataRow(new[] { "watch", "C", "--clear" }, "--clear belongs to cache.")]
    [DataRow(new[] { "watch", "C", "--profile", "sparse" }, "Unknown profile sparse.")]
    [DataRow(new[] { "watch", "C", "--seconds", "soon" }, "Option --seconds needs a whole number, not 'soon'.")]
    public void TryParse_BadFlags_AreRefusedWithTheirReason(string[] arguments, string expected)
    {
        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    public void TryParse_CacheAndElevationStatus_NeedNoBrokerAndKeepAnEmptyDriveList()
    {
        Assert.IsTrue(WatchArguments.TryParse(["cache", "--cache-directory", "cache-folder", "--clear"], out var cache, out _));
        Assert.IsTrue(WatchArguments.TryParse(["elevation-status"], out var status, out _));

        Assert.AreEqual(ElevationNeed.None, cache.Need);
        Assert.AreEqual(ElevationNeed.None, status.Need);
        Assert.AreEqual(0, cache.Drives.Count);
        Assert.AreEqual("cache-folder", cache.CacheDirectory);
        Assert.IsTrue(cache.Clear);
    }

    [TestMethod]
    public void ScanOptions_KeepNamesAndProfile_MapToTheBrokerScanOptions()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", "desktop.ini, .gitignore", "--profile", "directory-index"], out var parsed, out _));
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C"], out var plain, out _));

        var options = parsed.ScanOptions!;
        CollectionAssert.AreEqual(new[] { "desktop.ini", ".gitignore" }, options.KeepFileNames!.ToArray());
        Assert.AreEqual(BrokerScanProfile.DirectoryIndex, options.Profile);
        Assert.IsNull(plain.ScanOptions);
        Assert.AreNotEqual(plain.CacheTag, parsed.CacheTag, "A different profile or keep list is a different cache identity.");
    }
}
