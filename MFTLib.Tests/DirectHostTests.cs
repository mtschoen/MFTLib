using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The Direct sample's verbs end to end: over a synthetic dump file (the real parser, no elevation) and over a scripted
// source standing in for the live volume of an elevated run. Runs on every platform.
[TestClass]
[DoNotParallelize]
public sealed class DirectHostTests : IDisposable
{
    const string DumpRoot = "dump:/D";

    TempDirectory _tempDirectory = null!;
    string TemporaryDirectoryPath => _tempDirectory.Path;
    string _dumpFile = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = new TempDirectory("mftlib-dump", create: false);
        _dumpFile = MftDumpFixture.WriteFile(TemporaryDirectoryPath, MftDumpFixture.Standard());
    }

    public void Dispose()
    {
        FileEntry.Dependencies.ResetToDefaults();
        NativeTestHooks.NativeResetTestState();
        _tempDirectory.Dispose();
    }

    SampleHost HostOver(List<string> lines)
    {
        return new SampleHost(new LineCollectingWriter(lines), TextReader.Null, TemporaryDirectoryPath)
        {
            _isElevated = () => throw new AssertFailedException("A dump run must not ask about elevation.")
        };
    }

    string[] Dump(params string[] verb) => [.. verb, "D", "--source", "dump", "--dump-file", _dumpFile];

    [TestMethod]
    public void Run_SearchOverADump_ListsTheMatchingRowWithoutElevation()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(Dump("search", "--name", "notes"));

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.Contains($"{DumpRoot}/documents/Notes.txt  11", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("1 entries", lines);
    }

    [TestMethod]
    public void Run_DocumentedExactStreamedDumpSearch_PrintsPhasesMatchAndEntryCountWithoutElevation()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(
            ["search", "D", "--source", "dump", "--dump-file", _dumpFile, "--name", "Notes.txt", "--exact", "--stream"]);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.AreSequenceEqual(
            [
                "  D: ParsingMft: 16 rows of 16",
                "  D: Transferring: 7 rows",
                "  D: Finished: 11 rows (Succeeded)",
                $"{DumpRoot}/documents/Notes.txt  11",
                "1 entries"
            ], lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_StreamedSearchWithALimit_StopsAtTheLimit()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("search", "--stream", "--limit", "2")]);

        Assert.AreEqual(0, result);
        Assert.Contains("2 entries", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_TreeOverADump_PrintsTheRootThenItsChildrenToTheDepth()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("tree"), "--depth", "1"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(DumpRoot, lines.First(line => line.StartsWith("dump:", StringComparison.Ordinal)).TrimEnd('/'));
        Assert.Contains("  documents/", lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("Notes.txt"), lines, "depth 1 stops at the root's children");
    }

    [TestMethod]
    public void Run_ChildrenOfADirectory_ListsOnlyItsDirectChildren()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("children"), "--path", $"{DumpRoot}/documents"]);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.Contains($"{DumpRoot}/documents/Notes.txt  11", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains($"{DumpRoot}/documents/Deep  <dir>", lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("leaf.txt"), lines, "a grandchild is not a direct child");
    }

    [TestMethod]
    public void Run_ChildrenWithoutAPath_ListsTheRootRootsChildren()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(Dump("children"));

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.Contains($"{DumpRoot}/documents  <dir>", lines, string.Join(Environment.NewLine, lines));
        Assert.DoesNotContain(line => line.Contains("Notes.txt"), lines, "a grandchild is not a direct child");
    }

    [TestMethod]
    public async Task Run_TreeOverSeveralDirectories_ReadsTheRowsOnceAndKeepsTheSortedOrder()
    {
        var lines = new List<string>();
        var host = HostOver(lines);

        var result = host.Run([.. Dump("tree"), "--depth", "4"]);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        var documents = lines.IndexOf("  documents/");
        Assert.IsGreaterThanOrEqualTo(0, documents, string.Join(Environment.NewLine, lines));
        Assert.Contains("    Notes.txt", lines, string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.IndexOf("    Deep/") > documents && lines.IndexOf("    Deep/") < lines.IndexOf("    Notes.txt"), string.Join(Environment.NewLine, lines));

        static IEnumerable<FileEntry> Once(IEnumerable<FileEntry> rows, Action entered)
        {
            entered();
            foreach (var row in rows)
            {
                yield return row;
            }
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(_dumpFile, 'D')
        }, CancellationToken.None);
        var enumerations = 0;
        var lookup = SampleHost.ChildLookup(Once(index.Enumerate(new SearchQuery(null), TestContext.CancellationToken), () => Assert.AreEqual(0, enumerations++)));
        Assert.AreEqual(1, enumerations);
        Assert.IsNotEmpty(lookup);
    }

    [TestMethod]
    public void Run_LargestOverADump_ListsTheBiggestFileFirst()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("largest"), "--count", "1"]);

        Assert.AreEqual(0, result);
        Assert.Contains($"{DumpRoot}/documents/Deep/leaf.txt  22", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_ScanOverADump_PrintsThePhasesAndTheStatusLine()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(Dump("scan"));

        Assert.AreEqual(0, result);
        Assert.Contains(line => line.StartsWith("Index holds ", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("Catch-up held; watch supported: False", lines);
        Assert.Contains("D: 4 live files, 3 live directories, 0 retained deleted rows", lines, string.Join(Environment.NewLine, lines));
        Assert.Contains("D: 1 files with unknown size, 0 with unknown last-write time, 0 hidden, newest known last write 2019-04-17 18:40:01Z", lines,
            string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Direct_PrintsTheProducer()
    {
        var lines = new List<string>();

        HostOver(lines).Run(Dump("scan"));

        Assert.Contains("producer Mft", lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public async Task Run_ScanOverAScriptedSource_ReportsLiveAndRetainedDeletedCounts()
    {
        Assert.IsTrue(DirectArguments.TryParse(["scan", "C", "--include-freed"], out var parsed, out var error), error);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);

        await host.RunSourceAsync(parsed!, ScriptedSource(), new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None);

        Assert.Contains("C: 2 live files, 2 live directories, 1 retained deleted rows", output.ToString());
    }

    [TestMethod]
    public void Run_OpenOfADumpEntry_ReportsTheErrorAndReturnsOne()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("open"), "--path", $"{DumpRoot}/documents/Notes.txt"]);

        Assert.AreEqual(1, result);
        var description = $"{DumpRoot}/documents/Notes.txt ({new IndexRecordKey(new VolumeKey('D'), 7, ProducerKind.Mft)})";
        Assert.Contains(line => line.StartsWith($"{description}: record 7 on D (Mft), ", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
        Assert.AreEqual($"Error: InvalidOperationException: {description}: Files in an MFT dump cannot be opened through the index.", lines.Single(line => line.StartsWith("Error: ", StringComparison.Ordinal)));
        Assert.DoesNotContain(line => line.StartsWith("  caused by ", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    [DataRow(false, "2020-01-02 03:04:05Z", DisplayName = "534 known last-write timestamp")]
    [DataRow(true, "<unknown>", DisplayName = "534 unknown last-write timestamp")]
    public async Task Run_OpenOfAScriptedEntry_FormatsLastWriteFreshness(
        bool isLastWriteTimeUnknown, string expectedLastWriteTime)
    {
        var path = Path.Combine(TemporaryDirectoryPath, "a.txt");
        File.WriteAllBytes(path, [1, 2, 3]);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);
        var source = SyntheticIndexSource.Create(new SyntheticMftProducer(_ =>
        [
            new SyntheticRow(5, ".", 5) { IsDirectory = true },
            new SyntheticRow(7, "a.txt", 5)
            {
                Size = 3,
                LastWriteTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                IsLastWriteTimeUnknown = isLastWriteTimeUnknown
            }
        ]));
        FileEntry.Dependencies._openById = (_, _, _, access) =>
            new FileStream(path, FileMode.Open, access, FileShare.Read);

        Assert.IsTrue(DirectArguments.TryParse(["open", "C", "--path", path], out var parsed, out var error), error);
        await host.RunSourceAsync(parsed!, source, new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None);

        var report = output.ToString();
        Assert.Contains($"{path} ({new IndexRecordKey(new VolumeKey('C'), 7, ProducerKind.Mft)}): record ", report);
        Assert.Contains($"modified {expectedLastWriteTime}, parent ", report);
        Assert.Contains("3 bytes, first 3: 010203", report);
    }

    [TestMethod]
    public void Run_PathThatIsNotIndexed_ReportsNoEntry()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("open"), "--path", $"{DumpRoot}/missing.txt"]);

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains($"No entry at {DumpRoot}/missing.txt."), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_DumpFileThatIsMissing_ReportsTheProducerFailure()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(["scan", "D", "--source", "dump", "--dump-file", Path.Combine(TemporaryDirectoryPath, "absent.mft")]);

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.StartsWith("Error: ", StringComparison.Ordinal), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_IncludeFreedOnADump_PrintsUsageAndReturnsTwoWithoutScanning()
    {
        var lines = new List<string>();
        var host = HostOver(lines);

        var result = host.Run([.. Dump("search"), "--include-freed"]);

        Assert.AreEqual(2, result);
        Assert.Contains(line => line.Contains("a dump never yields freed rows"), lines);
        Assert.Contains(line => line.StartsWith("Usage:", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_LocalSourceNotElevatedAndUnattended_SkipsTheScanAndReturnsOne()
    {
        using var unattended = new UnattendedEnvironmentScope("1");
        var lines = new List<string>();
        var host = HostOver(lines);
        host._isElevated = () => false;

        var result = host.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () =>
            throw new AssertFailedException("An unelevated unattended run must not scan."));

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED"), lines);
    }

    [TestMethod]
    public void Run_LocalSourceElevated_ScansWhatTheSourceProducesAndRedirectsOutput()
    {
        using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath) { _isElevated = () => true };
        DirectArguments? seen = null;

        var result = host.RunWithElevation(["search", "C", "--name", "a.txt", "--include-freed"], ElevationNeed.SelfElevate, () =>
        {
            Assert.IsTrue(DirectArguments.TryParse(["search", "C", "--name", "a.txt", "--include-freed"], out var parsed, out var error), error);
            seen = parsed;
            host.RunSourceAsync(parsed!, ScriptedSource(), new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        });

        Assert.AreEqual(0, result);
        Assert.AreEqual(string.Empty, output.ToString(), "Elevated output goes to output.log.");
        var log = File.ReadAllText(host.OutputLogPath);
        Assert.IsTrue(seen!.IncludeFreed);
        Assert.Contains("a.txt  100  deleted=False", log);
        Assert.Contains("a.txt  7  deleted=True", log);

        var followUp = host.Run([]);

        Assert.AreEqual(2, followUp);
        Assert.Contains("Usage:", output.ToString(), "After the elevated run the original borrowed writer is restored.");
    }

    [TestMethod]
    public async Task Run_LocalSourceWithoutIncludeFreed_HidesTheDeletedRow()
    {
        Assert.IsTrue(DirectArguments.TryParse(["search", "C", "--name", "a.txt"], out var parsed, out var error), error);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);

        await host.RunSourceAsync(parsed!, ScriptedSource(), new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None);

        var report = output.ToString();
        Assert.DoesNotContain("deleted=", report, report);
        Assert.Contains("2 entries", report);
    }

    [TestMethod]
    public async Task Run_DocumentedLocalStreamedSearch_StreamsMatchingFilesAndRefusesUnelevatedUnattended()
    {
        string[] command = ["search", "C", "--source", "local", "--name", "*.log", "--files", "--stream", "--limit", "20"];
        Assert.IsTrue(DirectArguments.TryParse(command, out var parsed, out var error), error);
        await using var elevatedOutput = new StringWriter();
        var elevatedHost = new SampleHost(elevatedOutput, TextReader.Null, TemporaryDirectoryPath);
        var source = SyntheticIndexSource.Create(new SyntheticMftProducer(_ =>
        [
            new SyntheticRow(5, ".", 5) { IsDirectory = true },
            new SyntheticRow(6, "logs", 5) { IsDirectory = true },
            new SyntheticRow(7, "app.log", 6) { Size = 42 },
            new SyntheticRow(8, "pending.log", 6) { Size = null },
            new SyntheticRow(9, "archive.log", 5) { IsDirectory = true },
            new SyntheticRow(10, "Notes.txt", 5) { Size = 11 }
        ]));

        await elevatedHost.RunSourceAsync(parsed!, source, new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None);

        var elevatedLines = elevatedOutput.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var matches = elevatedLines.Where(line => line.Contains(".log", StringComparison.Ordinal) && !line.StartsWith("Indexing ", StringComparison.Ordinal)).ToList();
        Assert.AreSequenceEqual(
            [
                $"{Path.Combine(TemporaryDirectoryPath, "logs", "app.log")}  42",
                $"{Path.Combine(TemporaryDirectoryPath, "logs", "pending.log")}  ?"
            ], matches, SequenceOrder.InAnyOrder, elevatedOutput.ToString());
        Assert.IsTrue(elevatedLines.Contains("2 entries"), elevatedOutput.ToString());

        using var unattended = new UnattendedEnvironmentScope("1");
        await using var unattendedOutput = new StringWriter();
        var unattendedHost = new SampleHost(unattendedOutput, TextReader.Null, TemporaryDirectoryPath) { _isElevated = () => false };

        var unattendedResult = unattendedHost.RunWithElevation(command, ElevationNeed.SelfElevate, () =>
            throw new AssertFailedException("An unelevated unattended run must not scan."));

        Assert.AreEqual(1, unattendedResult);
        Assert.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped, no prompt shown.", unattendedOutput.ToString());
        Assert.Contains("AUTOMATIC ELEVATION FAILED.", unattendedOutput.ToString());
    }

    [TestMethod]
    public async Task Run_DuplicateNamesOverAScriptedSource_ListsTheGroup()
    {
        Assert.IsTrue(DirectArguments.TryParse(["duplicate-names", "C"], out var parsed, out var error), error);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);

        await host.RunSourceAsync(parsed!, ScriptedSource(), new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None);

        Assert.Contains("a.txt: 2 entries", output.ToString());
    }

    [TestMethod]
    public async Task Run_RootThatIsOffline_ReportsItAndReturnsOne()
    {
        Assert.IsTrue(DirectArguments.TryParse(["scan", "Q"], out var parsed, out var error), error);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.RunSourceAsync(parsed!, ScriptedSource(), new VolumeKey('Q'), options => SyntheticRoot.Create(new VolumeKey('Q'), Path.Combine(TemporaryDirectoryPath, "missing-root"), 4242).Apply(options), CancellationToken.None));
        Assert.Contains("The root is offline; nothing was scanned.", exception.Message);
    }

    [TestMethod]
    public async Task Run_ScanThatFails_ReportsTheProducerFailure()
    {
        Assert.IsTrue(DirectArguments.TryParse(["scan", "C"], out var parsed, out var error), error);
        await using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath);
        var source = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("volume unreadable")));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            host.RunSourceAsync(parsed!, source, new VolumeKey('C'), options => SyntheticRoot.Create(new VolumeKey('C'), TemporaryDirectoryPath, 4242).Apply(options), CancellationToken.None));
        Assert.Contains("volume unreadable", exception.Message);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Run_LocalSourceOffWindows_SkipsTheElevationFlowAndReportsThePlatform()
    {
        var lines = new List<string>();
        var host = HostOver(lines);

        var result = host.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains("the MFT is read on Windows only") && line.Contains("PlatformWithoutMft"), lines, string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_WrappedFailure_NamesTheTypeAndEveryInnerExceptionWithoutAStackTrace()
    {
        using var output = new StringWriter();
        var host = new SampleHost(output, TextReader.Null, TemporaryDirectoryPath)
        {
            _isElevated = () => true
        };

        var result = host.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () =>
            throw new AggregateException("outer failure", new IOException("inner detail", new InvalidOperationException("root cause"))));

        Assert.AreEqual(1, result);
        var report = File.ReadAllText(host.OutputLogPath);
        Assert.IsTrue(report.Contains("Error: AggregateException: outer failure", StringComparison.Ordinal), report);
        Assert.IsTrue(report.Contains("caused by IOException: inner detail", StringComparison.Ordinal), report);
        Assert.IsTrue(report.Contains("caused by InvalidOperationException: root cause", StringComparison.Ordinal), report);
        Assert.IsFalse(report.Contains("   at ", StringComparison.Ordinal), report);
    }

    static MftIndexSource ScriptedSource()
    {
        return SyntheticIndexSource.Create(new SyntheticMftProducer(_ =>
        [
            new SyntheticRow(5, ".", 5) { IsDirectory = true },
            new SyntheticRow(6, "docs", 5) { IsDirectory = true },
            new SyntheticRow(7, "a.txt", 6) { Size = 100 },
            new SyntheticRow(8, "a.txt", 5) { Size = 3 },
            new SyntheticRow(9, "a.txt", 5) { Size = 7, IsTombstone = true }
        ]));
    }

    public TestContext TestContext { get; set; } = null!;
}
