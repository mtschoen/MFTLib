# AGENTS.md

This file is the cross-tool source of truth for coding agents working in this repository. Tool-specific instruction files (`CLAUDE.md`, etc.) import it via `@AGENTS.md`.

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
# Build Release and pack the NuGet package
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64
dotnet pack MFTLib\MFTLib.csproj -c Release -p:Platform=x64

# Publish to nuget.org
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
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
of becoming an error. `DriveStatus.CheckpointLoss` is therefore always null
under the guard unless a test installs `JournalCheckpointCheck`'s journal
override. A test whose subject really is a real volume overrides with
`JournalCheckpointCheck.ReadLiveJournal`, which bypasses the guard; three tests
in `UsnJournalVolumeInteropTests` do. The flag is written once at module
initialization and only read afterwards, so it is safe under parallel test
execution and is not part of the native delegate seam family; the journal
override beside it is not, which is why its users carry `[DoNotParallelize]`.

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

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant. MFT record geometry (1024 or 4096-byte records) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through compact ABI version 1 (`MFT_NATIVE_ABI_VERSION`): 50-byte `MftCompactEntry` rows with int64 size at offset 32, int64 modified time (FILETIME) at offset 40, and uint16 sequence number at offset 48, plus separate UTF-16 string pools. Flags bit `0x8000` marks an unknown size. The broker's block path parses without path resolution.
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
      as before and reports nothing.
      The same check also runs mid-session, from `FileIndex.WatchPump`'s
      `RecordCheckpointLossForFaultedDrive`: any watch fault on an MFT-backed drive asks the live
      journal about that drive's current block cursor, which is both what `BuildWatchTarget` armed
      the watch from and what every applied batch has advanced it to. Running the check on every
      fault rather than on a classified subset is deliberate,
      because no journal error code reaches managed code: `ApplyUsnReadError` in
      `MFTLibNative/usn/usn_journal.cpp` turns native failures into English strings in
      the result's fixed `errorMessage` buffer. The journal is the classifier on both
      sides: `JournalBrokerHost.DescribeWatchFailure` calls `JournalCheckpointCheck.Check`
      for a failed nonzero cached-cursor startup, adding rescan wording only when the
      live journal proves that cursor is lost. A retained cursor or an unavailable
      query keeps the original message. Sentinel starts and failures after a batch
      keep their original messages without this broker query. The index independently
      checks its current block cursor on every watch fault, including mid-session
      failures, and records nothing new when that position is still retained or the
      journal cannot answer. Neither path classifies exception wording. The journal read runs
      outside `_stateLock` and the loss is recorded only while the block whose cursor it describes
      is still the drive's block, so it cannot resurrect a report a concurrent rescan cleared.
      A source stream that ends without a stop classifies every drive it was watching the same way.
      `JournalCheckpointLoss.DetectedDuring` (`JournalCheckpointLossDetection.DriveOpening` or
      `.LiveWatch`) records which check produced a report, because a report outlives the moment
      that produced it: a drive that cold-scanned at open carries its report for the whole
      session, so without the label a `WatchFaulted` handler reading a non-null `CheckpointLoss`
      would blame the journal for an unrelated fault (PR 227 review finding). A watch fault that
      finds the position still in the journal leaves any existing report untouched rather than
      clearing it. Do not "fix" that by clearing: the open's report explains the block still in
      place and an unrelated fault does not falsify it, and consumers build their
      grow-the-journal hint from exactly that report (git-wizard's `JournalWarning` via
      `IndexVolumeChangeSource.Journals.cs`, file-wizard's `JournalSettingsPresenter`), so
      clearing it would make their hint vanish on an unrelated error. A `LiveWatch` loss replaces
      a `DriveOpening` one as the newer fact about the same drive; a rescan clears either.
      `FileIndexOptions.InitialOpenCacheOnly` changes what an unresumable checkpoint does at open:
      a cache-only open never watches, and the block is still a correct snapshot as of its age, so
      `RejectUnresumableCheckpoint` adopts it instead of failing the drive - `DriveStatus.State`
      reads `Ready` and `BlockSource.WarmStartedFromCache`, with `CheckpointLoss` still set so a
      consumer sees why the checkpoint could not be resumed. The drive's ordinal is recorded in
      `FileIndex._cacheOnlyUnresumableCheckpointOrdinals` so a later `StartWatchingAsync` leaves it
      out of the watch rather than arming a cursor the journal no longer holds: `BuildWatchTargets`
      splits it out and `RecordUnresumableCheckpointWatchFailureLocked` reports it the same way any
      other watch failure is, through `DriveStatus.WatchFailureMessage` and
      `WatchCatchUpState.Faulted`, pointing at `FileIndex.RescanAsync`. A successful rescan writes a
      fresh cursor, clears the ordinal alongside `CheckpointLoss`, and arms the drive onto whatever
      watch session is running by the time the scan finishes - `ResumeDriveAfterRescanAsync` reads
      `_watchSession` fresh at resume time rather than trusting the session captured before the scan
      ran, because a session can start (excluding this drive, since its block has not swapped yet)
      while the scan is still in flight; without the fresh read the drive would join no session at
      all even though one now exists (PR 230 review finding 2). A rescan whose scan fails without
      throwing (`ProduceRescannedBlockAsync` returns null) leaves the old, still-unresumable block in
      place; `ResumeDriveAfterRescanAsync` checks `_cacheOnlyUnresumableCheckpointOrdinals` (through
      `LeavesDriveFaultedLocked`) before registering or arming anything, so a failed scan leaves the drive's refusal exactly as it was
      instead of arming a cursor the journal still cannot resume (PR 230 review finding 1). The
      whole cache-only adoption behavior traces to file-wizard#481.
      A rescan of a drive that was already watch-faulted requires a committed replacement
      block before it can recover that watch. The swap/adoption step reports an explicit
      internal replacement outcome; null production, a thrown swap, and cancellation before
      replacement leave the prior block, WatchFailureMessage, faulted catch-up, outstanding
      watch fault, and CheckpointLoss intact. Non-cancellation producer failures remain
      visible through MftProducerFailureMessage. Such a failure neither arms the old cursor
      nor restarts a session on that drive's behalf. A previously healthy drive may restore
      its old watch after a failed rescan, but only if it is still healthy when resume decides:
      a watch failure recorded while the producer ran (an item the pump accepted before the
      disarm and applied or failed afterwards, or a stream that ended without a stop, which
      faults every target including the disarmed drive) counts the same as one present at
      entry. Resume re-checks `RequiresReplacementForWatchRecoveryLocked` under `_stateLock`
      at each step that would clear the failure, re-arm, reclaim the ended session, or restart
      (`LeavesDriveFaultedLocked`), so a failed rescan never erases that newer fault or arms the
      cursor it condemns. Suspension remembers an ended session without
      reclaiming it, preserving its faults and restart intent until an eligible recovery
      or an explicit stop. Successful replacement retains per-drive re-arm and last-drive
      restart behavior, and healthy siblings are never stopped for the failed attempt.
      A rescan also survives the watch session ending while it is in flight (MFTLib issue 241). When
      the last watched drive faults, the pump marks the session `Ended` under `_stateLock` before it
      releases the source stream, and it makes that decision under the same lock a rescan's resume
      holds while it clears its drive's failure and registers it again, so a re-arm either keeps the
      session alive or finds it ended. The resume re-arms on the current session only while it is
      not ended, its pump has not completed, and its cancellation is not requested. Otherwise, or when
      the arm fails and the session is found ended by then, or when the rescan's disarm fails on an
      already-ended session, the rescan reclaims the session and starts a fresh one instead of arming
      a released stream. A session ended through cancellation is reclaimed but not restarted, and a
      running session is never stopped for this. That rescan-triggered restart clears the watch
      failure and faulted catch-up of the rescanned drive only. Every other drive keeps its recorded
      failure, faulted catch-up, and `CheckpointLoss`, and any drive with a recorded failure is left
      out of the new session's targets, which is what keeps a drive whose `LiveWatch` loss says its
      cursor is gone from being armed from that cursor; each such drive needs its own rescan. When
      an eligible rescan recovery reclaims the ended session it moves that session's outstanding faults, other than
      the rescanned drive's, into the index-level `_unreportedWatchFaults` ledger rather than into
      any session, so no later session can drop them by ending cleanly and releasing itself, and a
      restart that starts no session or a session ended by cancellation loses nothing either. The
      next `StopWatchingAsync` rethrows the earliest of them ahead of its own session's faults, even
      when no session is left, then clears the ledger; a rescan that recovers one of those drives
      removes its entry. The pump's own `HasFaults` never sees them. A source that
      releases its stream does so in its iterator's `finally`, before the pump can mark the session
      ended, so a disarm or arm rejected in that window with `WatchStreamNotRunningException` (the
      type `IIndexWatchSource` sources throw when no stream runs) on a session whose stream already
      delivered an item makes the rescan wait for the pump, bounded by its token, before classifying
      the failure. A rejection before the first item cannot be told apart from a stream not yet
      started, so it is judged on the session state as it stands. Public
      `StartWatchingAsync` keeps clearing every armed drive's failure and leaves the retained
      faults for the next stop.
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
    - **Watch start readiness**: `FileIndex.StartWatchingAsync` completes only once the session's
      source reports that its stream accepts per-drive arm and disarm, so a `RescanAsync` issued any
      time after it returns finds a running stream (MFTLib issue 247). The index always starts a
      source through `IIndexWatchSource.StartWatching(targets, reportStreamReady, cancellationToken)`;
      readiness belongs to one `WatchSession` (`WatchSession.Ready`), so no other session's source
      can satisfy it, and the pump settles it before it finishes, so no waiter is stranded. A first
      item also counts as ready. Readiness is not catch-up: `WaitForCatchUpAsync` stays the separate
      backlog wait. `BrokerIndexWatchSource` reports readiness once connected, the StartWatch frame is
      sent, and its stream is published with every initial reader running; its internal
      `BeforeStreamPublishedForTest` gate lets a test hold the window between the frame and the
      publish. A source implementing only the two-argument `StartWatching` gets the default
      interface member, which reports readiness once the stream's first `MoveNextAsync` call has
      returned control with the stream still running, pending or having produced an item
      (`ReadyOnFirstMoveWatchStream`); a first call that already ended the stream or faulted reports
      nothing, so the start fails with it. That closes the interval before the pump invokes the
      source for every source, and covers a source that goes live before its first incomplete
      await, but not one that awaits a connection first: such a source's failure or end after that
      await faults the running session rather than the start. A stream that throws or ends
      before it is ready fails the start with that exception after the usual `WatchFaulted` source
      announcement; cancelling the start's token, or a stop or dispose, before readiness cancels it.
      Either way the start cancels and releases the unready session once its pump finishes, so the
      fault is reported by the start rather than by a later `StopWatchingAsync` and a fresh start is
      accepted. The waits run outside `_stateLock` and `_swapGate`; zero-target starts complete
      without invoking the source. A rescan that meets a session still starting waits for it before
      disarming or arming (a rescan cancelled during that wait records nothing against its drive),
      and leaves a session whose start failed or was cancelled to that start.
    - **Watch and catch-up lifetime**: `FileIndex.StartWatchingAsync` arms each MFT-backed drive and
      transitions its `DriveStatus.WatchCatchUp` to `WatchCatchUpState.CatchingUp`. Backlog batches up to
      the journal tip captured at arm time are applied to the block before an epoch-tagged `CaughtUp`
      marker flips the drive to `WatchCatchUpState.CaughtUp`. `FileIndex.WaitForCatchUpAsync(char driveLetter, CancellationToken)`
      waits for a single drive's initial catch-up: completes immediately if already caught up, faults if the
      drive's watch faults (before or after the call), and cancels if superseded by a rescan re-arm, session
      cancellation, or index disposal. The all-drives overload `FileIndex.WaitForCatchUpAsync(CancellationToken)`
      completes when the slowest drive catches up and faults immediately on the first drive watch failure or
      cancellation. Like scan queries, catch-up waits are linked to the index's disposal token so disposing
      the index cancels pending waits rather than waiting them out; session cancellation or unhandled source
      exceptions fault or cancel pending waiters rather than stranding them.
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
    - **VolumeBroker**: `JournalBrokerHost`/`JournalBrokerClient` run elevated MFT scans and USN journal watches through one elevated child process over a named pipe (control/journal frames) - one UAC prompt per consumer session. `JournalBrokerClient` supports `QueryVolumesAsync`, `ArmScanAndCatchUpAsync`, and `GrowUsnJournalAsync` (a grow-only USN journal resize whose unelevated sizing query is `FileIndex.QueryUsnJournalSettings`, and whose reason to be offered is `DriveStatus.CheckpointLoss`) while other drives remain live-watched; `FileIndex.RescanAsync` uses this path to disarm one drive, rebuild and swap its block, then re-arm only that drive. The cold scan is written straight into a client-created file-backed block section. `BrokerMftBlockProducer` supplies the index producer delegate, validates finished blocks, and transfers their ownership to the caller; its connection factory retains client ownership. `BrokerMftBlockProducer.CreateWatchSource()` returns a `BrokerIndexWatchSource` that borrows the same connection factory to bridge the broker's per-drive live-watch enumerables onto `FileIndex`'s merged watch stream. `ElevatedEntryPoint`/`BrokerLauncher` dispatch and launch the `--broker` child mode; `BrokerDiagnostics` provides opt-in frame tracing; while tracing is on, the broker drops journal entries for both diagnostics log files (its own and the client's, forwarded as `--diag-log`) before building JournalBatch frames - matched by file reference number, so a renamed log stays filtered - and skips batches the filter empties, so the log's own writes cannot feed the watch stream; `MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` (forwarded as `--diag-include-self`) opts back in for debugging the diagnostics themselves.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Automatically self-elevates.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# MSTest) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing test harness (`ScanSessionTestHarness`) over MFTLib's internal `JournalBrokerScanSession` construction seams, so consumer test assemblies can build a session over a fake client without MFTLib friend-listing them. Ships as the separate `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.

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
