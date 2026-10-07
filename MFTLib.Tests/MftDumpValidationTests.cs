using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     What a dump import accepts and what it rejects, through the producer and through an index
///     opened on the public factory. A rejected dump settles the drive as a producer failure with
///     the check's own message; an accepted one is a complete block with a zero journal cursor.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpValidationTests
{
    const string RecordSizeMessage = "Invalid or unsupported MFT record size.";
    const string PartialRecordMessage = "File size is not a whole multiple of record size.";
    const string NoRootMessage = "The dump has no valid allocated root record.";
    const string DuplicateMessage = "The dump contains duplicate base record numbers.";
    const string OutsideIndexMessage = "The dump contains required record numbers outside the index range.";

    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        FileUtilities.ResetToDefaults();
        MFTLibNative.ResetToDefaults();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    public static IEnumerable<object[]> RejectedDumps =>
    [
        ["empty", Array.Empty<byte>(), "The dump file is empty."],
        ["one byte", new byte[1], "The dump file could not be read completely."],
        ["short header", MftDumpFixture.Standard()[..31], "The dump file could not be read completely."],
        ["bad signature", Changed(dump => dump[0] = (byte)'B'), RecordSizeMessage],
        ["512-byte records", Changed(dump => DeclareRecordSize(dump, 512)), RecordSizeMessage],
        ["2048-byte records", Changed(dump => DeclareRecordSize(dump, 2048)), RecordSizeMessage],
        ["8192-byte records", Changed(dump => DeclareRecordSize(dump, 8192)), RecordSizeMessage],
        ["partial final record", MftDumpFixture.Standard()[..^100], PartialRecordMessage],
        ["invalid fixup", Changed(dump => dump[7 * 1024 + 510] ^= 0xFF),
            "The dump contains an invalid MFT record fixup."],
        ["no root record", WithRecords(records => records.Remove(5)), NoRootMessage],
        ["freed root", WithRecords(records => records[5] = records[5] with { InUse = false }), NoRootMessage],
        ["root that is a file", WithRecords(records => records[5] = records[5] with { IsDirectory = false }),
            NoRootMessage],
        ["root whose parent is another record", WithRecords(records => records[5] = records[5] with { Parent = 6 }),
            NoRootMessage],
        ["root with a malformed attribute", Changed(dump => BreakFirstAttribute(dump, 5)), NoRootMessage],
        ["parent above 32 bits", WithRecords(records => records[9] = records[9] with { Parent = 0x1_0000_0000 }),
            OutsideIndexMessage],
        ["parent at the top of 48 bits",
            WithRecords(records => records[9] = records[9] with { Parent = 0xFFFF_FFFF_FFFF }), OutsideIndexMessage],
        ["parent past the planned block", WithRecords(records => records[9] = records[9] with { Parent = 10_000_000 }),
            OutsideIndexMessage]
    ];

    [TestMethod]
    [DynamicData(nameof(RejectedDumps))]
    public async Task Open_RejectedDump_SettlesAsProducerFailedWithTheChecksMessage(string description,
        byte[] dump, string message)
    {
        var path = MftDumpFixture.WriteFile(_directory, dump);

        await using var index = await FileIndex.OpenAsync(Options(path), CancellationToken.None);

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State, description);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, drive.FailureKind, description);
        Assert.AreEqual(message, drive.MftProducerFailureMessage, description);
        Assert.IsFalse(drive.WatchSupported, description);
    }

    [TestMethod]
    public async Task Open_MissingDump_SettlesAsProducerFailedWithTheOpenError()
    {
        await using var index = await FileIndex.OpenAsync(Options(Path.Combine(_directory, "absent.mft")),
            CancellationToken.None);

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveFailureKind.ProducerFailed, drive.FailureKind);
        StringAssert.StartsWith(drive.MftProducerFailureMessage, "Failed to open file. Error: ");
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public async Task Open_ValidDump_IsReadyWithEveryRecordsNameSizeAndTime(int recordSize)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(recordSize));

        await using var index = await FileIndex.OpenAsync(Options(path), CancellationToken.None);

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
        Assert.AreEqual(0, drive.SkippedRecordCount);
        Assert.AreEqual(7u, drive.LiveRowCount);
        Assert.IsFalse(drive.CompactionNeeded);
        Assert.IsFalse(drive.WatchSupported);
        Assert.IsNull(drive.MftProducerFailureMessage);

        var notes = index.Find("dump:/D/documents/Notes.txt")!.Value;
        Assert.AreEqual(11L, notes.Size);
        Assert.IsTrue(notes.IsSizeKnown);
        Assert.AreEqual(DateTime.FromFileTimeUtc(MftDumpFixture.BaseFileTime + 10_000_000), notes.LastWriteTime);
        Assert.AreEqual(22L, index.Find("dump:/D/documents/Deep/leaf.txt")!.Value.Size);
        Assert.IsFalse(index.Find("dump:/D/sizeless.bin")!.Value.IsSizeKnown);
        Assert.IsTrue(index.Find("dump:/D/documents")!.Value.IsDirectory);
        CollectionAssert.AreEquivalent(new[] { "$MFT", "documents", "sizeless.bin" },
            index.Root('D').Children().Select(child => child.Name).ToArray());
    }

    [TestMethod]
    public async Task Open_RecordWithAMalformedAttribute_IsOmittedAndNotCountedAsSkipped()
    {
        var path = MftDumpFixture.WriteFile(_directory, Changed(dump => BreakFirstAttribute(dump, 9)));

        await using var index = await FileIndex.OpenAsync(Options(path), CancellationToken.None);

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(0, drive.SkippedRecordCount);
        Assert.AreEqual(6u, drive.LiveRowCount);
        Assert.IsNull(index.Find("dump:/D/documents/Deep/leaf.txt"));
        Assert.IsNotNull(index.Find("dump:/D/documents/Deep"));
    }

    [TestMethod]
    public async Task Open_NeverTouchesTheLiveVolumeThatSharesTheLetter()
    {
        FileUtilities._getVolumeHandle = path => throw new AssertFailedException($"A dump opened the volume {path}.");
        MFTLibNative._queryUsnJournal = _ => throw new AssertFailedException("A dump queried a live journal.");
        MFTLibNative._readUsnJournal = (_, _, _, _) => throw new AssertFailedException("A dump read a live journal.");
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) =>
            throw new AssertFailedException("A dump parsed a live volume.");
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());

        await using var index = await FileIndex.OpenAsync(
            Options(path, MftIndexSources.FromMftDumpFile(path, 'c'), 'C'), CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.AreEqual("dump:/C/documents/Notes.txt", index.Find("dump:/C/documents/Notes.txt")!.Value.Path);
    }

    [TestMethod]
    public async Task Produce_ValidDump_CompletesABlockWithAZeroCursorAndTheRequestedIdentity()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        var request = Request() with { CacheTag = new CacheTag("DUMP", 3) };

        var result = await new MftDumpBlockProducer(path).ProduceAsync(request, CancellationToken.None);

        using var block = result.Block;
        var header = block.Header;
        Assert.AreEqual(0UL, result.JournalId);
        Assert.AreEqual(0L, result.NextUsn);
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.IsNull(result.CatchUpLoss);
        Assert.AreEqual(0UL, header.UsnJournalId);
        Assert.AreEqual(0L, header.UsnNextUsn);
        Assert.AreEqual(0u, header.VolumeSerial);
        Assert.AreEqual(ProducerKind.Mft, header.ProducerKind);
        Assert.AreEqual(5u, header.RootRow);
        Assert.AreEqual(new CacheTag("DUMP", 3), header.CacheTag);
        Assert.AreEqual(BlockValidationResult.Valid, BlockHeader.Validate(in header, 0, block.Length));
        Assert.AreEqual(11u, header.RowCount, "rows are dense by record number, and the last record is 10");
    }

    [TestMethod]
    public async Task Produce_ReportsTheParseTotalThenTheTransferredRows()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        var samples = new List<IndexScanProgress>();
        var request = Request() with { Progress = new SynchronousProgress<IndexScanProgress>(samples.Add) };

        using var block = (await new MftDumpBlockProducer(path).ProduceAsync(request, CancellationToken.None)).Block;

        var parsing = samples.Where(sample => sample.Phase == IndexScanPhase.ParsingMft).ToArray();
        var transferring = samples.Where(sample => sample.Phase == IndexScanPhase.Transferring).ToArray();
        Assert.IsTrue(parsing.Length > 0);
        Assert.AreEqual(16u, parsing[^1].RowsWritten);
        Assert.AreEqual(16u, parsing[^1].TotalRows);
        Assert.AreEqual(7u, transferring[^1].RowsWritten);
        Assert.IsNull(transferring[^1].TotalRows);
        Assert.IsTrue(samples.All(sample => sample.DriveLetter == 'D' && sample.Outcome is null));
        Assert.IsTrue(samples.IndexOf(parsing[^1]) < samples.IndexOf(transferring[0]),
            "the parse total is reported before the first transfer sample");
    }

    [TestMethod]
    public async Task Produce_ReleasesTheDumpFileAfterSuccessAndAfterFailure()
    {
        var valid = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(), "valid.mft");
        var invalid = MftDumpFixture.WriteFile(_directory, Changed(dump => dump[7 * 1024 + 510] ^= 0xFF),
            "invalid.mft");

        (await new MftDumpBlockProducer(valid).ProduceAsync(Request(), CancellationToken.None)).Block.Dispose();
        await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => new MftDumpBlockProducer(invalid).ProduceAsync(Request(), CancellationToken.None));

        foreach (var path in new[] { valid, invalid })
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsTrue(exclusive.Length > 0, path);
        }
    }

    [TestMethod]
    public async Task Produce_RejectedDump_LeavesNoBlockFileBehind()
    {
        var path = MftDumpFixture.WriteFile(_directory, WithRecords(records => records.Remove(5)));
        var request = Request();

        var failure = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => new MftDumpBlockProducer(path).ProduceAsync(request, CancellationToken.None));

        Assert.AreEqual(NoRootMessage, failure.Message);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_DumpTheParserRejects_FailsBeforeAnyBlockIsCreated()
    {
        // The block path names a directory that does not exist, so creating the block would fail
        // with another exception: the parser's rejection arriving proves the parse ran first.
        var path = MftDumpFixture.WriteFile(_directory, Changed(dump => dump[7 * 1024 + 510] ^= 0xFF));
        var request = Request() with { BlockPath = Path.Combine(_directory, "absent", "block.mlix") };

        var failure = await Assert.ThrowsExceptionAsync<InvalidDataException>(
            () => new MftDumpBlockProducer(path).ProduceAsync(request, CancellationToken.None));

        Assert.AreEqual("The dump contains an invalid MFT record fixup.", failure.Message);
        Assert.IsFalse(Directory.Exists(Path.Combine(_directory, "absent")));
    }

    [TestMethod]
    public async Task Produce_SourceWithNoBatches_HasNoRoot()
    {
        var failure = await ProduceFailsAsync();

        Assert.AreEqual(NoRootMessage, failure.Message);
    }

    [TestMethod]
    public async Task Produce_DuplicateBaseRecordNumbers_AreRejected()
    {
        var failure = await ProduceFailsAsync(_ => [Root(), Leaf(7, 5), Leaf(8, 5)], _ => [Leaf(9, 5), Leaf(7, 6)]);

        Assert.AreEqual(DuplicateMessage, failure.Message);
    }

    [TestMethod]
    public async Task Produce_DuplicateRecordNumbersPastTheBlock_AreRejected()
    {
        var failure = await ProduceFailsAsync(
            _ => [Root(), Leaf(0x2_0000_0000, 5), Leaf(0x2_0000_0001, 5), Leaf(0x2_0000_0000, 5)]);

        Assert.AreEqual(DuplicateMessage, failure.Message);
    }

    [TestMethod]
    public async Task Produce_AFreedRecordRepeatingAnAllocatedNumber_IsDroppedNotADuplicate()
    {
        var result = await ProduceAsync(_ => [Root(), Leaf(7, 5), Leaf(7, 5) with { InUse = false }]);

        using var block = result.Block;
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.AreEqual(8u, block.Header.RowCount);
    }

    [TestMethod]
    public async Task Produce_ParentAboveThirtyTwoBits_IsRejectedWhileAnOversizedLeafIsSkippedAndCounted()
    {
        var skipped = await ProduceAsync(capacity =>
            [Root(), Leaf(7, 5), Leaf(0x1_0000_0000, 5), Leaf(capacity, 5), Leaf(capacity + 9, 7)]);
        var rejected = await ProduceFailsAsync(_ => [Root(), Leaf(7, 0x1_0000_0000)]);

        using var block = skipped.Block;
        Assert.AreEqual(3, skipped.SkippedRecordCount, "one record above 32 bits and two past the block");
        Assert.IsTrue(block.Header.IsCompactionNeeded, "a row past the block marks the block as needing compaction");
        Assert.AreEqual(OutsideIndexMessage, rejected.Message);
    }

    [TestMethod]
    public async Task Produce_ParentAtTheLastRowIsAccepted_AndOneRowPastItIsRejected()
    {
        var accepted = await ProduceAsync(capacity => [Root(), Leaf(7, capacity - 1)]);
        var rejected = await ProduceFailsAsync(capacity => [Root(), Leaf(7, capacity)]);

        accepted.Block.Dispose();
        Assert.AreEqual(OutsideIndexMessage, rejected.Message);
    }

    [TestMethod]
    [DataRow("no record five", false, true, true, 5UL)]
    [DataRow("freed", true, false, true, 5UL)]
    [DataRow("a file", true, true, false, 5UL)]
    [DataRow("under another record", true, true, true, 6UL)]
    public async Task Produce_InvalidRoot_IsRejectedOnceEveryBatchIsWritten(string description, bool present,
        bool inUse, bool isDirectory, ulong parent)
    {
        MftRecordTestValues[] root = present
            ? [Root() with { InUse = inUse, IsDirectory = isDirectory, ParentRecordNumber = parent }]
            : [];
        var batchesRead = 0;
        var failure = await ProduceFailsAsync(_ =>
        {
            batchesRead++;
            return [.. root, Leaf(7, 5)];
        }, _ =>
        {
            batchesRead++;
            return [Leaf(8, 5)];
        });

        Assert.AreEqual(NoRootMessage, failure.Message, description);
        Assert.AreEqual(2, batchesRead, description);
    }

    [TestMethod]
    public async Task Produce_OnlyFreedRecords_HasNoRoot()
    {
        var failure = await ProduceFailsAsync(_ => [Root() with { InUse = false }, Leaf(7, 5) with { InUse = false }]);

        Assert.AreEqual(NoRootMessage, failure.Message);
    }

    [TestMethod]
    public async Task Produce_TokenCancelledBeforeTheScan_IsCancelledWithoutOpeningTheDump()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var producer = new MftDumpBlockProducer(Path.Combine(_directory, "absent.mft"));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => producer.ProduceAsync(Request(), cancellation.Token));
    }

    [TestMethod]
    public async Task Produce_TokenCancelledBetweenBatches_IsCancelledAndLeavesNoBlockFile()
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
        using var cancellation = new CancellationTokenSource();
        var request = Request();
        var producer = new MftDumpBlockProducer(path, CancellingSource(cancellation));

        var cancelled = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => producer.ProduceAsync(request, cancellation.Token));

        Assert.AreEqual(cancellation.Token, cancelled.CancellationToken);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_NullRequest_Throws()
    {
        var producer = new MftDumpBlockProducer(Path.Combine(_directory, "absent.mft"));

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => producer.ProduceAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    public void Constructor_BlankPath_Throws(string path)
    {
        Assert.ThrowsException<ArgumentException>(() => new MftDumpBlockProducer(path), path);
    }

    [TestMethod]
    public void Validation_NullArguments_Throw()
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(_directory, "validation.mlix"),
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 64,
            NamePoolCapacity = 1024,
            DeleteOnClose = true
        });

        var validation = new MftDumpRecordValidation(block);

        Assert.ThrowsException<ArgumentNullException>(() => new MftDumpRecordValidation(null!));
        Assert.ThrowsException<ArgumentNullException>(() => validation.Validate(null!));
    }

    static MftDumpRecordSource CancellingSource(CancellationTokenSource cancellation) =>
        (_, _, _) => CancelAfterFirstBatch(cancellation);

    static IEnumerable<IReadOnlyList<MftRecord>> CancelAfterFirstBatch(CancellationTokenSource cancellation)
    {
        yield return [MftRecord.CreateForTest(Root()), MftRecord.CreateForTest(Leaf(7, 5))];
        cancellation.Cancel();
        yield return [MftRecord.CreateForTest(Leaf(8, 5))];
    }

    async Task<InvalidDataException> ProduceFailsAsync(params Func<ulong, MftRecordTestValues[]>[] batches)
    {
        var request = Request();
        var failure = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Producer(batches)
            .ProduceAsync(request, CancellationToken.None));
        Assert.IsFalse(File.Exists(request.BlockPath), "a rejected import leaves no block file");
        return failure;
    }

    Task<MftBlockProduceResult> ProduceAsync(params Func<ulong, MftRecordTestValues[]>[] batches) =>
        Producer(batches).ProduceAsync(Request(), CancellationToken.None);

    // A producer over the standard dump whose records are replaced by synthetic batches, each built
    // from the slot capacity the producer planned for that dump.
    MftDumpBlockProducer Producer(Func<ulong, MftRecordTestValues[]>[] batches)
    {
        var path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard(), $"{Guid.NewGuid():N}.mft");
        return new MftDumpBlockProducer(path, (input, _, _) =>
        {
            ulong capacity = MftBlockCapacity.Plan(input.VolumeInformation).SlotCapacity;
            return batches.Select(IReadOnlyList<MftRecord> (batch) =>
                batch(capacity).Select(MftRecord.CreateForTest).ToArray());
        });
    }

    MftBlockProduceRequest Request() => new()
    {
        DriveLetter = 'D',
        VolumeSerial = 0,
        BlockPath = Path.Combine(_directory, $"{Guid.NewGuid():N}.mlix"),
        DeleteOnClose = true
    };

    static FileIndexOptions Options(string path, MftIndexSource? source = null, char driveLetter = 'D') => new()
    {
        Drives = [new IndexedDrive(driveLetter, MftDumpPaths.CanonicalRoot(driveLetter), 0)],
        NoCache = true,
        MftSource = source ?? MftIndexSources.FromMftDumpFile(path, driveLetter)
    };

    static MftRecordTestValues Root() => new()
    {
        RecordNumber = 5,
        ParentRecordNumber = 5,
        IsDirectory = true,
        FileName = "."
    };

    static MftRecordTestValues Leaf(ulong recordNumber, ulong parent) => new()
    {
        RecordNumber = recordNumber,
        ParentRecordNumber = parent,
        FileName = $"file-{recordNumber}.txt"
    };

    static byte[] Changed(Action<byte[]> change)
    {
        var dump = MftDumpFixture.Standard();
        change(dump);
        return dump;
    }

    static byte[] WithRecords(Action<Dictionary<int, DumpRecord>> change)
    {
        var records = MftDumpFixture.StandardRecords();
        change(records);
        return MftDumpFixture.Build(1024, 16, records);
    }

    static void DeclareRecordSize(byte[] dump, uint recordSize) =>
        BinaryPrimitives.WriteUInt32LittleEndian(dump.AsSpan(0x1C), recordSize);

    // Gives the record's first attribute a length that runs past the record, which the parser
    // refuses to walk, so the record yields no row. No sector tail is touched.
    static void BreakFirstAttribute(byte[] dump, int recordNumber)
    {
        var record = dump.AsSpan(recordNumber * 1024, 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(record[(MftDumpFixture.FirstAttributeOffset(record) + 4)..], 0x7FFF_FFF0);
    }
}
