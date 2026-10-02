# Test isolation

Tests that reference the process-wide native delegate seams in `MFTLibNative`
or `FileUtilities`, including calls to either `ResetToDefaults`, must carry
class-level `[DoNotParallelize]`. Keep cleanup resets, but do not rely on them
for isolation from concurrently running test classes. The same rule applies to
any member of `NativeTestHooks` (the native failure-injection and observation
hooks), whose calls mutate process-global native state. `NativeSeamIsolationTests`
checks compiled IL references, including nested generated methods and local
test helpers, and includes non-executed violation controls. Run this guard on
Windows as well as Linux: Linux compilation excludes several Windows-only test
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
