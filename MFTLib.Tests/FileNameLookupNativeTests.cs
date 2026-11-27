using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The native record reader's file name lookup, through a dump file so the same records exist on Windows and Linux.
///     It answers the name and parent a scan gives a file (the first non-DOS <c>$FILE_NAME</c> of the base record, or of
///     the lowest-numbered extension record the base's attribute list names), and never a stale answer for a record that
///     is free or carries another sequence number
///     (issue 1008).
/// </summary>
[TestClass]
public sealed class FileNameLookupNativeTests : IDisposable
{
    readonly TempDirectory _directory = new("mftlib-file-names");

    public void Dispose() => _directory.Dispose();

    [TestMethod]
    [TestCategory("Smoke")]
    public void Lookup_BaseRecordWithItsOwnName_ReturnsTheNameAndParent()
    {
        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Standard());

        var answers = Read(path, (7, 1), (9, 1));

        Assert.AreEqual(new FileNameResolution(7, 1, FileNameLookupOutcome.Resolved, 6, "Notes.txt"), answers[0]);
        Assert.AreEqual(new FileNameResolution(9, 1, FileNameLookupOutcome.Resolved, 8, "leaf.txt"), answers[1]);
    }

    [TestMethod]
    public void Lookup_RecordFreedReusedOrPastTheEnd_IsSuperseded()
    {
        var records = MftDumpFixture.StandardRecords();
        records[11] = new DumpRecord("freed.txt", 5) { InUse = false };
        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(1024, 16, records));

        var answers = Read(path, (7, 2), (11, 1), (99, 1));

        Assert.IsTrue(answers.All(answer => answer.Outcome == FileNameLookupOutcome.Superseded),
            string.Join(", ", answers));
    }

    [TestMethod]
    [DataRow(true, DisplayName = "base keeps only its DOS name")]
    [DataRow(false, DisplayName = "base keeps no name")]
    public void Lookup_NameOnlyInAnExtensionRecord_ReturnsTheExtensionsName(bool baseKeepsDosName)
    {
        var path = MftDumpFixture.WriteFile(_directory.Path, ExtensionNameFixture.Build(new ExtensionNameFixture.Options
        {
            BaseKeepsDosName = baseKeepsDosName,
            ListsExtensions = true
        }));

        var answer = Read(path, (ExtensionNameFixture.BaseRecordNumber, ExtensionNameFixture.BaseSequenceNumber)).Single();

        Assert.AreEqual(new FileNameResolution(ExtensionNameFixture.BaseRecordNumber, ExtensionNameFixture.BaseSequenceNumber,
            FileNameLookupOutcome.Resolved, 5, ExtensionNameFixture.DirectoryName), answer);
    }

    [TestMethod]
    public void Lookup_TwoExtensionRecordsWithNames_TakesTheLowerNumberedOneAsTheScanDoes()
    {
        var path = MftDumpFixture.WriteFile(_directory.Path, ExtensionNameFixture.Build(new ExtensionNameFixture.Options
        {
            SecondExtensionRecordNumber = 8,
            ListsExtensions = true
        }));

        var answer = Read(path, (ExtensionNameFixture.BaseRecordNumber, ExtensionNameFixture.BaseSequenceNumber)).Single();

        Assert.AreEqual("Second", answer.Name);
    }

    /// <summary>An extension that does not refer back to this incarnation, and a list with no entries, name nothing.</summary>
    [TestMethod]
    [DataRow("sequence", DisplayName = "extension names an earlier base incarnation")]
    [DataRow("freed", DisplayName = "extension is free")]
    [DataRow("placeholder", DisplayName = "attribute list has no entries")]
    public void Lookup_NoEligibleExtensionName_IsUnreadable(string scenario)
    {
        var options = scenario switch
        {
            "sequence" => new ExtensionNameFixture.Options { ReferencedSequenceNumber = 4, ListsExtensions = true },
            "freed" => new ExtensionNameFixture.Options { ExtensionInUse = false, ListsExtensions = true },
            "placeholder" => new ExtensionNameFixture.Options(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var path = MftDumpFixture.WriteFile(_directory.Path, ExtensionNameFixture.Build(options));

        var answer = Read(path, (ExtensionNameFixture.BaseRecordNumber, ExtensionNameFixture.BaseSequenceNumber)).Single();

        Assert.AreEqual(FileNameLookupOutcome.Unreadable, answer.Outcome);
    }

    [TestMethod]
    public void Lookup_ExtensionRecordItself_IsUnreadable()
    {
        var path = MftDumpFixture.WriteFile(_directory.Path, ExtensionNameFixture.Build(new ExtensionNameFixture.Options
        {
            ListsExtensions = true
        }));

        var answer = Read(path, (24, ExtensionNameFixture.ExtensionSequenceNumber)).Single();

        Assert.AreEqual(FileNameLookupOutcome.Unreadable, answer.Outcome, "an extension record names no file of its own");
    }

    static FileNameResolution[] Read(string dumpPath, params (long Record, ushort Sequence)[] wanted)
    {
        var requests = wanted.Select((pair, position) => new InlineContentRequest(position, pair.Record, pair.Sequence))
            .OrderBy(request => request.RecordNumber).ToArray();
        var answers = new FileNameResolution?[wanted.Length];
        using var input = MftDumpInput.Open(dumpPath);
        using var reader = input.OpenInlineContentReader();
        reader.ReadFileNames(requests, (position, outcome, content) =>
        {
            Assert.IsNull(answers[position], $"position {position} was answered twice");
            answers[position] = FileNameResolution.FromLookup((ulong)wanted[position].Record, wanted[position].Sequence,
                outcome, content);
        }, CancellationToken.None);
        return [.. answers.Select(answer => answer ?? throw new AssertFailedException("a position was not answered"))];
    }
}
