using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Index;
using SampleProgram.Watch;

namespace MFTLib.Tests;

[TestClass]
public class WatchArgumentsTests
{
    [TestMethod]
    public void TryParse_NoArguments_WatchesAndLeavesTheDrivesToTheHost()
    {
        Assert.IsTrue(WatchArguments.TryParse([], out var parsed, out var error));

        Assert.IsNull(error);
        Assert.AreEqual(WatchVerb.Watch, parsed!.Verb);
        Assert.IsEmpty(parsed.Roots);
        Assert.IsNull(parsed.Seconds);
        Assert.IsTrue(parsed.NeedsRoots);
    }

    [TestMethod]
    public void TryParse_OnlyDriveLetters_WatchesEachDrive()
    {
        Assert.IsTrue(WatchArguments.TryParse(["C", "D:"], out var parsed, out _));

        Assert.AreEqual(WatchVerb.Watch, parsed!.Verb);
        Assert.AreSequenceEqual(["C", "D"], [.. parsed.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void TryParse_RepeatedDrivesThatNormalizeToTheSameDrive_KeepsEachDriveOnce()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan", "c:", "C", "D"], out var parsed, out var error));

        Assert.IsNull(error);
        Assert.AreSequenceEqual(["C", "D"], [.. parsed!.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    [DataRow("scan")]
    [DataRow("SCAN")]
    public void TryParse_VerbName_SelectsTheVerbAndKeepsTheDrivesAfterIt(string name)
    {
        Assert.IsTrue(WatchArguments.TryParse([name, "C", "E"], out var parsed, out _));

        Assert.AreEqual(WatchVerb.Scan, parsed!.Verb);
        Assert.AreSequenceEqual(["C", "E"], [.. parsed.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void TryParse_VerbWithoutDrives_LeavesTheDrivesToTheHost()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan"], out var parsed, out _));

        Assert.AreEqual(WatchVerb.Scan, parsed!.Verb);
        Assert.IsEmpty(parsed.Roots);
    }

    [TestMethod]
    [DataRow("cache", false)]
    [DataRow("elevation-status", false)]
    [DataRow("rescan", true)]
    [DataRow("journal", true)]
    public void NeedsRoots_OnlyTheVerbsThatOpenAnIndex(string name, bool expected)
    {
        Assert.IsTrue(WatchArguments.TryParse([name], out var parsed, out _));

        Assert.AreEqual(expected, parsed!.NeedsRoots);
    }

    [TestMethod]
    public void TryParse_UnknownOption_IsRefusedAndNamed()
    {
        Assert.IsFalse(WatchArguments.TryParse(["scan", "C", "--verbose"], out var parsed, out var error));

        Assert.AreEqual("Unknown option --verbose.", error);
        Assert.IsNull(parsed);
    }

    [TestMethod]
    public void Need_Scan_IsTheBrokerLaunchBecauseTheBrokerIsTheElevatedProcess()
    {
        Assert.IsTrue(WatchArguments.TryParse(["scan", "C"], out var parsed, out _));

        Assert.AreEqual(ElevationNeed.BrokerLaunch, parsed!.Need);
    }

    [TestMethod]
    public void Usage_NamesEveryVerb()
    {
        foreach (var name in WatchVerbs.Names.Keys)
        {
            Assert.Contains(name, WatchArguments.Usage);
        }

        Assert.DoesNotContain("--keep-name", WatchArguments.Usage);
        Assert.DoesNotContain("--directories-only", WatchArguments.Usage);
    }

    [TestMethod]
    public void Usage_QualifiesTheUnattendedDefaultAsElevated()
    {
        Assert.Contains($"an unattended elevated one for {WatchArguments.DefaultSeconds} seconds", WatchArguments.Usage);
        Assert.Contains("an unattended run that is not elevated exits 1 without watching", WatchArguments.Usage);
    }

    [TestMethod]
    [DataRow("watch", "Watch")]
    [DataRow("rescan", "Rescan")]
    [DataRow("journal", "Journal")]
    public void TryParse_IndexVerb_SelectsItAndLeavesTheDrives(string name, string verb)
    {
        Assert.IsTrue(WatchArguments.TryParse([name], out var parsed, out _));

        Assert.AreEqual(verb, parsed!.Verb.ToString());
        Assert.IsEmpty(parsed.Roots);
        Assert.AreEqual(ElevationNeed.BrokerLaunch, parsed.Need);
    }

    [TestMethod]
    public void TryParse_WatchSeconds_AreRead()
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--seconds", "45"], out var parsed, out _));

        Assert.AreEqual(45, parsed!.Seconds);
        Assert.IsTrue(WatchArguments.TryParse(["watch"], out var plain, out _));
        Assert.IsNull(plain!.Seconds, "Without --seconds an attended watch accepts commands until Enter.");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("4294967295")]
    [DataRow("4294967296")]
    [DataRow("2147484")]
    public void TryParse_SecondsOutsideTheDelayRange_AreRefusedNamingTheRange(string seconds)
    {
        Assert.IsFalse(WatchArguments.TryParse(["watch", "C", "--seconds", seconds], out _, out var error));

        Assert.AreEqual("Option --seconds must be from 1 to 2147483.", error);
    }

    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("2147483", 2147483)]
    public void TryParse_SecondsAtTheEdgesOfTheDelayRange_AreAccepted(string seconds, int expected)
    {
        Assert.IsTrue(WatchArguments.TryParse(["watch", "C", "--seconds", seconds], out var parsed, out _));

        Assert.AreEqual(expected, parsed!.Seconds);
    }

    [TestMethod]
    [DataRow("watch")]
    [DataRow("rescan")]
    [DataRow("journal")]
    [DataRow("scan")]
    [DataRow("cache")]
    public void TryParse_EmptyOrGarbageDriveToken_IsRefusedForEveryVerb(string verb)
    {
        Assert.IsFalse(WatchArguments.TryParse([verb, string.Empty], out _, out var empty));
        Assert.IsFalse(WatchArguments.TryParse([verb, "Cjunk"], out _, out var garbage));
        Assert.IsFalse(WatchArguments.TryParse([verb, "C:x"], out _, out var path));

        Assert.AreEqual("Root '' is neither a drive nor a full path.", empty);
        Assert.AreEqual("Root 'Cjunk' is neither a drive nor a full path.", garbage);
        Assert.AreEqual("Root 'C:x' is neither a drive nor a full path.", path);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryParse_CacheWithAGarbageToken_IsRefusedWithAndWithoutClear(bool clear)
    {
        string[] arguments = clear ? ["cache", "junk", "--clear"] : ["cache", "junk"];

        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual("Root 'junk' is neither a drive nor a full path.", error);
    }

    [TestMethod]
    public void TryParse_DriveListWithOneBadToken_NamesThatToken()
    {
        Assert.IsFalse(WatchArguments.TryParse(["watch", "C", "9", "d:"], out _, out var error));

        Assert.AreEqual("Root '9' is neither a drive nor a full path.", error);
    }

    [TestMethod]
    public void TryParse_DriveTokens_AreNormalizedToUpperCaseLetters()
    {
        Assert.IsTrue(WatchArguments.TryParse(["cache", "c:", "d"], out var parsed, out _));

        Assert.AreSequenceEqual(["C", "D"], [.. parsed!.Roots.Select(root => root.ToString())]);
    }

    [TestMethod]
    public void TryParse_Journal_ReadsBothSizes()
    {
        Assert.IsTrue(WatchArguments.TryParse(["journal", "C", "--maximum-size", "9000", "--allocation-delta", "4096"], out var parsed, out _));

        Assert.AreEqual((9000L, 4096L), (parsed!.MaximumSize, parsed.AllocationDelta));
    }

    [TestMethod]
    [DataRow(new[] { "journal", "C", "--maximum-size", "9000" }, "journal needs --maximum-size and --allocation-delta together.")]
    [DataRow(new[] { "journal", "C", "--allocation-delta", "1" }, "journal needs --maximum-size and --allocation-delta together.")]
    [DataRow(new[] { "watch", "C", "--clear" }, "Option --clear does not apply to watch.")]
    [DataRow(new[] { "watch", "C", "--profile", "sparse" }, "Unknown option --profile.")]
    [DataRow(new[] { "watch", "C", "--seconds", "soon" }, "Option --seconds needs a whole number, not 'soon'.")]
    public void TryParse_BadFlags_AreRefusedWithTheirReason(string[] arguments, string expected)
    {
        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    [DataRow(new[] { "scan", "C", "--seconds", "5" }, "Option --seconds does not apply to scan.")]
    [DataRow(new[] { "C", "--allocation-delta", "5" }, "Option --allocation-delta does not apply to watch.")]
    [DataRow(new[] { "watch", "C", "--maximum-size", "1" }, "Option --maximum-size does not apply to watch.")]
    [DataRow(new[] { "rescan", "C", "--allocation-delta", "1" }, "Option --allocation-delta does not apply to rescan.")]
    [DataRow(new[] { "journal", "C", "--seconds", "3" }, "Option --seconds does not apply to journal.")]
    [DataRow(new[] { "watch", "C", "--keep-name", "a" }, "Option --keep-name does not apply to watch.")]
    [DataRow(new[] { "watch", "C", "--directories-only" }, "Option --directories-only does not apply to watch.")]
    [DataRow(new[] { "cache", "--keep-name", "a.txt" }, "Option --keep-name does not apply to cache.")]
    [DataRow(new[] { "cache", "--directories-only" }, "Option --directories-only does not apply to cache.")]
    [DataRow(new[] { "cache", "--seconds", "3" }, "Option --seconds does not apply to cache.")]
    [DataRow(new[] { "scan", "C", "--cache-directory", "folder" }, "Option --cache-directory does not apply to scan.")]
    [DataRow(new[] { "elevation-status", "--cache-directory", "folder" }, "Option --cache-directory does not apply to elevation-status.")]
    [DataRow(new[] { "elevation-status", "--clear" }, "Option --clear does not apply to elevation-status.")]
    public void TryParse_FlagTheVerbNeverReads_IsRefusedWithTheFlagAndTheVerbNamed(string[] arguments, string expected)
    {
        Assert.IsFalse(WatchArguments.TryParse(arguments, out _, out var error));

        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    [DataRow("watch")]
    [DataRow("rescan")]
    [DataRow("journal")]
    public void TryParse_CacheDirectory_AppliesToEveryVerbThatOpensAnIndex(string verb)
    {
        Assert.IsTrue(WatchArguments.TryParse([verb, "C", "--cache-directory", "folder"], out var parsed, out var error), error);

        Assert.AreEqual("folder", parsed!.CacheDirectory);
    }

    [TestMethod]
    [DataRow("scan")]
    [DataRow("watch")]
    [DataRow("rescan")]
    [DataRow("journal")]
    public void TryParse_ConnectTimeout_IsReadForEveryVerbThatLaunchesTheBroker(string verb)
    {
        Assert.IsTrue(WatchArguments.TryParse([verb, "C", "--connect-timeout", "90"], out var parsed, out var error), error);
        Assert.IsTrue(WatchArguments.TryParse([verb, "C"], out var plain, out _));

        Assert.AreEqual(90, parsed!.ConnectTimeoutSeconds);
        Assert.IsNull(plain!.ConnectTimeoutSeconds, "Without --connect-timeout the library default applies.");
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("2147484")]
    public void TryParse_ConnectTimeoutOutsideTheRange_IsRefusedNamingTheRange(string seconds)
    {
        Assert.IsFalse(WatchArguments.TryParse(["watch", "C", "--connect-timeout", seconds], out _, out var error));

        Assert.AreEqual("Option --connect-timeout must be from 1 to 2147483.", error);
    }

    [TestMethod]
    [DataRow("cache")]
    [DataRow("elevation-status")]
    public void TryParse_ConnectTimeoutOnAVerbThatLaunchesNoBroker_IsRefused(string verb)
    {
        Assert.IsFalse(WatchArguments.TryParse([verb, "--connect-timeout", "5"], out _, out var error));

        Assert.AreEqual($"Option --connect-timeout does not apply to {verb}.", error);
    }

    [TestMethod]
    public void Usage_NamesTheConnectTimeoutFlag()
    {
        Assert.Contains("--connect-timeout N", WatchArguments.Usage);
    }

    [TestMethod]
    public void TryParse_CacheAndElevationStatus_NeedNoBrokerAndKeepAnEmptyDriveList()
    {
        Assert.IsTrue(WatchArguments.TryParse(["cache", "--cache-directory", "cache-folder", "--clear"], out var cache, out _));
        Assert.IsTrue(WatchArguments.TryParse(["elevation-status"], out var status, out _));

        Assert.AreEqual(ElevationNeed.None, cache!.Need);
        Assert.AreEqual(ElevationNeed.None, status!.Need);
        Assert.IsEmpty(cache.Roots);
        Assert.AreEqual("cache-folder", cache.CacheDirectory);
        Assert.IsTrue(cache.Clear);
    }

    [TestMethod]
    [DataRow(null, ProducerSelection.Automatic)]
    [DataRow("automatic", ProducerSelection.Automatic)]
    [DataRow("mft", ProducerSelection.MftOnly)]
    [DataRow("WALK", ProducerSelection.WalkOnly)]
    public void TryParse_ProducerFlag_SelectsTheProducer(string? text, ProducerSelection expected)
    {
        string[] arguments = text is null ? ["scan", "C"] : ["scan", "C", "--producer", text];

        Assert.IsTrue(WatchArguments.TryParse(arguments, out var parsed, out var error), error);

        Assert.AreEqual(expected, parsed!.Producer);
    }

    [TestMethod]
    public void TryParse_UnknownProducer_IsAUsageError()
    {
        Assert.IsFalse(WatchArguments.TryParse(["scan", "C", "--producer", "fast"], out _, out var error));

        Assert.AreEqual("Option --producer must be automatic, mft or walk.", error);
    }

    [TestMethod]
    public void TryParse_ADirectoryRoot_ParsesAsADirectoryKey()
    {
        var directory = Path.GetFullPath(Path.GetTempPath());

        Assert.IsTrue(WatchArguments.TryParse(["scan", directory], out var parsed, out var error), error);

        Assert.AreSequenceEqual([RootKey.FromDirectory(directory)], parsed!.Roots);
    }

    [TestMethod]
    public void CacheTag_IsSmpw3() =>
        Assert.AreEqual(new CacheTag("SMPW", 3), WatchArguments.CacheTag);
}
