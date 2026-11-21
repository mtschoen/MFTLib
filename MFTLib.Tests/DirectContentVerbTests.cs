using System.Text;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

/// <summary>
///     The Direct sample's <c>content</c> verb, the library caller of <c>FileIndex.ReadInlineContentAsync</c>: it reads a
///     file's resident data from a dump with no elevation and prints what the read found.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DirectContentVerbTests : IDisposable
{
    const string DumpRoot = "dump:/D";

    readonly TempDirectory _tempDirectory = new("mftlib-direct-content", create: false);
    string _dumpFile = null!;

    [TestInitialize]
    public void Initialize()
    {
        var records = MftDumpFixture.StandardRecords();
        records[7] = records[7] with { Content = Encoding.UTF8.GetBytes("hello world") };
        records[9] = records[9] with { Content = [0x00, 0xFF, 0x10, 0x80] };
        records[11] = new DumpRecord("empty.txt", 5) { Content = [] };
        records[12] = new DumpRecord("crlf.txt", 5) { Content = Encoding.UTF8.GetBytes("first line\r\nsecond\tcolumn\n") };
        _dumpFile = MftDumpFixture.WriteFile(_tempDirectory.Path, MftDumpFixture.Build(1024, 16, records));
    }

    public void Dispose() => _tempDirectory.Dispose();

    List<string> Run(string path, out int exitCode)
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null, _tempDirectory.Path)
        {
            _isElevated = () => throw new AssertFailedException("A dump run must not ask about elevation.")
        };
        exitCode = host.Run(["content", "D", "--path", path, "--source", "dump", "--dump-file", _dumpFile]);
        return lines;
    }

    [TestMethod]
    public void Run_ContentOfAResidentTextFile_PrintsTheOutcomeTheSizeAndTheBytes()
    {
        var lines = Run($"{DumpRoot}/documents/Notes.txt", out var exitCode);

        Assert.AreEqual(0, exitCode, string.Join(Environment.NewLine, lines));
        Assert.Contains($"{DumpRoot}/documents/Notes.txt: Returned, 11 bytes", lines);
        Assert.Contains("  hex: 68656C6C6F20776F726C64", lines);
        Assert.Contains("  text: hello world", lines);
    }

    [TestMethod]
    public void Run_ContentOfBinaryData_PrintsHexAndNamesItBinary()
    {
        var lines = Run($"{DumpRoot}/documents/Deep/leaf.txt", out var exitCode);

        Assert.AreEqual(0, exitCode);
        Assert.Contains($"{DumpRoot}/documents/Deep/leaf.txt: Returned, 4 bytes", lines);
        Assert.Contains("  hex: 00FF1080", lines);
        Assert.Contains("  text: <binary>", lines);
    }

    [TestMethod]
    public void Run_ContentOfATextFileWithLineBreaksAndTabs_PrintsItAsText()
    {
        var lines = Run($"{DumpRoot}/crlf.txt", out var exitCode);

        Assert.AreEqual(0, exitCode);
        Assert.DoesNotContain("  text: <binary>", lines);
        Assert.Contains("  text: first line\r\nsecond\tcolumn\n", lines);
    }

    [TestMethod]
    public void Run_ContentOfAnEmptyFile_ReportsReturnedWithZeroBytes()
    {
        var lines = Run($"{DumpRoot}/empty.txt", out var exitCode);

        Assert.AreEqual(0, exitCode);
        Assert.Contains($"{DumpRoot}/empty.txt: Returned, 0 bytes", lines);
        Assert.DoesNotContain(line => line.StartsWith("  hex:", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_ContentOfADirectory_ReportsNotInline()
    {
        var lines = Run($"{DumpRoot}/documents", out var exitCode);

        Assert.AreEqual(0, exitCode);
        Assert.Contains($"{DumpRoot}/documents: NotInline, 0 bytes", lines);
    }

    // An elevated run writes output.log, an ordinary file: it gets the outcome and the byte count, never the bytes.
    [TestMethod]
    public void Run_ContentInAnElevatedRun_LogsTheOutcomeAndSizeButNoBytes()
    {
        var console = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(console), TextReader.Null, _tempDirectory.Path)
        {
            _isElevated = () => true
        };

        var exitCode = host.RunWithElevation(["content", "D"], ElevationNeed.SelfElevate, () => host.Run(
            ["content", "D", "--path", $"{DumpRoot}/documents/Notes.txt", "--source", "dump", "--dump-file", _dumpFile]));

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(console, "Elevated output goes to output.log.");
        var log = File.ReadAllText(host.OutputLogPath);
        Assert.Contains($"{DumpRoot}/documents/Notes.txt: Returned, 11 bytes", log);
        Assert.DoesNotContain("hello world", log);
        Assert.DoesNotContain("68656C6C6F", log);
    }

    [TestMethod]
    public void Run_ContentWithoutAPath_IsAUsageError()
    {
        var lines = new List<string>();
        var host = new SampleHost(new LineCollectingWriter(lines), TextReader.Null, _tempDirectory.Path);

        var exitCode = host.Run(["content", "D", "--source", "dump", "--dump-file", _dumpFile]);

        Assert.AreEqual(2, exitCode);
        Assert.Contains("content needs --path P.", lines);
    }
}
