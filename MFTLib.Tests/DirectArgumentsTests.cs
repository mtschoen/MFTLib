using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The Direct sample's command line: every verb, every flag, and the combinations it refuses.
[TestClass]
public class DirectArgumentsTests
{
    static DirectArguments Parse(params string[] arguments)
    {
        Assert.IsTrue(DirectArguments.TryParse(arguments, out var parsed, out var error), error);
        Assert.IsNull(error);
        return parsed;
    }

    static string Refused(params string[] arguments)
    {
        Assert.IsFalse(DirectArguments.TryParse(arguments, out var parsed, out var error));
        Assert.IsNull(parsed);
        return error;
    }

    [TestMethod]
    public void TryParse_SearchWithEveryFlag_ReadsEachOne()
    {
        var parsed = Parse("search", "c:", "--name", "*.log", "--exact", "--case-sensitive", "--under", "C:\\logs",
            "--directories", "--min-size", "10", "--max-size", "2000", "--after", "2026-01-02", "--before", "2026-03-04",
            "--stream", "--limit", "7", "--include-freed");

        Assert.AreEqual(DirectVerb.Search, parsed.Verb);
        Assert.AreEqual("C", parsed.Drive);
        Assert.AreEqual("*.log", parsed.Name);
        Assert.IsTrue(parsed.Exact);
        Assert.IsTrue(parsed.CaseSensitive);
        Assert.AreEqual("C:\\logs", parsed.Under);
        Assert.AreEqual(true, parsed.Directories);
        Assert.AreEqual(10L, parsed.MinimumSize);
        Assert.AreEqual(2000L, parsed.MaximumSize);
        Assert.AreEqual(new DateTime(2026, 1, 2), parsed.After);
        Assert.AreEqual(new DateTime(2026, 3, 4), parsed.Before);
        Assert.IsTrue(parsed.Stream);
        Assert.AreEqual(7, parsed.Limit);
        Assert.IsTrue(parsed.IncludeFreed);
        Assert.AreEqual(SourceKind.Local, parsed.Source);
        Assert.IsTrue(parsed.RequiresElevation);
    }

    [TestMethod]
    public void TryParse_SearchWithNoFlags_UsesTheDefaults()
    {
        var parsed = Parse("SEARCH", "D");

        Assert.AreEqual(DirectArguments.DefaultLimit, parsed.Limit);
        Assert.IsNull(parsed.Directories);
        Assert.IsFalse(parsed.IncludeFreed);
        Assert.IsFalse(parsed.Stream);
    }

    [TestMethod]
    public void TryParse_Files_SelectsNonDirectories()
    {
        Assert.AreEqual(false, Parse("search", "C", "--files").Directories);
    }

    [TestMethod]
    public void TryParse_TreeOpenLargestDuplicateNamesAndScan_ReadTheirFlags()
    {
        var tree = Parse("tree", "C", "--path", "C:\\a", "--depth", "4");
        var open = Parse("open", "C", "--path", "C:\\a\\b.txt");
        var largest = Parse("largest", "C", "--count", "3", "--under", "C:\\a");
        var duplicates = Parse("duplicate-names", "C", "--count", "5");
        var scan = Parse("scan", "C");

        Assert.AreEqual(("C:\\a", 4), (tree.Path, tree.Depth));
        Assert.AreEqual("C:\\a\\b.txt", open.Path);
        Assert.AreEqual((3, "C:\\a"), (largest.Count, largest.Under));
        Assert.AreEqual(5, duplicates.Count);
        Assert.AreEqual(DirectVerb.Scan, scan.Verb);
    }

    [TestMethod]
    public void TryParse_DumpSource_NeedsNoElevation()
    {
        var parsed = Parse("search", "D", "--source", "dump", "--dump-file", "volume.mft");

        Assert.AreEqual(SourceKind.Dump, parsed.Source);
        Assert.AreEqual("volume.mft", parsed.DumpFile);
        Assert.IsFalse(parsed.RequiresElevation);
    }

    [DataTestMethod]
    [DataRow(new string[0], "A verb is required.")]
    [DataRow(new[] { "list", "C" }, "Unknown verb list.")]
    [DataRow(new[] { "search" }, "Expected exactly one drive letter.")]
    [DataRow(new[] { "search", "C", "D" }, "Expected exactly one drive letter.")]
    [DataRow(new[] { "search", "1" }, "'1' is not a drive letter.")]
    [DataRow(new[] { "search", "Cjunk" }, "'Cjunk' is not a drive letter.")]
    [DataRow(new[] { "search", "C:\\" }, "'C:\\' is not a drive letter.")]
    [DataRow(new[] { "search", "C", "--limit", "99999999999" }, "Option --limit must be at most 2147483647.")]
    [DataRow(new[] { "tree", "C", "--depth", "-1" }, "Option --depth needs a whole number, not '-1'.")]
    [DataRow(new[] { "search", "C", "--verbose" }, "Unknown option --verbose.")]
    [DataRow(new[] { "search", "C", "--limit" }, "Option --limit needs a value.")]
    [DataRow(new[] { "search", "C", "--limit", "many" }, "Option --limit needs a whole number, not 'many'.")]
    [DataRow(new[] { "search", "C", "--after", "soon" }, "Option --after needs a date such as 2026-10-01, not 'soon'.")]
    [DataRow(new[] { "search", "C", "--source", "tape" }, "Unknown source tape.")]
    [DataRow(new[] { "search", "C", "--source", "dump" }, "--source dump needs --dump-file PATH.")]
    [DataRow(new[] { "search", "C", "--dump-file", "x.mft" }, "--dump-file needs --source dump.")]
    [DataRow(new[] { "search", "C", "--directories", "--files" }, "--directories and --files exclude each other.")]
    [DataRow(new[] { "open", "C" }, "open needs --path P.")]
    public void TryParse_BadCommandLine_IsRefusedWithItsReason(string[] arguments, string expected)
    {
        Assert.AreEqual(expected, Refused(arguments));
    }

    [TestMethod]
    public void TryParse_IncludeFreedOnADump_IsRefusedBecauseADumpNeverYieldsFreedRows()
    {
        var error = Refused("search", "D", "--source", "dump", "--dump-file", "volume.mft", "--include-freed");

        StringAssert.Contains(error, "a dump never yields freed rows");
    }

    [TestMethod]
    public void IncludeFreed_SetsTheScanOptionAndTheQueryFlagTogether()
    {
        var withFlag = Parse("search", "C", "--include-freed");
        var withoutFlag = Parse("search", "C");

        Assert.IsTrue(SampleHost.ScanOptions(withFlag).IncludeFreed);
        Assert.IsTrue(SampleHost.BuildQuery(withFlag, null).IncludeDeleted);
        Assert.IsFalse(SampleHost.ScanOptions(withoutFlag).IncludeFreed);
        Assert.IsFalse(SampleHost.BuildQuery(withoutFlag, null).IncludeDeleted);
    }

    [DataTestMethod]
    [DataRow("report", false, NameMatchMode.Substring)]
    [DataRow("report", true, NameMatchMode.Exact)]
    [DataRow("*.log", false, NameMatchMode.Glob)]
    [DataRow("a?.txt", false, NameMatchMode.Glob)]
    public void BuildQuery_NamePattern_PicksTheMatchMode(string pattern, bool exact, NameMatchMode expected)
    {
        var parsed = exact ? Parse("search", "C", "--name", pattern, "--exact") : Parse("search", "C", "--name", pattern);

        var query = SampleHost.BuildQuery(parsed, null);

        Assert.AreEqual(expected, query.MatchMode);
        Assert.AreEqual(pattern, query.NamePattern);
    }

    [TestMethod]
    public void BuildQuery_EveryFlag_LandsOnItsQueryMember()
    {
        var parsed = Parse("search", "C", "--case-sensitive", "--files", "--min-size", "1", "--max-size", "9",
            "--after", "2026-01-01", "--before", "2026-02-01");

        var query = SampleHost.BuildQuery(parsed, null);

        Assert.IsTrue(query.CaseSensitive);
        Assert.AreEqual(false, query.Directories);
        Assert.AreEqual((1L, 9L), (query.MinimumSize, query.MaximumSize));
        Assert.AreEqual(new DateTime(2026, 1, 1), query.ModifiedAfter);
        Assert.AreEqual(new DateTime(2026, 2, 1), query.ModifiedBefore);
    }
}
