using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker re-reads the surviving name and parent of each file a watch batch reports a hard-link change for,
///     ships the answers with the batch, and the index applies them
///     (issue 1008). The host's
///     record read is the synthetic seam: it answers in the native reader's shape, a parent file reference followed
///     by the name's UTF-16 units.
/// </summary>
// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public sealed class BrokerHardLinkWatchTests
{
    const uint RootRecord = 5;
    const uint OldParentRecord = 20;
    const uint NewParentRecord = 21;
    const uint ChildRecord = 30;
    const uint OtherRecord = 31;
    const ushort ChildSequence = 6;
    const string ChildName = "AppIcon.scale-125.png";

    static readonly DateTime ChangeMoment = new(2026, 11, 18, 6, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Host_LooksUpEachHardLinkedFileOncePerBatch_AndShipsTheAnswersWithIt()
    {
        var lookups = new ConcurrentQueue<InlineContentRequest[]>();
        await using var harness = new ScriptedWatchBrokerHarness(seams: new ScriptedBrokerSeams
        {
            ReadFileNames = (_, requests, sink, _) =>
            {
                lookups.Enqueue(requests.ToArray());
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.Returned,
                        NativeName(NewParentRecord, 25, request.RecordNumber == ChildRecord ? ChildName : "other.png"));
                }
            }
        });
        var (reader, run) = await StartReadingAsync(harness);
        await using var _ = reader.ConfigureAwait(false);

        run.Push([
            Entry(OtherRecord, NewParentRecord, "other.png", UsnReason.HardLinkChange, sequence: 2),
            Entry(ChildRecord, NewParentRecord, ChildName, UsnReason.HardLinkChange),
            Entry(ChildRecord, OldParentRecord, ChildName, UsnReason.HardLinkChange | UsnReason.Close),
            Entry(40, RootRecord, "plain.txt", UsnReason.DataExtend)
        ], nextUsn: 110);
        var batch = await WatchReads.NextBatchAsync(reader);

        var requested = lookups.Single();
        CollectionAssert.AreEqual(new long[] { ChildRecord, OtherRecord }, requested.Select(request => request.RecordNumber).ToArray(),
            "one ascending request per hard-linked file, none for the other entries");
        CollectionAssert.AreEqual(new ushort[] { ChildSequence, 2 }, requested.Select(request => request.SequenceNumber).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            new FileNameResolution(ChildRecord, ChildSequence, FileNameLookupOutcome.Resolved, NewParentRecord, ChildName),
            new FileNameResolution(OtherRecord, 2, FileNameLookupOutcome.Resolved, NewParentRecord, "other.png")
        }, batch.FileNames.ToArray());
    }

    [TestMethod]
    public async Task Host_BatchWithoutHardLinkChanges_LooksNothingUp()
    {
        var lookups = 0;
        await using var harness = new ScriptedWatchBrokerHarness(seams: new ScriptedBrokerSeams
        {
            ReadFileNames = (_, _, _, _) => Interlocked.Increment(ref lookups)
        });
        var (reader, run) = await StartReadingAsync(harness);
        await using var _ = reader.ConfigureAwait(false);

        run.Push(40, "plain.txt", nextUsn: 110);
        var batch = await WatchReads.NextBatchAsync(reader);

        Assert.IsEmpty(batch.FileNames);
        Assert.AreEqual(0, Volatile.Read(ref lookups));
    }

    /// <summary>A lookup that throws, or answers only part of a batch, leaves the rest unreadable and the watch running.</summary>
    [TestMethod]
    [DataRow(true, DisplayName = "lookup throws")]
    [DataRow(false, DisplayName = "lookup answers one file")]
    public async Task Host_LookupThatFails_ShipsTheUnansweredFilesUnreadable(bool throws)
    {
        await using var harness = new ScriptedWatchBrokerHarness(seams: new ScriptedBrokerSeams
        {
            ReadFileNames = (_, requests, sink, _) =>
            {
                if (throws)
                {
                    throw new IOException("the volume went away");
                }

                sink(requests.Span[0].Position, InlineContentOutcome.Superseded, []);
            }
        });
        var (reader, run) = await StartReadingAsync(harness);
        await using var _ = reader.ConfigureAwait(false);

        run.Push([
            Entry(ChildRecord, OldParentRecord, ChildName, UsnReason.HardLinkChange),
            Entry(OtherRecord, OldParentRecord, "other.png", UsnReason.HardLinkChange)
        ], nextUsn: 110);
        var batch = await WatchReads.NextBatchAsync(reader);
        run.Push(40, "after.txt", nextUsn: 120);

        Assert.AreEqual(throws ? FileNameLookupOutcome.Unreadable : FileNameLookupOutcome.Superseded,
            batch.FileNames[0].Outcome);
        Assert.AreEqual(FileNameLookupOutcome.Unreadable, batch.FileNames[1].Outcome);
        Assert.AreEqual("after.txt", (await WatchReads.NextBatchAsync(reader)).Entries.Single().FileName,
            "the watch keeps running after a failed lookup");
    }

    /// <summary>
    ///     The issue's shape end to end: a scanned file is hard-linked into the new version's directory, its old link
    ///     is removed, the old directory is deleted and a file reuses its record. The index roots the file under the
    ///     parent the broker read back, not under the reused record.
    /// </summary>
    [TestMethod]
    public async Task LinkAddedInNewParentThenOldLinkRemoved_OverTheBroker_RootsTheRowUnderTheSurvivingParent()
    {
        using var roots = new TempDirectory("broker-hard-link-roots");
        using var cache = new TempDirectory("broker-hard-link-cache", create: false);
        await using var harness = new ScriptedWatchBrokerHarness(scanVolume: ScanWithChildUnderOldParent,
            seams: new ScriptedBrokerSeams
            {
                ReadFileNames = (_, requests, sink, _) =>
                {
                    foreach (var request in requests.Span)
                    {
                        sink(request.Position, InlineContentOutcome.Returned, NativeName(NewParentRecord, 25, ChildName));
                    }
                }
            });
        var token = harness.CancellationToken;
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource();
        var rootDirectory = Directory.CreateDirectory(Path.Combine(roots.Path, "T")).FullName;
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheDirectory = cache.Path,
            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource(source.Producer, source.WatchSource)
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), rootDirectory, 123u)), token);
        await index.StartWatchingAsync(token);
        var run = await harness.Watch('T').RunAsync(1);
        var reused = ChangeSignal.WhenApplied(index, "servicehub.config.json");

        run.Push([
            Entry(ChildRecord, NewParentRecord, ChildName, UsnReason.HardLinkChange),
            Entry(ChildRecord, NewParentRecord, ChildName, UsnReason.HardLinkChange | UsnReason.Close),
            Entry(ChildRecord, OldParentRecord, ChildName, UsnReason.HardLinkChange),
            Entry(ChildRecord, OldParentRecord, ChildName, UsnReason.HardLinkChange | UsnReason.Close),
            Entry(OldParentRecord, RootRecord, "OldVersion", UsnReason.FileDelete | UsnReason.Close, sequence: 3,
                FileAttributes.Directory),
            Entry(OldParentRecord, NewParentRecord, "servicehub.config.json", UsnReason.FileCreate | UsnReason.Close,
                sequence: 4)
        ], nextUsn: 200);
        await reused;

        var child = index.Search(new SearchQuery(ChildName, NameMatchMode.Exact), token).Single();
        Assert.AreEqual(Path.Combine(rootDirectory, "NewVersion", ChildName), child.Path);
        await index.StopWatchingAsync(token);
    }

    static IEnumerable<IReadOnlyList<MftRecord>> ScanWithChildUnderOldParent(VolumeKey volume,
        ParseThreadAllowance parseThreads, BlockWriteReporting reporting, MftScanOptions scanOptions,
        Action<long> recordsOmitted, CancellationToken cancellationToken)
    {
        const ushort inUseDirectory = 3;
        yield return
        [
            new MftRecord(RootRecord, RootRecord, new MftRecordFields(inUseDirectory, FileAttributes.Directory, sequenceNumber: 5), "."),
            new MftRecord(OldParentRecord, RootRecord,
                new MftRecordFields(inUseDirectory, FileAttributes.Directory, sequenceNumber: 3), "OldVersion"),
            new MftRecord(NewParentRecord, RootRecord,
                new MftRecordFields(inUseDirectory, FileAttributes.Directory, sequenceNumber: 25), "NewVersion"),
            new MftRecord(ChildRecord, OldParentRecord,
                new MftRecordFields(1, FileAttributes.Archive, size: 100, sequenceNumber: ChildSequence), ChildName)
        ];
    }

    static async Task<(IAsyncEnumerator<WatchStreamItem> Reader, ScriptedWatchRun Run)> StartReadingAsync(
        ScriptedWatchBrokerHarness harness)
    {
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateIndexSource().WatchSource!;
        var token = harness.CancellationToken;
        var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var reader = new DisposingReader(handle, handle.ReadAsync(token).GetAsyncEnumerator(token));
        var run = await harness.Watch('C').RunAsync(1);
        Assert.IsInstanceOfType<VolumeCaughtUp>(await WatchReads.NextAsync(reader));
        return (reader, run);
    }

    /// <summary>The native record reader's file name answer: the parent file reference, then the name's UTF-16 units.</summary>
    static byte[] NativeName(ulong parentRecord, ushort parentSequence, string name)
    {
        var answer = new byte[8 + (name.Length * 2)];
        BinaryPrimitives.WriteUInt64LittleEndian(answer, parentRecord | ((ulong)parentSequence << 48));
        Encoding.Unicode.GetBytes(name).CopyTo(answer, 8);
        return answer;
    }

    static UsnJournalEntry Entry(ulong recordNumber, ulong parentRecordNumber, string fileName, UsnReason reason,
        ushort sequence = ChildSequence, FileAttributes attributes = FileAttributes.Archive) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            SequenceNumber = sequence,
            Usn = 1,
            TimestampUtc = ChangeMoment,
            Reason = reason,
            FileAttributes = attributes,
            FileName = fileName
        });

    /// <summary>A watch reader that also disposes the watch handle it reads.</summary>
    sealed class DisposingReader(IIndexVolumeWatch handle, IAsyncEnumerator<WatchStreamItem> inner)
        : IAsyncEnumerator<WatchStreamItem>
    {
        public WatchStreamItem Current => inner.Current;

        public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await handle.DisposeAsync();
        }
    }
}
