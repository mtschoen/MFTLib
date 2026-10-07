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
public class DirectHostTests
{
    const string DumpRoot = "dump:/D";

    string _directory = null!;
    string _dumpFile = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        _dumpFile = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    static SampleHost HostOver(List<string> lines)
    {
        return new SampleHost
        {
            _isElevated = () => throw new AssertFailedException("A dump run must not ask about elevation."),
            _writeLine = lines.Add
        };
    }

    string[] Dump(params string[] verb) => [.. verb, "D", "--source", "dump", "--dump-file", _dumpFile];

    [TestMethod]
    public void Run_SearchOverADump_ListsTheMatchingRowWithoutElevation()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(Dump("search", "--name", "notes"));

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains($"{DumpRoot}/documents/Notes.txt  11"), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("1 entries"));
    }

    [TestMethod]
    public void Run_StreamedSearchWithALimit_StopsAtTheLimit()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("search", "--stream", "--limit", "2")]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("2 entries"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_TreeOverADump_PrintsTheRootThenItsChildrenToTheDepth()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("tree"), "--depth", "1"]);

        Assert.AreEqual(0, result);
        Assert.AreEqual(DumpRoot, lines.First(line => line.StartsWith("dump:", StringComparison.Ordinal)).TrimEnd('/'));
        Assert.IsTrue(lines.Contains("  documents/"), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(line => line.Contains("Notes.txt")), "depth 1 stops at the root's children");
    }

    [TestMethod]
    public void Run_LargestOverADump_ListsTheBiggestFileFirst()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("largest"), "--count", "1"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains($"{DumpRoot}/documents/Deep/leaf.txt  22"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_ScanOverADump_PrintsThePhasesAndTheStatusLine()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(Dump("scan"));

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Index holds ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("Catch-up held; watch supported: False"));
    }

    [TestMethod]
    public void Run_OpenOfADumpEntry_ReportsTheErrorAndReturnsOne()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("open"), "--path", $"{DumpRoot}/documents/Notes.txt"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith($"{DumpRoot}/documents/Notes.txt: record 7 on D (Mft), ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_PathThatIsNotIndexed_ReportsNoEntry()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run([.. Dump("open"), "--path", $"{DumpRoot}/missing.txt"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains($"No entry at {DumpRoot}/missing.txt.")), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_DumpFileThatIsMissing_ReportsTheProducerFailure()
    {
        var lines = new List<string>();

        var result = HostOver(lines).Run(["scan", "D", "--source", "dump", "--dump-file", Path.Combine(_directory, "absent.mft")]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_IncludeFreedOnADump_PrintsUsageAndReturnsTwoWithoutScanning()
    {
        var lines = new List<string>();
        var host = HostOver(lines);
        host._createSource = _ => throw new AssertFailedException("A refused command line must not build a source.");

        var result = host.Run([.. Dump("search"), "--include-freed"]);

        Assert.AreEqual(2, result);
        Assert.IsTrue(lines.Any(line => line.Contains("a dump never yields freed rows")));
        Assert.IsTrue(lines.Any(line => line.StartsWith("Usage:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_LocalSourceNotElevatedAndUnattended_SkipsTheScanAndReturnsOne()
    {
        var lines = new List<string>();
        var host = HostOver(lines);
        host._isElevated = () => false;
        host._isWindows = () => true;
        host._getEnvironmentVariable = name => name == SampleHost.UnattendedVariableName ? "1" : null;
        host._createSource = _ => throw new AssertFailedException("An unelevated unattended run must not scan.");

        var result = host.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    [TestMethod]
    public void Run_LocalSourceElevated_ScansWhatTheSourceProducesAndRedirectsOutput()
    {
        var lines = new List<string>();
        DirectArguments? seen = null;
        var redirected = false;
        var host = ElevatedHost(lines, parsed =>
        {
            seen = parsed;
            return ScriptedSource();
        });
        host._wFreopen = (_, _, _) =>
        {
            redirected = true;
            return IntPtr.Zero;
        };

        var result = host.Run(["search", "C", "--name", "a.txt", "--include-freed"]);

        Assert.AreEqual(0, result, string.Join(Environment.NewLine, lines));
        Assert.IsTrue(redirected, "An elevated self-elevating run writes to output.log.");
        Assert.IsTrue(seen!.IncludeFreed);
        Assert.IsTrue(lines.Any(line => line.EndsWith("a.txt  100  deleted=False", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Any(line => line.EndsWith("a.txt  7  deleted=True", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_LocalSourceWithoutIncludeFreed_HidesTheDeletedRow()
    {
        var lines = new List<string>();
        var host = ElevatedHost(lines, _ => ScriptedSource());

        var result = host.Run(["search", "C", "--name", "a.txt"]);

        Assert.AreEqual(0, result);
        Assert.IsFalse(lines.Any(line => line.Contains("deleted=")), string.Join(Environment.NewLine, lines));
        Assert.IsTrue(lines.Contains("2 entries"), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_DuplicateNamesOverAScriptedSource_ListsTheGroup()
    {
        var lines = new List<string>();
        var host = ElevatedHost(lines, _ => ScriptedSource());

        var result = host.Run(["duplicate-names", "C"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("a.txt: 2 entries", StringComparison.Ordinal)), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_DriveThatIsOffline_ReportsItAndReturnsOne()
    {
        var lines = new List<string>();
        var host = ElevatedHost(lines, _ => ScriptedSource());
        host._resolveDrive = _ => new IndexedDrive('Q', Path.Combine(_directory, "missing-root"), 4242);

        var result = host.Run(["scan", "Q"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains("The drive is offline; nothing was scanned.")), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_ScanThatFails_ReportsTheProducerFailure()
    {
        var lines = new List<string>();
        var host = ElevatedHost(lines, _ => SyntheticIndexSource.Create(new SyntheticMftProducer(_ => throw new IOException("volume unreadable"))));

        var result = host.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains("volume unreadable")), string.Join(Environment.NewLine, lines));
    }

    [TestMethod]
    public void Run_LocalSourceOffWindows_SkipsTheElevationFlowAndReportsThePlatform()
    {
        var lines = new List<string>();
        var host = HostOver(lines);
        host._isWindows = () => false;
        host._resolveDrive = _ => throw new PlatformNotSupportedException("Volume serials are read on Windows only.");

        var result = host.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains("PlatformNotSupportedException")), string.Join(Environment.NewLine, lines));
    }

    SampleHost ElevatedHost(List<string> lines, Func<DirectArguments, MftIndexSource> createSource)
    {
        Directory.CreateDirectory(_directory);
        return new SampleHost
        {
            _isElevated = () => true,
            _isWindows = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
            _createSource = createSource,
            _resolveDrive = letter => new IndexedDrive(char.ToUpperInvariant(letter[0]), _directory, 4242),
            _cacheDirectory = Path.Combine(_directory, "cache"),
            _writeLine = lines.Add
        };
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
}
