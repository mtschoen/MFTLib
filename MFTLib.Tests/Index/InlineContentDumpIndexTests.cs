using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.ReadInlineContentAsync" /> end to end over a dump source and the native reader: the bytes
///     returned are the bytes the fixture wrote, on Windows and Linux, at both record sizes; a record that changes in
///     place after the index was built is answered from what the file holds now, and a path renamed, deleted or
///     replaced after the scan never substitutes another file's bytes.
/// </summary>
[TestClass]
public sealed class InlineContentDumpIndexTests : IDisposable
{
    const string NotesPath = "dump:/D/documents/Notes.txt";
    const string LeafPath = "dump:/D/documents/Deep/leaf.txt";

    readonly TempDirectory _directory = new("mftlib-inline-index");

    public TestContext TestContext { get; set; } = null!;

    public void Dispose() => _directory.Dispose();

    static Dictionary<int, DumpRecord> Records(int recordSize)
    {
        var records = MftDumpFixture.StandardRecords();
        records[7] = records[7] with { Content = InlineContentDump.Pattern(7, 11) };
        records[9] = records[9] with { Content = InlineContentDump.Pattern(9, InlineContentDump.LargestContent(recordSize, "leaf.txt")) };
        records[11] = new DumpRecord("empty.txt", 5) { Content = [] };
        records[12] = new DumpRecord("compressed.dat", 5) { NonResidentData = true, Size = 1 << 20 };
        records[13] = new DumpRecord("damaged.txt", 5) { Content = [4, 5, 6] };
        return records;
    }

    async Task<(FileIndex Index, string Path, Dictionary<int, DumpRecord> Records)> OpenAsync(int recordSize)
    {
        var records = Records(recordSize);
        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(recordSize, 16, records));
        var index = await FileIndex.OpenAsync(new FileIndexOptions { MftSource = MftIndexSources.FromMftDumpFile(path, 'D') },
            TestContext.CancellationToken);
        return (index, path, records);
    }

    static async Task<InlineAnswer[]> ReadAsync(FileIndex index, params FileEntry[] entries)
    {
        var answers = new InlineAnswer[entries.Length];
        var result = await index.ReadInlineContentAsync(entries,
            result => answers[result.Index] = new InlineAnswer(result.Outcome, result.Content.ToArray()),
            CancellationToken.None);
        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome, result.Failure?.ToString());
        return answers;
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    [TestCategory("Smoke")]
    public async Task Read_FilesOfADump_ReturnTheBytesTheDumpHolds(int recordSize)
    {
        var (index, _, records) = await OpenAsync(recordSize);
        await using var owner = index;
        var notes = index.Find(NotesPath, TestContext.CancellationToken)!.Value;
        var leaf = index.Find(LeafPath, TestContext.CancellationToken)!.Value;
        var empty = index.Find("dump:/D/empty.txt", TestContext.CancellationToken)!.Value;
        var nonresident = index.Find("dump:/D/compressed.dat", TestContext.CancellationToken)!.Value;
        var directory = index.Find("dump:/D/documents", TestContext.CancellationToken)!.Value;
        var sizeless = index.Find("dump:/D/sizeless.bin", TestContext.CancellationToken)!.Value;

        var answers = await ReadAsync(index, leaf, notes, directory, empty, nonresident, sizeless);

        Assert.AreEqual(InlineContentOutcome.Returned, answers[0].Outcome);
        CollectionAssert.AreEqual(records[9].Content, answers[0].Content);
        Assert.AreEqual(InlineContentOutcome.Returned, answers[1].Outcome);
        CollectionAssert.AreEqual(records[7].Content, answers[1].Content);
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[2].Outcome, "a directory");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[3].Outcome);
        Assert.AreEqual(0, answers[3].Content.Length, "valid empty content");
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[4].Outcome, "nonresident data");
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[5].Outcome, "no data attribute");
    }

    [TestMethod]
    public async Task Read_AfterTheRecordChanged_AnswersFromWhatTheFileHoldsNow()
    {
        var (index, path, records) = await OpenAsync(1024);
        await using var owner = index;
        var notes = index.Find(NotesPath, TestContext.CancellationToken)!.Value;
        var leaf = index.Find(LeafPath, TestContext.CancellationToken)!.Value;
        var empty = index.Find("dump:/D/empty.txt", TestContext.CancellationToken)!.Value;
        var compressed = index.Find("dump:/D/compressed.dat", TestContext.CancellationToken)!.Value;
        var damaged = index.Find("dump:/D/damaged.txt", TestContext.CancellationToken)!.Value;
        records[7] = records[7] with { Content = InlineContentDump.Pattern(70, 11) };
        records[9] = records[9] with { SequenceNumber = 2 };
        records[11] = records[11] with { InUse = false };
        records[12] = records[12] with { NonResidentData = false, Content = [1, 2, 3] };
        var changed = MftDumpFixture.Build(1024, 16, records);
        changed[(13 * 1024) + 510] ^= 0xFF;
        await File.WriteAllBytesAsync(path, changed, TestContext.CancellationToken);

        var answers = await ReadAsync(index, notes, leaf, empty, compressed,
            index.Find("dump:/D/documents", TestContext.CancellationToken)!.Value, damaged);

        Assert.AreEqual(InlineContentOutcome.Returned, answers[0].Outcome);
        CollectionAssert.AreEqual(InlineContentDump.Pattern(70, 11), answers[0].Content, "new content under the same incarnation");
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[1].Outcome, "another sequence number");
        Assert.AreEqual(InlineContentOutcome.Superseded, answers[2].Outcome, "the record was freed");
        Assert.AreEqual(InlineContentOutcome.Returned, answers[3].Outcome, "the file shrank into its record");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, answers[3].Content);
        Assert.AreEqual(InlineContentOutcome.NotInline, answers[4].Outcome, "a directory is decided without reading it");
        Assert.AreEqual(InlineContentOutcome.Unreadable, answers[5].Outcome, "a file record whose fixup no longer matches");
    }

    [TestMethod]
    public async Task Read_AfterTheDumpPathIsReplaced_ReadsTheScannedFileUntilARescan()
    {
        var (index, path, records) = await OpenAsync(1024);
        await using var owner = index;
        var notes = index.Find(NotesPath, TestContext.CancellationToken)!.Value;
        var replacement = MftDumpFixture.StandardRecords();
        replacement[7] = replacement[7] with { Content = "unrelated"u8.ToArray() };
        var replacementPath = Path.Combine(_directory.Path, "replacement.mft");
        await File.WriteAllBytesAsync(replacementPath, MftDumpFixture.Build(1024, 16, replacement), TestContext.CancellationToken);
        File.Move(path, path + ".original");
        File.Move(replacementPath, path);

        var beforeRescan = await ReadAsync(index, notes);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await index.RescanAsync(TestContext.CancellationToken)).Outcome);
        var renamed = index.Find(NotesPath, TestContext.CancellationToken)!.Value;
        var afterRescan = await ReadAsync(index, renamed);

        CollectionAssert.AreEqual(records[7].Content, beforeRescan[0].Content,
            "a replaced path must not substitute unrelated bytes into the old index");
        CollectionAssert.AreEqual("unrelated"u8.ToArray(), afterRescan[0].Content,
            "a successful rescan binds reads to the replacement");
    }

    [TestMethod]
    public async Task Read_AfterTheDumpIsUnlinkedAndRecreated_ReadsTheScannedFile()
    {
        var (index, path, records) = await OpenAsync(1024);
        await using var owner = index;
        var notes = index.Find(NotesPath, TestContext.CancellationToken)!.Value;
        File.Delete(path);
        // The same slot and sequence in a new file at the same path: a recycled file identity (on ext4 the new
        // file often reuses the inode) must not let the old index read it.
        var replacement = MftDumpFixture.StandardRecords();
        replacement[7] = new DumpRecord("other.txt", 6) { Content = "replacement"u8.ToArray() };
        await File.WriteAllBytesAsync(path, MftDumpFixture.Build(1024, 16, replacement), TestContext.CancellationToken);

        var beforeRescan = await ReadAsync(index, notes);
        Assert.AreEqual(RootOperationOutcome.Succeeded, (await index.RescanAsync(TestContext.CancellationToken)).Outcome);
        var afterRescan = await ReadAsync(index, index.Find("dump:/D/documents/other.txt", TestContext.CancellationToken)!.Value);

        CollectionAssert.AreEqual(records[7].Content, beforeRescan[0].Content,
            "the old index reads the file it scanned, never the one now at its path");
        CollectionAssert.AreEqual(replacement[7].Content, afterRescan[0].Content,
            "a successful rescan reads the new file");
    }

    [TestMethod]
    [DataRow(NotesPath, DisplayName = "answers read from one record")]
    [DataRow("dump:/D/documents", DisplayName = "answers decided without I/O")]
    public async Task Read_CancelledInsideTheHandler_ThrowsOperationCanceled(string entryPath)
    {
        var (index, _, _) = await OpenAsync(1024);
        await using var owner = index;
        var entry = index.Find(entryPath, TestContext.CancellationToken)!.Value;
        using var cancellation = new CancellationTokenSource();
        var delivered = 0;

        // The cancellation lands after the cadence's first check, and no later answer would check it again.
        await Assert.ThrowsAsync<OperationCanceledException>(() => index.ReadInlineContentAsync(
            [entry, entry, entry], _ =>
            {
                delivered++;
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
            }, cancellation.Token));

        Assert.IsGreaterThanOrEqualTo(1, delivered, "the handler that cancelled was called");
    }

    [TestMethod]
    public async Task Reader_KeepsTheFileOpenUntilTheBlockAndEveryLeaseAreReleased()
    {
        var records = Records(1024);
        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(1024, 16, records));
        using var input = MftDumpInput.Open(path);
        var reader = new DumpInlineContentReader(input);
        var lease = reader.Lease();
        reader.Dispose();
        reader.Dispose();
        var answers = new List<InlineAnswer>();

        await reader.ReadAsync('D', 0, new[] { new InlineContentRequest(0, 7, 1) },
            (_, outcome, content) => answers.Add(new InlineAnswer(outcome, content.ToArray())), TestContext.CancellationToken);
        lease.Dispose();
        lease.Dispose();

        CollectionAssert.AreEqual(records[7].Content, answers.Single().Content, "a lease outlives the block's release");
        Assert.ThrowsExactly<ObjectDisposedException>(() => input.OpenInlineContentReader(), "the last release closes the file");
        Assert.ThrowsExactly<ObjectDisposedException>(() => reader.Lease());
    }

    [TestMethod]
    public async Task Read_MoreEntriesThanOneNativeBatch_AnswersEveryEntryOnce()
    {
        const int fileCount = NativeInlineContentReader.BatchRequests + 2000;
        var records = new Dictionary<int, DumpRecord>
        {
            [0] = new DumpRecord("$MFT", 5),
            [5] = new DumpRecord(".", 5) { IsDirectory = true }
        };
        for (var number = 16; number < 16 + fileCount; number++)
        {
            records[number] = new DumpRecord($"f{number}.bin", 5) { Content = BitConverter.GetBytes(number) };
        }

        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(1024, 16 + fileCount, records));
        await using var index = await FileIndex.OpenAsync(
            new FileIndexOptions { MftSource = MftIndexSources.FromMftDumpFile(path, 'D') },
            TestContext.CancellationToken);
        var entries = index.Search(new SearchQuery(".bin"), TestContext.CancellationToken)
            .Reverse().ToArray();
        Assert.HasCount(fileCount, entries);
        var seen = new int[entries.Length];
        var wrong = 0;

        var result = await index.ReadInlineContentAsync(entries, answer =>
        {
            seen[answer.Index]++;
            if (answer.Outcome != InlineContentOutcome.Returned ||
                BitConverter.ToInt32(answer.Content) != (int)entries[answer.Index].RecordKey.RecordNumber)
            {
                wrong++;
            }
        }, CancellationToken.None);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(seen.All(count => count == 1), "every position is answered exactly once");
        Assert.AreEqual(0, wrong, "every answer carries the bytes of its own record");
    }

    [TestMethod]
    public async Task Readers_AreBoundToTheDumpBlockOrLiveVolumeSource()
    {
        var (index, _, _) = await OpenAsync(1024);
        await using var owner = index;
        Assert.IsNotNull(index.Root(TestContext.CancellationToken).RootBlock.Block.DumpContentReader);
        Assert.IsNotNull(MftIndexSources.FromLocalVolumes().InlineContentReader);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "a second index publishes its own scan")]
    [DataRow(true, DisplayName = "a rescan fails before publication")]
    public async Task Read_OriginalBlockKeepsReadingItsScannedFile(bool failRescan)
    {
        var records = Records(1024);
        var path = MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(1024, 16, records));
        var token = TestContext.CancellationToken;
        var options = new FileIndexOptions { MftSource = MftIndexSources.FromMftDumpFile(path, 'D') };
        await using var first = await FileIndex.OpenAsync(options, token);
        var entry = first.Find(NotesPath, token)!.Value;
        File.Move(path, path + ".original");
        var replacement = MftDumpFixture.StandardRecords();
        replacement[7] = new DumpRecord("other.txt", 6) { Content = "replacement"u8.ToArray() };
        await File.WriteAllBytesAsync(path, MftDumpFixture.Build(1024, 16, replacement), token);
        if (failRescan)
        {
            first.Hooks.PublishInsideWriteGate = _ => throw new IOException("Refuse replacement before commit");
            Assert.AreEqual(RootOperationOutcome.Failed, (await first.RescanAsync(token)).Outcome);
        }
        else
        {
            await using var second = await FileIndex.OpenAsync(options, token);
            var other = second.Find("dump:/D/documents/other.txt", token)!.Value;
            CollectionAssert.AreEqual(replacement[7].Content, (await ReadAsync(second, other))[0].Content);
        }

        var answer = await ReadAsync(first, entry);
        CollectionAssert.AreEqual(records[7].Content, answer[0].Content, "the replacement's bytes were delivered");
    }
}
