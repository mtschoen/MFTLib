using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The managed dump parse over a 1000-record synthetic image: the records and columns it returns,
///     and the native generator that writes the image.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class DumpFileParseTests : IDisposable
{
    TempFile _syntheticFile = null!;
    string _syntheticPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _syntheticFile = new TempFile();
        _syntheticPath = _syntheticFile.Path;
        MftVolume.GenerateSyntheticMFT(_syntheticPath, 1000, 256);
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        _syntheticFile.Dispose();
    }

    public void Dispose() => Cleanup();

    // Parses the synthetic image in 256-record chunks, so several chunks run.
    const uint StreamFileChunkRecords = 256;

    MftResult StreamFile(IProgress<MftScanProgress>? progress = null, ParseThreadAllowance? parseThreads = null,
        CancellationToken cancellationToken = default)
    {
        using var input = MftDumpInput.Open(_syntheticPath);
        return input.Parse(new MftFileScanOptions(progress, parseThreads, StreamFileChunkRecords, cancellationToken));
    }

    [TestMethod]
    public void Parse_ReadAll_ReturnsRecords()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out var totalRecords);

        Assert.IsNotEmpty(records);
        Assert.IsGreaterThanOrEqualTo(1000UL, totalRecords);
        Assert.IsNotNull(records[0].FileName);
    }

    [TestMethod]
    public void Parse_AllRecords_HaveFileNames()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);

        foreach (var record in records)
        {
            Assert.IsNotNull(record.FileName);
            Assert.AreNotEqual(string.Empty, record.FileName);
        }
    }

    [TestMethod]
    public void Parse_ContainsDirectoriesAndFiles()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);

        var hasDirectory = records.Any(r => r.IsDirectory);
        var hasFile = records.Any(r => !r.IsDirectory && r.InUse);

        Assert.IsTrue(hasDirectory, "Expected at least one directory");
        Assert.IsTrue(hasFile, "Expected at least one file");
    }

    [TestMethod]
    public void Parse_RecordNumbers_AreUnique()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);
        var uniqueCount = records.Select(r => r.RecordNumber).Distinct().Count();
        Assert.AreEqual(records.Length, uniqueCount, "Expected all record numbers to be unique");
    }

    [TestMethod]
    public void Parse_RootRecord_IsDirectory()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);

        // Synthetic MFT places root at record 5 with name "."
        var root = records.FirstOrDefault(r => r.RecordNumber == 5);
        Assert.AreEqual(".", root.FileName);
        Assert.IsTrue(root.IsDirectory);
        Assert.IsTrue(root.InUse);
    }

    [TestMethod]
    public void Parse_SystemRecords_ArePresent()
    {
        var records = DirectParse.ParseFile(_syntheticPath, out _);

        // Records 0-4 are $MFT in synthetic data
        var mftRecords = records.Where(r => r.RecordNumber < 5).ToArray();
        Assert.IsNotEmpty(mftRecords, "Expected system records to be present");
        foreach (var r in mftRecords)
        {
            Assert.AreEqual("$MFT", r.FileName);
        }
    }

    [TestMethod]
    public void GenerateSyntheticMFT_CreatesFile()
    {
        using var temporaryFile = new TempFile();
        var path = temporaryFile.Path;
        MftVolume.GenerateSyntheticMFT(path, 100, 256);
        Assert.IsGreaterThan(0, new FileInfo(path).Length);
    }

    [TestMethod]
    public void GenerateSyntheticMFT_FileSize_MatchesRecordCount()
    {
        using var temporaryFile = new TempFile();
        var path = temporaryFile.Path;
        const ulong recordCount = 500;
        MftVolume.GenerateSyntheticMFT(path, recordCount, 256);
        // Each MFT record is 1024 bytes
        const long expectedSize = (long)recordCount * 1024;
        Assert.AreEqual(expectedSize, new FileInfo(path).Length);
    }

    [TestMethod]
    [DataRow(1024u)]
    [DataRow(4096u)]
    [TestCategory("Smoke")]
    public void Parse_RecordSizes_RoundTrip(uint recordSize)
    {
        using var temporaryFile = new TempFile();
        var tempPath = temporaryFile.Path;
        MftVolume.GenerateSyntheticMFT(tempPath, 1000, 256, recordSize);
        var records = DirectParse.ParseFile(tempPath, out var totalRecords);

        Assert.IsNotEmpty(records);
        Assert.AreEqual(1000UL, totalRecords);

        Assert.Contains(r => r.RecordNumber == 0, records, "Record 0 ($MFT) must exist");
        var rec0 = records.First(r => r.RecordNumber == 0);
        Assert.AreEqual("$MFT", rec0.FileName);

        Assert.Contains(r => r.RecordNumber == 5, records, "Record 5 (root directory) must exist");
        var rec5 = records.First(r => r.RecordNumber == 5);
        Assert.AreEqual(".", rec5.FileName);
        Assert.IsTrue(rec5.IsDirectory, "Record 5 must be a directory");
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(256u)]
    [DataRow(1536u)]
    [DataRow(131072u)]
    public void GenerateSyntheticMFT_UnsupportedRecordSizes_Throw(uint recordSize)
    {
        using var temporaryFile = new TempFile();
        var tempPath = temporaryFile.Path;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT(tempPath, 100, 256, recordSize));
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ZeroRecordsPerBuffer_Throws()
    {
        using var temporaryFile = new TempFile();
        var tempPath = temporaryFile.Path;
        Assert.ThrowsExactly<InvalidOperationException>(() => MftVolume.GenerateSyntheticMFT(tempPath, 100, 0));
    }
}
