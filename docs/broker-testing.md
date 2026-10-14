# Testing your integration

Reference **`MFTLib.TestExtensions`** from consumer test projects. It provides an
in-process broker harness, a scripted drive watch, synthetic journal entries and
records, synthetic cache blocks and their editor, and an opt-in guard against the index's automatic
checkpoint observations reading a real volume's USN journal. Tests supply application-owned
temporary cache directories.

## Synthetic events and operation results

`WatchStateChange` and `DriveOperationResult` are library-constructed records.
Consumer tests that feed event handlers or result presenters use
`SyntheticNotifications` from the test package:

```csharp
using MFTLib.Index;
using MFTLibTestExtensions;

var watch = SyntheticNotifications.CreateDriveStatus('T', DriveState.Offline, null, null).Watch
    with { State = WatchState.CatchingUp, StateVersion = 1 };
var change = SyntheticNotifications.CreateWatchStateChange('T', watch, null);
var failure = new System.IO.IOException("scan failed");
var result = SyntheticNotifications.CreateDriveOperationResult('T', DriveOperationOutcome.Failed, failure);
```

The factories retain the supplied watch, fault, and exception references and
forward to the library constructors, including their drive-key validation.
Both nullable trailing arguments are explicit: pass null when there is no
fault or failure. Read the records' properties instead of positional
deconstruction.

Production applications receive events from `FileIndex.WatchStateChanged` and
results from index operations. Seed watch history directly from `DriveStatus.Watch`
using the shared version check in [broker integration](broker-integration.md);
an application-owned refusal stays in the application's own error path.

## BrokerTestHarness

`BrokerTestHarness.StartInProcess(ScriptedBrokerVolumes)` runs the real broker host on a
background task and returns an `InProcessBrokerHandle` exposing `Ended`, `Crash()`,
`Scans` and `DisposeAsync`, connected through in-memory control and drive pipes. It needs no
elevation and launches no child process. Consumer tests therefore exercise the same
request routing, per-drive channels, frame decoding, timeouts, producer, and watch source
used in production. The harness supplies everything except the volumes: the client creates
real block sections, and each scan's records are written through the production row writer
and filter.

`BrokerTestHarness.CreateSession(launchAsync)` returns a `BrokerSession` whose launch callback hands it
an `InProcessBrokerHandle`, so a test of code that takes a `BrokerSession` needs no
elevation either. The session owns only the handle's process: the test retains every
launched handle and disposes it after its indexes and the session. The callback is `Func<CancellationToken, Task<InProcessBrokerHandle>>`.

`ScriptedBrokerVolumes` is the fake volumes the host serves. Only `QueryJournalCursor` is
required; a null source refuses that operation as the real host does.

- `QueryJournalCursor` arms a drive's journal cursor before its scan and bounds a watch's
  backlog.
- `ScanDrive` receives a `ScriptedScan` (the drive letter and cancellation token)
  and returns the scan's records in batches. `ScriptedScan.ReportParsed`
  emits one parsing-phase progress frame, which the client reports as
  `IndexScanPhase.ParsingMft`. A batch sequence that yields lazily is written as it is
  enumerated, so a test can pause after the first batch, or call `InProcessBrokerHandle.Crash()`
  from inside the scan. Null refuses every scan with the channel error "Broker has no scan
  source".
- `ReadJournal` answers the bounded catch-up read after a scan. Null answers "nothing new"
  from every cursor.
- `WatchDrive` streams a drive's journal for a watch. Null refuses every watch.
- Volume sizing is supplied by the harness (256 KiB of 1024-byte records).
- `GrowUsnJournal` grows a drive's journal. Null refuses every grow request.

```csharp
await using var broker = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
{
    QueryJournalCursor = _ => new SyntheticJournalCursor(7, 1000),
    ScanDrive = scan =>
    {
        scan.ReportParsed(1, 1);
        return [[new SyntheticScanRecord
        {
            RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true
        }]];
    }
});
```

`InProcessBrokerHandle.Scans` lists every scan the host served, in order, as an
`InProcessBrokerScan` (the drive and nullable `RetainedFileNames` the client
requested), so a test asserts what it asked for. Null means full retention, an empty
collection means directories only, and names mean directories plus matching files.
The observation preserves null versus empty. The harness does not offer the section
lifetime or the write path: those are MFTLib's own, and `MFTLib.Tests` covers them. A consumer
asserts the outcome it owns, such as the block file under its cache directory.

The handle owns the harness session from the client side. Disposing the handle
disposes the process: it closes the control pipe, ends the host, closes the drive pipes, waits for
the session task, and then releases the block sections the scans wrote into. Disposal is
safe to repeat. The harness has no separate fault event or stored host
exception. A host failure reaches the test through the production surfaces:

- the `InProcessBrokerHandle.Ended` task;
- an `IOException` on pending control or drive operations; or
- a host `Error` frame translated by the operation reading that channel.

Host exception detail is written only to broker diagnostics. Disposing the
process does not throw the host fault again.

`InProcessBrokerHandle.Crash()` simulates the broker process dying: it closes every
host pipe end at once, without the client's cooperation, so the client reads EOF on
the control pipe and every drive channel, `Ended` reports the loss, and pending
operations fail with an `IOException`; a later session use fails with
`InvalidOperationException` naming the end reason. It differs from disposing the
process, which ends with the reason "The broker process was disposed.". The handle
still disposes normally after a crash.

A test creates an owned temporary log directory and calls
`BrokerDiagnostics.Enable("client", temporaryDirectory)` and reads
`broker-diagnostics.client.log`; the broker writes `broker-diagnostics.broker.log`.
Roles contain only nonempty ASCII letters, digits, dash and underscore; invalid roles
throw `ArgumentException` before changing diagnostics state. Mark the fixture nonparallel
because diagnostics configuration and environment switches are process-global.
It awaits `BrokerDiagnosticsIsolation.FlushAsync` to
read the log file deterministically, writes its own marker line with
`BrokerDiagnosticsIsolation.Log`, and calls `BrokerDiagnosticsIsolation.Reset()` in
cleanup to restore the default role, OS temp directory, client log path, include-self
setting and environment-controlled enablement. Reset does not clear environment
variables; tests restore any switches they changed.

Child-dispatch tests reject missing or valueless `--diag-log`, relative or nonnormalized
paths, directory-only arguments (including an existing directory named `broker-diagnostics.ui.log`),
names other than `broker-diagnostics.<role>.log` or invalid role segments, `.` or `..`
components, invalid characters, and reserved device directory names in the ordinary Windows namespace. Each rejection
handles broker mode, skips the runner, and exits with code 1. Client-produced paths in
owned directories containing spaces, including Windows extended-length paths, remain valid.
Extended-length cases create literal `NUL`, `CON`, `COM1`, `NUL.txt` and `LPT` followed by
superscript one directories, verify both client and broker log writes, and clean up through
their extended-length paths. These cases run only on Windows.

The client clock, per-pipe connection failures, held host writes, the host clock and the
processor count are not exposed: no consumer drives them. They stay internal seams that
`MFTLib.Tests` reaches through the same assembly, together with the section recorders that
back `Scans`, which live in `MFTLibTestExtensions` as internal types.

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
    [new SyntheticJournalRecord
    {
        RecordNumber = 20,
        ParentRecordNumber = 5,
        UpdateSequenceNumber = 1200,
        FileName = "new.txt",
        Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close
    }],
    new SyntheticJournalCursor(
        source.Starts.Last(start => start.Drive == watch.Drive).Cursor.JournalIdentifier,
        1300));
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
- `StartFailure` is consulted on every start and throws whatever it returns; and
- a start for a drive whose previous watch has not been disposed throws
  `InvalidOperationException`, because the index disposes a watch before it
  restarts one.

A watch scripts the read the index's pump performs:

- `PublishBatchAsync` and `QueueBatchAsync` deliver a journal batch that applies its
  changes and advances the block cursor; the publishing form completes once the
  pump has taken the item after it and throws `TimeoutException` after ten
  seconds, and the queueing form returns the task that completes then;
- `PublishCaughtUpAsync` settles that drive's catch-up wait;
- `FailDrive` produces `WatchFaultKind.Drive` and automatic recovery;
- `FailApply` queues a batch whose application throws, producing `WatchFaultKind.Apply` and automatic recovery;
- `LoseChannel`, or `End` for a normal end before the index cancels the read,
  produces `WatchFaultKind.Channel` with no automatic recovery.

`ReadEnded` (true when cancellation ended the read) reports how the read ended. A watch closes when its
read ends, however it ends, or when it is disposed: every delivery method then throws
`InvalidOperationException`, and each unread item's task settles once, faulted with
the read's failure or cancelled, before `ReadEnded` completes. The index disposes a
watch exactly once, so a second disposal throws. An idle watch is a source nobody
publishes to. Keep one source per index and one watch per drive: sharing a queue
between watches would reintroduce cross-drive ordering and failure coupling that
the public seam is designed to exclude.

## Seed and edit cache blocks

A test that needs a cache block on disk does not write one by hand. `SyntheticBlock`
writes and edits blocks through the production block writer, so a test never
sees the block format. Rows are `SyntheticRow` values: row number, name, parent row,
and optional `IsDirectory`, `IsTombstone` (a deleted record whose name is kept),
`IsFree` (a slot holding no record, so scans, lookups and counts skip it),
`Attributes`, `Size` (null writes the size-unknown flag), `Modified` and `SequenceNumber`. Capacity is planned from the
rows, with headroom for later edits.

```csharp
var cacheDirectory = Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}");
var path = SyntheticBlock.WriteCached(cacheDirectory, 'C', volumeSerial: 0x1234,
    new SyntheticBlockOptions
    {
        JournalCursor = new SyntheticJournalCursor(7, 4096),
        Completed = completed,
        CacheTag = new CacheTag("TEST", 1)
    },
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true },
        new SyntheticRow(6, "notes.txt", 5) { Size = 12 }
    ]);

SyntheticBlock.Edit(path, 0x1234, editor =>
{
    editor.MarkSizesUnknown(row => row.Row == 6);
    editor.MarkCompactionNeeded();
});
```

- `CachedPath` is the canonical file path for a drive, so a test never builds the
  file name itself.
- `WriteCached` writes a complete block into the drive's cache slot. Set
  `ProducerKind = ProducerKind.Enumeration` and `RootRow = 0` for an enumeration block.
- `Edit` opens an existing block and hands a `SyntheticBlockEditor` to the callback,
  then flushes and closes it. The editor sets attributes, marks sizes unknown
  (`MarkSizesUnknown` for every matching row, returning how many) or compaction needed,
  and sets the scan timestamp through `Complete`. `SyntheticIndexInspection.ReadHeader(index, driveLetter)`
  reports the timestamp of an open index's block for a later edit after disposal.
  `SetProducerKind(ProducerKind.Mft)` turns a block
  written by an enumeration scan into one an index warm-starts without a producer and
  whose rows accept `FileIndexTestAccess.ApplyJournalEntries`.
  `SetCacheTag(new CacheTag("TEST", 7))` replaces only the stored cache identity,
  preserving the scan timestamp, journal cursor, other header fields and every row;
  passing `default` stores the all-zero identity. A cache-only `FileIndex` opened
  with a different requested tag reports `DriveFailureKind.CacheTagMismatch`.
  `CorruptNamePool` makes the next open reject the block with
  `BlockValidationResult.InvalidNameDescriptor`. Using the editor after the edit
  returns throws `InvalidOperationException`.
- `MaximumPathDepth` is the deepest path the index resolves.

Every operation holds the cache slot's owner lock for its duration and throws
`InvalidOperationException` when an open index owns the slot, so dispose the index
before seeding or editing its block. A missing block or one that fails
validation throws the same exception naming the problem.

`SyntheticMftProducer` is the block producer of a test index. It writes the rows a
callback returns for each drive through the same writer and reports a real
produce result carrying the request's cache tag. Its settings model what a
test needs from a scan:

- `JournalCursor` and `Completed` are stamped into every block. The cursor's
  `JournalIdentifier` identifies the journal instance and `NextUsn` is the next
  position a reader resumes from;
- `BeforeProduceAsync` is awaited before each production, so a test holds a scan or
  rescan in progress and releases it when ready;
- `CatchUpLoss` returns the proven catch-up loss a production reports, which the
  index surfaces as `DriveWatchStatus.CheckpointLoss`.

The callback must return the root row (row 5 for an MFT block).

## Isolate cache and journal state

Consumer test assemblies activate journal isolation from a module initializer:

```csharp
using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

static class TestIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        JournalIsolation.ForbidLiveJournalReads();
    }
}
```

Cached opens require an explicit nonblank `FileIndexOptions.CacheDirectory`,
including empty-drive tests. Supply a temporary directory the test owns.
Missing configuration throws `ArgumentException`; there is no library default
cache directory or cache guard. `NoCache` needs no path and ignores any supplied
path without creating it. Dump sources prohibit cache options.

`JournalIsolation.ForbidLiveJournalReads` is process-wide, idempotent, and
one-way. It makes live journal observations answer "cannot say" instead of
reading a volume. It does not throw and does not affect broker source delegates
supplied by the test. It covers the index's automatic checkpoint observations only:
production scan and watch sources still query a journal cursor, and explicit
journal settings queries still open the volume.

Use `JournalIsolation.OverrideJournalWindow` when a test needs a retained,
trimmed, or recreated journal window. The callback receives a drive letter and
returns `SyntheticJournalWindow?`; null means "cannot say" and never falls back
to a live read. The returned scope is process-global, so mark the whole fixture
nonparallel, keep the scope through all awaited work, and stop and dispose every
index and watch before disposing it. Nested or overlapping public scopes throw
`InvalidOperationException`. Mutable callback state must be synchronized because
different drive pumps can query it concurrently.
