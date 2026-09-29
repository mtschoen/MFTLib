# Task CI report: MFTLib.Index coverage tests

Branch `task/265-CI`, base 2889deb, commit 729863f. Status: DONE.

## Totals (nscov.py, covered/total lines)

| Namespace | Before | After |
|---|---|---|
| MFTLib.Index | 3352 / 3401 (98.56) | 3360 / 3401 (98.79) |
| MFTLib (other) | 2763 / 2779 | 2763 / 2779 |
| MFTLibTestExtensions | 131 / 163 | 131 / 163 |

Suite before: 1928 total, 1922 passed, 6 skipped. After: 1933 total, 1927 passed, 6 skipped, 0 failed
(`run-coverage.ps1 -NonInteractive`). aislop: 99/100, exactly the four baseline warnings plus the ruled
8-parameter `JournalBrokerHost` constructor warning.

## Uncovered lines at the start (49): class and disposition

| Line(s) | Class | Covering test or reason |
|---|---|---|
| FileIndex.WatchPump.cs:34, 41 | a | `FileIndexWatchPumpTests.StopWatchingAsync_HandleDisposalThrows_StopsTheDriveAndAllowsARestart` |
| FileIndex.CatchUp.cs:158, FileIndex.Rescan.cs:211 | a | `FileIndexCatchUpLossTests.Open_CatchUpLostThenTheRetryScanFails_SettlesReadyWithTheLostBlockAndTheFailure` (LossScriptedCache gained `FailProductionNumber`) |
| FileIndex.WatchCatchUp.cs:78 | a | `FileIndexWatchCatchUpTests.WaitForCatchUpAsync_TokenAlreadyCancelled_ReturnsACancelledTaskAndLeavesTheWatch` |
| WatchCatchUpState.cs:64 | a | `FileIndexWatchCatchUpTests.DriveCaughtUpItem_Repeated_LeavesTheDriveCaughtUpWithoutAFault` |
| JournalCheckpointCheck.cs:151, 154 | a | `UsnJournalVolumeInteropTests.ReadLiveJournal_VolumeRootWillNotOpen_ReportsNothing` (unmounted letter, no elevation) |
| BlockFile.Flush.cs:65-76 | b | non-Windows msync path (Linux job covers) |
| CacheDirectory.cs:372-382 | b | non-Windows owner-only directory path (Linux job) |
| CacheDirectory.cs:33, 46, 47 | b | default path computation is forbidden by the one-way module-initializer guard in every test process; 46-47 also need an empty LocalApplicationData, impossible on Windows |
| FileEntry.Open.cs:33, 34 | b | non-Windows PlatformNotSupported branch |
| UsnJournalSettingsQuery.cs:28, 29 | b | non-Windows branch |
| JournalCheckpointCheck.cs:129 | b | live read when the guard is off; the guard is one-way |
| JournalCheckpointCheck.cs:140 | b | non-Windows branch |
| BlockFile.cs:197 | c | `InitializeHeader` only writes fields through a valid mapped pointer and cannot throw after the constructor returned; no input reaches `block.Dispose()` |
| BlockFile.cs:238, 240-248 | c | `BlockFile.Open` IOException/UnauthorizedAccessException from `FileInfo.Length` after `Exists` is true: a delete or ACL race, no seam |
| FileIndex.ScanCleanup.cs:169-177 | c | `Directory.EnumerateFiles` failure while the owner lock file (inside that directory) is held: race, no seam |
| FileIndex.cs:371, 372 | c | `DescribeBlocklessDrive` invariant throw: every option drive is online or blockless |
| FileIndex.Rescan.cs:218 | c | null producer message: `PendingDriveResult` always sets a message with a producer failure |
| FileIndex.Watch.cs:213 | c | closing brace after `ExceptionDispatchInfo.Throw()`, never returns |
| FileIndex.WatchCatchUp.cs:18 | c | every option drive has a runtime created at construction |
| FileIndex.WatchDrive.cs:193 | c | `PrepareStart(restart)` re-check: `RestartRequestedWatchAsync` reads the same state synchronously immediately before, only a disposal race in a synchronous window can differ, no seam (the registration re-check at line 281 has the `RestartBeforeRegistrationForTest` seam and is covered) |
| IndexNavigation.cs:146 | c | after the loop `current == target` is impossible: every iteration returns true on equality after updating `current` |
| JournalMutator.cs:241 | c | `ApplyModification` is reached only when `classification != None`, so `meaningful == None` cannot occur |

Counts: a = 8 lines (now covered), b = 22 lines, c = 19 lines (41 remain). No code was deleted; the
controller decides on the c lines (candidates for deletion: BlockFile.cs:197 branch, FileIndex.cs:371-372,
FileIndex.Rescan.cs:216-218, WatchCatchUp.cs:16-19, IndexNavigation.cs:144-147, JournalMutator.cs:239-242).

## Suspected defects

None. All new tests passed on first run.

## Commands

- `pwsh -NoProfile -File scripts/run-coverage.ps1 -NonInteractive` (before and after)
- `python .../scratch/nscov.py MFTLib.Tests/coverage.xml "MFTLib.Index"`
- targeted: `dotnet test MFTLib.Tests -c Release -p:Platform=x64 --filter "...FileIndexWatchCatchUpTests|FileIndexWatchPumpTests|FileIndexCatchUpLossTests|UsnJournalVolumeInteropTests"`: Passed 60, Failed 0
- `aislop scan .`: 99/100, 5 warnings as above

`git -C C:\Users\mtsch\MFTLib status --short`: empty.

## Fix round 1 (report-only, nothing committed)

Ruling CI-Q1 applied: CacheDirectory.cs:33 and JournalCheckpointCheck.cs:129 are class (b)-guard. The
module-initializer guards are one-way in the test process, and a child test process's hits are not
collected by the in-process coverage run. No child-process test.

Reachable races reclassified from (c) to (b)-race (no existing seam can force the interleaving, and a
new hook would be production code added only to reach a defensive branch):

| Lines | Class | Reason no seam exists |
|---|---|---|
| BlockFile.cs:238, 240-248 | b-race | `FileInfo.Exists` then `Length` (delete or ACL change between the two calls): both are framework calls on a local `FileInfo` inside one synchronous block; no injection point, and an ACL denial of attribute reads cannot be set up deterministically |
| FileIndex.ScanCleanup.cs:169-177 | b-race | `Directory.EnumerateFiles` failing after the owner lock file in that directory is held: needs the directory removed or denied mid-call; no injection point between lock acquisition and enumeration |
| FileIndex.WatchDrive.cs:191-193 | b-race | a stop or disposal clearing `WatchRequested` or the ticket between `RestartRequestedWatchAsync` (RescanRestart.cs:172-178) and `PrepareStart`; that window is fully synchronous (no await), so a seam cannot be placed inside it. `RestartBeforeRegistrationForTest` fires after `PrepareStart` and covers only the later re-check at line 281 |

Updated counts of the 49 starting lines: a = 8 (covered), b = 33 (the earlier 22, plus 6 BlockFile.cs, 4 ScanCleanup, 1 WatchDrive), c = 8 (BlockFile.cs:197, FileIndex.cs:371-372, Rescan.cs:218, Watch.cs:213, WatchCatchUp.cs:18, IndexNavigation.cs:146, JournalMutator.cs:241).

## Fix round 2 (commit "FileIndex keeps its own copy of the configured drives")

- Rescan.cs:218 is class (a): `FileIndexCatchUpLossTests.Rescan_ProducerFailureWithNoMessage_ClearsTheDrivesReportedFailureMessage` (a producer exception whose `Message` is null clears the drive's `MftProducerFailureMessage`). Now covered.
- FileIndex.cs:371-372 (now 373-374) per ruling CI-Q2: production fix, `_options = options with { Drives = [.. options.Drives] }` in the `FileIndex` constructor.
  RED: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FileIndexDriveStatusTests.Drives_CallerListMutatedAfterOpen_LeavesTheIndexUnchanged"` before the fix:
  `Failed Drives_CallerListMutatedAfterOpen_LeavesTheIndexUnchanged` / `System.InvalidOperationException: Drive U is in FileIndexOptions.Drives but has no online or blockless status.` (Failed: 1). GREEN after the fix (Failed 0). The lines are class (c) again: every drive in the private copy is online or blockless.
- Verification: targeted FileIndexDriveStatusTests + FileIndexCatchUpLossTests passed; `run-coverage.ps1 -NonInteractive`: Total 1935, Passed 1929, Failed 0, Skipped 6; nscov MFTLib.Index 3361 / 3401 (98.82%); aislop 99/100, the four baseline warnings plus the ruled JournalBrokerHost warning (an intermediate exception-constructor finding on the test helper was fixed by giving it the three standard constructors and using each).
- Corrected counts of the 49 starting lines: a = 9 (covered), b = 33, c = 7 (BlockFile.cs:197, FileIndex.cs:373-374, Watch.cs:213, WatchCatchUp.cs:18, IndexNavigation.cs:146, JournalMutator.cs:241; 1+2+1+1+1+1 = 7). 40 lines remain uncovered (33 b + 7 c).

## Fix round 3

- FileIndex.cs constructor now builds `_driveRuntimes` from `_options.Drives` (the private copy), so the constructor enumerates the caller's list once. No RED test: `OpenAsync` itself reads `options.Drives` several times before the constructor runs, so a list whose enumeration changes between passes would fail earlier reads first, and which pass the constructor's second read is depends on internal call order; a test pinning it would be brittle and would not isolate this line.
- FileIndexDriveStatusTests: the mutated-list test disposes the index explicitly in a finally, bounded by `DisposeAsync().AsTask().WaitAsync(10 s)`.
- run-coverage.ps1 -NonInteractive: Total 1935, Passed 1929, Failed 0, Skipped 6; FileIndexDriveStatusTests targeted: Passed 6, Failed 0; aislop 99/100, baseline warnings only. Counts unchanged (a 9, b 33, c 7).

## Fix round 4

Base dd4024f, commit 50882db on task/265-CI: "FileIndex enumerates the caller's drive list once, pinned by a test".

### Test

`MFTLib.Tests/Index/FileIndexDriveStatusTests.cs`: `OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration`, with a nested `EnumerableOnceDriveList : IReadOnlyList<IndexedDrive>` whose `GetEnumerator` throws `InvalidOperationException` on its second call. The test configures drives Z (absent root) and T (the owned tree), sets `CacheDirectory` to the owned temporary directory, bounds the open with the test token plus `WaitAsync(30 s)`, and disposes in a `finally` with `DisposeAsync().AsTask().WaitAsync(10 s)`. It asserts the list was enumerated exactly once, `Drives` reports `Z, T` in that order, Z is `Offline` and T is `Ready`. No sleep.

### RED (scratch mutation, uncommitted)

Mutation at `MFTLib/Index/FileIndex.cs:98`: `foreach (var drive in _options.Drives)` changed to `foreach (var drive in options.Drives)` (the pre-fix constructor). Rebuilt with `dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64` (0 warnings, 0 errors).

Command:

    dotnet test C:\Users\mtsch\MFTLib-worktrees\265-CI\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.FileIndexDriveStatusTests.OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration"

Output:

      Failed OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration [21 ms]
      Error Message:
       Test method MFTLib.Tests.Index.FileIndexDriveStatusTests.OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration threw exception:
    System.InvalidOperationException: The caller's drive list was enumerated a second time.
      Stack Trace:
          at MFTLib.Tests.Index.FileIndexDriveStatusTests.EnumerableOnceDriveList.GetEnumerator() in ...\MFTLib.Tests\Index\FileIndexDriveStatusTests.cs:line 221
       at MFTLib.Index.FileIndex..ctor(FileIndexOptions options, String cacheDirectoryPath) in ...\MFTLib\Index\FileIndex.cs:line 98
       at MFTLib.Index.FileIndex.OpenAsync(FileIndexOptions options, CancellationToken cancellationToken) in ...\MFTLib\Index\FileIndex.cs:line 150
       at MFTLib.Tests.Index.FileIndexDriveStatusTests.OpenAsync_CallerDriveListEnumerableOnlyOnce_OpensFromTheFirstEnumeration() in ...\MFTLib.Tests\Index\FileIndexDriveStatusTests.cs:line 243
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 31 ms - MFTLib.Tests.dll (net10.0)

### GREEN

`git -C C:\Users\mtsch\MFTLib-worktrees\265-CI checkout -- MFTLib/Index/FileIndex.cs`, rebuilt the test project (0 warnings, 0 errors), then the same command:

    Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 50 ms - MFTLib.Tests.dll (net10.0)

Class filter `--filter "FullyQualifiedName~FileIndexDriveStatusTests"`:

    Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 101 ms - MFTLib.Tests.dll (net10.0)

### aislop

`aislop scan C:\Users\mtsch\MFTLib-worktrees\265-CI`: 99 / 100, 0 errors, 5 warnings, exactly the gated set: AsyncFixer01 at `MFTLib.Tests/NativeSeamIsolationFixtures.cs:73` and `:79`, redundant XML-doc summary at `MFTLib/Index/CachedBlockDeletionOutcome.cs:8` and `:10`, too-many-params at `MFTLib/Broker/Host/JournalBrokerHost.cs:46` (8 params). No finding in the touched file. CRLF preserved.

### Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty). Branch `main`.
