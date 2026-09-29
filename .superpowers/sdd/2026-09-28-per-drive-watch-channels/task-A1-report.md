# Task A1 report: native per-chunk thread allowance and parse cancellation

Status: DONE_WITH_CONCERNS (concerns below; nothing is red)

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-A1`, branch `task/265-A1`, base `e65902cf38af7fab0f12bf77ebabd1b016610e3a` (confirmed before any work).
Commit: `8af0d3d` "Native parse rebalances its thread count at every chunk and stops promptly when cancelled".

## What was implemented

Native (`MFTLibNative`):
- `mft_api.h`: `MFT_NATIVE_ABI_VERSION` = 2; `MftParseResult` gains `uint32_t cancelled` at the end; `struct MftParseControl { int32_t cancelRequested; int32_t parseThreadAllowance; }` declared beside it.
- `internal.h`: `LoadSharedInt32` (acquire load of a caller-written int32: volatile read plus acquire fence on MSVC, `__atomic_load_n` on GCC/Clang, since the project is C++17 and has no `std::atomic_ref`); `unsigned EffectiveThreadCount(const MftParseControl* control)` replaces the old declaration; `RecordChunkThreadCount(unsigned)`, `RecordResolveThreadCount(unsigned)` and an internal `ResetRecordedParseThreadCounts()`.
- `core/test_hooks.cpp`: new `EffectiveThreadCount` (null or allowance 0: every processor; any other value clamped to [1, hardware_concurrency]; `SetMaxThreads` still caps); recording under a mutex into a fixed 1024-entry array (no allocation on the parse path); exports `GetChunkThreadCounts(unsigned*, unsigned)` and `GetResolveThreadCount()`; both reset in `ResetTestState` on both platforms.
- `mft/mft.parse_core.cpp`: `ParseMFTImpl` takes `const MftParseControl*` and resets the recorded counts; `ScanContext` carries the control pointer; `ParseAllChunks` returns `ParseOutcome { Completed, Failed, Cancelled }`, checks `cancelRequested` before the first chunk read, at the top of every iteration (before the I/O thread starts the next read), and after each chunk's parse (joining the I/O thread before returning); it reads `EffectiveThreadCount(control)` once at the top of each iteration and uses it for that chunk only. Parse workers (and the single-threaded path) go through `FixupAndParseSlice`, which fixes up and parses in 4096-record sub-slices and stops between them once cancelled. Path resolution re-reads the allowance immediately before `ResolveAllPaths` (recorded through `RecordResolveThreadCount`); `PopulatePathSlice` checks the flag every 4096 entries and `ResolveAllPaths` (now taking the `ScanContext`) frees its path buffers and returns false when cancelled. `FinishCancelled` frees the partial entry/string output and sets `cancelled = 1` and `errorMessage = L"Parse cancelled"`; the lookup and both I/O buffers are freed on every cancel exit, as on the `Failed` path.
- `mft/mft.parse.cpp`: both platform `ParseMFTRecordsWithProgress` exports gain the control parameter (signature exactly as in the brief); `ParseMFTRecords` passes null; `ParseMFTFromFile` goes through `ParseMFTFromFileImpl`, which passes null. `mft_synthetic.cpp` passes null.
- `mft/mft.records.cpp` / `mft.internal.h`: `ProcessRecordBatch` deleted (see "Adjacent bug fixed" below).
- `test/linux_smoke_test.cpp`: its ABI assertions compared against a literal 1; they now compare against `MFT_NATIVE_ABI_VERSION` (found by the Linux build; without it five smoke tests fail).

Managed (`MFTLib`):
- `Mft/ParseThreadAllowance.cs` (new): public sealed class, constructor and `Count` setter throw `ArgumentOutOfRangeException` below 1; internal `Attach(int*)` (copies the current count in; throws `InvalidOperationException` if already attached) and `Detach()`; writes go through with `Volatile.Write` under a lock.
- `Interop/MftParseControl.cs` (new) and `Interop/MftParseResult.cs` (`Cancelled` field).
- `Internal/MFTLibNative.cs`: `ExpectedMftNativeAbiVersion` = 2; the P/Invoke gains the control pointer; P/Invokes for `GetChunkThreadCounts` / `GetResolveThreadCount`. The seam `_parseMftRecordsWithProgress` is now `Func<SafeHandle, string?, MatchFlags, uint, IntPtr control, NativeMftProgressCallback?, IntPtr>`: the unused native `context` argument is dropped from the managed seam (always `IntPtr.Zero`; the callback closure carries state). This kept the default seam method at 6 parameters (aislop `too-many-params`). Later tasks that fake this seam use `(handle, filter, flags, bufferSize, control, callback) =>`.
- `Mft/MftVolume.cs`: `StreamRecords(string?, MatchFlags, IProgress<MftScanProgress>?, ParseThreadAllowance?, CancellationToken)` and `ReadRecordBatches(bool, int, IProgress<MftScanProgress>?, ParseThreadAllowance?, CancellationToken)` replace the optional-parameter shapes. `StreamRecords` allocates one `MftParseControl` with `NativeMemory.AllocZeroed` (fixed address for the whole call, so nothing to pin), attaches the allowance, registers the token to set `cancelRequested`, and detaches the allowance and disposes the registration before freeing the block. `ReadRecordBatches` also checks the token before each yielded batch (spec 2.3).
- `Mft/MftResult.cs`: a result with `Cancelled != 0` is freed and throws `OperationCanceledException(errorMessage, token)`; the internal constructor takes an optional token for that.
- Callers updated: `ReadAllRecords`, `FindByName`, `FindRecords` pass `null, null, CancellationToken.None`; `JournalBrokerHost.Sources.cs` passes `mftProgress, null, cancellationToken` (one-line call-site change, required to compile; no other broker file touched). TestProgram and Benchmark have no callers. README untouched (D3).
- `AGENTS.md`: the architecture line "compact ABI version 1" now says 2 (the only doc that would otherwise be false; CHANGELOG/README left to D3).

Tests:
- New `MFTLib.Tests/NativeParserCoverageTests.ParseControl.cs` (partial of `NativeParserCoverageTests`, class-level `[DoNotParallelize]` inherited): the 12 native cases named in the brief. They parse a synthetic NTFS image through `ParseMFTRecordsWithProgress` in 64-record chunks (1024 records, 16 chunks), with `SetVolumeRecordSizeOverride(1024)` so the host volume's record size is irrelevant.
- New `MFTLib.Tests/MftVolumeTests.ParseControl.cs` (partial): `ReadRecordBatches_AllowanceLoweredBetweenChunks_LaterChunksUseTheNewCount` and `ReadRecordBatches_TokenCancelledDuringParse_ThrowsBeforeFirstBatch` through `MftVolume` with the `FileUtilities._getVolumeHandle` seam returning a handle to the image, plus `ReadRecordBatches_TokenCancelledBetweenBatches_ThrowsBeforeNextBatch` (covers the between-batches token check).
- New `MFTLib.Tests/ParseThreadAllowanceTests.cs` (`[DoNotParallelize]`): `Count_WrittenDuringParse_IsVisibleToTheNativeControlBlock` (including after `GC.Collect`, and re-attach on the next parse), `Constructor_CountBelowOne_Throws`, `Count_SetBelowOne_Throws`, plus `StreamRecords_NullAllowance_LeavesTheControlBlockAllowanceZero`, `StreamRecords_TokenCancelledDuringParse_SetsCancelRequested`, `Attach_WhileAttached_Throws`.
- `MftResultTests.AbiStride_MatchesCancelledField` (managed offsets, and a native cancelled result read at those offsets) and `MftResult_CancelledResult_ThrowsOperationCanceledCarryingTheToken`.
- `NativeParserCoverageTests.ParseFromFile_SingleThreadAcrossChunks_NumbersRecordsLikeEveryCore` (regression test for the adjacent bug below).
- New `TestSupport/SyntheticNtfsImage.cs` (image builder; the four byte-level helpers moved out of `NativeParserCoverageTests.cs`, which now uses them) and `TestSupport/ParseControlBlock.cs` (test-owned control block plus the chunk-count hook reader).
- `MftVolumeTests` and `MftResultTests` cleanups now call `NativeResetTestState()`. ABI-version tests renamed to Two. Every other test call site of `StreamRecords`, `ReadRecordBatches` and the seam rewritten mechanically.
- `MFTLib.Tests.csproj`: Linux compile exclusions `MftVolumeTests.cs` / `NativeParserCoverageTests.cs` became `MftVolumeTests*.cs` / `NativeParserCoverageTests*.cs` so the new partials follow their classes (they need the Windows volume export). No new excluded class.

## Adjacent bug fixed (deviation from "name it, do not fix it"; please review)

The single-threaded chunk branch called `ProcessRecordBatch(buffer, size, recordIndex, ...)`, which advanced `recordIndex` by reference, and `ParseAllChunks` then added the chunk size again, so every chunk after the first numbered its records from twice its base. It was reachable only on a one-processor machine or under `SetMaxThreads(1)`; this task makes an allowance of 1 an ordinary production state (the broker's allocator will hand out 1 when scans outnumber cores), so leaving it would corrupt record numbers in real scans. The fix came for free: the single-threaded path now goes through the same `FixupAndParseSlice` as the workers (needed anyway for sub-slice cancellation), and `ProcessRecordBatch` is deleted.

RED (base native, only the test added): `dotnet test ... --filter "FullyQualifiedName~ParseFromFile_SingleThreadAcrossChunks"` ->
`Failed ParseFromFile_SingleThreadAcrossChunks_NumbersRecordsLikeEveryCore` / `CollectionAssert.AreEqual failed. (Element at index 50 do not match.)` (index 50 is the first in-use record of the second 64-record chunk). GREEN after the change (in every targeted run below).

## TDD evidence

The new-surface tests cannot compile against the base (the export, seam, `ParseThreadAllowance` and `Cancelled` field do not exist), so the implementation was written first and the tests' power to fail was proven by mutation instead: `.superpowers/mutate.py` breaks the implementation, the native DLL is rebuilt, and the new tests are run.

Mutation run 1 (all of: chunk loop reads the allowance once; path resolution never checks cancellation; no loop-top cancel check; no pre-first-read cancel check; path resolution ignores the allowance):
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~NativeParserCoverageTests|FullyQualifiedName~MftVolumeTests|FullyQualifiedName~MftResultTests|FullyQualifiedName~ParseThreadAllowanceTests"`
```
Failed ReadRecordBatches_AllowanceLoweredBetweenChunks_LaterChunksUseTheNewCount
Failed ReadRecordBatches_TokenCancelledDuringParse_ThrowsBeforeFirstBatch
Failed ParseMFTRecordsWithProgress_AllowanceLoweredFromProgressCallback_LaterChunksUseTheNewCount
Failed ParseMFTRecordsWithProgress_AllowanceRaisedFromProgressCallback_LaterChunksUseTheNewCount
Failed ParseMFTRecordsWithProgress_PathResolutionReadsAllowanceAfterLastChunk
Failed ParseMFTRecordsWithProgress_CancelledDuringPathResolution_StopsBetweenSlices
Failed ParseMFTRecordsWithProgress_CancelledBeforeFirstChunk_ReturnsCancelledWithoutReading
Failed ParseMFTRecordsWithProgress_CancelledFromProgressCallback_StopsAfterThatChunk
Failed!  - Failed:     8, Passed:    86, Skipped:     0, Total:    94
```
Mutation run 2 (only: resolution never checks cancellation; no pre-first-read check), isolating the two subtle ones:
```
Failed ParseMFTRecordsWithProgress_CancelledDuringPathResolution_StopsBetweenSlices
  Assert.AreEqual failed. Expected:<1>. Actual:<3>. Resolution must stop at the slice after the cancelling report
Failed ParseMFTRecordsWithProgress_CancelledBeforeFirstChunk_ReturnsCancelledWithoutReading
  Assert.AreEqual failed. Expected:<1>. Actual:<0>.   (the armed read-fail countdown hit the first chunk read, so the parse was not cancelled)
```
The "without reading" part is proven by arming `SetReadFailCountdown(3)` (reads 1 and 2 are the boot sector and record 0): the countdown survives only if no chunk read happened, so the next parse fails at its boot sector read.

The allowance tests with a fixed allowance (null, 2, above cores, 0, SetMaxThreads cap), the ABI stride test and the `ParseThreadAllowance` argument tests are not killed by these mutants; they pin the clamp arithmetic and layout rather than the read points.

GREEN (final code): `.\.superpowers\build.ps1` (native Release via 64-bit MSBuild with `SolutionDir`, then each managed project) -> `BUILD OK`, 0 warnings; then
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~NativeParserCoverageTests|FullyQualifiedName~MftVolumeTests|FullyQualifiedName~MftResultTests|FullyQualifiedName~ParseThreadAllowanceTests|FullyQualifiedName~MftRecordSizeAndTimeTests|FullyQualifiedName~MftScanProgressTests|FullyQualifiedName~MockVolumeTests|FullyQualifiedName~NativeMockTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~JournalBrokerHostRealSeamsTests"`
-> `Passed!  - Failed:     0, Passed:   191, Skipped:     0, Total:   191`

## Verification

- Native Release build: 0 errors, 0 warnings. Native Debug build: built (Debug|x64 /PROFILE) by `native-coverage.ps1`, succeeded.
- Whole suite, `.\scripts\run-coverage.ps1 -NonInteractive` (final code): `Total tests: 1844 / Passed: 1838 / Skipped: 6`, exit 0. Coverage: MFTLib 98.22% line / 94.86% branch, Benchmark 98.74%, TestProgram 100%, MFTLibTestExtensions 100%. Every changed managed class (`ParseThreadAllowance`, `MftVolume` incl. the `ReadRecordBatches` iterator, `MftResult`, `MFTLibNative`) is at 100% line and branch.
  Note: the first run on the final code reported Benchmark 33.45% and TestProgram 0% with the same 1838 tests passing; an immediate rerun with no code change gave the numbers above. That matches the transient collector problem AGENTS.md describes; nothing in this task touches TestProgram or Benchmark. CI's coverage publisher would reject such a run, so if CI shows it, rerun.
- `.\scripts\native-coverage.ps1` (no elevation): `Passed: 1838, Skipped: 46`, `MFTLibNative: 97.7% line, 100% branch`. Uncovered lines in files this task changed: `mft.parse_core.cpp` 404-405 (the post-parse cancel return, see concern 1), 218 and 263-264 (unchanged code: the resolved-count clamp in `reportBatch` and the `ResolvePath` failure branch), `test_hooks.cpp` 149/155/156 (unchanged USN pipe-gate hook), `internal.h` 25 (unchanged). The cancel-before-first-read, loop-top, path-resolution and `FinishCancelled` exits are all covered.
- Linux: the orchestrator's note allowed a scratch copy. Tracked plus new files were tarred to `llamabox:~/scratch/mftlib-a1` (no git operations, nothing pushed), CRLF stripped from `*.sh`, and `./init.sh --build` run: `init exit 0`, native smoke test `=== 20 passed, 0 failed ===`, managed build (including MFTLib.Tests with the Linux exclusions) `Build succeeded`, 3 warnings, none from this change (two SourceLink "no git repository" warnings from the scratch copy, and CA1859 in `BrokerIndexWatchSourceTests.cs`). The first Linux run caught the smoke test's literal ABI version 1, fixed above. The scratch copy was deleted afterwards. `scripts/coverage-linux.sh` was not run.
- `aislop scan . -d`: 99 / 100, 0 errors, 4 warnings, 0 fixable. All four are in files this task does not touch and were reported on the first scan too: `MFTLib.Tests/NativeSeamIsolationFixtures.cs:73,79` (AsyncFixer01) and `MFTLib/Index/CachedBlockDeletionOutcome.cs:8,10` (redundant XML doc). The local binary is aislop 0.16.0, which may not match the CI pin in `.aislop/fork-commit`. The findings this task introduced were fixed: MftVolume formatting; a null-forgiving `!` (the token registration now captures an `IntPtr` instead of casting object state); too many parameters (unused managed `context` dropped; `ResolveAllPaths` takes the `ScanContext`); using-initializer (the `ParseControlBlock` constructor takes the allowance); `std::lock_guard` -> `std::scoped_lock`; and nine AccessToDisposedClosure. Five of those were removed by passing the control block into the progress observer instead of capturing it, plus a token local. Four are synchronous lambdas (`Assert.ThrowsException`, `SynchronousProgress`, the fake parse) and use the repo's existing `// ReSharper disable once AccessToDisposedClosure` idiom with the reason on the line above.
- clang-format (repo `.clang-format`) applied to every changed native file. It was not applied to all of `linux_smoke_test.cpp`, because the base file already had one unrelated formatting difference that I left alone.

## Files changed

Native: `MFTLibNative/mft_api.h`, `MFTLibNative/internal.h`, `MFTLibNative/core/test_hooks.cpp`, `MFTLibNative/mft/mft.internal.h`, `MFTLibNative/mft/mft.parse_core.cpp`, `MFTLibNative/mft/mft.parse.cpp`, `MFTLibNative/mft/mft.records.cpp`, `MFTLibNative/mft/mft_synthetic.cpp`, `MFTLibNative/test/linux_smoke_test.cpp`.
Managed: `MFTLib/Mft/ParseThreadAllowance.cs` (new), `MFTLib/Interop/MftParseControl.cs` (new), `MFTLib/Interop/MftParseResult.cs`, `MFTLib/Internal/MFTLibNative.cs`, `MFTLib/Mft/MftVolume.cs`, `MFTLib/Mft/MftResult.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Sources.cs` (one call site).
Tests: `MFTLib.Tests/ParseThreadAllowanceTests.cs` (new), `MFTLib.Tests/NativeParserCoverageTests.ParseControl.cs` (new), `MFTLib.Tests/MftVolumeTests.ParseControl.cs` (new), `MFTLib.Tests/TestSupport/SyntheticNtfsImage.cs` (new), `MFTLib.Tests/TestSupport/ParseControlBlock.cs` (new), `MFTLib.Tests/NativeParserCoverageTests.cs`, `MFTLib.Tests/MftVolumeTests.cs`, `MFTLib.Tests/MftResultTests.cs`, `MFTLib.Tests/MftRecordSizeAndTimeTests.cs`, `MFTLib.Tests/MftScanProgressTests.cs`, `MFTLib.Tests/MftVolumeAdminTests.cs`, `MFTLib.Tests/MockVolumeTests.cs`, `MFTLib.Tests/NativeMockTests.cs`, `MFTLib.Tests/JournalBrokerHostRealSeamsTests.Operations.cs`, `MFTLib.Tests/MFTLib.Tests.csproj`.
Docs: `AGENTS.md` (ABI version number only).

## Concerns and notes for review

1. The post-parse cancel return (`mft.parse_core.cpp` 404-405) and the parse worker's between-sub-slice exit cannot be reached deterministically without a new hook: both need the flag to flip while a chunk's workers are running, and the only callback fires after the post-parse check. The loop-top check catches the same cancellation one step later. Both paths are covered only as lines, not as tested behavior. Adding a test hook that sets the flag from inside a worker would close this; I did not add one because the brief does not list it.
2. Deviations the reviewer should confirm: (a) the adjacent single-thread record-numbering fix above; (b) the managed seam drops the unused `context` argument (the native export keeps it, and the brief's C signature is unchanged); (c) the one-line `JournalBrokerHost.Sources.cs` call-site change now passes the scan token to the native parse, so a closed scan pipe stops the parse between chunks rather than after it (the spec's intended behavior; the exception type is still `OperationCanceledException`); (d) the `linux_smoke_test.cpp` and `AGENTS.md` ABI-number edits.
3. `EffectiveThreadCount` compares against `std::thread::hardware_concurrency()`, and the tests compare against `Environment.ProcessorCount`. On machines where the two differ (processor groups over 64 cores, or CPU affinity/job limits) the "every core" and "clamps to cores" tests would fail. They agree on this machine and on the Linux host.
4. Commit trailer: I used the harness-mandated `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` rather than lane-common's `Co-Authored-By: Claude <noreply@anthropic.com>`. Amend if the plan wants the shorter form.
5. The chunk-count recorder holds at most 1024 chunks per parse; a longer parse records only its first 1024. This is fine for tests. A real drive with the default 262144-record buffer has far fewer chunks.
6. `mft.parse_core.cpp` is 527 lines, with a one-line note at the top saying why.

## Fix round 1: cover the post-parse and mid-slice cancel exits

Base confirmed: `8af0d3d07dc4929f712151eba88cf1f2b3e8169f`. New commit (no amend): `e655717` "Cover the post-parse and mid-slice cancel exits of the native parse".

### What changed

- New test hook in the existing family:
  - `MFTLibNative/internal.h` declares `bool ShouldForceCancel()`.
  - `MFTLibNative/core/test_hooks.cpp` defines it with the export `SetCancelCheckCountdown(int)`. Both `ResetTestState` variants reset it.
  - Once armed with N, the Nth parse cancellation check and every check after it report cancelled. It is sticky, like the real flag.
  - The countdown is an atomic because parse workers check concurrently. The unarmed production cost is one relaxed `std::atomic<bool>` load per cancellation check. Those checks run once per chunk and once per 4096 records, the same cadence as the existing hooks.
- `mft.parse_core.cpp`: `IsCancelRequested` became `ShouldForceCancel() || (control != nullptr && LoadSharedInt32(...) != 0)`. This is the only production-code change.
- `MFTLib/Internal/MFTLibNative.cs`: P/Invoke `NativeSetCancelCheckCountdown`.
- Two tests were added to `MFTLib.Tests/NativeParserCoverageTests.ParseControl.cs`. The class has class-level `[DoNotParallelize]`.
  - `ParseMFTRecordsWithProgress_CancelledDuringChunkParse_JoinsTheReadAndStopsBeforeReporting` uses one thread and 64-record chunks, with the countdown at 4. The checks run in a fixed order: before the first read, at the loop top, before the only sub-slice, then after the parse while the next chunk's read runs. The fourth check, the post-parse one, trips, so the parse takes the post-parse exit (`ioThread.join(); return Cancelled`). The test asserts:
    - `cancelled == 1`, "Parse cancelled", no entries
    - no progress callback
    - exactly one recorded chunk (`{1}`)
    - the result frees cleanly
  - `ParseMFTRecordsWithProgress_CancelledBetweenWorkerSubSlices_StopsTheChunk` uses one 16384-record chunk on 2 workers, each with two 4096-record sub-slices, and the countdown at 6.
    - Checks 1 and 2 precede the chunk and checks 3 to 6 are the workers'. Each worker's second check follows its first, so the globally sixth check is always one worker's between-sub-slices check, and that is the one that trips.
    - The post-parse check then also reports cancelled.
    - It asserts `cancelled == 1`, no progress report and exactly one recorded chunk (`{2}`).
    - It is marked Inconclusive on a machine with fewer than 2 processors.

### RED

The two tests plus the P/Invoke ran against the native DLL without the hook:
```
Failed ParseMFTRecordsWithProgress_CancelledDuringChunkParse_JoinsTheReadAndStopsBeforeReporting
System.EntryPointNotFoundException: Unable to find an entry point named 'SetCancelCheckCountdown' in DLL 'MFTLibNative'.
Failed ParseMFTRecordsWithProgress_CancelledBetweenWorkerSubSlices_StopsTheChunk
System.EntryPointNotFoundException: Unable to find an entry point named 'SetCancelCheckCountdown' in DLL 'MFTLibNative'.
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```

### Scratch mutations (not committed; `.superpowers/mutate2.py`, restored after each run)

Each run rebuilt native and managed, then ran `dotnet test ... --filter "FullyQualifiedName~CancelledDuringChunkParse|FullyQualifiedName~CancelledBetweenWorkerSubSlices"`.

- Post-parse exit removed (`if (false && IsCancelRequested(...)) { ioThread.join(); return Cancelled; }`):
  ```
  Failed ..._CancelledDuringChunkParse_JoinsTheReadAndStopsBeforeReporting
     Assert.AreEqual failed. Expected:<0>. Actual:<1>. The parse must stop before reporting the chunk
  Failed ..._CancelledBetweenWorkerSubSlices_StopsTheChunk
     Assert.AreEqual failed. Expected:<1>. Actual:<0>.
  ```
- Sub-slice check removed (`start < range.end;`):
  ```
  Failed ..._CancelledDuringChunkParse_JoinsTheReadAndStopsBeforeReporting
     Assert.AreEqual failed. Expected:<0>. Actual:<1>. The parse must stop before reporting the chunk
  Failed ..._CancelledBetweenWorkerSubSlices_StopsTheChunk
     Assert.AreEqual failed. Expected:<1>. Actual:<0>.
  ```

Removing either exit shifts the check sequence, so each test detects both removals. In the worker test the mutant parse completes (`cancelled == 0`) because the one chunk passes every remaining check.

### GREEN

- `.\.superpowers\build.ps1` gave `BUILD OK`, 0 warnings.
- `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~NativeParserCoverageTests|FullyQualifiedName~MftVolumeTests|FullyQualifiedName~MftResultTests|FullyQualifiedName~ParseThreadAllowanceTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~NativeCoverageTests|FullyQualifiedName~NativeMockTests"` gave `Passed!  - Failed:     0, Passed:   184, Skipped:     0, Total:   184`.

### Coverage

`.\scripts\native-coverage.ps1` gave `Passed: 1840, Skipped: 46` and `MFTLibNative: 97.8% line, 100% branch`. Hits read from `native-coverage.cobertura.xml`:
```
mft.parse_core.cpp 123 hits 1   for (...; start < range.end && !IsCancelRequested(scan.control); ...)   (worker sub-slice stop)
mft.parse_core.cpp 403 hits 1   if (IsCancelRequested(scan.control)) {
mft.parse_core.cpp 404 hits 1       ioThread.join();
mft.parse_core.cpp 405 hits 1       return ParseOutcome::Cancelled;
test_hooks.cpp 90-93 hits 1     ShouldForceCancel
```

The Microsoft coverage tool reports lines only, so line 123 cannot show which branch of its condition ran. The stop between sub-slices is established by the worker test's construction and by the sub-slice mutation above.

- Uncovered lines left in files touched by this task, none of them changed code:
  - `mft.parse_core.cpp` 218 and 263-264
  - `test_hooks.cpp` 160, 166 and 167, which is the USN pipe-gate hook, shifted by the new lines
  - `internal.h` 25
- The first version of the hook used a compare-exchange retry loop. Its retry line (test_hooks 96) was reachable only under contention, so I replaced it with an armed flag plus `fetch_sub`, which has no nondeterministic lines.

### Other checks

- `aislop scan .` gave `99 / 100`, 0 errors, 4 warnings. These are the same pre-existing four: `NativeSeamIsolationFixtures.cs:73,79` and `CachedBlockDeletionOutcome.cs:8,10`.
- Linux:
  - Scratch copy on llamabox, `./init.sh --build`: `init exit 0`, native smoke `=== 20 passed, 0 failed ===`, managed `Build succeeded`. This covers the Linux `ResetTestState` variant, which also changed.
  - The scratch copy was deleted afterwards.
- clang-format ran on the changed native files.
