using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The command line of the Index verbs: what parses, what each verb requires, and what it rejects before any
// index opens.
[TestClass]
public class TestProgramIndexVerbArgumentsTests
{
    [TestMethod]
    public void TryParse_AnIndexVerb_SelectsTheIndexModeWithNoElevationRequirement()
    {
        var parsed = TestProgramArguments.TryParse(["search", "c:", "--name", "readme"], out var arguments, out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(ProgramMode.IndexVerb, arguments.Mode);
        Assert.IsFalse(arguments.RequiresElevation);
        CollectionAssert.AreEqual(new[] { "C" }, arguments.Drives.ToArray());
        Assert.AreEqual("search", arguments.Index!.Verb);
        Assert.AreEqual("readme", arguments.Index.Text("--name"));
    }

    [TestMethod]
    public void Usage_NamesEveryIndexVerb()
    {
        foreach (var verb in IndexVerbArguments.VerbNames)
        {
            StringAssert.Contains(TestProgramArguments.Usage, verb);
        }

        Assert.AreEqual(14, IndexVerbArguments.VerbNames.Count);
    }

    [TestMethod]
    public void TryParse_NoDrive_DefaultsToTheSampleDriveForOpeningVerbs()
    {
        IndexVerbArguments.TryParse(["largest", "--source", "enumeration", "--root", "x"], out var arguments, out _);

        CollectionAssert.AreEqual(new[] { 'G' }, arguments!.Drives.ToArray());
    }

    [TestMethod]
    public void TryParse_CacheVerbsWithoutDrives_MeanEveryDrive()
    {
        IndexVerbArguments.TryParse(["cache-inspect"], out var arguments, out _);

        Assert.AreEqual(0, arguments!.Drives.Count);
    }

    [TestMethod]
    public void TryParse_EveryTypedOption_ReadsBackItsValue()
    {
        var parsed = IndexVerbArguments.TryParse(
            ["search", "d", "--source", "enumeration", "--root", "r", "--volume-serial", "77", "--cache-tag", "ABCD:9",
             "--min-size", "0", "--max-size", "12", "--after", "2020-02-03", "--before", "2021-02-03T04:05:06Z",
             "--limit", "4", "--timeout-seconds", "9"], out var arguments, out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(77L, arguments!.Number("--volume-serial"));
        Assert.AreEqual(0L, arguments.Number("--min-size"));
        Assert.AreEqual(12L, arguments.Number("--max-size"));
        Assert.AreEqual(new DateTime(2020, 2, 3, 0, 0, 0, DateTimeKind.Utc), arguments.Date("--after"));
        Assert.AreEqual(new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc), arguments.Date("--before"));
        Assert.AreEqual(new CacheTag("ABCD", 9), arguments.Tag());
        Assert.IsNull(arguments.RecordKey());
    }

    [TestMethod]
    public void TryParse_RecordKey_BuildsTheKeyThroughThePublicConstructor()
    {
        IndexVerbArguments.TryParse(["tree", "s", "--source", "enumeration", "--root", "r", "--record-key", "s:42:enumeration"],
            out var arguments, out var error);

        Assert.AreEqual(new IndexRecordKey('S', 42, ProducerKind.Enumeration), arguments!.RecordKey(), error);
    }

    [TestMethod]
    public void TryParse_RepeatedKeepName_CollectsEveryName()
    {
        IndexVerbArguments.TryParse(["search", "c", "--profile", "directories", "--keep-name", "a.txt", "--keep-name", "b.txt"],
            out var arguments, out var error);

        CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, arguments!.TextList("--keep-name").ToArray(), error);
        Assert.IsTrue(arguments.Has("--profile"));
        Assert.IsFalse(arguments.Has("--no-cache"));
    }

    [TestMethod]
    public void TryParse_DriveListScope_ReadsTheLetters()
    {
        IndexVerbArguments.TryParse(["rescan", "c", "d", "--source", "enumeration", "--root", "x"], out _, out var rootError);
        IndexVerbArguments.TryParse(["rescan", "c", "d", "--drive-list", "d,c"], out var arguments, out var error);

        Assert.AreEqual("--root names one directory, so it takes one drive letter.", rootError);
        CollectionAssert.AreEqual(new[] { 'D', 'C' }, arguments!.Letters("--drive-list")!.ToArray(), error);
        Assert.IsNull(arguments.Letters("--drive-scope"));
    }

    [DataTestMethod]
    [DataRow(new[] { "search", "c", "--name" }, "--name needs a non-empty value.")]
    [DataRow(new[] { "search", "c", "--name", "a", "--name", "b" }, "--name was given twice.")]
    [DataRow(new[] { "search", "c", "--bogus" }, "--bogus does not apply to search.")]
    [DataRow(new[] { "search", "cc" }, "cc is not a drive letter.")]
    [DataRow(new[] { "search", "c", "--limit", "0" }, "--limit needs a positive whole number that fits in an int.")]
    [DataRow(new[] { "search", "c", "--min-size", "-1" }, "--min-size needs a whole number of bytes, zero or more.")]
    [DataRow(new[] { "search", "c", "--after", "soon" }, "--after needs a date")]
    [DataRow(new[] { "search", "c", "--source", "tape" }, "--source needs one of broker, enumeration, unavailable.")]
    [DataRow(new[] { "search", "c", "--cache-tag", "ABC:1" }, "--cache-tag needs FOURCC:VERSION")]
    [DataRow(new[] { "search", "c", "--timeout-seconds", "2000000" }, "--timeout-seconds needs a positive number of seconds")]
    [DataRow(new[] { "search", "c", "--source", "enumeration", "--root", "r", "--volume-serial", "5000000000" },
        "--volume-serial needs a positive volume serial that fits in 32 bits.")]
    [DataRow(new[] { "tree", "c", "--record-key", "c:1" }, "--record-key needs DRIVE:ROW:PRODUCER")]
    [DataRow(new[] { "tree", "c", "--record-key", "c:1:Dump" }, "--record-key needs DRIVE:ROW:PRODUCER")]
    [DataRow(new[] { "rescan", "c", "--drive-list", "c,,d" }, "--drive-list needs drive letters such as G or G,H")]
    [DataRow(new[] { "search", "c", "--source", "enumeration" }, "--source enumeration needs --root DIRECTORY.")]
    [DataRow(new[] { "search", "c", "--root", "r" }, "--root and --volume-serial apply only to --source enumeration or unavailable.")]
    [DataRow(new[] { "search", "c", "--source", "enumeration", "--root", "r", "--profile", "full" },
        "--profile, --keep-name and --connection-timeout apply only to --source broker.")]
    [DataRow(new[] { "search", "c", "--keep-name", "a" }, "--keep-name needs --profile directories.")]
    [DataRow(new[] { "search", "c", "--unavailable-reason", "why" }, "--unavailable-reason applies only to --source unavailable.")]
    [DataRow(new[] { "search", "c", "--source", "unavailable", "--volume-serial", "3" }, "--volume-serial needs --root.")]
    [DataRow(new[] { "search", "c", "--directories", "--files" }, "--directories and --files exclude each other.")]
    [DataRow(new[] { "search", "c", "--min-size", "9", "--max-size", "3" }, "--min-size is larger than --max-size.")]
    [DataRow(new[] { "search", "c", "--after", "2025-01-01", "--before", "2024-01-01" }, "--after is later than --before.")]
    [DataRow(new[] { "search", "c", "--exact" }, "--exact needs --name and filters by name only")]
    [DataRow(new[] { "search", "c", "--exact", "--name", "a", "--files" }, "--exact needs --name and filters by name only")]
    [DataRow(new[] { "tree", "c", "--path", "p", "--record-key", "c:1:Mft" }, "--path and --record-key select the start differently; give one.")]
    [DataRow(new[] { "open", "c" }, "open needs --path FILE.")]
    [DataRow(new[] { "rescan", "c", "--all", "--drive-scope", "c" }, "--drive-scope, --drive-list and --all select one scope each; give one.")]
    [DataRow(new[] { "rescan", "c", "--drive-scope", "d" }, "--drive-scope needs one of the opened drive letters.")]
    [DataRow(new[] { "watch", "c", "--drive-list", "c,d" }, "--drive-list names a drive that is not opened.")]
    [DataRow(new[] { "journal-grow", "--maximum-size", "5", "--allocation-delta", "1" },
        "journal-grow needs exactly one drive letter; it never defaults to a drive.")]
    [DataRow(new[] { "journal-grow", "c", "d", "--maximum-size", "5", "--allocation-delta", "1" },
        "journal-grow needs exactly one drive letter; it never defaults to a drive.")]
    [DataRow(new[] { "journal-grow", "c", "--maximum-size", "5" }, "journal-grow needs both --maximum-size and --allocation-delta.")]
    [DataRow(new[] { "cache-clear" }, "cache-clear needs exactly one of --cache-directory DIRECTORY and --default-cache.")]
    [DataRow(new[] { "cache-clear", "--cache-directory", "x", "--default-cache" },
        "cache-clear needs exactly one of --cache-directory DIRECTORY and --default-cache.")]
    public void TryParse_AnInvalidCommandLine_NamesTheProblem(string[] commandLine, string expected)
    {
        var parsed = IndexVerbArguments.TryParse(commandLine, out _, out var error);

        Assert.IsFalse(parsed);
        StringAssert.StartsWith(error, expected);
    }

    [TestMethod]
    public void TryParse_AValidCommandLineForEveryVerb_Parses()
    {
        var commandLines = new[]
        {
            new[] { "search", "c", "--name", "a" }, ["enumerate", "c"], ["find-path", "c", "--path", "p"],
            ["tree", "c", "--depth", "2"], ["open", "c", "--path", "p", "--bytes", "9"], ["largest", "c", "--count", "3"],
            ["duplicate-names", "c"], ["rescan", "c", "--all"], ["watch", "c", "--seconds", "2", "--inspect-session"],
            ["journal-status", "c", "--wait-catch-up"], ["journal-grow", "c", "--maximum-size", "5", "--allocation-delta", "1"],
            ["cache-inspect", "--ensure-created"], ["cache-clear", "--default-cache"], ["elevation-status", "--relaunch-elevated"]
        };

        foreach (var commandLine in commandLines)
        {
            Assert.IsTrue(IndexVerbArguments.TryParse(commandLine, out _, out var error), $"{commandLine[0]}: {error}");
        }

        Assert.AreEqual(IndexVerbArguments.VerbNames.Count, commandLines.Length);
    }

    [TestMethod]
    public void IsVerb_IgnoresCaseAndRejectsTheRawModes()
    {
        Assert.IsTrue(IndexVerbArguments.IsVerb("SEARCH"));
        Assert.IsFalse(IndexVerbArguments.IsVerb("find-git"));
        Assert.IsFalse(IndexVerbArguments.IsVerb("C"));
    }
}
