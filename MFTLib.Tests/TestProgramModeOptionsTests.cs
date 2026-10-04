using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The options that tune a mode's library call: what each mode accepts, what it rejects, and what a
// mode requires before it may run.
[TestClass]
public class TestProgramModeOptionsTests
{
    [TestMethod]
    public void TryParse_FindName_ReadsTheNameAndTheMatchOptions()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(
            ["find-name", "C", "--name", "git", "--contains", "--include-freed", "--no-paths", "--buffer-size", "512"],
            out var parsed, out _));

        var options = parsed.Options;
        Assert.AreEqual("git", options.Name);
        Assert.IsTrue(options.Contains);
        Assert.AreEqual(512u, options.BufferSizeRecords);
        Assert.AreEqual(MatchFlags.Contains | MatchFlags.IncludeFreed, options.ToMatchFlags());
        CollectionAssert.AreEqual(new[] { "C" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void ToMatchFlags_NameWithoutContains_IsExactAndResolvesPaths()
    {
        var options = new ModeOptions { Name = "a" };

        Assert.AreEqual(MatchFlags.ExactMatch | MatchFlags.ResolvePaths, options.ToMatchFlags());
    }

    [TestMethod]
    public void ToMatchFlags_NoOptions_OnlyResolvesPaths()
    {
        Assert.AreEqual(MatchFlags.ResolvePaths, new ModeOptions().ToMatchFlags());
        Assert.AreEqual(MatchFlags.None, new ModeOptions { NoPaths = true }.ToMatchFlags());
    }

    [TestMethod]
    public void TryParse_StreamRecords_ReadsThreadsBatchSizeAndTimeout()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(
            ["stream-records", "--threads", "3", "--batch-size", "100", "--timeout-seconds", "9", "D"],
            out var parsed, out _));

        Assert.AreEqual(3, parsed.Options.ParseThreads);
        Assert.AreEqual(100, parsed.Options.BatchSize);
        Assert.AreEqual(9, parsed.Options.TimeoutSeconds);
        CollectionAssert.AreEqual(new[] { "D" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_ReadRecords_ReadsTheTimingAndPathOptions()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(["read-records", "--timings", "--no-paths"], out var parsed, out _));

        Assert.IsTrue(parsed.Options.Timings);
        Assert.IsTrue(parsed.Options.NoPaths);
        CollectionAssert.AreEqual(new[] { "G" }, parsed.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_ParseFile_TakesThePathInsteadOfADrive()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(
            ["parse-file", "image.mft", "--stream", "--batch-size", "4", "--name", "a"], out var parsed, out _));

        Assert.AreEqual("image.mft", parsed.Options.FilePath);
        Assert.IsTrue(parsed.Options.Stream);
        Assert.AreEqual(0, parsed.Drives.Count);
        Assert.IsFalse(parsed.RequiresElevation);
    }

    [TestMethod]
    public void TryParse_UsnGrow_ReadsBothSizesAndOneDrive()
    {
        Assert.IsTrue(TestProgramArguments.TryParse(
            ["usn-grow", "c:", "--maximum-size", "8589934592", "--allocation-delta", "1048576"], out var parsed, out _));

        Assert.AreEqual(8589934592L, parsed.Options.MaximumSize);
        Assert.AreEqual(1048576L, parsed.Options.AllocationDelta);
        CollectionAssert.AreEqual(new[] { "c:" }, parsed.Drives.ToArray());
    }

    [DataTestMethod]
    [DataRow("find-git", "--name", "x")]
    [DataRow("find-git", "--no-paths")]
    [DataRow("find-name", "--name", "x", "--threads", "2")]
    [DataRow("find-name", "--name", "x", "--timings")]
    [DataRow("read-records", "--include-freed")]
    [DataRow("read-records", "--name", "x")]
    [DataRow("stream-records", "--stream")]
    [DataRow("stream-records", "--maximum-size", "5")]
    [DataRow("parse-file", "image.mft", "--threads", "2")]
    [DataRow("parse-file", "image.mft", "--timeout-seconds", "2")]
    [DataRow("usn-query", "--buffer-size", "10")]
    [DataRow("usn-watch", "--include-freed")]
    [DataRow("volume-info", "--buffer-size", "10")]
    [DataRow("usn-grow", "C", "--maximum-size", "2", "--allocation-delta", "1", "--no-paths")]
    public void TryParse_OptionOnAModeItDoesNotApplyTo_FailsNamingTheOption(params string[] arguments)
    {
        Assert.IsFalse(TestProgramArguments.TryParse(arguments, out _, out var error));

        StringAssert.Contains(error, "does not apply to");
    }

    [DataTestMethod]
    [DataRow("--threads", "stream-records", "--threads")]
    [DataRow("--threads", "stream-records", "--threads", "0")]
    [DataRow("--threads", "stream-records", "--threads", "many")]
    [DataRow("--batch-size", "stream-records", "--batch-size", "-1")]
    [DataRow("--timeout-seconds", "stream-records", "--timeout-seconds", "0")]
    [DataRow("--buffer-size", "read-records", "--buffer-size", "0")]
    [DataRow("--buffer-size", "read-records", "--buffer-size", "5000000000")]
    [DataRow("--name", "find-name", "--name")]
    [DataRow("--maximum-size", "usn-grow", "C", "--maximum-size", "big")]
    [DataRow("--allocation-delta", "usn-grow", "C", "--allocation-delta", "0")]
    public void TryParse_OptionWithABadValue_FailsNamingTheOption(string option, params string[] arguments)
    {
        Assert.IsFalse(TestProgramArguments.TryParse(arguments, out _, out var error));

        StringAssert.Contains(error, $"{option} needs");
    }

    [DataTestMethod]
    [DataRow("find-name needs --name", "find-name", "C")]
    [DataRow("--contains needs --name", "stream-records", "C", "--contains")]
    [DataRow("exactly one MFT file path", "parse-file")]
    [DataRow("exactly one MFT file path", "parse-file", "a.mft", "b.mft")]
    [DataRow("--batch-size needs --stream", "parse-file", "a.mft", "--batch-size", "5")]
    [DataRow("exactly one drive letter", "usn-grow", "--maximum-size", "2", "--allocation-delta", "1")]
    [DataRow("exactly one drive letter", "usn-grow", "C", "D", "--maximum-size", "2", "--allocation-delta", "1")]
    [DataRow("both --maximum-size and --allocation-delta", "usn-grow", "C", "--maximum-size", "2")]
    [DataRow("both --maximum-size and --allocation-delta", "usn-grow", "C", "--allocation-delta", "2")]
    public void TryParse_MissingWhatTheModeRequires_FailsWithAMessage(string expected, params string[] arguments)
    {
        Assert.IsFalse(TestProgramArguments.TryParse(arguments, out _, out var error));

        StringAssert.Contains(error, expected);
    }

    [TestMethod]
    public void RequiredOptions_NotSupplied_ThrowAnExplainedBug()
    {
        var options = new ModeOptions();

        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => options.RequiredName).Message, "--name");
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => options.RequiredFilePath).Message, "file path");
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => options.RequiredMaximumSize).Message, "--maximum-size");
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() => options.RequiredAllocationDelta).Message,
            "--allocation-delta");
    }

    [TestMethod]
    public void NameOf_EveryMode_RoundTripsThroughTheModeTable()
    {
        foreach (var mode in Enum.GetValues<ProgramMode>())
        {
            Assert.AreEqual(mode, ProgramModes.Names[ProgramModes.NameOf(mode)]);
        }
    }
}
