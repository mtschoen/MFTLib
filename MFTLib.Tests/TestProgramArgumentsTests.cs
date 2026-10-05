using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

[TestClass]
public class TestProgramArgumentsTests
{
    [TestMethod]
    public void TryParse_NoArguments_FindsGitOnTheDefaultDrive()
    {
        Assert.IsTrue(TestProgramArguments.TryParse([], out var parsed, out var error));

        Assert.IsNull(error);
        Assert.AreEqual(ProgramMode.FindGit, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
        Assert.AreEqual(TestProgramArguments.DefaultWatchSeconds, parsed.WatchSeconds);
    }

    [TestMethod]
    public void TryParse_OnlyDriveLetters_FindsGitOnEachDrive()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(["C", "D:"], out var parsed, out _));

        Assert.AreEqual(ProgramMode.FindGit, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "C", "D:" }, parsed.Drives.ToArray());
    }

    [DataTestMethod]
    [DataRow("find-git", nameof(ProgramMode.FindGit))]
    [DataRow("scan-drive", nameof(ProgramMode.ScanDrive))]
    [DataRow("read-records", nameof(ProgramMode.ReadRecords))]
    [DataRow("usn-query", nameof(ProgramMode.UsnQuery))]
    [DataRow("usn-read", nameof(ProgramMode.UsnRead))]
    [DataRow("usn-watch", nameof(ProgramMode.UsnWatch))]
    [DataRow("USN-Watch", nameof(ProgramMode.UsnWatch))]
    [DataRow("stream-records", nameof(ProgramMode.StreamRecords))]
    [DataRow("volume-info", nameof(ProgramMode.VolumeInfo))]
    public void TryParse_ModeName_SelectsTheModeAndKeepsTheDrivesAfterIt(string name, string expected)
    {
        Assert.IsTrue(TestProgramArguments.TryParse([name, "C", "E"], out var parsed, out _));

        Assert.AreEqual(Enum.Parse<ProgramMode>(expected), parsed.Mode);
        CollectionAssert.AreEqual(new[] { "C", "E" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_FindName_KeepsTheDrivesAroundItsRequiredName()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(["find-name", "C", "--name", "x", "E"], out var parsed, out _));

        Assert.AreEqual(ProgramMode.FindName, parsed.Mode);
        CollectionAssert.AreEqual(new[] { "C", "E" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_ModeWithoutDrives_UsesTheDefaultDrive()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(["usn-query"], out var parsed, out _));

        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_WatchSeconds_AreReadFromTheOptionInEitherPosition()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(["usn-watch", "--seconds", "5", "C"], out var before, out _));
        Assert.IsTrue(TestProgramArguments.TryParse(["usn-watch", "C", "--seconds", "7"], out var after, out _));

        Assert.AreEqual(5, before.WatchSeconds);
        CollectionAssert.AreEqual(new[] { "C" }, before.Drives.ToArray());
        Assert.AreEqual(7, after.WatchSeconds);
    }

    [DataTestMethod]
    [DataRow("find-git", "--seconds", "5")]
    [DataRow("usn-watch", "--seconds")]
    [DataRow("usn-watch", "--seconds", "0")]
    [DataRow("usn-watch", "--seconds", "-3")]
    [DataRow("usn-watch", "--seconds", "soon")]
    [DataRow("usn-read", "--verbose")]
    public void TryParse_BadOption_FailsWithAMessage(params string[] arguments)
    {
        Assert.IsFalse(TestProgramArguments.TryParse(arguments, out _, out var error));

        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [DataTestMethod]
    [DataRow(nameof(ProgramMode.FindGit), true)]
    [DataRow(nameof(ProgramMode.ScanDrive), false)]
    [DataRow(nameof(ProgramMode.ReadRecords), true)]
    [DataRow(nameof(ProgramMode.UsnQuery), true)]
    [DataRow(nameof(ProgramMode.UsnRead), true)]
    [DataRow(nameof(ProgramMode.UsnWatch), true)]
    [DataRow(nameof(ProgramMode.FindName), true)]
    [DataRow(nameof(ProgramMode.StreamRecords), true)]
    [DataRow(nameof(ProgramMode.ParseFile), false)]
    [DataRow(nameof(ProgramMode.VolumeInfo), true)]
    [DataRow(nameof(ProgramMode.UsnGrow), true)]
    public void RequiresElevation_OnlyBrokerAndFileModesRunUnelevated(string mode, bool expected)
    {
        var arguments = new TestProgramArguments(Enum.Parse<ProgramMode>(mode), ["C"], 1);

        Assert.AreEqual(expected, arguments.RequiresElevation);
    }

    [DataTestMethod]
    [DataRow(nameof(ProgramMode.FindGit), 3, 10, null, 60000)]
    [DataRow(nameof(ProgramMode.UsnWatch), 1, 120, null, 60000 + 120_000)]
    [DataRow(nameof(ProgramMode.UsnWatch), 2, 120, null, 60000 + 240_000)]
    [DataRow(nameof(ProgramMode.UsnWatch), 4, int.MaxValue, null, int.MaxValue)]
    [DataRow(nameof(ProgramMode.StreamRecords), 1, 10, 120, 60000 + 120_000)]
    [DataRow(nameof(ProgramMode.StreamRecords), 2, 10, 120, 60000 + 240_000)]
    [DataRow(nameof(ProgramMode.StreamRecords), 2, 10, null, -1)]
    public void ElevationTimeout_CoversEveryRequestedDuration(string mode, int drives, int watchSeconds,
        int? streamTimeoutSeconds, int expected)
    {
        // A stream without a requested timeout gets Timeout.InfiniteTimeSpan (-1 ms): the scan runs unbounded.
        var arguments = new TestProgramArguments(Enum.Parse<ProgramMode>(mode),
            Enumerable.Repeat("C", drives).ToArray(), watchSeconds)
        {
            Options = new ModeOptions { TimeoutSeconds = streamTimeoutSeconds }
        };

        Assert.AreEqual(TimeSpan.FromMilliseconds(expected), arguments.ElevationTimeout);
    }

    [TestMethod]
    public void Usage_NamesEveryMode()
    {
        foreach (var mode in new[]
                 {
                     "find-git", "scan-drive", "read-records", "usn-query", "usn-read", "usn-watch", "find-name",
                     "stream-records", "parse-file", "volume-info", "usn-grow"
                 })
        {
            StringAssert.Contains(TestProgramArguments.Usage, mode);
        }
    }
}
