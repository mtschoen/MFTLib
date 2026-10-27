# Test isolation

MFTLib.Tests runs eligible test classes in parallel by default. Its
`MSTestSettings.cs` assembly declaration uses `ExecutionScope.ClassLevel` and
`Workers = 0` (the machine's logical processor count); methods within a class
remain sequential. Classes marked `[DoNotParallelize]` run in the isolated
nonparallel set.

MSTest 4.5.1 runs the parallel set first and the `[DoNotParallelize]` set
afterwards, one test at a time, so a nonparallel class never overlaps any other
class. `TestExecutionManager.ExecuteParallelizedTestsAsync` awaits
`Task.WhenAll(tasks)` before executing the nonparallel set
([pinned source](https://github.com/microsoft/testfx/blob/v4.5.1/src/Adapter/MSTestAdapter.PlatformServices/Execution/TestExecutionManager.ParallelExecution.cs)).
That phase order is an implementation detail, so framework upgrades and
scheduling changes require the qualification below. A class that only reads
shared state (for example `PlatformBranchTests` resolving a relative path)
stays parallel because every class that writes that state is nonparallel.

A test class must carry class-level `[DoNotParallelize]` when any of its code
(including helpers, nested types and async state machines) does one of these:

- calls a process-wide seam or its `ResetToDefaults` (the native delegate seams below, and
  any other static mutable field, static property setter, `ResetToDefaults`, `Enable`,
  `OverrideJournalWindow`, or access to the `BrokerDiagnostics.Dependencies` owner in MFTLib,
  MFTLibTestExtensions, the samples or Benchmark, for example `BrokerLauncher`,
  `BrokerDiagnostics`);
- changes `Environment.CurrentDirectory`, an environment variable, `Console` output,
  error or input, `Environment.ExitCode`, an `AppContext` switch, the default thread
  culture or the thread pool size;
- uses a fixed, non-unique file, directory, pipe, mutex or port name. Use `Guid` or
  `TestVolumeSerial.GetNext()` names instead; none of the current tests need an exception.

`NativeSeamIsolationTests.NativeSeamReferences_RequireClassLevelDoNotParallelize`
enforces the first two bullets from compiled IL and fails with the class and member
named; its fixtures include non-executed violation controls and isolated or read-only
controls. The third bullet is not detectable from IL and is a review rule.

Qualify scheduling changes with three consecutive green runs using 32 ClassLevel
workers on both Windows and Linux, plus normal pull-request CI. Pass
`-- MSTest.Parallelize.Workers=32 MSTest.Parallelize.Scope=ClassLevel` to
`dotnet test`, retaining the existing platform filters, and record serial-before
and parallel-after wall clock and per-run results in the pull request. A code
change after a qualifying run invalidates it on both operating systems. Fix
missing class isolation or genuine isolation defects; never add exclusions to
hide failures. If the runs cannot stay green, revert the scheduling change
and name the failing classes in the issue. Tests that need administrator rights
(`TestCategory=RequiresAdmin`) are not part of the unelevated runs and are
qualified only by the elevated coverage job.

Tests that reference the process-wide native hooks in `MFTLibNative.Hooks`,
`FileUtilities.Hooks` or `Kernel32.Hooks`, including calls to their `ResetToDefaults`,
must carry class-level `[DoNotParallelize]`, cleanup resets, and one explicit
control-code dispatcher when a scenario needs multiple `DeviceIoControl` operations. Keep cleanup
resets, but do not rely on them for isolation from concurrently running test classes. The same rule
applies to any member of `NativeTestHooks` (the native failure-injection and observation
hooks), whose calls mutate process-global native state. The guard checks compiled
IL references, including nested generated methods and local test helpers. Run it
on Windows as well as Linux: the Linux build leaves out the real WMI tests and its
run filters out `UsnJournalSyntheticTests`. For stress validation, use 32 ClassLevel
MSTest workers with the existing Linux platform exclusions in `scripts/coverage-linux.sh`;
do not add new exclusions to hide seam races.

The shared `MFTLibNative.Hooks` object owns ABI-query, controlled parse and result-free substitutions. `FileUtilities.Hooks` owns ordinary and watch-volume handle substitution. `Kernel32.Hooks` owns CreateFile, DeviceIoControl and GetDiskSpaceInformation substitution. These are process-global even though the delegates are instance fields. Tests accessing the objects or their members require class-level DoNotParallelize and restore defaults only after their operations have stopped. `NativeSeamIsolationTests` recognizes the hooks-object types and their access fields, including calls through generated methods and test helpers. Higher-level broker and local-producer tests inject IVolumeAccess instead.

The retained native substitutions remain for targeted low-level testing:
- `MFTLibNative.Hooks._getMftNativeAbiVersion`: ABI-mismatch handling in `MftResultTests.Batches.cs` and `MftDumpInputFaultTests.cs`.
- `MFTLibNative.Hooks._parseMftRecordsWithProgress`: callback exceptions, NaN progress samples, and synchronous delivery in `MftVolumeTests` and `MftProgressParseFixture`.
- `MFTLibNative.Hooks._freeMftResult`: freeing deliberately constructed malformed ABI buffers in `MftResultTests` and `MockVolumeTests`.
- `FileUtilities.Hooks._getVolumeHandle` and `_getWatchVolumeHandle`: fixture-file scans and cancellable idle-pipe tests using owned handles without privileged live volumes.
- `Kernel32.Hooks._createFile`, `_deviceIoControl`, and `_getDiskSpaceInformation`: volume-open flags and error paths in `MockVolumeTests`, plus synthetic geometry, disk-space and grow buffer injection.

`Persistent` and `ExistingOnly` opens (null `FileIndexOptions.CacheMode` is `Persistent` for non-dump sources)
require an explicit nonblank `FileIndexOptions.CacheDirectory` and a non-null `FileIndexOptions.Root`.
Missing configuration throws `ArgumentException`
before creating a directory. Tests supply temporary directories they own. `Disabled` opens
require no cache path and ignore supplied paths without resolving or creating
them. A dump source supplies its own volume: validation runs first and requires
null `Root`, `CacheMode` and `CacheDirectory`, and
default `CacheTag`. Every non-null cache mode, including `Disabled`, is rejected.

MFTLib.Tests activates journal isolation from a module initializer, through
`MFTLibTestExtensions.JournalIsolation.ForbidLiveJournalReads()`. Consumer test
assemblies must call it from their own `[ModuleInitializer]`; referencing the
assembly alone does not activate it. Activation is one-way and idempotent, has
no reset and is not inherited by child processes.
Opening a volume reads the live USN journal to decide
whether a cached block's checkpoint is still resumable, and a faulting watch
reads it again to decide whether that volume's position is still in the journal,
so a test that warm-starts or watches a synthetic MFT-kind block over a volume
letter that happens to name a real NTFS volume would have that block rejected as
`JournalRecreated`: a synthetic journal identifier never matches a real one. The same
test would cold-scan on one machine and warm-start on another.

Journal isolation does **not** throw. Warm-starting is a
legitimate thing for a test to do and most such tests have no interest in the
journal, so the guard returns "cannot say", which is exactly what a volume with
no readable journal already answers; the outcome becomes deterministic instead
of becoming an error. Without a synthetic override, a new index opened under the guard reports no
CheckpointLoss from journal observations. Consumer tests can call
MFTLibTestExtensions.JournalIsolation.OverrideJournalWindow with a
Func<VolumeKey, SyntheticJournalWindow?> to volume real open-time and watch-fault
checks. SyntheticJournalWindow carries JournalIdentifier, FirstUsn, NextUsn,
AllocationDelta, and MaximumSize; null means "cannot say" and never falls back
to a live read. The callback is evaluated for each query and may run on any
root's pump thread, concurrently for different roots, so mutable per-root
observations need synchronization.

The data owner is `JournalCheckpointCheck.Hooks` (`JournalHooks`), and the exclusive scope owner is `ProcessGlobalScope` through public `JournalIsolation.OverrideJournalWindow`. Nested and overlapping scopes throw InvalidOperationException. Callback publication is volatile, and mutable callback captures must be independently synchronized. Disposal does not drain callbacks already in flight, and restores the guard-controlled observation behavior exactly once, including when a using block exits through an exception; it does not clear reports already recorded on an index. The one-way live-read guard stays enabled after scope exit and is not inherited by children. Mark the entire consumer fixture nonparallel (class-level [DoNotParallelize] in MSTest), keep the scope alive through all awaited work, and stop/dispose indexes and watches before disposing it.

MFTLib.Tests calls `JournalIsolation.OverrideJournalWindow` directly for every journal override, sharing the public scope's single-active ownership. Explicit live-reader cases in `UsnJournalVolumeInteropTests` use a private `ReadLive`, which converts `JournalCheckpointCheck.ReadLiveJournal` observations into `SyntheticJournalWindow` values. The guarded-versus-explicit-read test supplies a fake volume handle and journal bytes through the native hooks; the real-volume test exercises the unelevated handle against the host journal. A null observation remains unknown. The one-way guard is never reset.

## Shared scenario fixtures

TestSupport owns temporary resources. `TempDirectory` gives each owned root a GUID,
supports a deferred path for missing-directory scenarios, and retries recursive
deletion five times before tolerating a Windows IOException from a still-locked
file. Other deletion failures propagate. `TempFile` composes `TempDirectory` for
single-file inputs and generated `.mft`/`.img` siblings so every temporary file
and companion lives under a disposable directory owner. Dispose indexes, watches,
enumerators, writers, handles and mapped blocks before disposing their directory
or file owners.

Helper methods that allocate a mapped block or temporary directory return a
composite disposable or accept a caller-owned `TempDirectory`; they never discard
the owner path after opening a mapping. Raw `Path.GetTempPath()` in `MFTLib.Tests`
is limited to `TempDirectory` itself and temp-path assertions/inputs
(`ElevatedEntryPointTests`, `BrokerDiagnosticsTests`, and
`DefenderExclusionSequenceTests.AssertNoScriptFile`). Non-existent path literals
that never touch disk use `AppContext.BaseDirectory`, `TestRootDirectory.For(...)`, or
a `TempDirectory(..., create: false)` owner.

`TempFile` owns one uniquely created temporary file; dispose its streams before
disposing the owner. `NativeTestHooks.ParseImage(byte[] image, uint bufferSizeRecords = 256)`
writes an image, parses it and releases its stream, native result and file before
returning a marshalled `MftParseResult` metadata snapshot. `ParseHandle(IntPtr
volumeHandle, uint bufferSizeRecords = 256)` borrows a caller-owned handle and returns
a nullable metadata snapshot, preserving native result-allocation failure as null.
Copied error strings and scalar fields survive release; `Entries` and `EntryStrings`
are freed pointer values that may be checked for nullness but never dereferenced.
Scenarios own geometry overrides, failure injection and cleanup resets, and their
test classes require `[DoNotParallelize]`.

`IndexTreeFixture` owns a tree and a deferred cache path. Its default tree contains
`Documents/readme.md` with `hello`; custom-tree scenarios opt out of that seed and
write their own data. Its `Root` and `Options` methods retain explicit producer,
serial and cache-mode choices. `CheckpointLossFixture` adds MFT cache seeding and
named roots; scenario code still owns watch and fault choreography.

MSTest classes sharing an index tree inherit `IndexTreeTestBase`. Its inherited
`InitializeIndexTree` creates the fixture and a unique serial before derived
`TestInitialize` methods run. `TreeRoot` and `CacheDirectoryPath` expose the owned
paths; `SeedDocuments` defaults to true and empty-tree scenarios override it to
false. Scenarios needing a unique volume identity pass `UniqueVolumeSerial`
explicitly to the fixture's options. Derived setup owns only scenario data.
Derived disposal preserves hook resets and releases indexes and additional
resources before calling `base.Dispose()` in a finally block. Async owners await
index disposal before base cleanup; repeated directory disposal is safe. Helpers
constructed inside tests keep explicit `IndexTreeFixture` composition and their
own disposal scopes. Isolation attributes remain on the concrete test classes.

`BrokerHostFactory` constructs scripted hosts, `ScriptedJournalStreams` owns the
cancellable one-batch stream, and `JournalWindows` owns internal journal windows
and overrides. Journal overrides still require class-level isolation and the
scope/lifetime rules above. Consumer-facing journal isolation tests exercise the
public scopes directly.

The single ten-second `TestTimeouts.HangGuard` lives in the test package's
`TestSupport` directory and is imported by the test assembly's TestSupport global
using. Longer watchdogs are explicit multiples. Protocol timeout assertions and
fake-clock inputs remain independent scenario data.

`TestBlocks` centralizes production-backed block setup in the test assembly.
`Create` returns an incomplete, unseeded block owned by the caller; `Options`
supplies the same defaults to named-section and low-level construction tests.
An omitted path gets a unique temporary file with delete-on-close; a supplied
path remains caller-owned and persistent unless explicitly overridden.
Capacities are exact (the name pool is measured in bytes), and root row zero
is the default for every producer. `WriteCache` completes and closes an
inspection seed after any requested post-completion mutation;
`WriteInvalidCache` writes deliberately invalid content for deletion tests.
Neither seed creates an owner lock. `SyntheticBlockBuilder` writes mapped
rows independently of the production writer and remains the fixture for
tests requiring that independence. Dispose mapped blocks before their
temporary-directory owners.

## Broker client and launch dependencies

Each BrokerProcess owns one internal ProcessHooks object through Hooks.
Hooks.PendingRequestCount reads the reservation table under the process state lock.
Hooks.AfterControlWriteLockAcquired runs with the control write semaphore held,
outside the process state lock, immediately before caller cancellation is checked.
It must not throw or wait for another control writer. Configure callbacks before
starting the operation and keep captured state alive until its work has drained.
These hooks are instance-local and do not themselves require DoNotParallelize.

The channel-open disposal test uses ScriptedBroker's pipe-factory wrapper and
TestGate to hold delivery of an already-connected stream. It routes ChannelOpened
before disposing the process, then releases the connection result and verifies
that tracking rejects the volume and disposes its pipe before writing a request.
Release gates in finally blocks and observe the pending open before fixture cleanup.

BrokerLauncher.Dependencies is the process-global LaunchDependencies owner of
_elevationProvider, _getProcessPath and _startProcess. Its defaults are
ElevationUtilities.DefaultProvider, Environment.ProcessPath and Process.Start.
Tests accessing that owner or its members require class-level
DoNotParallelize and call Dependencies.ResetToDefaults only after their launches
and sessions have stopped. NativeSeamIsolationTests recognizes the owner type
and its access field, including access through helpers and generated methods.

DefaultElevatedEntryRunner captures TimeProvider and Action<int> at construction.
Its production constructor uses TimeProvider.System and Environment.Exit.
Internal entry dispatch also receives an explicit exit action for rejected
diagnostics arguments. Tests supply recording actions to both routes; runner
cleanup cannot change an existing runner's captured exit action. Fixtures that
also replace native or diagnostic state remain nonparallel and drain that work
before restoring its defaults.

## Broker diagnostics dependencies

`BrokerDiagnostics.Dependencies` is the process-global `DiagnosticsDependencies`
owner. It holds the diagnostics state itself: the forced opt-in, role, log directory,
client log path, include-self flag and current writer; the `BrokerDiagnostics` static
API reads and writes them through it. `InstallWriter` installs a sink-injected writer
and completes its predecessor under the same writer lock used for writer creation and
queue admission. Append
I/O happens on each writer's drain outside that lock. `ResetToDefaults` completes
and detaches the current writer and restores process diagnostics configuration.
The existing isolation detector recognizes both the owner field and its members.

Fixtures using the owner or changing diagnostics configuration require class-level
`DoNotParallelize`. Stop brokers and logging tasks, release sink gates, complete each owned writer, and await its `Completion` before resetting configuration or deleting its files. Await retired writers through their retained instances: process-level operations observe only the current writer. `FlushAsync` remains an accepted-record snapshot and does not close the sink. Reset is synchronous and is not a drain wait. The public `BrokerDiagnosticsIsolation` facade retains its client-log flush/reset contract; elevated sink ownership is internal.

`BrokerDiagnosticsWriter` captures its append sink and role provider. Flush
snapshots accepted records under the queue-admission lock and completes when
those records have been processed, including failed append attempts. Refused
enqueues are counted as drops but do not increase the flush target. Gated sinks
test saturation, pending flushes and recovery without callbacks inside accounting.

Each `BrokerDiagnosticsLogFilter` captures its own file-reference resolver. The
default is the real resolver. A host captures a filter factory and creates a
fresh filter for every watch; tests pass resolver-owning factories through the
internal constructor. Resolver-only instances need no global reset. Fixtures
that also enable diagnostics or change environment variables remain nonparallel.

Replacement routing is tested before and after installation with blocked sink
I/O and one or two replacements, asserting exactly one delivery per accepted
line and refusal by retired writers. Acquisition and admission are one critical
section, so a replacement cannot occur between them. Full-queue flush tests hold
the first and final accepted appends, refuse another enqueue, and verify one
drop notice and completion only after the accepted lines drain.

## FileIndex component injection

Each FileIndex owns one internal Hooks object of type FileIndex.IndexHooks.
Public OpenAsync creates a fresh default object. Tests that replace cache listing
use OpenWithHooksAsync(options, hooks, cancellationToken), which supplies that
object to construction before any root settles. A hooks object belongs to one
index and is instance-local, so it needs no DoNotParallelize. FileIndex.Hooks.cs
documents each member's lock context and waiting restriction. Release test gates
in finally blocks, and dispose the index before discarding callback state.

| Member | Purpose | Test owner |
| --- | --- | --- |
| `EnumerateCacheFiles` | stale-sibling sweep listing | `FileIndexResilienceTests.RetiredSiblings.cs:OpenAsync_CacheMode_RetiredSiblingListingFails_OpensAndLeavesTheSibling` |
| `RootsReadBeforeLock` | Roots read before the state lock | `FileIndexRootStatusTests.cs`; `FileIndexQueryTests.cs:RootAcrossDisposalAsync` |
| `ReaderAdmitted` | query admitted, before first row | `FileIndexDisposeCancelsQueryTests.cs:ReadAcrossDisposalAsync`; `SubtreeAnchorTests.cs` |
| `LifecycleGatesTakenForDisposal` | disposal holds lifecycle gates | `FileIndexDisposalOrderTests.cs` |
| `DisposedFlagSet` | disposed flag set, before cancel | `FileIndexAdmittedOperationDisposalTests.cs`; `FileIndexDisposalRaceTests.cs` |
| `PublishInsideWriteGate` | publication inside the write gate | `FileIndexConcurrentRescanTests.cs:BatchOnT_DoesNotWaitForUCommit`; `FileIndexWatchRescanHandoffTests.cs` |
| `RecoveryBeforeLifecycleGate` | recovery before its lifecycle gate | `FileIndexWatchRecoveryTests.cs:HoldRecoveryBeforeItsGate`; `RecoveryHolds.cs` |
| `RecoveryCompletionWrapper` | wraps recovery ticket completion | `FileIndexWatchRecoveryTests.cs:RecoveryTicketCompletion_ContinuationNotInline` |
| `BeforeRestartDecision` | before the restart decision | `FileIndexWatchRecoveryTests.RestartDecision.cs`; `FileIndexAdmittedOperationDisposalTests.cs` |
| `RestartRequested` | restart requested, before revalidation | `FileIndexWatchRecoveryTests.RestartDecision.cs` |
| `RestartBeforeRegistration` | restart before registration | `FileIndexWatchRecoveryTests.StopWindow.cs`; `FileIndexAdmittedOperationDisposalTests.cs` |
| `ApplyJournalEntriesEntered` | pump enters apply | `WatchHarness.cs:HoldFirstApply`; `FileIndexWatchRescanHandoffTests.cs` |
| `ApplyJournalEntriesInsideWriteGate` | apply inside the write gate | `FileIndexConcurrentRescanTests.cs` |
| `PumpFaultSettlementWrapper` | wraps pump fault settlement | `FileIndexBatchedOperationTests.cs`; `FileIndexWatchRecoveryTests.cs` |
| `BeforeWatchChanged` | before the Changed event | `FileIndexWatchRescanHandoffTests.cs`; fault-handoff tests |

`RecoveryCompletionWrapper` is retained for `FileIndexWatchRecoveryTests.RecoveryTicketCompletion_ContinuationNotInline`. Its callback brackets the actual `RecoveryTicket.Complete` call with a thread-static marker while a continuation requests `ExecuteSynchronously`. `TryGetRecoveryCompletionForTest` identifies the task but does not identify its completing stack. `RecoveryBeforeLifecycleGate`, producer gates and state/fault events all precede completion and cannot bracket it. Retain the wrapper so the test detects inline continuation execution rather than inferring it from timing. The wrapper runs after lifecycle release and outside the state lock; it must invoke completion exactly once, must not throw, and must not wait for disposal, which drains the ticket.

`EnumerateCacheFiles` is retained for `FileIndexResilienceTests.RetiredSiblings.cs:OpenAsync_CacheMode_RetiredSiblingListingFails_OpensAndLeavesTheSibling`. It injects `IOException` and `UnauthorizedAccessException` only at stale-sibling listing, after owner-lock acquisition, while leaving preparation and block production usable. Locked or read-only sibling files exercise deletion failures instead. Directory-wide permission changes also affect cache preparation, ownership and production, and preparation reapplies directory protection. Those filesystem scenarios do not isolate the listing-failure contract on both platforms. Supply this per-index delegate before opening, retain its state through disposal, and do not wait for an operation requiring the held owner/lifecycle gate.

### Retained observations and controls

Single access paths remain for non-substitutable test observation and gate control:
- `Task FileIndex.WaitForRootWriteGateForTest(RootKey)` and `void FileIndex.ReleaseRootWriteGateForTest(RootKey)` in `MFTLib/Index/FileIndex.Roots.cs`: Tests own a real gate acquisition and release it in `finally`. Used by `FileIndexRescanCleanupTests.cs:RescanAsync_CancelledAtTheWriteGate_RestoresTheRenamedAsideCacheFile` and `FileIndexWatchFailedRescanTests.cs:CancelledPublication_PreservesFaultAndDisposesUnpublishedBlock` to test cancellation while queued at the write gate, and by handoff tests to observe publication leaving its write gate.
- `bool FileIndex.AreRootGatesFreeForTest(RootKey)` in `MFTLib/Index/FileIndex.Roots.cs`: Nonblocking acquire/release of lifecycle and write gates under a test-controlled ordering. Owned by `FileIndexConcurrentRescanTests.cs:ApplyJournalEntries_TakesOnlyItsRootsWriteGate`, `FileIndexCatchUpLossTests.cs:CatchUpLost_HandlerRunsWithLifecycleGateHeld`, and `FileIndexDisposalOrderTests.cs:Dispose_DuringGatedRescans_CancelsThemAwaitsPumpsReleasesGates`.
- `bool FileIndex.TryGetRecoveryCompletionForTest(RootKey, [NotNullWhen(true)] out Task?)` in `MFTLib/Index/FileIndex.Roots.cs`: Exposes exact recovery ticket completion identity under the state lock. Owned by `FileIndexWatchRecoveryTests.cs:RecoveryTicketCompletion_ContinuationNotInline`, `QueuedRecovery_SupersededByConsumerStart_DoesNotScan`, and harness recovery waits.
- `long FileIndex.ContendedWatchStateDeliveriesForTest` in `MFTLib/Index/FileIndex.Roots.cs` observes actual failed `DeliveryLock.TryEnter` attempts through the per-volume Interlocked counter in `FileIndex.RootRuntime.Notifications.cs`. `FileIndexWatchStateChangedTests.Paths.cs:FaultStateTakenByAConcurrentStop_IsDeliveredBeforeTheFaultIsReported` holds the stop's Faulted subscriber until the pump has actually contended for delivery. `BeforeWatchChanged` belongs to file-change delivery after journal application and is absent from this fault path. A signal before lock acquisition, or the final event history alone, cannot establish that the pump attempted delivery while the subscriber was held. Retain the counter to preserve that deterministic race witness.

Lifecycle races are tested behaviorally without one-use test seams:
- Freedom from `_stateLock` during `WatchStateChanged` delivery is tested positively in `FileIndexWatchStateChangedTests.cs:Handler_RunsWithoutTheStateLock_SeesItsChange_AndCannotCallLifecycleMethods`: a held subscriber permits a concurrent second thread to read `Drives` while asserting that the subscriber has not yet exited.
- Callback reentrancy rejection during disposal is tested positively in `FileIndexCallbackReentrancyTests.cs:ChangedHandler_CallsWaitWhileDisposalBegins_GetsTheGuardException`: a caller-owned deferred `Drives` read throws `ObjectDisposedException` during `ValidateBatch` materialization, which `WaitForCatchUpAsync` translates to the reentrancy guard exception.

## Broker host component hooks

Each `JournalBrokerHost` owns one internal `HostHooks` object through `Hooks`.
Configure it before serving and retain observation state until the session has
stopped. These controls are instance-local; they need no process-wide reset.
Test classes using journal overrides or diagnostics still require their existing
class-level isolation.

| Member | Purpose and execution context | Test owner |
| --- | --- | --- |
| `ScanStarting` | Records an admitted scan before its cursor query, including attempts that subsequently fail. Runs on the scan worker without pipe locks and with its parse registration held; callbacks must be thread-safe, nonblocking and nonthrowing. | `BrokerTestHarness` connects `ScriptedBrokerResources.RecordScan`; `BrokerProcessTests.TestExtensions.cs` verifies public scan-log ordering, failed attempts and retention values. |
| `HeartbeatVisited` | Signals completion of a sender pass on the dedicated background thread, outside pipe and sender-list locks. Started writes may still be in flight. Callbacks must not block, throw or wait for session disposal. | `JournalBrokerHostLivenessTests` paces fake-clock ticks and proves thread identity; `ScriptedWatchBrokerHarness` supplies `CrossRootScenario` synchronization. |

Scan progress uses a fixed 250 ms minimum interval between intermediate reports. `JournalBrokerHostTests.Progress.cs` supplies per-host `FakeTimeProvider` instances: frozen clocks exercise throttling and completion flushing, and advancing one host by 250 ms exercises emission independently of another host. Source gates and consumed pipe frames order reports against clock advancement. A source iterator can pause after a yielded batch because the real block writer reports its byte progress before requesting the next batch. Tests release all gates in `finally` before host disposal.

Liveness tests establish readiness through test-owned source gates and allocator
observations, then assert emitted frames and cancellation. Catch-up reads and
real block-flush reports are held at existing collaborator seams while fake time
advances. A completed heartbeat visit is not proof of write completion: tests
also consume the expected frames. Gates are released in local finally blocks
before disposing the host harness.

## Index-support seam ownership

`EnumerationWalkLimit` holds a fixed capacity of `Environment.ProcessorCount`; tests that need contention reserve real leases with `EnterAsync` and release them in `finally`, never resize the semaphore. A test class touching a process-global owner below carries class-level `[DoNotParallelize]` and resets it in cleanup.

| Component owner/members | Lifetime | Tests |
| --- | --- | --- |
| `EnumerationWalkLimit.Hooks._walkQueued` | Process-global; reset after opens drain. | `FileIndexConcurrentOpenTests.Open_EnumerationWalks_NeverExceedTheWalkLimit` |
| `CacheDirectory.Hooks._beforeDelete` | Process-global; runs under the owner lock, reset after deletion drains. | `CacheDirectoryDeletionLiveOwnerTests`; `CacheDirectoryDeletionTests` |
| `IndexedRoot.Dependencies._volumeSerialReader` | Process-global; null selects the native reader. | `IndexedRootTests`; `ReadmeWalkthroughTests` |
| `FileEntry.Dependencies._openById` | Process-global; defaults to `DefaultOpenById`. | `FileEntryOpenTests`; `FileEntryIncarnationTests`; `MftDumpPathTests`; `DirectHostTests` |
| `UsnJournalSettingsQuery.Dependencies._queryOverride` | Process-global; null selects the platform query. | `FileIndexJournalSettingsTests`; `MftDumpApplicabilityTests`; `UsnJournalSettingsQueryLiveTests` |
| `JournalCheckpointCheck.Hooks.ObservationOverride` and one-way guard | Process-global; exclusive scope only through public `JournalIsolation`, the guard never resets. | `JournalWindowOverrideTests`; `ConsumerJournalIsolationTests`; `UsnJournalVolumeInteropTests` |
| `BlockWriter.Hooks._rowCaptured`, `_completeAccessTaken` | Per writer; set before work, no reset. | `BlockWriterDisposalRaceTests`; `BlockWriterCompleteDisposalRaceTests`; `JournalMutatorMetadataDisposalRaceTests` |
| `BlockFile.Hooks` (`_disposeStarted`, `_flushRangeBytes`, `_flushViewRange`, `_synchronizeViewRange`, `_pauseMilliseconds`, `_spinOnce`) | Per block; set before work, ends with the block. | `BlockFileFlushDisposalRaceTests`; `BlockFileRangedFlushTests`; the writer race classes |
| `RootBlockRelease.Hooks` (`_releaseStarted`, `_readerAdmitted`, `OutstandingBorrowCount`) | Per release object; count read under `_gate`. | `RootBlockTests`; `FileIndexBlockReleaseTests`; `FileIndexDisposeCancelsQueryTests`; `SnapshotBorrowTests` |
| `DeletedChildLinks.Observations` (`_descendantsVisited`, `_siblingsExamined`) | Per links object; accumulates, no reset. | `DeletedChildLinksTests`; `JournalMutatorFreedRowTests` |

## Direct sample algorithm observations

`SampleProgram.Direct` keeps only per-call observation callbacks that cannot be replaced by row-enumeration cancellation or returned results:
- `DuplicateNameSieve.Find(..., Action<int>? resultAssembledForTest = null, Action<int>? nameMaterializedForTest = null)` in `SampleProgram.Direct/DuplicateNameSieve.cs`: `nameMaterializedForTest` counts candidate names materialized after the hash passes in `DirectDuplicateNameSieveTests.cs:Find_TwentyThousandUniqueNames_ProducesNoGroupsAndMaterializesAtMostOnePercentOfNames`, because unique names return an empty group list; `resultAssembledForTest` triggers cancellation between completed duplicate groups after row enumeration has already finished in `DirectDuplicateNameSieveTests.cs:Find_CancellationDuringResultAssembly_StopsBeforeMoreWorkOrReturn`. Bitmap capacity always comes from `SieveBitmap.ComputeBucketCount(LiveRowCount(index))`; collision tests construct colliding input names for that natural capacity.
- `LargestFiles.Find(..., Action<int>? resultAssembledForTest = null)` in `SampleProgram.Direct/LargestFiles.cs`: triggers cancellation while draining the bounded heap into final output after row enumeration has finished in `DirectLargestFilesTests.cs:Find_CancellationDuringResultAssembly_StopsBeforeMoreWorkOrReturn`. Each callback is invocation-local, runs synchronously on the caller's thread after the snapshot borrow has been released, and needs no reset or `[DoNotParallelize]`.

## Object lifetime

Parallel classes allocate concurrently, so a garbage collection can land at any
point in a test. A test that asserts on a side effect a finalizable object
controls (a delete-on-close block file existing, a mapping open, a release
started, a retired file present) must hold a strong reference to that object
through the assertion: keep it in a local and end with `GC.KeepAlive(local)`
after the last assertion, because the Release JIT treats a local as dead after
its last use. Reading a block path or `ReleaseState` through a helper or an
unstored property read holds nothing. `[DoNotParallelize]` does not help: the
hazard is object lifetime, not shared state. The finalizer's own behavior is
tested by dropping the reference in a `NoInlining` helper, then
`GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`.
