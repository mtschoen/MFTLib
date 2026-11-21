using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A record's answer never depends on which other records share its read: a short or failed group read keeps every
// record it did deliver, retries each other record on its own, and makes Unreadable only a record whose own read fails.
public sealed partial class InlineContentReaderStateTests
{
    const int SalvageRecordSize = 1024;

    // Records 7 and 9 sit close enough to share one read, as do 20, 21 and 22.
    static Dictionary<int, DumpRecord> SalvageFiles()
    {
        var records = Files(32, 40);
        records[7] = new DumpRecord("seven.bin", 5) { Content = InlineContentDump.Pattern(7, 40) };
        records[9] = new DumpRecord("nine.bin", 5) { Content = InlineContentDump.Pattern(9, 40) };
        return records;
    }

    static InlineAnswer[] ReadOpen(NativeInlineContentReader reader, params long[] records)
    {
        var requests = records.Select((number, position) => new InlineContentRequest(position, number, 1)).ToArray();
        var answers = new InlineAnswer[requests.Length];
        reader.Read(requests, (position, outcome, content) => answers[position] = new InlineAnswer(outcome, content.ToArray()),
            CancellationToken.None);
        return answers;
    }

    [TestMethod]
    public void Read_RecordBesideOneTheTruncatedDumpLost_ReturnsTheSameBytesAsAlone()
    {
        var records = SalvageFiles();
        var path = WriteDump(SalvageRecordSize, 32, records);
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            file.SetLength(8 * SalvageRecordSize);
        }

        var alone = ReadOpen(reader, 7);
        var beside = ReadOpen(reader, 7, 9);

        Assert.AreEqual(InlineContentOutcome.Returned, alone[0].Outcome);
        CollectionAssert.AreEqual(records[7].Content, alone[0].Content);
        Assert.AreEqual(InlineContentOutcome.Returned, beside[0].Outcome, "the record the short read delivered");
        CollectionAssert.AreEqual(alone[0].Content, beside[0].Content);
        Assert.AreEqual(InlineContentOutcome.Unreadable, beside[1].Outcome, "the record past the truncated end");
    }

    [TestMethod]
    [DataRow(0b1UL, true, true, true, DisplayName = "only the group read fails")]
    [DataRow(0b101UL, true, false, true, DisplayName = "the group read and the middle record's own read fail")]
    [DataRow(0b11UL, false, true, true, DisplayName = "the group read and the first record's own read fail")]
    public void Read_GroupReadThatFails_RetriesEachRecordAndKeepsEverySurvivor(ulong failingReads, bool first,
        bool middle, bool last)
    {
        var records = SalvageFiles();
        var path = WriteDump(SalvageRecordSize, 32, records);
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        NativeTestHooks.NativeSetReadFailMask(failingReads);

        var answers = ReadOpen(reader, 20, 21, 22);

        Assert.AreEqual(4, reader.NativeReadCalls, "one group read, then one bounded read per record");
        foreach (var (survives, number, answer) in new[] { first, middle, last }.Zip([20, 21, 22], answers))
        {
            Assert.AreEqual(survives ? InlineContentOutcome.Returned : InlineContentOutcome.Unreadable, answer.Outcome,
                $"record {number}");
            CollectionAssert.AreEqual(survives ? records[number].Content : [], answer.Content, $"record {number}");
        }
    }

    [TestMethod]
    public void Read_EveryRecordsOwnReadFails_AnswersEachUnreadableWithoutFailingTheRead()
    {
        var path = WriteDump(1024, 4096, Files(4096, 32));
        // Records far apart are separate one-record groups, so each of the three reads is one record's own read.
        NativeTestHooks.NativeSetReadFailMask(0b111);

        var answers = InlineContentDump.Read(path, (100, 1), (2100, 1), (4000, 1));

        Assert.AreSequenceEqual(
            [InlineContentOutcome.Unreadable, InlineContentOutcome.Unreadable, InlineContentOutcome.Unreadable],
            answers.Select(answer => answer.Outcome));
    }

    [TestMethod]
    public void Read_CancelledDuringTheRecordRetries_ThrowsOperationCanceled()
    {
        var path = WriteDump(SalvageRecordSize, 32, SalvageFiles());
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        NativeTestHooks.NativeSetReadFailMask(0b1);
        // The first check precedes the group read; the second precedes its first retry.
        NativeTestHooks.NativeSetCancelCheckCountdown(2);

        Assert.ThrowsExactly<OperationCanceledException>(() => ReadOpen(reader, 20, 21, 22));
    }

    [TestMethod]
    public void Read_GroupReadOfAVolumeThatFails_KeepsTheRecordsWhoseOwnReadsSucceed()
    {
        var records = Files(64, 40);
        var path = Path.Combine(_directory.Path, "fragmented.img");
        FragmentedNtfsImage.Write(path, MftDumpFixture.Build(SalvageRecordSize, 64, records), firstRunRecords: 32,
            holeClusters: 3);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(SalvageRecordSize);
        using var image = File.OpenHandle(path);
        using var reader = NativeInlineContentReader.Open(image);
        var alone = ReadOpen(reader, 21);
        NativeTestHooks.NativeSetReadFailMask(0b101);

        var beside = ReadOpen(reader, 20, 21, 22);

        Assert.AreEqual(InlineContentOutcome.Returned, beside[0].Outcome);
        Assert.AreEqual(InlineContentOutcome.Unreadable, beside[1].Outcome, "its own read failed");
        Assert.AreEqual(InlineContentOutcome.Returned, beside[2].Outcome);
        CollectionAssert.AreEqual(records[20].Content, beside[0].Content);
        CollectionAssert.AreEqual(records[22].Content, beside[2].Content);
        Assert.AreEqual(InlineContentOutcome.Returned, alone[0].Outcome);
        CollectionAssert.AreEqual(records[21].Content, alone[0].Content);
    }

    [TestMethod]
    public void Read_RecordBesideAMalformedOne_ReturnsTheSameBytesAsAlone()
    {
        var records = SalvageFiles();
        var dump = MftDumpFixture.Build(SalvageRecordSize, 32, records);
        // A torn sector: the update sequence number at the end of record 21's first sector no longer matches.
        dump[(21 * SalvageRecordSize) + 510] ^= 0xFF;
        var path = MftDumpFixture.WriteFile(_directory.Path, dump);
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();

        var alone = ReadOpen(reader, 20);
        var beside = ReadOpen(reader, 20, 21, 22);

        Assert.AreEqual(InlineContentOutcome.Unreadable, beside[1].Outcome);
        foreach (var (number, index) in new[] { (20, 0), (22, 2) })
        {
            Assert.AreEqual(InlineContentOutcome.Returned, beside[index].Outcome, $"record {number}");
            CollectionAssert.AreEqual(records[number].Content, beside[index].Content, $"record {number}");
        }

        CollectionAssert.AreEqual(alone[0].Content, beside[0].Content);
        Assert.AreEqual(2, reader.NativeReadCalls, "one read each: a malformed record costs its group no retry");
    }
}
