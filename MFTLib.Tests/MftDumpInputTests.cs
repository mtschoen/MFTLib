using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A dump is opened once: its geometry and its records come from that one open file, and a file
///     whose content fails a check is rejected with the check's own message.
/// </summary>
[TestClass]
public class MftDumpInputTests
{
    internal const string IncompleteMessage = "The dump file could not be read completely.";

    const string RecordSizeMessage = "Invalid or unsupported MFT record size.";

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public void Open_ValidDump_ReportsTheFilesOwnLengthAndRecordSize(int recordSize)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(recordSize));

        using var input = MftDumpInput.Open(path);

        Assert.AreEqual(16L * recordSize, input.LengthBytes);
        Assert.AreEqual((uint)recordSize, input.RecordSize);
        Assert.AreEqual(new NtfsVolumeInformation(16L * recordSize, (uint)recordSize), input.VolumeInformation);
        Assert.AreEqual(16L, input.VolumeInformation.MftRecordCount);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public void Parse_ValidDump_ReturnsEveryAllocatedRecordWithItsColumns(int recordSize)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(recordSize));
        using var input = MftDumpInput.Open(path);

        using var result = input.Parse();
        var records = result.ToArray().ToDictionary(record => record.RecordNumber);

        CollectionAssert.AreEquivalent(new ulong[] { 0, 5, 6, 7, 8, 9, 10 }, records.Keys.ToArray());
        Assert.AreEqual(16UL, result.TotalRecords);
        Assert.IsTrue(records[5].IsDirectory);
        Assert.AreEqual(5UL, records[5].ParentRecordNumber);
        Assert.AreEqual("Notes.txt", records[7].FileName);
        Assert.AreEqual(6UL, records[7].ParentRecordNumber);
        Assert.AreEqual(11L, records[7].Size);
        Assert.IsTrue(records[7].SizeKnown);
        Assert.AreEqual(DateTime.FromFileTimeUtc(MftDumpFixture.BaseFileTime + 10_000_000), records[7].ModifiedUtc);
        Assert.IsFalse(records[10].SizeKnown, "a file with no data attribute has an unknown size");
        Assert.IsTrue(records.Values.All(record => record.InUse));
    }

    public static IEnumerable<object[]> RejectedContent =>
    [
        ["empty", Array.Empty<byte>(), "The dump file is empty."],
        ["one byte", new byte[1], IncompleteMessage],
        ["one byte short of a header", Header(1024)[..31], IncompleteMessage],
        ["a header and nothing else", Header(1024), "File size is not a whole multiple of record size."],
        ["bad signature", WithSignature("BAAD"), RecordSizeMessage],
        ["zeroed first record", new byte[1024], RecordSizeMessage],
        ["record size 0", WithDeclaredRecordSize(0), RecordSizeMessage],
        ["record size 256", WithDeclaredRecordSize(256), RecordSizeMessage],
        ["record size 512", WithDeclaredRecordSize(512), RecordSizeMessage],
        ["record size 1536", WithDeclaredRecordSize(1536), RecordSizeMessage],
        ["record size 2048", WithDeclaredRecordSize(2048), RecordSizeMessage],
        ["record size 8192", WithDeclaredRecordSize(8192), RecordSizeMessage],
        ["record size 3000", WithDeclaredRecordSize(3000), RecordSizeMessage],
        ["record size 131072", WithDeclaredRecordSize(131072), RecordSizeMessage],
        ["record size 4294967295", WithDeclaredRecordSize(uint.MaxValue), RecordSizeMessage],
        ["partial final record", MftDumpFixture.Standard()[..^1], "File size is not a whole multiple of record size."],
        ["one extra byte", (byte[])[.. MftDumpFixture.Standard(), 0],
            "File size is not a whole multiple of record size."],
        ["4096-byte records cut at a 1024-byte boundary", MftDumpFixture.Standard(4096)[..(4096 * 3 + 1024)],
            "File size is not a whole multiple of record size."]
    ];

    [TestMethod]
    [DynamicData(nameof(RejectedContent))]
    public void Open_RejectedContent_ThrowsInvalidDataWithTheChecksMessage(string description, byte[] content,
        string message)
    {
        var path = MftDumpFixture.WriteFile(_directory, content);

        var failure = Assert.ThrowsException<InvalidDataException>(() => MftDumpInput.Open(path), description);

        Assert.AreEqual(message, failure.Message, description);
    }

    [TestMethod]
    public void Open_RejectedContent_LeavesTheFileUnlocked()
    {
        var path = MftDumpFixture.WriteFile(_directory, WithDeclaredRecordSize(2048));
        Assert.ThrowsException<InvalidDataException>(() => MftDumpInput.Open(path));

        File.Delete(path);

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Open_MissingFile_ThrowsIOExceptionWithThePlatformErrorCode()
    {
        var failure = Assert.ThrowsException<IOException>(
            () => MftDumpInput.Open(Path.Combine(_directory, "absent.mft")));

        StringAssert.StartsWith(failure.Message, "Failed to open file. Error: ");
    }

    [TestMethod]
    public void Open_EmptyPath_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => MftDumpInput.Open(string.Empty));
    }

    [TestMethod]
    public void Open_PathWithAnEmbeddedNull_IsRefusedInsteadOfOpeningTheFileBeforeTheNull()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());

        var failure = Assert.ThrowsException<ArgumentException>(() => MftDumpInput.Open(path + "\0.ignored"));

        Assert.AreEqual("filePath", failure.ParamName);
    }

    [TestMethod]
    public void Open_NonAsciiPath_OpensTheFileItNames()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(), "dépôt-日本.mft");

        using var input = MftDumpInput.Open(path);

        Assert.AreEqual(16L * 1024, input.LengthBytes);
    }

    [TestMethod]
    public void Parse_PathRenamedAndReplacedAfterTheOpen_StillReadsTheOpenedFile()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        var replacement = MftDumpFixture.StandardRecords();
        replacement[7] = new DumpRecord("Replaced.txt", 6);

        File.Move(path, path + ".old");
        File.WriteAllBytes(path, MftDumpFixture.Build(4096, 32, replacement));

        CollectionAssert.Contains(Names(input), "Notes.txt");
        Assert.AreEqual(1024u, input.RecordSize);
        using var reopened = MftDumpInput.Open(path);
        CollectionAssert.Contains(Names(reopened), "Replaced.txt");
        Assert.AreEqual(4096u, reopened.RecordSize);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1024L)]
    [DataRow(1024L * 8)]
    [DataRow(1024L * 15 + 1)]
    [DataRow(1024L * 17)]
    public void Parse_FileLengthChangedAfterTheOpen_FailsAsIncomplete(long newLength)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            writer.SetLength(newLength);
        }

        var failure = ParseThrows<InvalidDataException>(input, new MftFileScanOptions(BufferSizeRecords: 4));

        Assert.AreEqual(IncompleteMessage, failure.Message);
    }

    [TestMethod]
    public void Parse_TokenCancelledBeforeTheParse_ThrowsOperationCanceled()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = ParseThrows<OperationCanceledException>(input,
            new MftFileScanOptions(CancellationToken: cancellation.Token));

        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
    }

    [TestMethod]
    public void Parse_TokenCancelledFromProgress_StopsAsCancelledNotAsIncomplete()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        using var cancellation = new CancellationTokenSource();

        ParseThrows<OperationCanceledException>(input, new MftFileScanOptions(CancelOnReport(cancellation),
            BufferSizeRecords: 4, CancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void Parse_Progress_EndsAtTheFilesRecordCount()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        var samples = new List<MftScanProgress>();
        var progress = new SynchronousProgress<MftScanProgress>(samples.Add);

        using var result = input.Parse(new MftFileScanOptions(progress, BufferSizeRecords: 4));

        Assert.AreEqual(4, samples.Count, "sixteen records in chunks of four");
        Assert.AreEqual(16L, samples[^1].RecordsScanned);
        Assert.IsTrue(samples.All(sample => sample.TotalRecords == 16));
    }

    [TestMethod]
    public void ReadRecordBatches_YieldsMaterializedBatchesOfTheAskedSize()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);

        var batches = input.ReadRecordBatches(3, null, CancellationToken.None).ToArray();

        CollectionAssert.AreEqual(new[] { 3, 3, 1 }, batches.Select(batch => batch.Length).ToArray());
        Assert.AreEqual("$MFT", batches[0][0].FileName);
    }

    [TestMethod]
    public void ReadRecordBatches_TokenCancelledBetweenBatches_Throws()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);
        using var cancellation = new CancellationTokenSource();

        AssertCancelledAfterTheFirstBatch(input, cancellation);
    }

    [TestMethod]
    public void Parse_TwoParsesOfOneInput_EachReadTheWholeFile()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);

        CollectionAssert.AreEqual(Names(input), Names(input));
    }

    [TestMethod]
    public void Parse_AfterDispose_ThrowsObjectDisposed()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        var input = MftDumpInput.Open(path);
        input.Dispose();
        input.Dispose();

        ParseThrows<ObjectDisposedException>(input);
    }

    [TestMethod]
    public void Dispose_ReleasesTheFile()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        MftDumpInput.Open(path).Dispose();

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.AreEqual(16L * 1024, exclusive.Length);
    }

    internal static TException ParseThrows<TException>(MftDumpInput input, MftFileScanOptions options = default)
        where TException : Exception =>
        Assert.ThrowsException<TException>(() => input.Parse(options));

    static SynchronousProgress<MftScanProgress> CancelOnReport(CancellationTokenSource cancellation) =>
        new(_ => cancellation.Cancel());

    static void AssertCancelledAfterTheFirstBatch(MftDumpInput input, CancellationTokenSource cancellation)
    {
        Assert.ThrowsException<OperationCanceledException>(() =>
        {
            foreach (var _ in input.ReadRecordBatches(3, null, cancellation.Token))
            {
                cancellation.Cancel();
            }
        });
    }

    static string[] Names(MftDumpInput input)
    {
        using var result = input.Parse();
        return result.ToArray().Select(record => record.FileName).ToArray();
    }

    static byte[] Header(int recordSize) => MftDumpFixture.Standard(recordSize)[..32];

    static byte[] WithSignature(string signature)
    {
        var dump = MftDumpFixture.Standard();
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(dump, 0);
        return dump;
    }

    // The standard dump, sixteen kibibytes long, whose record zero declares another record size:
    // every size here divides that length or is rejected before the division.
    static byte[] WithDeclaredRecordSize(uint recordSize)
    {
        var dump = MftDumpFixture.Standard();
        BinaryPrimitives.WriteUInt32LittleEndian(dump.AsSpan(0x1C), recordSize);
        return dump;
    }
}
