using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The native inline-content reader where only process-global native state reaches: a volume image through the
///     record-size override, an injected read failure, an injected cancellation, and a counted native call. Isolated
///     like every class that touches the native hooks.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class InlineContentReaderStateTests : IDisposable
{
    readonly TempDirectory _directory = new("mftlib-inline-state");
    readonly CancellationTokenSource _cancellation = new();

    public void Dispose()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.Hooks.ResetToDefaults();
        _cancellation.Dispose();
        _directory.Dispose();
    }

    string WriteDump(int recordSize, int recordCount, Dictionary<int, DumpRecord> records) =>
        MftDumpFixture.WriteFile(_directory.Path, MftDumpFixture.Build(recordSize, recordCount, records));

    static Dictionary<int, DumpRecord> Files(int recordCount, int contentLength, int recordSize = 1024)
    {
        var records = new Dictionary<int, DumpRecord>
        {
            [0] = new DumpRecord("$MFT", 5),
            [5] = new DumpRecord(".", 5) { IsDirectory = true }
        };
        for (var number = 16; number < recordCount; number++)
        {
            records[number] = new DumpRecord("f.bin", 5)
            {
                Content = InlineContentDump.Pattern(number, Math.Min(contentLength, InlineContentDump.LargestContent(recordSize, "f.bin")))
            };
        }

        return records;
    }

    static InlineContentRequest[] Everything(int first, int recordCount) =>
        Enumerable.Range(first, recordCount - first)
            .Select(number => new InlineContentRequest(number - first, number, 1)).ToArray();

    [TestMethod]
    [DataRow(1024)]
    [DataRow(4096)]
    public void Read_ContentFillingTheBufferSeveralTimes_ResumesWithoutLosingOrRepeatingAnAnswer(int recordSize)
    {
        // About 9 MiB of content for a 4 MiB buffer: at least three native calls.
        var contentLength = recordSize == 1024 ? 700 : 3500;
        var recordCount = (9 * 1024 * 1024 / contentLength) + 64;
        var records = Files(recordCount, contentLength, recordSize);
        var path = WriteDump(recordSize, recordCount, records);
        var nativeCalls = 0;
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call,
            out result) =>
        {
            nativeCalls++;
            native(reader, ref call, out result);
        };
        var requests = Everything(16, recordCount);
        var answers = new InlineAnswer[requests.Length];
        var seen = new int[requests.Length];

        using (var input = MftDumpInput.Open(path))
        using (var reader = input.OpenInlineContentReader())
        {
            reader.Read(requests, (position, outcome, content) =>
            {
                seen[position]++;
                answers[position] = new InlineAnswer(outcome, content.ToArray());
            }, CancellationToken.None);
        }

        Assert.IsGreaterThanOrEqualTo(3, nativeCalls, "the buffer filled and the read resumed");
        Assert.IsTrue(seen.All(count => count == 1), "every position is answered exactly once");
        for (var index = 0; index < requests.Length; index++)
        {
            Assert.AreEqual(InlineContentOutcome.Returned, answers[index].Outcome);
            CollectionAssert.AreEqual(records[(int)requests[index].RecordNumber].Content, answers[index].Content,
                $"record {requests[index].RecordNumber}");
        }
    }

    [TestMethod]
    public void Read_NeighbouringRecords_AreOneReadAndDistantOnesAreSeparateReads()
    {
        var records = Files(4096, 64);
        var path = WriteDump(1024, 4096, records);

        var neighbours = InlineContentDump.Read(path, CancellationToken.None, (100, 1), (101, 1), (103, 1), (110, 1));
        var distant = InlineContentDump.Read(path, CancellationToken.None, (100, 1), (2100, 1), (4000, 1));

        Assert.AreEqual(1, neighbours.ReadCalls, "records within the gap share one read");
        Assert.AreEqual(11 * 1024, neighbours.BytesRead, "from the first wanted record to the end of the last");
        Assert.AreEqual(3, distant.ReadCalls, "records a megabyte apart are not bridged");
        Assert.AreEqual(3 * 1024, distant.BytesRead, "a sparse request reads only what it wants");
        foreach (var answer in neighbours.Answers.Concat(distant.Answers))
        {
            Assert.AreEqual(InlineContentOutcome.Returned, answer.Outcome);
        }
    }

    [TestMethod]
    public void Read_EachNativeCall_MakesOneGroupReadAndAnswersOnlyThatGroup()
    {
        var path = WriteDump(1024, 4096, Files(4096, 64));
        // 100-101 share a group; 228 and 356 are each their own; 4096 is past the end and needs no read.
        InlineContentRequest[] requests =
            [new(0, 100, 1), new(1, 101, 1), new(2, 228, 1), new(3, 356, 1), new(4, 4096, 1)];
        var calls = new List<(ulong Start, ulong Next, ulong Reads)>();
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            native(reader, ref call, out result);
            calls.Add((call.StartIndex, result.NextRequest, result.ReadCalls));
        };
        var outcomes = new InlineContentOutcome?[requests.Length];

        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        reader.Read(requests, (position, outcome, _) => outcomes[position] = outcome, CancellationToken.None);

        Assert.AreSequenceEqual(new (ulong, ulong, ulong)[] { (0, 2, 1), (2, 3, 1), (3, 4, 1), (4, 5, 0) },
            calls.Select(call => (call.Start, call.Next, call.Reads)),
            "a call returns after its one read; the record past the end is answered by a call that reads nothing");
        Assert.AreSequenceEqual(
            [InlineContentOutcome.Returned, InlineContentOutcome.Returned, InlineContentOutcome.Returned,
             InlineContentOutcome.Returned, InlineContentOutcome.Superseded],
            outcomes.Select(outcome => outcome!.Value));
    }

    [TestMethod]
    public void Read_ResumedCallWhoseRequestsWereReorderedAfterTheFirstCall_NeverGroupsADescendingRecord()
    {
        var records = Files(4096, 64);
        var path = WriteDump(1024, 4096, records);
        InlineContentRequest[] requests = [new(0, 100, 1), new(1, 2000, 1), new(2, 2001, 1)];
        var native = MFTLibNative.Hooks._readMftInlineContent;
        var reads = new List<ulong>();
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            if (call.StartIndex == 1)
            {
                // After the first call checked the order, the record after 2000 becomes 1999, one record before it.
                SetRecordNumber(call.Requests, 2, 1999);
            }

            native(reader, ref call, out result);
            reads.Add(result.ReadCalls);
        };
        var answers = new InlineAnswer[requests.Length];

        using (var input = MftDumpInput.Open(path))
        using (var reader = input.OpenInlineContentReader())
        {
            reader.Read(requests, (position, outcome, content) => answers[position] = new InlineAnswer(outcome, content.ToArray()),
                CancellationToken.None);
        }

        Assert.AreSequenceEqual(new ulong[] { 1, 1, 1 }, reads, "1999 is not grouped behind 2000, so it is read on its own");
        CollectionAssert.AreEqual(records[2000].Content, answers[1].Content);
        CollectionAssert.AreEqual(records[1999].Content, answers[2].Content, "the record actually requested, read in bounds");
    }

    [TestMethod]
    public void Read_EveryRecordOfALargeMft_IsReadInBoundedPieces()
    {
        const int recordCount = 20 * 1024;
        var path = WriteDump(1024, recordCount, Files(recordCount, 32));
        var wanted = Enumerable.Range(16, recordCount - 16).Select(number => ((long)number, (ushort)1)).ToArray();

        var read = InlineContentDump.Read(path, CancellationToken.None, wanted);

        Assert.IsGreaterThanOrEqualTo(3, read.ReadCalls, "20 MiB of MFT is not one read");
        Assert.AreEqual((recordCount - 16) * 1024L, read.BytesRead);
        Assert.IsTrue(read.Answers.All(answer => answer.Outcome == InlineContentOutcome.Returned));
    }

    [TestMethod]
    public void Read_RecordsInBothRunsOfAFragmentedMft_AreNeverReadAcrossTheHole()
    {
        var records = Files(64, 40);
        var mft = MftDumpFixture.Build(1024, 64, records);
        var path = Path.Combine(_directory.Path, "fragmented.img");
        FragmentedNtfsImage.Write(path, mft, firstRunRecords: 32, holeClusters: 3);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);

        InlineAnswer[] answers;
        long readCalls;
        using (var image = File.OpenHandle(path))
        using (var reader = NativeInlineContentReader.Open(image))
        {
            Assert.AreEqual(64UL, reader.TotalRecords);
            var requests = new[] { 30, 31, 32, 33, 63 }
                .Select((number, position) => new InlineContentRequest(position, number, 1)).ToArray();
            answers = new InlineAnswer[requests.Length];
            reader.Read(requests, (position, outcome, content) =>
                answers[position] = new InlineAnswer(outcome, content.ToArray()), CancellationToken.None);
            readCalls = reader.NativeReadCalls;
        }

        Assert.AreEqual(2, readCalls,
            "30-31 are in the first run; 32, 33 and 63 are in the second, though 31 and 32 are adjacent in the MFT");
        foreach (var (number, answer) in new[] { 30, 31, 32, 33, 63 }.Zip(answers))
        {
            Assert.AreEqual(InlineContentOutcome.Returned, answer.Outcome, $"record {number}");
            CollectionAssert.AreEqual(records[number].Content, answer.Content, $"record {number}");
        }
    }

    // Each native call reads one group, so a record in each of many runs is one call per run, and each call makes exactly
    // one run lookup. Finding the run must stay a search of the sorted runs, not a walk of every run before it.
    [TestMethod]
    public void Read_OneRecordInEachOfAHundredRuns_FindsEachRunInLogarithmicallyManyProbes()
    {
        const int runCount = 100;
        var records = Files(runCount * FragmentedNtfsImage.RecordsPerCluster, 40);
        var mft = MftDumpFixture.Build(1024, runCount * FragmentedNtfsImage.RecordsPerCluster, records);
        var path = Path.Combine(_directory.Path, "many-runs.img");
        FragmentedNtfsImage.WriteOneClusterRuns(path, mft);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        // Runs 4 to 99: the first file record is 16, at the start of run 4.
        var numbers = Enumerable.Range(4, runCount - 4).Select(run => (run * FragmentedNtfsImage.RecordsPerCluster) + 2)
            .ToArray();
        var requests = numbers.Select((number, position) => new InlineContentRequest(position, number, 1)).ToArray();
        var answers = new InlineAnswer[requests.Length];

        var calls = ReadCountingProbes(path, requests, answers);

        // A binary search of 100 runs examines at most ceil(log2(101)) = 7. A walk from the first run on every call
        // examines k + 1 runs for the record in run k.
        Assert.HasCount(requests.Length, calls, "one call per run");
        foreach (var call in calls)
        {
            Assert.AreEqual((1UL, 1UL), (call.Next - call.Start, call.Reads), $"call at {call.Start}: one lookup, one read");
            Assert.IsInRange(1UL, 7UL, call.Probes, $"call at {call.Start}: runs examined by its one lookup");
        }

        for (var index = 0; index < numbers.Length; index++)
        {
            Assert.AreEqual(InlineContentOutcome.Returned, answers[index].Outcome, $"record {numbers[index]}");
            CollectionAssert.AreEqual(records[numbers[index]].Content, answers[index].Content, $"record {numbers[index]}");
        }
    }

    // 8 KiB records over 4 KiB clusters in runs of 3, 1 and 2 clusters: record 1 straddles the first two runs and cannot
    // be read as one piece, and record 2 fills the last run. A call meets the straddling record, answers it without I/O,
    // and goes on to its one group read, so it makes one lookup per distinct record it meets: a search of three runs
    // examines exactly two, and repeated requests for the record just found straddling are answered without a search.
    [TestMethod]
    public void Read_StraddlingRecordRequestedThriceThenAReadableRun_OneCallWithOneLookupPerDistinctRecord()
    {
        const int recordSize = 8192;
        var records = new Dictionary<int, DumpRecord>
        {
            [0] = new DumpRecord("$MFT", 5),
            [2] = new DumpRecord("f.bin", 5) { Content = [0x41, 0x42, 0x43] }
        };
        var path = Path.Combine(_directory.Path, "straddling.img");
        FragmentedNtfsImage.WriteRuns(path, MftDumpFixture.Build(recordSize, 3, records), [(3, 0), (1, 1), (2, 1)],
            recordSize);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(recordSize);
        InlineContentRequest[] requests = [new(0, 1, 1), new(1, 1, 1), new(2, 1, 1), new(3, 2, 1)];
        var answers = new InlineAnswer[requests.Length];

        var calls = ReadCountingProbes(path, requests, answers);

        Assert.AreSequenceEqual(new (ulong, ulong, ulong, ulong)[] { (0, 4, 1, 4) },
            calls.Select(call => (call.Start, call.Next, call.Reads, call.Probes)),
            "one call: two lookups of two probes each (record 1 once, record 2), then the one read");
        Assert.AreSequenceEqual(
            [InlineContentOutcome.Unreadable, InlineContentOutcome.Unreadable, InlineContentOutcome.Unreadable,
             InlineContentOutcome.Returned],
            answers.Select(answer => answer.Outcome));
        CollectionAssert.AreEqual(records[2].Content, answers[3].Content);
    }

    [TestMethod]
    public void Read_WithoutArmedProbeCounting_PublishesNoProbes()
    {
        var path = WriteDump(1024, 4096, Files(4096, 32));

        var answers = InlineContentDump.Read(path, (100, 1), (2100, 1), (4000, 1));

        Assert.IsTrue(answers.All(answer => answer.Outcome == InlineContentOutcome.Returned), "every lookup ran");
        Assert.AreEqual(0UL, NativeTestHooks.NativeGetInlineSegmentProbes(),
            "an unarmed lookup counts only in a local, so production reads write no shared counter");
    }

    // Reads the requests from the image and returns, per native call, where it started, where it stopped, its reads and
    // the run probes the lookups made inside it.
    static List<(ulong Start, ulong Next, ulong Reads, ulong Probes)> ReadCountingProbes(string path,
        InlineContentRequest[] requests, InlineAnswer[] answers)
    {
        var calls = new List<(ulong Start, ulong Next, ulong Reads, ulong Probes)>();
        NativeTestHooks.NativeSetInlineSegmentProbeCounting(1);
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            var before = NativeTestHooks.NativeGetInlineSegmentProbes();
            native(reader, ref call, out result);
            calls.Add((call.StartIndex, result.NextRequest, result.ReadCalls,
                NativeTestHooks.NativeGetInlineSegmentProbes() - before));
        };
        using var image = File.OpenHandle(path);
        using var inlineReader = NativeInlineContentReader.Open(image);
        inlineReader.Read(requests, (position, outcome, content) =>
            answers[position] = new InlineAnswer(outcome, content.ToArray()), CancellationToken.None);
        return calls;
    }

    [TestMethod]
    public void Read_RecordPastTheEndOfAFragmentedMft_IsSuperseded()
    {
        var mft = MftDumpFixture.Build(1024, 64, Files(64, 40));
        var path = Path.Combine(_directory.Path, "fragmented.img");
        FragmentedNtfsImage.Write(path, mft, firstRunRecords: 32, holeClusters: 3);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);

        using var image = File.OpenHandle(path);
        using var reader = NativeInlineContentReader.Open(image);
        InlineContentOutcome? outcome = null;
        reader.Read([new InlineContentRequest(0, 64, 1)], (_, found, _) => outcome = found, CancellationToken.None);

        Assert.AreEqual(InlineContentOutcome.Superseded, outcome);
        Assert.AreEqual(0, reader.NativeReadCalls, "a record past the end costs no read");
    }

    [TestMethod]
    public void Open_HandleThatIsNotAnNtfsVolume_FailsWithTheNativeMessage()
    {
        var path = Path.Combine(_directory.Path, "not-ntfs.img");
        File.WriteAllBytes(path, new byte[8192]);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        using var image = File.OpenHandle(path);

        var failure = Assert.ThrowsExactly<IOException>(() => NativeInlineContentReader.Open(image));

        Assert.AreEqual("Volume is not NTFS", failure.Message);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void Read_AReadThatFails_MakesOnlyItsOwnRecordsUnreadable(int failingRead)
    {
        var records = Files(4096, 32);
        var path = WriteDump(1024, 4096, records);
        // Records far apart are separate reads, so the Nth read is the Nth record.
        NativeTestHooks.NativeSetReadFailCountdown(failingRead);

        var answers = InlineContentDump.Read(path, (100, 1), (2100, 1), (4000, 1));

        for (var index = 0; index < answers.Length; index++)
        {
            Assert.AreEqual(index == failingRead - 1 ? InlineContentOutcome.Unreadable : InlineContentOutcome.Returned,
                answers[index].Outcome, $"answer {index}");
        }
    }

    [TestMethod]
    public void Read_CancelledBeforeItStarts_ThrowsWithoutAnswering()
    {
        var path = WriteDump(1024, 64, Files(64, 32));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => InlineContentDump.Read(path, cancellation.Token, (20, 1), (21, 1)));
    }

    [TestMethod]
    public void Read_CancelledInsideTheNativeRead_ThrowsOperationCanceled()
    {
        var path = WriteDump(1024, 4096, Files(4096, 32));
        // Records a megabyte apart are separate reads; the second cancellation check reports cancelled.
        NativeTestHooks.NativeSetCancelCheckCountdown(2);

        Assert.ThrowsExactly<OperationCanceledException>(
            () => InlineContentDump.Read(path, CancellationToken.None, (100, 1), (2100, 1), (4000, 1)));
    }

    [TestMethod]
    public void Read_RequestsOutOfOrder_AreRefusedByTheNativeCall()
    {
        var path = WriteDump(1024, 64, Files(64, 32));
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        var outOfOrder = new[] { new InlineContentRequest(0, 21, 1), new InlineContentRequest(1, 20, 1) };

        var failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => reader.Read(outOfOrder, (_, _, _) => { }, CancellationToken.None));

        Assert.AreEqual("The requests are not in ascending record order", failure.Message);
    }

    [TestMethod]
    public void Read_ExpectedSequenceNumberWiderThanSixteenBits_IsSuperseded()
    {
        var path = WriteDump(1024, 64, Files(64, 32));
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            SetExpectedSequenceNumber(call.Requests, 0x10001);
            native(reader, ref call, out result);
        };

        var answer = InlineContentDump.Read(path, (20, 1))[0];

        Assert.AreEqual(InlineContentOutcome.Superseded, answer.Outcome,
            "the record's sequence number is 1, and 0x10001 is not 1");
    }

    [TestMethod]
    public void Read_ContentThatFillsTheBuffer_LeavesTheUnansweredRequestNotReturned()
    {
        const int recordCount = 16 + 40;
        var path = WriteDump(4096, recordCount, Files(recordCount, 3500, 4096));
        var native = MFTLibNative.Hooks._readMftInlineContent;
        var unanswered = new List<uint>();
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            call.BufferLength = MFTLibNative.MinimumInlineContentBufferBytes;
            native(reader, ref call, out result);
            if (result.NextRequest < call.RequestCount)
            {
                unanswered.Add(OutcomeAt(call.Requests, result.NextRequest));
            }
        };
        var requests = Everything(16, recordCount);
        var delivered = 0;

        using (var input = MftDumpInput.Open(path))
        using (var reader = input.OpenInlineContentReader())
        {
            reader.Read(requests, (_, outcome, _) =>
            {
                delivered++;
                Assert.AreEqual(InlineContentOutcome.Returned, outcome);
            }, CancellationToken.None);
        }

        Assert.AreEqual(requests.Length, delivered);
        Assert.IsNotEmpty(unanswered, "the 64 KiB buffer filled before the last request");
        Assert.IsTrue(unanswered.All(outcome => outcome != (uint)InlineContentOutcome.Returned),
            "a request the call did not answer must not read as a returned answer with no content");
    }

    [TestMethod]
    [DataRow("offset-overflow", "lies outside its buffer")]
    [DataRow("length-past-buffer", "lies outside its buffer")]
    [DataRow("unknown-outcome", "returned outcome 99")]
    [DataRow("no-progress", "no consistent progress")]
    [DataRow("progress-past-count", "no consistent progress")]
    [DataRow("bytes-past-buffer", "no consistent progress")]
    public void Read_NativeAnswerThatIsInconsistent_IsRefused(string tamper, string expectedMessage)
    {
        var path = WriteDump(1024, 64, Files(64, 32));
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            native(reader, ref call, out result);
            Tamper(tamper, call.Requests, ref result);
        };

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => InlineContentDump.Read(path, (20, 1)));

        Assert.Contains(expectedMessage, failure.Message);
    }

    [TestMethod]
    public void Read_NativeResultThatSaysCancelled_ThrowsOperationCanceled()
    {
        var path = WriteDump(1024, 64, Files(64, 32));
        var native = MFTLibNative.Hooks._readMftInlineContent;
        MFTLibNative.Hooks._readMftInlineContent = (reader, ref call, out result) =>
        {
            native(reader, ref call, out result);
            result.Cancelled = 1;
        };

        Assert.ThrowsExactly<OperationCanceledException>(() => InlineContentDump.Read(path, (20, 1)));
    }

    [TestMethod]
    public void Read_CancelledWhileAnswersAreDelivered_StopsWithinTheCheckInterval()
    {
        const int recordCount = 16 + 5000;
        var path = WriteDump(1024, recordCount, Files(recordCount, 8));
        var delivered = 0;

        Assert.ThrowsExactly<OperationCanceledException>(() => ReadCancellingOnTheFirstAnswer(path, recordCount, () => delivered++));

        Assert.AreEqual(4096, delivered, "one native call answered all 5000; the token is looked at every 4096 answers");
    }

    // Reads every file of the dump and cancels the token from inside the handler of the first answer.
    void ReadCancellingOnTheFirstAnswer(string path, int recordCount, Action onAnswer)
    {
        using var input = MftDumpInput.Open(path);
        using var reader = input.OpenInlineContentReader();
        reader.Read(Everything(16, recordCount), (_, _, _) =>
        {
            onAnswer();
            _cancellation.Cancel();
        }, _cancellation.Token);
    }

    static unsafe void SetExpectedSequenceNumber(IntPtr requests, uint sequenceNumber) =>
        ((MftInlineRequest*)requests)[0].ExpectedSequenceNumber = sequenceNumber;

    static unsafe void SetRecordNumber(IntPtr requests, int index, ulong recordNumber) =>
        ((MftInlineRequest*)requests)[index].RecordNumber = recordNumber;

    static unsafe uint OutcomeAt(IntPtr requests, ulong index) => ((MftInlineRequest*)requests)[index].Outcome;

    // Corrupts the answer of the one-request read the way a damaged native result would.
    static unsafe void Tamper(string kind, IntPtr requests, ref MftInlineReadResult result)
    {
        var request = (MftInlineRequest*)requests;
        switch (kind)
        {
            case "offset-overflow":
                request->ContentOffset = ulong.MaxValue - 3;
                request->ContentLength = 8;
                break;
            case "length-past-buffer":
                request->ContentOffset = 0;
                request->ContentLength = uint.MaxValue;
                break;
            case "unknown-outcome":
                request->Outcome = 99;
                break;
            case "no-progress":
                result.NextRequest = 0;
                break;
            case "progress-past-count":
                result.NextRequest = 2;
                break;
            case "bytes-past-buffer":
                result.BytesUsed = ulong.MaxValue;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
