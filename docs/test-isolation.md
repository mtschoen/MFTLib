# Test isolation

MFTLib.Tests runs eligible test classes in parallel by default. Its
`MSTestSettings.cs` assembly declaration uses `ExecutionScope.ClassLevel` and
`Workers = 0` (the machine's logical processor count); methods within a class
remain sequential. Classes marked `[DoNotParallelize]` run in the isolated
nonparallel set.

MSTest runs the parallel set first and the `[DoNotParallelize]` set afterwards,
one test at a time, so a nonparallel class never overlaps any other class (MSTest
`TestExecutionManager`: parallel tasks, `Task.WaitAll`, then the nonparallel set;
[v3.1.1 source](https://github.com/microsoft/testfx/blob/v3.1.1/src/Adapter/MSTest.TestAdapter/Execution/TestExecutionManager.cs),
and the [Microsoft Learn description](https://learn.microsoft.com/dotnet/core/testing/unit-testing-mstest-writing-tests-controlling-execution)
of the deferred set). That phase order is an MSTest implementation detail, which is
why every change to scheduling is re-qualified as below. A class that only reads
shared state (for example `PlatformBranchTests` resolving a relative path) stays
parallel because every class that writes that state is nonparallel.

A test class must carry class-level `[DoNotParallelize]` when any of its code
(including helpers, nested types and async state machines) does one of these:

- calls a process-wide seam or its `ResetToDefaults` (the native delegate seams below, and
  any other static mutable field, static property setter, `ResetToDefaults`, `Enable`,
  `OverrideJournalWindow` or `ReplaceWriterForTest` in MFTLib, MFTLibTestExtensions,
  the samples or Benchmark, for example `BrokerLauncher`, `ElevationUtilities`,
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

Tests that reference the process-wide native delegate seams in `MFTLibNative`
or `FileUtilities`, including calls to either `ResetToDefaults`, must carry
class-level `[DoNotParallelize]`. Keep cleanup resets, but do not rely on them
for isolation from concurrently running test classes. The same rule applies to
any member of `NativeTestHooks` (the native failure-injection and observation
hooks), whose calls mutate process-global native state. The guard checks compiled
IL references, including nested generated methods and local test helpers. Run it
on Windows as well as Linux: Linux compilation excludes several Windows-only test
classes. For stress validation, use 32 ClassLevel MSTest workers with the
existing Linux platform exclusions in `scripts/coverage-linux.sh`; do not add
new exclusions to hide seam races.

MFTLib.Tests activates default-cache isolation from a module initializer, so
`dotnet test`, IDE runners, and the coverage scripts all reject accidental use
of the real per-user cache. Consumer test assemblies can opt in by referencing
MFTLibTestExtensions and calling
`MFTLibTestExtensions.CacheDirectoryIsolation.ForbidDefaultCacheDirectory()`
from their own `[ModuleInitializer]` before opening indexes. Referencing the
assembly alone does not activate protection.

Activation is idempotent and one-way for the test process; there is no reset,
and it is not part of the native delegate seam family. It is not inherited by
child processes. Once activated, `CacheDirectory.ResolveDefaultPath()` throws
`InvalidOperationException`; tests must set `FileIndexOptions.CacheDirectory`
to an owned temporary path, including empty-drive and `NoCache` opens. The
guard runs before `FileIndex.OpenAsync` creates the cache directory. It blocks
default resolution, not arbitrary explicitly supplied paths. Production hosts
that do not opt in retain the existing default-cache behavior.

The same initializer activates journal isolation, through
`MFTLibTestExtensions.JournalIsolation.ForbidLiveJournalReads()`, on the same
one-way idempotent terms. Opening a drive reads the live USN journal to decide
whether a cached block's checkpoint is still resumable, and a faulting watch
reads it again to decide whether that drive's position is still in the journal,
so a test that warm-starts or watches a synthetic MFT-kind block over a drive
letter that happens to name a real NTFS volume would have that block rejected as
`JournalRecreated`: a synthetic journal id never matches a real one. The same
test would cold-scan on one machine and warm-start on another. Measured before
the guard, nine existing test methods reached the live read on letters `T` and
`U`, which pass here only because neither letter is mounted on this machine.

Unlike the cache guard this one does **not** throw. Warm-starting is a
legitimate thing for a test to do and most such tests have no interest in the
journal, so the guard returns "cannot say", which is exactly what a volume with
no readable journal already answers; the outcome becomes deterministic instead
of becoming an error. Without a synthetic override, a new index opened under the guard reports no
CheckpointLoss from journal observations. Consumer tests can call
MFTLibTestExtensions.JournalIsolation.OverrideJournalWindow with a
Func<char, SyntheticJournalWindow?> to drive real open-time and watch-fault
checks. SyntheticJournalWindow carries JournalId, FirstUsn, NextUsn,
AllocationDelta, and MaximumSize; null means "cannot say" and never falls back
to a live read. The callback is evaluated for each query and may run on any
drive's pump thread, concurrently for different drives, so mutable per-drive
observations need synchronization.

The returned IDisposable owns a process-global override. Nested and overlapping
public scopes throw InvalidOperationException. Disposal restores the previous
behavior exactly once without resetting the one-way guard, including when a
using block exits through an exception; it does not clear reports already
recorded on an index or drain callbacks already in flight. Mark the entire
consumer fixture nonparallel (class-level [DoNotParallelize] in MSTest), keep
the scope alive through all awaited work, and stop/dispose indexes and watches
before disposing it. Do not combine independently installed internal overrides
with an active public scope. The immutable guard flag itself still needs no
per-test synchronization and is not part of the native delegate seam family.
A test whose subject really is a real volume overrides with
`JournalCheckpointCheck.ReadLiveJournal`, which bypasses the guard; three tests
in `UsnJournalVolumeInteropTests` do.

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
