# Testing your integration

Reference **`MFTLib.TestExtensions`** from consumer test projects. It provides an
in-process broker harness, a scripted drive watch, synthetic journal entries and
records, synthetic cache blocks and their editor, and opt-in guards against accidental access to the real per-user cache or
a real volume's USN journal.

## BrokerTestHarness

`BrokerTestHarness.StartInProcess(ScriptedBrokerVolumes)` runs a real `JournalBrokerHost` on a
background task and returns an `InProcessBrokerHandle` whose `Process` is a real
`BrokerProcess` connected through in-memory control and drive pipes. It needs no
elevation and launches no child process. Consumer tests therefore exercise the same
request routing, per-drive channels, frame decoding, timeouts, producer, and watch source
used in production. The harness supplies everything except the volumes: the client creates
real block sections, and each scan's records are written through the production row writer
and filter.

`BrokerTestHarness.CreateSession(launchAsync)` returns a `BrokerSession` whose launch callback hands it
the `BrokerProcess` of an in-process broker (for example `StartInProcess(...).Process`), so a test of
code that takes a `BrokerSession` needs no elevation either.

`ScriptedBrokerVolumes` is the fake volumes the host serves. Only `QueryJournalCursor` is
required; a null source refuses that operation as the real host does.

- `QueryJournalCursor` arms a drive's journal cursor before its scan and bounds a watch's
  backlog.
- `ScanDrive` receives a `ScriptedScan` (the drive letter, the scan's parse-thread allowance
  and cancellation token) and returns the scan's records in batches. `ScriptedScan.ReportParsed`
  emits one parsing-phase progress frame, which the client reports as
  `IndexScanPhase.ParsingMft`. A batch sequence that yields lazily is written as it is
  enumerated, so a test can pause after the first batch, or call `InProcessBrokerHandle.Crash()`
  from inside the scan. Null refuses every scan with the channel error "Broker has no scan
  source".
- `ReadJournal` answers the bounded catch-up read after a scan. Null answers "nothing new"
  from every cursor.
- `WatchDrive` streams a drive's journal for a watch. Null refuses every watch.
- `QueryVolume` answers volume sizing queries. Null answers a small fixed volume (256 KiB of
  1024-byte records).
- `GrowUsnJournal` grows a drive's journal. Null refuses every grow request.

```csharp
await using var broker = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
{
    QueryJournalCursor = _ => new UsnJournalCursor(7, 1000),
    ScanDrive = scan =>
    {
        scan.ReportParsed(1, 1);
        return [[SyntheticMftRecord.Create(new SyntheticMftRecordOptions
        {
            RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true
        })]];
    }
});
```

`InProcessBrokerHandle.Scans` lists every scan the host served, in order, as an
`InProcessBrokerScan` (the drive, the scan profile and the keep-file-names list the client
requested), so a test asserts what it asked for. The harness does not offer the section
lifetime or the write path: those are MFTLib's own, and `MFTLib.Tests` covers them. A consumer
asserts the outcome it owns, such as the block file under its cache directory.

The handle owns the harness session from the client side. Disposing the handle
disposes the process: it closes the control pipe, ends the host, closes the drive pipes, waits for
the session task, and then releases the block sections the scans wrote into. Disposal is
safe to repeat. The harness has no separate fault event or stored host
exception. A host failure reaches the test through the production surfaces:

- the `BrokerProcess.Ended` task;
- `BrokerChannelLostException` on pending control or drive operations; or
- a host `Error` frame translated by the operation reading that channel.

Host exception detail is written only to broker diagnostics. Disposing the
process does not throw the host fault again.

`InProcessBrokerHandle.Crash()` simulates the broker process dying: it closes every
host pipe end at once, without the client's cooperation, so the client reads EOF on
the control pipe and every drive channel, `Ended` reports the loss, and pending and
later requests fail with `BrokerChannelLostException`. It differs from disposing the
process, which ends with the reason "The broker process was disposed.". The handle
still disposes normally after a crash.

A test that enables `BrokerDiagnostics` awaits `BrokerDiagnosticsIsolation.FlushAsync` to
read the log file deterministically, writes its own marker line with
`BrokerDiagnosticsIsolation.Log`, and calls `BrokerDiagnosticsIsolation.Reset()` in
cleanup to restore the default diagnostics state.

The client clock, per-pipe connection failures, held host writes, the host clock and the
processor count are not exposed: no consumer drives them. They stay internal seams that
`MFTLib.Tests` reaches through the same assembly, together with the section recorders that
back `Scans` (`InProcessBroker`, `TestBlockSections`, `RecordingBlockSectionWriter`), which
live in `MFTLibTestExtensions` as internal types.

## Script a drive watch

A test focused on `FileIndex` policy does not need a broker. `ScriptedWatchSource`
answers every watch start the index asks for with one `ScriptedDriveWatch` for
that drive, and the test then scripts what the watch yields and how it ends. Hand
it to the index beside a producer with
`FileIndexOptions.MftSource = SyntheticIndexSource.Create(producer, source)`.

```csharp
using MFTLib;
using MFTLibTestExtensions;

var source = new ScriptedWatchSource();
// ... open the index with the source, then await index.StartWatchingAsync ...
var watch = source.WatchFor('T');

await watch.PublishBatchAsync(
    [SyntheticJournalEntry.Create(new SyntheticJournalEntryOptions
    {
        RecordNumber = 20,
        ParentRecordNumber = 5,
        Usn = 1200,
        FileName = "new.txt",
        Reason = UsnReason.FileCreate | UsnReason.Close
    })],
    new UsnJournalCursor(watch.StartCursor.JournalId, 1300));
await watch.PublishCaughtUpAsync();
```

The source records every start in `Starts` (the drive and the cursor it resumed
from, including starts that were then failed) and every watch it handed out in
`Watches`; `WatchFor` returns the latest watch of one drive and
`WaitForStartAsync(drive, startNumber, token)` completes with the drive's
`startNumber`th watch (one for the first). A watch handed out before the call satisfies it
at once, so a test may trigger the start and wait afterwards, or wait first; neither misses it.
A drive the index restarts gets a new watch each time: the first start is number one, the
restart number two. A start that failed or is held has handed out no watch and is not
counted. A test that needs both drives started awaits one call per drive. Starts are
scripted per source:

- `CatchUpOnStart` queues the caught-up marker the moment each watch starts, for a
  test that only needs every drive to settle;
- `FailNextStart` and `FailNextStartFor` make one start throw;
- `StartFailure` is consulted on every start and throws whatever it returns; and
- a start for a drive whose previous watch has not been disposed throws
  `InvalidOperationException`, because the index disposes a watch before it
  restarts one.

A watch scripts the read the index's pump performs:

- `PublishBatchAsync` and `QueueBatchAsync` deliver a journal batch that applies its
  changes and advances the block cursor; the publishing form completes once the
  pump has taken the item after it and throws `TimeoutException` after ten
  seconds, and the queueing form returns the task that completes then;
- `PublishCaughtUpAsync` and `QueueCaughtUp` settle that drive's catch-up wait;
- `FailDrive` produces `WatchFaultKind.Drive` and automatic recovery;
- `LoseChannel`, or `End` for a normal end before the index cancels the read,
  produces `WatchFaultKind.Channel` with no automatic recovery; and
- `FailOnCancellation` makes a cancelled read throw an I/O failure instead of
  `OperationCanceledException`.

`ReadStarted`, `ReadEnded` (true when cancellation ended the read), `Disposed`
and `DisposeCount` report what the index did with the watch. A watch closes when its
read ends, however it ends, or when it is disposed: every delivery method then throws
`InvalidOperationException`, and each unread item's task settles once, faulted with
the read's failure or cancelled, before `ReadEnded` completes. The index disposes a
watch exactly once, so a second disposal throws. An idle watch is a source nobody
publishes to. Keep one source per index and one watch per drive: sharing a queue
between watches would reintroduce cross-drive ordering and failure coupling that
the public seam is designed to exclude.

## Seed and edit cache blocks

A test that needs a cache block on disk does not write one by hand. `SyntheticBlock`
writes, edits and reads blocks through the production block writer, so a test never
sees the block format. Rows are `SyntheticRow` values: row number, name, parent row,
and optional `IsDirectory`, `IsTombstone` (a deleted record whose name is kept),
`IsFree` (a slot holding no record, so scans and counts skip it and `ReadRows` omits it),
`Attributes`, `Size` (null writes the size-unknown flag), `ModifiedUtc` and `SequenceNumber`. Capacity is planned from the
rows, with headroom for later edits.

```csharp
var cacheDirectory = Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}");
var path = SyntheticBlock.WriteCached(cacheDirectory, 'C', volumeSerial: 0x1234,
    new SyntheticBlockOptions
    {
        JournalCursor = new UsnJournalCursor(7, 4096),
        CompletedUtc = completed,
        CacheTag = new CacheTag("TEST", 1)
    },
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true },
        new SyntheticRow(6, "notes.txt", 5) { Size = 12 }
    ]);

SyntheticBlock.Edit(path, 0x1234, editor =>
{
    editor.WriteRow(editor.ReadRow(6) with { Size = null });
    editor.MarkCompactionNeeded();
});
```

- `CachedPath` is the canonical file path for a drive, so a test never builds the
  file name itself.
- `WriteCached` writes a complete block into the drive's cache slot. Set
  `ProducerKind = ProducerKind.Enumeration` and `RootRow = 0` for an enumeration block.
- `Edit` opens an existing block and hands a `SyntheticBlockEditor` to the callback,
  then flushes and closes it. The editor reads and writes rows, sets attributes,
  marks a tombstone, a size unknown (`MarkSizeUnknown` for one row, `MarkSizesUnknown`
  for every row a filter selects, returning how many) or compaction needed, replaces the
  journal cursor or scan timestamp (`ReadHeader(...).CompletedUtc` is the stored one,
  so an edit can write it back), `SetProducerKind(ProducerKind.Mft)` turns a block
  written by an enumeration scan into one an index warm-starts without a producer and
  whose rows accept `FileIndexTestAccess.ApplyJournalEntries`.
  `SetCacheTag(new CacheTag("TEST", 7))` replaces only the stored cache identity,
  preserving the scan timestamp, journal cursor, other header fields and every row;
  passing `default` stores the all-zero identity. A cache-only `FileIndex` opened
  with a different requested tag reports `DriveFailureKind.CacheTagMismatch`.
  `CorruptNamePool` makes the next open reject the block with
  `BlockValidationResult.InvalidNameDescriptor`. Using the editor after the edit
  returns throws `InvalidOperationException`.
- `ReadRows` returns every row in use, tombstones included, in row order.
- `MaximumPathDepth` is the deepest path the index resolves.

Every operation holds the cache slot's owner lock for its duration and throws
`InvalidOperationException` when an open index owns the slot, so dispose the index
before seeding, editing or reading its block. A missing block or one that fails
validation throws the same exception naming the problem.

`SyntheticMftProducer` is the block producer of a test index. It writes the rows a
callback returns for each drive through the same writer and reports a real
`MftBlockProduceResult` carrying the request's cache tag. Its settings model what a
test needs from a scan:

- `JournalCursor` and `CompletedUtc` are stamped into every block and
  `SkippedRecordCount` is reported on the produce result;
- `BeforeProduceAsync` is awaited before each production, so a test holds a scan or
  rescan in progress and releases it when ready;
- `CatchUpLoss` returns the proven catch-up loss a production reports, which the
  index surfaces as `DriveStatus.CheckpointLoss`;
- `ProducedDrives` lists every drive whose production started, in order, including
  repeats, so a test counts scans and rescans.

The callback must return the root row (row 5 for an MFT block).

## Isolate cache and journal state

Consumer test assemblies can activate both guards from a module initializer:

```csharp
using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

static class TestIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
        JournalIsolation.ForbidLiveJournalReads();
    }
}
```

`CacheDirectoryIsolation.ForbidDefaultCacheDirectory` is process-wide,
idempotent, and one-way. After activation, resolving the default cache directory
throws `InvalidOperationException`. Every test opening an index must set
`FileIndexOptions.CacheDirectory` to a temporary directory it owns, including
`NoCache` and empty-drive tests.

`JournalIsolation.ForbidLiveJournalReads` is also process-wide, idempotent, and
one-way. It makes live journal observations answer "cannot say" instead of
reading a volume. It does not throw and does not affect broker source delegates
supplied by the test.

Use `JournalIsolation.OverrideJournalWindow` when a test needs a retained,
trimmed, or recreated journal window. The callback receives a drive letter and
returns `SyntheticJournalWindow?`; null means "cannot say" and never falls back
to a live read. The returned scope is process-global, so mark the whole fixture
nonparallel, keep the scope through all awaited work, and stop and dispose every
index and watch before disposing it. Nested or overlapping public scopes throw
`InvalidOperationException`. Mutable callback state must be synchronized because
different drive pumps can query it concurrently.
