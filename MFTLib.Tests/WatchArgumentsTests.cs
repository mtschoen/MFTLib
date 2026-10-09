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
        CollectionAssert.AreEqual(new[] { "C", "D" }, parsed.Drives.ToArray());
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

    [DataTestMethod]
    [DataRow("0")]
    [DataRow("4294967295")]
    [DataRow("4294967296")]
    [DataRow("2147484")]
    public void TryParse_SecondsOutsideTheDelayRange_AreRefusedNamingTheRange(string seconds)
    {
        Assert.IsFalse(WatchArguments.TryParse(["watch", "C", "--seconds", seconds], out _, out var error));

        Assert.AreEqual("Option --seconds must be from 1 to 2147483.", error);
    }

    [DataTestMethod]
    [DataRow("1", 1)]
    [DataRow("2147483", 2147483)]
    public void TryParse_SecondsAtTheEdgesOfTheDelayRange_AreAccepted(string seconds, int expected)
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--seconds", seconds], out var parsed, out _));

        Assert.AreEqual(expected, parsed.Seconds);
    }

    [DataTestMethod]
    [DataRow("watch")]
    [DataRow("rescan")]
    [DataRow("journal")]
    [DataRow("scan-drive")]
    [DataRow("cache")]
    public void TryParse_EmptyOrGarbageDriveToken_IsRefusedForEveryMode(string mode)
    {
        Assert.IsFalse(WatchArguments.TryParse([mode, string.Empty], out _, out var empty));
        Assert.IsFalse(WatchArguments.TryParse([mode, "Cjunk"], out _, out var garbage));
        Assert.IsFalse(WatchArguments.TryParse([mode, "C:x"], out _, out var path));

        Assert.AreEqual("'' is not a drive letter.", empty);
        Assert.AreEqual("'Cjunk' is not a drive letter.", garbage);
        Assert.AreEqual("'C:x' is not a drive letter.", path);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryParse_CacheWithAGarbageToken_IsRefusedWithAndWithoutClear(bool clear)
    {
        string[] arguments = clear ? ["cache", "junk", "--clear"] : ["cache", "junk"];

        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual("'junk' is not a drive letter.", error);
    }

    [TestMethod]
    public void TryParse_DriveListWithOneBadToken_NamesThatToken()
    {
        Assert.IsFalse(WatchArguments.TryParse(["watch", "C", "9", "d:"], out _, out var error));

        Assert.AreEqual("'9' is not a drive letter.", error);
    }

    [TestMethod]
    public void TryParse_DriveTokens_AreNormalizedToUpperCaseLetters()
    {
        Assert.IsTrue(WatchArguments.TryParse(["cache", "c:", "d"], out var parsed, out _));

        CollectionAssert.AreEqual(new[] { "C", "D" }, parsed.Drives.ToArray());
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
    [DataRow(new[] { "watch", "C", "--clear" }, "Option --clear does not apply to watch.")]
    [DataRow(new[] { "watch", "C", "--profile", "sparse" }, "Unknown option --profile.")]
    [DataRow(new[] { "watch", "C", "--directories-only", "--keep-name", "a" }, "Options --directories-only and --keep-name cannot be combined.")]
    [DataRow(new[] { "watch", "C", "--keep-name", "" }, "Option --keep-name needs a file name; use --directories-only for directories alone.")]
    [DataRow(new[] { "watch", "C", "--keep-name", " , " }, "Option --keep-name needs a file name; use --directories-only for directories alone.")]
    [DataRow(new[] { "watch", "C", "--seconds", "soon" }, "Option --seconds needs a whole number, not 'soon'.")]
    public void TryParse_BadFlags_AreRefusedWithTheirReason(string[] arguments, string expected)
    {
        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual(expected, error);
    }

    [DataTestMethod]
    [DataRow(new[] { "scan-drive", "C", "--seconds", "5" }, "Option --seconds does not apply to scan-drive.")]
    [DataRow(new[] { "C", "--seconds", "5" }, "Option --seconds does not apply to scan-drive.")]
    [DataRow(new[] { "watch", "C", "--maximum-size", "1" }, "Option --maximum-size does not apply to watch.")]
    [DataRow(new[] { "rescan", "C", "--allocation-delta", "1" }, "Option --allocation-delta does not apply to rescan.")]
    [DataRow(new[] { "journal", "C", "--seconds", "3" }, "Option --seconds does not apply to journal.")]
    [DataRow(new[] { "cache", "--keep-name", "a.txt" }, "Option --keep-name does not apply to cache.")]
    [DataRow(new[] { "cache", "--directories-only" }, "Option --directories-only does not apply to cache.")]
    [DataRow(new[] { "cache", "--seconds", "3" }, "Option --seconds does not apply to cache.")]
    [DataRow(new[] { "elevation-status", "--cache-directory", "folder" }, "Option --cache-directory does not apply to elevation-status.")]
    [DataRow(new[] { "elevation-status", "--clear" }, "Option --clear does not apply to elevation-status.")]
    public void TryParse_FlagTheModeNeverReads_IsRefusedWithTheFlagAndTheModeNamed(string[] arguments, string expected)
    {
        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual(expected, error);
    }

    [DataTestMethod]
    [DataRow("scan-drive")]
    [DataRow("watch")]
    [DataRow("rescan")]
    [DataRow("journal")]
    public void TryParse_ScanAndCacheFlags_ApplyToEveryModeThatOpensAnIndex(string mode)
    {
        Assert.IsTrue(WatchArguments.TryParse([mode, "C", "--keep-name", "a.txt", "--cache-directory", "folder"], out var parsed, out var error), error);

        Assert.AreEqual("folder", parsed.CacheDirectory);
        CollectionAssert.AreEqual(new[] { "a.txt" }, parsed.DirectoryScanFileNames!.ToArray());
    }

    [DataTestMethod]
    [DataRow("Aa", "BB")]
    [DataRow("b", "a")]
    [DataRow("keep63926.txt", "keep68897.txt")]
    [DataRow("keep20143.txt", "keep118021.txt")]
    [DataRow("policy96088.txt", "policy188525.txt")]
    public void PolicyDirectoryName_DistinctRetainedNames_GetDifferentDirectories(string keepOne, string keepTwo)
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", keepOne], out var one, out _));
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", keepTwo], out var two, out _));

        Assert.AreNotEqual(one.PolicyDirectoryName, two.PolicyDirectoryName);
    }

    [TestMethod]
    public void PolicyDirectoryName_IsThePrefixAndTheFull64DigitSha256()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", "a.txt"], out var parsed, out _));

        StringAssert.Matches(parsed.PolicyDirectoryName, new System.Text.RegularExpressions.Regex("^policy-[0-9a-f]{64}$"));
    }

    [TestMethod]
    public void PolicyDirectoryName_SameKeepNamesInAnotherOrder_IsTheSameDirectory()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", "a.txt,b.txt"], out var one, out _));
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", "b.txt, a.txt"], out var two, out _));

        Assert.AreEqual(one.PolicyDirectoryName, two.PolicyDirectoryName);
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
    public void ScanOptions_RetentionStates_MapToTheBrokerScanOptions()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--keep-name", "desktop.ini, .gitignore"], out var parsed, out _));
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C"], out var plain, out _));
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--directories-only"], out var directories, out _));

        var options = parsed.ScanOptions!;
        CollectionAssert.AreEqual(new[] { "desktop.ini", ".gitignore" }, options.DirectoryScanFileNames!.ToArray());
        Assert.AreEqual(0, directories.ScanOptions!.DirectoryScanFileNames!.Count);
        Assert.AreNotEqual(plain.PolicyDirectoryName, directories.PolicyDirectoryName);
        Assert.AreNotEqual(parsed.PolicyDirectoryName, directories.PolicyDirectoryName);
        Assert.AreEqual(new MFTLib.Index.CacheTag("SMPW", 2), WatchArguments.CacheTag);
        Assert.IsNull(plain.ScanOptions);
        Assert.AreNotEqual(plain.PolicyDirectoryName, parsed.PolicyDirectoryName, "A different retention policy is a different cache directory.");
    }
}
