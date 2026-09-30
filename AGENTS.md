# AGENTS.md

This file is the cross-tool source of truth for coding agents working in this repository. Tool-specific instruction files (`CLAUDE.md`, etc.) import it via `@AGENTS.md`.

## No backward compatibility

MFTLib has never shipped to an external consumer. file-wizard and git-wizard are its only consumers; they are dogfooding projects and are migrated in their pin-bump pull requests. Breaking changes are the purpose of 0.3.0.

- A breaking change is never a cost, a risk, or a trade-off. Do not list it as one in a plan, a specification, a review, or a pull request, and do not weigh it when choosing between designs.
- Delete in place. Do not keep old overloads, `[Obsolete]` members, flags or enum members that preserve previous behavior, transitional wire fields, dual read paths, or a parameter "in case a caller needs it".
- A constant or format that changes does not need version-bump ceremony for its own sake. A cache block in an old format is discarded and the drive cold-scans.
- Describe what each consumer must change as plain migration work, listed by file.
- Do not ask the owner whether to preserve compatibility. The answer is no.

## Build Commands

**Use MSBuild with `-p:Platform=x64` for the native C++ DLL**, and `dotnet build` for managed projects:

```bash
# Build native C++ DLL
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64

# Build managed projects (or test program)
dotnet build -c Release -p:Platform=x64
dotnet build TestProgram\TestProgram.csproj -c Release -p:Platform=x64
```

`dotnet build` cannot build the native C++ project (`MFTLibNative.vcxproj`), which must be compiled with MSBuild.

### NuGet packaging

```bash
# Build Release and pack the NuGet packages
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64
dotnet pack MFTLib\MFTLib.csproj -c Release -p:Platform=x64
dotnet pack MFTLibTestExtensions\MFTLibTestExtensions.csproj -c Release -p:Platform=x64

# Publish to nuget.org, with MFTLib before its exact-version test dependency
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push "MFTLibTestExtensions\bin\x64\Release\MFTLib.TestExtensions.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Running the test program

The test program requires admin elevation (raw volume access). It now includes **self-elevation logic** via `ElevationUtilities`.

For the most reliable experience (proper UAC prompt handling), **run the compiled .exe directly**:

```bash
# Launch directly (will trigger UAC prompt if not already elevated)
.\TestProgram\bin\x64\Release\net10.0\TestProgram.exe C:

# Results are written to output.log in the same directory
cat .\TestProgram\bin\x64\Release\net10.0\output.log
```

If running via `dotnet TestProgram.dll`, the helper will still attempt to relaunch the process with `runas`, but running the `.exe` is preferred.

### Test coverage

**Managed (C#):** Run `scripts/run-coverage.ps1` - builds, runs all tests (including admin with UAC prompt), and reports coverage:
```powershell
.\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
.\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
```

The Windows CI publisher validates coverage before posting `pr-crew/coverage`.
A drop of more than 10 percentage points from the latest successful main-line
coverage status, or zero covered executable lines in a tested namespace, posts
a nonnumeric error status and fails the publishing step. Failed collection,
missing/malformed reports, and unavailable baseline lookup also fail closed.
The baseline follows main's first-parent history (up to 100 commits), using
the latest successful coverage status on the nearest measured commit; it is
not a hard-coded percentage. The tested namespace checks cover MFTLib,
MFTLib.Index, TestProgram, and Benchmark; extend that list and its regression
fixtures when adding another tested executable namespace.

Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1` for the offline
publisher regression checks. On a rejected run, inspect the `windows-coverage`
artifact: `MFTLib.Tests/coverage.xml`, `MFTLib.Tests/coverage-report/`, and
`MFTLib.Tests/coverage-run.stdout.log` plus `coverage-run.stderr.log`. Collection
output is captured raw and printed after the child exits. Preserve the artifact
and rerun CI to distinguish a transient measurement failure from a reproducible
drop. An error is not a trusted low-coverage reading and still blocks the gate.
The collector-exit race is an unverified hypothesis; this guard does not repair
hit collection or change the admin coverage merge path.

**Native (C++):** Microsoft.CodeCoverage.Console via `scripts/native-coverage.ps1`:
```powershell
.\scripts\native-coverage.ps1           # cobertura XML output
.\scripts\native-coverage.ps1 -HtmlReport  # also generate HTML
```

The native DLL must be built Debug|x64 (linked with `/PROFILE`) for instrumentation. The script handles build, instrument, test, and report automatically. Settings in `native-coverage.runsettings`.

The USN journal tests need admin. `scripts/native-coverage-elevated.ps1` self-elevates, runs `native-coverage.ps1` hidden, and streams results live to `native-coverage-elevated.log` at the repository root while the visible parent prints new log lines plus a heartbeat (every 30 seconds by default, configurable via `-HeartbeatSeconds` with a 2-second polling granularity). Pass `-TimeoutSeconds <int>` (default 1800) to adjust the warning threshold when running on slower hardware (the parent warns but continues waiting as long as the child process remains alive).

Tests that reference the process-wide native delegate seams in `MFTLibNative`
or `FileUtilities`, including calls to either `ResetToDefaults`, must carry
class-level `[DoNotParallelize]`. Keep cleanup resets, but do not rely on them
for isolation from concurrently running test classes. `NativeSeamIsolationTests`
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

## Cleaning the working tree

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

Git offers no way to protect a file from `git clean`. Aliases cannot shadow built-in commands, there is no `pre-clean` hook, and `-x` overrides `.git/info/exclude`. Only tracked files and files outside the tree are safe, which is why the invariant above is the whole mechanism.

What a clean removes and how each comes back:

| Removed | Restored by |
| --- | --- |
| `bin/`, `obj/`, `x64/`, `build/`, `.vs/`, `node_modules/`, `TestResults/` | rebuild |
| Coverage reports and logs at the repository root | re-run the coverage scripts |
| `.claude/` and `.aislop/` scan output and caches | re-run aislop or inspectcode |
| `.claude/settings.local.json` | re-granted as needed; broad grants live in the user-scope settings, and any provisioned project-scope overrides are re-applied by the tool that wrote them |
| `.claude/AISLOP.md`, `.claude/CLAUDE.md` | `aislop hook install claude --project` |

`.claude/AISLOP.md` and `.claude/CLAUDE.md` are generated boilerplate the aislop installer writes into a sentinel-fenced block, which is why they are not tracked. They do not restore themselves - nothing rewrites them until that command is run, so run it after a clean. It is also how to refresh them after an aislop upgrade.

`.aislop/` run history (`history.jsonl`) and session logs are ephemeral runtime telemetry. The quality gate enforces an absolute `failBelow: 100` threshold on the current tree rather than relative historical deltas, so past run logs are not required to build or verify the repository, and fresh scan output and caches are regenerated on the next run.

### Getting back to work after a clean

`init.ps1` (Windows) and `init.sh` (Linux) at the repository root do the two things a clean does not undo by itself - the NuGet restore and the generated agent files - and report any prerequisite they cannot install for you:

```powershell
git clean -ffxd && .\init.ps1          # restore only, a few seconds
git clean -ffxd && .\init.ps1 -Build   # also build the solution Release|x64
```

From `cmd.exe`, use `init.bat`, which forwards to the same script:

```bat
git clean -ffxd && .\init.bat
git clean -ffxd && .\init.bat -Build
```

Keep the `.\` prefix. This machine sets `NoDefaultCurrentDirectoryInExePath=1`, so `cmd.exe` does not search the working directory for executables and a bare `init.bat` fails with "not recognized as an internal or external command".

```bash
git clean -ffxd && ./init.sh           # restore only
git clean -ffxd && ./init.sh --build   # also build native (cmake/ninja) + managed
```

Both are idempotent, so they are safe to run at any time, not only after a clean.

They also make one optional call: if a settings-provisioning tool is on PATH, they ask it to re-apply the project-scope settings it owns, since a clean removes `.claude/settings.local.json`. The call names a single feature rather than running the tool's whole pipeline, and the step is skipped entirely when the tool is absent, so nothing here depends on it.

They are separate scripts rather than one cross-platform script because the work genuinely differs: Windows resolves 64-bit MSBuild through `vswhere` to build `MFTLibNative.vcxproj` and builds the managed projects via `dotnet build` (mirroring `run-coverage.ps1`), while Linux drives cmake/Ninja through `scripts/build-linux.sh` and restores the managed projects individually, since the dotnet CLI cannot load the `.vcxproj` at all. This matches the existing split between `run-coverage.ps1` and `coverage-linux.sh`.

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop, reportgenerator (HTML coverage only). Linux: .NET SDK, cmake, ninja, g++, aislop, gcovr (native coverage only).

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` runs `windows` + `linux` jobs on every PR and on push to `main`. Both run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires both `(pull_request)` checks to pass before merge.

For Gitea-specific gotchas (act_runner host-mode quirks, VS BuildTools quirks, .NET version mismatch, PS7 + dotnet test comma-splitting, etc.), read `~/schoen-lab/packages/local_ci/docs/project-ci-setup.md` before modifying the workflow. Runner-account environment needs (pwsh on PATH, `DOTNET_INSTALL_DIR`) are fixed at the runner service level - do not add per-workflow bootstrap steps for them.

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant. MFT record geometry (1024 or 4096-byte records) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through compact ABI version 2 (`MFT_NATIVE_ABI_VERSION`): 50-byte `MftCompactEntry` rows with int64 size at offset 32, int64 modified time (FILETIME) at offset 40, and uint16 sequence number at offset 48, plus separate UTF-16 string pools. Flags bit `0x8000` marks an unknown size. The broker's block path parses without path resolution.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop. The `MFTLib.Index` namespace provides a substrate-neutral columnar block format and query engine; see `docs/index-format.md`.
    - **Index namespace boundary**: `MFTLib.Index` depends on nothing in the flat `MFTLib` namespace or in `MFTLib.Interop` beyond an allowlist of journal value types (`UsnJournalEntry`, `UsnJournalEntryOptions`, `UsnReason`). Enforced by `MFTLib.Tests/Index/NamespaceBoundaryTests.cs`, an IL-level ArchUnitNET test over the built assembly, with a mandatory negative-control fixture. Not an aislop rule: the forbidden folders share the flat `MFTLib` namespace, so there is no `using` for an import rule to match. Growing the allowlist is a review decision.
    - **Consumer cache identity**: `FileIndexOptions.CacheTag` carries an opaque
      four-ASCII-character code plus a `uint` version; default is all zeros and
      compares exactly, not as a wildcard. Block format 3 stores the two values
      at offsets 104 and 108 in a 112-byte declared header. Old-format blocks
      cold-scan once. Consumers bump their own version when their profile or
      keep-list changes. Mismatches report `WrongCacheTag` and cold-scan; a
      cache-only open fails with `DriveFailureKind.CacheTagMismatch`. Both emit
      stored/requested tag diagnostics. `InspectCached` reports tags on available
      blocks. Tags are initialized before completion; custom MFT producers copy
      `request.CacheTag` into creation options, and the built-in broker adapter
      forwards it automatically. See `docs/index-format.md` for the contract.
    - **Checkpoint loss**: opening a drive reads the live journal through the unelevated
      volume-root handle (`UsnJournalVolumeInterop`) before adopting a cached block. A block
      whose `BlockHeader.UsnNextUsn` is below the journal's `FirstUsn`, or whose
      `BlockHeader.UsnJournalId` no longer matches, cannot be resumed, so the drive cold-scans
      and `DriveStatus.CheckpointLoss` records the checkpoint, the journal window, and the size
      a journal would need to be at least to have kept it (`JournalSizeArithmetic`: the
      checkpoint-to-tip span rounded up to the allocation delta, plus one more allocation delta).
      The margin follows NTFS's documented trimming behavior in CREATE_USN_JOURNAL_DATA and
      USN_JOURNAL_DATA, not a live measurement. A volume that cannot answer the query warm-starts
      and reports nothing. A faulting MFT-backed drive asks the live journal about the cursor in
      its current block; the read runs outside `_stateLock`, and the result is recorded only while
      that block is still published. `JournalBrokerHost.DescribeWatchFailure` applies the same
      journal classification to a failed nonzero watch start. Neither path classifies exception
      wording, and a retained cursor or unavailable query records nothing new.
      `JournalCheckpointLoss.DetectedDuring` distinguishes `DriveOpening`, `LiveWatch`, and
      `ScanCatchUp`. A report remains until a newer report replaces it or a consumer rescan clears
      it; an unrelated watch fault does not clear it. A `LiveWatch` report is retained by its
      automatic recovery rescan, because it explains why the published replacement block exists.
      A cache-only open adopts an unresumable block as a `Ready` snapshot with its report attached,
      but `StartWatchingAsync` refuses that drive and leaves no watch request. After `RescanAsync`
      publishes a resumable block, the consumer calls `StartWatchingAsync` again.
      When catch-up after a scan fails, the host checks the armed cursor against the live journal.
      A proven loss travels through `BrokerDriveScanResult.CatchUpLoss` and
      `MftBlockProduceResult.CatchUpLoss`; the index publishes the complete block as unresumable,
      records a `ScanCatchUp` report (including `SizeThatWouldHaveRetained` when the journal was
      trimmed), and scans the drive again. `DriveStatus.ConsecutiveLostCatchUps` reaches
      `FileIndex.LostCatchUpRecoveryLimit` after three consecutive losses; a scan whose catch-up
      holds resets it to zero. At the limit an open settles the drive `Ready` with its last block,
      refuses its watch, and requires a consumer `RescanAsync` before another watch start.
      A catch-up failure the journal cannot prove is an ordinary failed scan and carries no report.
      A bounded catch-up read that returns entries without advancing its cursor fails the scan.
    - **ABI versioning**: `MFTLibNative.EnsureCompatibleNativeAbi()` / `MftResult`'s constructor check the native ABI version and entry stride before parsing, and throw `InvalidOperationException` immediately on a managed/native mismatch instead of decoding mismatched memory.
    - **Query lifetime**: the eight entry points that scan rows (`Find`, `FindByName`, `Search`, `Enumerate`, `Largest`,
      `DuplicateNames`, `Root`, and `FileEntry.Children`) each take an optional `CancellationToken`, observed
      before the first row and then at least every 4096 rows from inside `RowScanner`, and each holds a borrow
      on the `Snapshot` it reads for its whole duration. `FileEntry.Children` observes only its caller's token,
      since a handle holds no index reference; disposal waits that listing out rather than cancelling it. Every
      other `FileEntry` member reads one row and keeps the per-access `IsReleased` check instead.
      `SnapshotRelease` counts borrows; `DisposeAsync` cancels a disposal token the seven `FileIndex` queries
      and `WaitForCatchUpAsync` waits are linked to, waits for the borrows on the current and retired snapshots
      to drop, and only then unmaps. A suspended `Enumerate` enumerator holds its borrow between yields, so
      disposal waits until it advances or is disposed, or, if abandoned, until garbage collection and finalization
      return the borrow. Dispose enumerators promptly; collection timing is not guaranteed. The 4096-row checkpoint
      bound applies to active scanning, not time spent suspended. Scanning on one thread while another
      disposes is therefore safe. The snapshot finalizer path
      is unaffected: a borrow holds the snapshot, so a borrowed snapshot is never collected.
    - **Watch start readiness**: `StartWatchingAsync(X)` returns once X's channel is connected and
      `StartWatch` is written.
    - **Watch and catch-up lifetime**: each MFT-backed drive has an independent
      `IIndexDriveWatch`, pump, stop source, and catch-up slot. `StartWatchingAsync(X)` begins at
      `WatchCatchUpState.CatchingUp`; journal batches through the tip captured at start are applied
      before `DriveCaughtUp` moves X to `CaughtUp`. `WaitForCatchUpAsync(X)` follows that instance:
      it completes when X catches up, faults with X's watch fault, and is cancelled when stop,
      rescan, or disposal retires the instance. The list and all-drive overloads fan out to the
      requested drives concurrently and return one `DriveOperationResult` per drive. A `Drive` or
      `Apply` fault publishes `Recovering`, raises `WatchFaulted`, and automatically rescans and
      restarts only X. A second fault before the restarted watch reaches `CaughtUp`, or a failed
      recovery scan, raises `WatchFaulted(Recovery, X)` and leaves X `Faulted` until a consumer
      calls `RescanAsync(X)` or `StartWatchingAsync(X)`. `Channel` faults never recover.
      A rescan keeps a healthy watch and its pending catch-up waits attached throughout
      production. Failed or cancelled production leaves them untouched; retirement cancels
      pending waits when the replacement commits. Changes applied before that commit can be
      delivered after it and repeated by replacement catch-up. Queries can lag until the new
      watch reports `CaughtUp`. A successful manual scan whose replacement watch cannot start
      returns normally and raises `RescanRestart` once, with the start failure as its inner
      exception. Both its exception message and `WatchFailureMessage` identify the rescan's
      replacement and the failed watch start. The drive stays `Faulted` without automatic
      recovery until a consumer starts or rescans it; stop rethrows that fault once.
      A failed automatic recovery, including its restart, reports `Recovery`.
    - **Per-drive state machine**: each configured drive has a `DriveRuntime`; each start creates
      a distinct `WatchInstance` with its own generation, cancellation sources, handle, pump,
      catch-up slot, armed block, fault, and `Drained` task. `Current` holds at most one instance,
      and `Retiring` holds its predecessor until teardown finishes. A pump applies a batch only
      while it is still the current running instance armed against the published block; every
      completion, fault, checkpoint report, recovery ticket, and cleanup performs the same
      identity check, so a retiring pump cannot change its successor's block or state.
      Start linearizes when the returned handle is published and the instance becomes running;
      stop linearizes when `WatchRequested` is cleared and the instance becomes retiring; rescan
      and recovery linearize when the replacement block is committed; disposal linearizes when
      `_disposed` is set and its token is cancelled. A rescan or recovery holds the drive's
      lifecycle gate through production, commit-time retirement, draining, and an eligible restart.
      Snapshot creation and cancellation/disposal checks precede retirement; a healthy current
      watch retires in the same state-lock section as block, status, and snapshot publication.
      An already faulted current watch stays until restart registration supersedes it. The first
      publication retires the old watch even when catch-up was lost and the scan must retry.
      Recovery is
      ticketed to the faulted instance and its block and is dropped if either is not current.
      A fault during production can recover after a failed scan; after a successful commit its
      block is stale, and even a delayed queue cannot publish `Recovering` over the replacement.
      A manual commit clears the superseded ticket and recovery state under the publication
      lock, before draining; an automatic recovery's own commit keeps its ticket and state.
      Stop during a recovery or rescan wins: it clears the request, prevents the restart, and
      rethrows the stopped instance's outstanding fault once. A rescan retains a retired watch's
      subscriber fault through draining and replacement startup, until stop consumes it, the
      replacement handle is published, or `RescanRestart` supersedes it with the start failure.
      A fresh consumer start supersedes a
      recovery and discards the faulted instance's outstanding fault. If a restart itself failed
      before publishing a handle in a consumer start or automatic recovery, stop clears the
      request and refused-start fault without rethrowing it. A manual rescan's failed restart
      retains a faulted instance whose `RescanRestart` fault stop rethrows once. `DisposeAsync` cancels and drains all instances and recovery work without
      throwing a watch fault.
    - **Concurrent open**: `OpenAsync` settles every configured drive concurrently and waits for
      them all before publishing the first snapshot or throwing. `FileIndexOptions.OpenProgress`
      reports once for each drive that settles, from that drive's settling thread with no lock
      held. Callbacks may overlap and arrive out of `IndexDriveOpened.SettledCount` order, so a
      consumer keeps the report with the largest count. A cancelled settle claims no count and
      reports nothing; a cancelled or failed open may therefore have reported only some drives.
    - **Lock order**: for drive X the order is X's lifecycle gate, X's write gate, then
      `_stateLock`. No gate is acquired while `_stateLock` is held, and no operation except
      disposal holds gates for two drives; disposal takes every lifecycle gate and then every
      write gate in ascending drive-letter order. Production holds the lifecycle gate but neither
      the write gate nor `_stateLock`; publication takes the write gate and commits the block,
      snapshot, and pending result under `_stateLock`, retiring a healthy watch in that same
      section. After releasing both `_stateLock` and the write gate, it requests stop and awaits
      the old pump's `Drained` and any predecessor's, holding only the lifecycle gate. `Changed`
      and `WatchFaulted` run with no
      write gate and no `_stateLock`; pumps hold no gate when raising either event. The scan path
      may raise `WatchFaulted(CatchUpLost, X)` or `WatchFaulted(RescanRestart, X)` while
      holding X's lifecycle gate, after draining the old pump.
    - **Callback reentrancy**: a `Changed` or `WatchFaulted` handler runs on a drive's pump (or, for `WatchFaultKind.CatchUpLost` and `WatchFaultKind.RescanRestart`, on the scan operation that holds the drive's lifecycle gate), so a lifecycle call that waits for a pump can deadlock across drives (X's handler stops Y while Y's handler stops X). `FileIndex.Reentrancy.cs` sets an `AsyncLocal` delivery marker around every handler invocation (`Deliver`, used by `RaiseChanged` and `RaiseWatchFaulted`); `StartWatchingAsync`, `StopWatchingAsync`, `RescanAsync`, `DisposeAsync`, their batched forms and an unsettled `WaitForCatchUpAsync` check it synchronously at entry and return an already faulted task carrying `InvalidOperationException`, whichever drive they name. The marker flows into awaits and work the handler starts; its `Active` flag is cleared when the invocation returns, so work queued from a handler that runs afterwards is allowed. Queries, `Drives`, `QueryUsnJournalSettings` and a settled catch-up wait are never rejected. The recovery's restart and a rescan's restart use internal helpers that carry no check. Every waiter a handler can settle or cancel completes its continuations asynchronously (`RunContinuationsAsynchronously`, `AwaitQueuedAsync` for token cancellation), so a continuation never runs on the handler's stack.
    - **Writer lifetime**: every `BlockWriter` operation holds a `BlockAccessScope` on its
      `BlockFile` for the operation's whole duration. `BlockFile.Dispose` refuses new scopes when
      it begins and waits for outstanding ones before unmapping, so a write racing disposal either
      completes against mapped memory or, if it arrives after disposal began, fails with
      `ObjectDisposedException` instead of dereferencing an unmapped view. Raw `BlockFile`
      property reads are not serialized against disposal; readers go through snapshot borrows.
    - **Block ownership**: a cache-mode `FileIndex` holds a sibling `<block>.lock` (opened with
      `FileShare.None`, an exclusive non-blocking `flock` on Unix) for as long as it owns a canonical
      cache block, and that lock is taken before any validation, retired-sibling sweep, rename, or
      delete. A second `FileIndex` over the same cache directory, in any process, that cannot take the
      lock reports the drive `Failed` with `DriveFailureKind.InUse` on a cache-only open; a non-cache-only
      open instead scans into a private `mftlib-private-*` delete-on-close block and leaves the canonical
      cache untouched. `.lock` files are deliberately never unlinked (unlinking races a fresh
      create-and-lock), so cache-pruning tooling must leave `*.mlix.lock` alone. `FileIndexOptions.Diagnostics`
      and `BlockFileCreateOptions.Diagnostics` receive one line per block-file delete with the path and
      reason; null by default. Consumers that deliberately shared one cache block between two live indexes
      must open the second with `NoCache` or expect the in-use outcome. `CacheDirectory.DeleteCached(cacheDirectoryPath,
      driveLetters, diagnostics)` is the lock-safe way for a consumer to clear cache blocks: it takes each
      candidate block's owner lock non-blockingly and deletes only while holding it, the same rule `FileIndex`
      itself follows, so it never deletes, renames, or unlinks a block a live `FileIndex` still owns. A block
      whose lock is already held reports `CachedBlockDeletionOutcome.InUse` and is left untouched; a delete
      that fails after the lock is taken reports `Failed` with the filesystem error message, and neither
      outcome stops the remaining candidates. `.lock` files are never deleted by this path either, for the
      same create-and-lock race reason as above. Each attempt returns a `CachedBlockDeletionResult` (the
      `CachedBlockFile` inventory entry, the `CachedBlockDeletionOutcome`, and a `FailureReason` that is
      non-null only for `Failed`); an already-absent file is reported `Deleted`, an idempotent success rather
      than a `Failed`. A successful delete logs through the optional `diagnostics` callback with the same
      "Deleted block file '...'" shape `FileIndexOptions.Diagnostics` uses elsewhere, invoked synchronously
      while the block's lock is still held. `CacheDirectory.EnumerateCached`, `InspectCached`, and
      `DeleteCached` also have callback overloads that report rejected filenames via
      `Action<CachedBlockRejection>? rejectedFile`, carrying a full `Path` and human-readable `Reason`.
      Existing overloads and canonical result lists retain their behavior. Reporting happens synchronously
      during eager enumeration, before drive filtering and any inspection/deletion lock, including for empty
      selections; callback exceptions propagate before canonical work. Rejected files are never opened, locked,
      or deleted. The deletion success logger remains separate. Consumers can collect these reports to
      preserve their own corruption diagnostics without globbing the cache directory themselves.
      `DriveStatus.CacheSlot` reports the current published block's backing:
      `CacheSlotState.OwnedCanonical` for the canonical slot held by this index,
      `PrivateFallback` for a private fallback block, and `NotApplicable` for
      NoCache and blockless (failed or offline) drives. It is independent of
      `BlockSource`: both canonical and private scans report `ProducedByScan`.
      The status is captured under the state lock; reread `FileIndex.Drives` after
      a rescan. Taking a lock for an in-flight rescan does not change the old
      private block's status; only publishing the replacement does. A failed or
      cancelled scan that leaves the old block in place leaves its backing status
      in place too. The value identifies no process and does not promise that a
      private block's slot is still held elsewhere.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`; `MaterializeBatches`/`ReadRecordBatches` provide bounded-memory batch materialization over the same result.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges.
    - **VolumeBroker**: `BrokerProcess` owns one elevated process and its control pipe, with one
      UAC prompt per consumer session. Nonzero request ids route concurrent `OpenChannel`,
      `QueryVolume`, and `GrowUsnJournal` replies; an id remains reserved until its reply arrives
      or the process ends. Opening a drive operation creates a client-owned pipe, sends its name
      on the control pipe, waits for both the host connection and `ChannelOpened`, and writes one
      request. Each scan or watch then owns that drive channel, so closing or faulting it cancels
      only that operation. Control EOF ends the process and every channel;
      `BrokerProcess.Ended` and `HasEnded` report that lifecycle, and pending work fails with
      `BrokerChannelLostException`. The host admits concurrent scans under one processor-sized
      parse-thread budget and rebalances each `ParseThreadAllowance` when scans enter or leave;
      native parsing reads the allowance at each chunk and before path resolution. A scan writes
      `ScanReady` before bounded catch-up; a journal-proven loss ends it with `CatchUpLost`, while
      any unproven failure ends it with `Error`.
      One dedicated background thread visits every pipe every five seconds. An idle control pipe,
      a watch waiting on its volume, a queued scan, and a processing operation that has reported
      progress less than 30 seconds ago receive `Heartbeat`. This includes a `Processing` step
      that reports progress without writing an operation frame, such as bounded catch-up after
      `ScanReady` or block flush. Heartbeats do not reset the processing progress clock: at or
      beyond `ProcessingLimit` (30 seconds) without progress, the pipe writes `Stalled` and its
      channel is cancelled. A pipe that wrote an operation frame since the previous visit skips
      that visit. A pipe with a frame write in flight is also skipped, so it cannot delay other
      pipes; the client's 30-second no-frame limit then closes that pipe. Any frame resets the
      client limit. `BrokerMftBlockProducer` validates
      completed blocks and transfers them to the index, while `BrokerIndexWatchSource` opens one
      channel per drive watch. `ElevatedEntryPoint` and `BrokerLauncher` dispatch `--broker` mode.
      `BrokerDiagnostics` writes through a bounded background queue and filters the two diagnostic
      logs from journal batches unless `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Automatically self-elevates.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# MSTest) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing `BrokerTestHarness` that runs a
  `JournalBrokerHost` in process over in-memory control and drive pipes and returns the production
  `BrokerProcess` connected to it. `BrokerTestHarnessOptions` supplies the client clock, per-pipe
  connection failures, and held host writes. Host faults surface only through production behavior:
  `BrokerProcess.Ended` and `HasEnded`, `BrokerChannelLostException` on pending operations, and
  `Error` frames; disposing the process never throws a host fault. Ships as the separate
  `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.

### Native error messages

Native exports write failure reasons into fixed-size `wchar_t errorMessage[256]` buffers on their result structs (`MftParseResult`, `UsnJournalInfo`, `UsnJournalResult`). Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` - a variadic template that deduces buffer size, silently truncates via `_vsnwprintf_s(_TRUNCATE)`, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

## Roadmap

See `.plan` for details. Current release is **0.3.0** with USN journal support (`QueryUsnJournal`, `ReadUsnJournal`, `WatchUsnJournal`). Primary consumer is [file-wizard](C:\Users\mtsch\file-wizard).

## Quality gate: aislop

This project uses **aislop** as a deterministic quality gate for AI-written code
(narrative comments, swallowed exceptions, `as any`, dead stubs, oversized
functions, etc.) across TS/JS, Python, Go, Rust, Ruby, PHP, Java, and C#.

`aislop` is installed globally on this machine, pinned to a **specific commit** of
the fork `mtschoen/aislop` (which adds the C# engine: roslynator + jb
inspectcode; upstream npm `aislop` is Python-only). Note that the local global
installation pin and the CI commit pin (`.aislop/fork-commit`) are separate
things to keep in rough sync. Call the installed binary directly - do NOT use
`npx aislop`, which pulls upstream from npm with no C# support:

- **Before declaring work complete**, run `aislop scan .` and address findings.
- **Before committing**, run `aislop scan --staged` (staged files only).
- `aislop fix` auto-clears mechanical issues (formatting, unused imports, dead
  code); `aislop fix --claude` hands the rest back with full context.
- `aislop ci .` is the gate - exits non-zero if the score drops below the
  threshold (`failBelow: 100`) in `.aislop/config.yml`. Treat a failing gate
  like a failing test.

### CI gate (Windows)

`.gitea/workflows/aislop.yml` runs the gate on every PR and on push to `main`.
It runs on **windows-latest**, not Linux like the rest of the fleet: `MFTLib.sln`
includes the native `MFTLibNative.vcxproj`, which only loads/builds under
MSBuild + MSVC, and both jb inspectcode and roslynator load the full solution.
`lint.csharp.jbProjects` in `.aislop/config.yml` scopes jb inspection to the four
C# projects so the C++ tree stays on its own clang-tidy/cppcheck gate. The
workflow installs `aislop` by cloning the `schoen/aislop` fork from Gitea at the
commit pinned in `.aislop/fork-commit` (built with `pnpm`) and runs it via `node`.
It deliberately does NOT use `actions/setup-node`
(its 7zr extraction dies with exit code 2 on the host-mode act_runner). The
build step also mirrors `run-coverage.ps1`'s 64-bit-amd64-MSBuild recipe (the
checkout path is WOW64-virtualized away from 32-bit MSBuild). See the traps in
`~/schoen-lab/packages/local_ci/docs/project-ci-setup.md`. `aislop / quality-gate
(pull_request)` is one of the required status checks in the branch protection
rule on `main`, alongside `test / windows` and `test / linux`, so a failing gate
blocks the merge.

The global pin is a commit-ish, not a version. Do not assume a tag: the `v0.14.1`
tag and the commit currently installed both report version `0.14.1` but are
different commits, so installing the tag would change the code that runs. Ask the
binary what it is rather than trusting this file:

```powershell
aislop --version
pnpm ls -g --depth 0            # or read the spec in pnpm's global package.json
```

To move the global binary to a different commit or tag of the fork:
`pnpm add -g --allow-build=aislop "github:mtschoen/aislop#<commit-ish>"`
(CI pin updates are separate and tracked in `.aislop/fork-commit`.)
