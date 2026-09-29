# Task CI2 report: MFTLib.Index line coverage

Branch `task/265-CI2`, worktree `C:\Users\mtsch\MFTLib-worktrees\265-CI2`, base f1a53db (verified).
Status: DONE_WITH_CONCERNS (only class iv lines remain; one concern about Linux coverage of one of them).

Commits (base..HEAD):

- 33e26d0 BlockFile drops handlers that no failure reaches
- 4f57e15 Default cache path and journal read take the guard state as an argument
- 396f88a FileIndex.Drives checks disposal under the state lock
- df65272 Index code drops branches no input reaches
- fcf6bbc Rescan restart decision gets a seam, and its stop-wins window a test
- c6df78c Restart decision test reads the index outside the harness's disposal scope

## Measurements

Command: `pwsh -NoProfile -File scripts\run-coverage.ps1 -NonInteractive`, then
`python ...\scratch\nscov.py MFTLib.Tests\coverage.xml MFTLib.Index` and a scratch lister over the same XML.

| Run | Suite | MFTLib.Index (covered / total) |
|---|---|---|
| 1 (base f1a53db) | Total 1941, Passed 1935, Skipped 6 | 3361 / 3401 (98.82) |
| 2 (fcf6bbc) | Total 1949, Passed 1943, Skipped 6 | 3365 / 3383 (99.47) |
| 3 final (c6df78c) | Total 1949, Passed 1943, Skipped 6, Failed 0 | 3365 / 3383 (99.47) |

Final per-namespace (nscov.py, run 3): `Program 4 4 100.0`, `Benchmark 547 554 98.74`,
`MFTLib(other) 2766 2782 99.42`, `MFTLib.Index 3365 3383 99.47`, `MFTLibTestExtensions 131 163 80.37`,
`TestProgram 58 58 100.0`. ReportGenerator summary: line coverage 98.9%, covered 6871, uncovered 73.
The total fell from 3401 to 3383 coverable lines because dead code was deleted (class iii).

Uncovered MFTLib.Index lines after run 3 (all class iv):
`BlockFile.Flush.cs [65, 66, 67, 70, 71, 72, 74, 76]`, `CacheDirectory.cs [373, 375, 378, 380, 383]`,
`FileEntry.Open.cs [33, 34]`, `JournalCheckpointCheck.cs [145]`, `UsnJournalSettingsQuery.cs [28, 29]`.

## Every line uncovered at the first measurement (40)

Line numbers are those at base f1a53db.

| File:line | What it is | Class | Disposition |
|---|---|---|---|
| BlockFile.Flush.cs:65-67, 70-72, 76 | `FlushRange` msync path (non-Windows) | iv | Linux: `BlockFileRangedFlushTests` (linux-final-report.md line 25: "The msync path executed and passed"). Still uncovered here: now the same numbers. |
| BlockFile.Flush.cs:74 | throw when msync fails (non-Windows) | iv | Linux-only branch. See concern 1: no Linux test forces msync to fail. |
| BlockFile.cs:197 | `block.Dispose()` when `InitializeHeader` throws | iii | Deleted. `InitializeHeader` only stores option fields through the pointer the constructor acquired; the `Header` getter throws only after disposal, which cannot have happened to a block constructed one statement earlier. `BuildAndInitialize` now unwinds only a failed construction (still covered by `BlockFileTests.BuildAndInitialize_ConstructionFails_DisposesTheMappingAndDeletesTheFile`). |
| BlockFile.cs:238, 240, 241, 243, 247, 248 | `BlockFile.Open` catches around `FileInfo.Exists`/`Length` | iii | Deleted. `Exists` refreshes the attributes once and turns any failure into false; `Length` reads the same cached attributes and throws only when that cached refresh failed, which `Exists` true rules out (a directory also reads `Exists` false for a `FileInfo`). No caller can reach either catch. An unreadable block still fails in `OpenMapped`, whose IOException/UnauthorizedAccessException handlers report WrongMagic. |
| CacheDirectory.cs:33 | `return ComputeDefaultPath()` past the one-way guard | ii | `ResolveDefaultPath()` now forwards the guard's state and `Environment.GetFolderPath` to an internal `ResolveDefaultPath(bool forbidden, Func<SpecialFolder,string>)`. Tests: `CacheDirectoryTests.ResolveDefaultPath_Unforbidden_IsTheIndexFolderUnderLocalApplicationData`, `ResolveDefaultPath_Forbidden_ThrowsBeforeReadingAnyFolder`; the reflection helper for `ComputeDefaultPath` now calls the overload. |
| CacheDirectory.cs:46, 47 | fallback to `UserProfile/.cache` when LocalApplicationData is empty | ii | Same parameter. Test: `ResolveDefaultPath_NoLocalApplicationData_FallsBackToTheCacheFolderInTheUserProfile`. |
| CacheDirectory.cs:372, 374, 377, 379, 382 (now 373-383) | `EnsureCreated` Unix owner-only mode | iv | Linux: `CacheDirectoryTests.EnsureCreated_OnUnixLeavesOwnerOnlyPermissions`, `EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory`, `EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory` (Inconclusive on Windows). |
| FileEntry.Open.cs:33, 34 | PlatformNotSupported off Windows | iv | Linux: `FileEntryOpenTests.Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported`. |
| FileIndex.ScanCleanup.cs:169, 173, 174, 177 | retired-sibling sweep: listing throws IOException / UnauthorizedAccessException | ii | The sweep only runs during an open, while the directory holds this index's owner lock file, and `EnsureCreated` has just re-applied the ACL, so only a listing failure (dropped share, concurrent ACL change) reaches the handlers. Seams: per-instance `FileIndex._enumerateCacheFiles` (default `Directory.EnumerateFiles`) and internal `FileIndex.OpenCoreAsync(options, configureBeforeSettle, token)` behind the public `OpenAsync`, which hands a test the index before any drive settles. Test: `FileIndexResilienceTests.OpenAsync_CacheMode_RetiredSiblingListingFails_OpensAndLeavesTheSibling` (DataRow for each exception): asserts the listing was attempted with the drive's pattern, the drive is Ready, and the stale sibling is untouched. |
| FileIndex.Watch.cs:213 | closing brace after `ExceptionDispatchInfo.Throw()` | iii | `RaiseChanged` restructured: the single-exception rethrow sits in `if (Count == 1)` and the method ends in `throw new AggregateException`, so no unreachable epilogue remains. Behavior unchanged; existing Changed-handler tests cover both throws. |
| FileIndex.WatchCatchUp.cs:18 | `NotStarted` when the drive has no runtime | iii | Deleted: the only caller, `DescribeOnlineDriveBlock`, passes the letter of a published block, and every configured drive has a runtime from construction (`_driveRuntimes` built from the private drive copy). Now `GetDriveRuntime(driveLetter)`, the same lookup the next statement in `DescribeOnlineDriveBlock` already used. |
| FileIndex.WatchDrive.cs:193 | `PrepareStart` returns null when a stop overtook a restart | ii | Seam `FileIndex.RestartRequestedForTest` (internal `Action<char>?`, invoked in `RestartRequestedWatchAsync` after it read the watch as requested, before the start re-reads it). Test: `FileIndexWatchRecoveryTests.StopAfterTheRescanDecidedToRestart_StopWins` (new file `FileIndexWatchRecoveryTests.RestartDecision.cs`): the hook calls `StopWatchingAsync`; asserts the stop completes, the source was started once only, the drive reads NotStarted, a second stop throws, and `RestartBeforeRegistrationForTest` was never reached (the first step saw the stop). |
| FileIndex.cs:373, 374 | `DescribeBlocklessDrive` invariant throw | defect, then iii | Reachable: see Defects. After the fix the throw was unreachable and is replaced by `_blocklessDriveStatuses.First(...)`. |
| IndexNavigation.cs:146 | `return true` when `current == target` after the depth loop | iii | Deleted: every loop iteration compares the row it just moved to with the target and returns on equality, and `MaximumPathDepth` is positive, so the loop always ends on a row that is not the target. |
| JournalCheckpointCheck.cs:129 | `return ReadLiveJournal` past the one-way guard | ii | `Check` passes the guard's state into an internal `ReadJournal(char, bool liveReadsForbidden)`. Test: `UsnJournalVolumeInteropTests.ReadJournal_NoOverride_TheGuardAloneDecidesWhetherTheLiveJournalIsRead` (C: via the real unelevated handle: null when forbidden, a journal with nonzero id and positive allocation delta when not). |
| JournalCheckpointCheck.cs:140 (now 145) | `return null` off Windows | iv | `ReadLiveJournal_VolumeRootWillNotOpen_ReportsNothing` now runs on every platform (its Windows skip and platform attribute removed), so the Linux job covers this line. Not verified by a Linux run in this lane. |
| JournalMutator.cs:241 | `meaningful == None` early return in `ApplyModification` | iii | Deleted with its mask: `ApplyOne` calls it only when `classification != None`, and `classification` is a subset of the same `meaningful` mask (plus RenameNewName only when `meaningful` holds it). |
| UsnJournalSettingsQuery.cs:28, 29 | PlatformNotSupported off Windows | iv | Linux: `UsnJournalSettingsQueryLiveTests.Query_NonWindowsHost_ThrowsPlatformNotSupported`. |

Counts of the 40: class i 0, class ii 9 (CacheDirectory 3, ScanCleanup 4, WatchDrive 1, JournalCheckpointCheck 1),
class iii 13 (BlockFile 7, Watch 1, WatchCatchUp 1, IndexNavigation 1, JournalMutator 1, FileIndex 2 after the
defect fix), class iv 18 (Flush 8, CacheDirectory 5, FileEntry.Open 2, UsnJournalSettingsQuery 2,
JournalCheckpointCheck 1). Remaining uncovered: 18, all class iv.

## Seams added (all internal, instance-level unless noted)

- `FileIndex.DrivesReadBeforeLockForTest` (`Action?`): invoked by a `Drives` read just before `_stateLock`.
- `FileIndex.RestartRequestedForTest` (`Action<char>?`): invoked after a restart decided the watch is still requested.
- `FileIndex._enumerateCacheFiles` (`Func<string,string,IEnumerable<string>>`, default `Directory.EnumerateFiles`).
- `FileIndex.OpenCoreAsync(options, Action<FileIndex>? configureBeforeSettle, token)`: internal static method
  behind public `OpenAsync`; holds no state.
- `CacheDirectory.ResolveDefaultPath(bool, Func<SpecialFolder,string>)` and
  `JournalCheckpointCheck.ReadJournal(char, bool)`: internal static methods taking the guard state as an argument;
  no new static state. Crefs to `ResolveDefaultPath` in `FileIndexOptions` and `CacheDirectoryIsolation` now name
  `ResolveDefaultPath()` to stay unambiguous.

No production path assigns any of these; defaults are null or the real call.

## Defects found

1. `FileIndex.Drives` raced `DisposeAsync`: it checked `_disposed` before taking `_stateLock`; a disposal that
   completed in between cleared `_driveBlocks`, so the read threw `InvalidOperationException` ("Drive T is in
   FileIndexOptions.Drives but has no online or blockless status.") instead of `ObjectDisposedException`. Fix: the
   check moved under `_stateLock` (disposal sets the flag under the lock before it unpublishes anything).
   Regression test: `FileIndexDriveStatusTests.Drives_DisposalCompletesBeforeTheReadTakesTheStateLock_ThrowsObjectDisposed`.

   RED (seam in place, check still before the lock), command:
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.FileIndexDriveStatusTests.Drives_DisposalCompletesBeforeTheReadTakesTheStateLock_ThrowsObjectDisposed"`

       Failed Drives_DisposalCompletesBeforeTheReadTakesTheStateLock_ThrowsObjectDisposed [50 ms]
       Assert.ThrowsException failed. Threw exception InvalidOperationException, but exception ObjectDisposedException was expected.
       Exception Message: Drive T is in FileIndexOptions.Drives but has no online or blockless status.
       at MFTLib.Index.FileIndex.DescribeBlocklessDrive(Char driveLetter) in ...\MFTLib\Index\FileIndex.cs:line 377
       at MFTLib.Index.FileIndex.get_Drives() in ...\MFTLib\Index\FileIndex.cs:line 123
       Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1

   GREEN after the fix: the targeted class set (184 passed, 3 skipped, 0 failed) and the full suite.

## RED for the restart-window test (scratch mutation, not committed)

Mutation: `FileIndex.WatchDrive.cs:191` condition extended with `&& runtime.DriveLetter == (char)0` (disables
PrepareStart's re-check). Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.FileIndexWatchRecoveryTests.StopAfterTheRescanDecidedToRestart_StopWins"`

    Failed StopAfterTheRescanDecidedToRestart_StopWins [66 ms]
    Assert.AreEqual failed. Expected:<0>. Actual:<1>. the start's first step already saw the stop
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1

Reverted with `git checkout -- MFTLib/Index/FileIndex.WatchDrive.cs`; the test passes on the committed code.
The other new tests pin existing behavior (coverage-only) and assert outcomes, not merely execution.

## aislop

`aislop scan .` (final): `99 / 100 Healthy 0 errors · 5 warnings · 0 fixable`: AsyncFixer01 at
`MFTLib.Tests/NativeSeamIsolationFixtures.cs:73` and `:79`, redundant XML-doc at
`MFTLib/Index/CachedBlockDeletionOutcome.cs:8` and `:10`, too-many-params at
`MFTLib/Broker/Host/JournalBrokerHost.cs:46` (8 params, ruled). An intermediate scan flagged LF endings in four
files (Git Bash `sed -i` and the Write tool wrote LF) and a jb AccessToDisposedClosure in the new restart test;
both fixed (files converted to CRLF, test reads `harness.Index` into a local before the closure).

## Concerns and adjacent observations

1. `BlockFile.Flush.cs:74` (msync failure throw) is Linux-only and, per the Linux reports, no Linux test makes
   msync fail, so it is likely uncovered on Linux too. A per-instance msync seam mirroring `_flushViewRange` would
   make it testable there; not done here because it cannot be verified without a Linux run.
2. `JournalCheckpointCheck.cs:145` depends on the now cross-platform `ReadLiveJournal_VolumeRootWillNotOpen_ReportsNothing`
   running on Linux; not verified by a Linux run in this lane.
3. Adjacent, not changed: other `FileIndex` members check `_disposed` before taking `_stateLock` (for example
   `TryGetDriveOrdinal`, `CurrentSnapshot`); `CurrentSnapshot` already re-checks under the lock, others may return
   "not found" rather than ObjectDisposedException under the same race.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Follow-up: Linux

Status: BLOCKED on the Linux coverage measurement (details below). Windows work done.

### Commits (on top of c6df78c; pushed to gitea `task/265-CI2`)

- f973303 BlockFile's msync flush takes its platform as input behind an instance seam
- 9957da6 Platform guards in the index take the host as input
- 89332bb A rescan that meets a still-retiring watch is tested deterministically
- 6a6c978 msync failure test captures the exception without a closure over the block

Push note: my first `git push -u origin` went to the GitHub remote (`origin` is github.com:mtschoen/MFTLib here,
the Gitea remote is named `gitea`). I deleted that branch from GitHub at once (`git push origin --delete
task/265-CI2`) and pushed to `gitea`. The branch was on GitHub for under a minute.

### What changed to move lines off the Windows-only list

- `BlockFile.Flush.cs`: the Unix flush is now `SynchronizeRange(start, end, OSPlatform platform)`, which calls the
  new per-instance seam `_synchronizeViewRange(alignedStart, end, flag)` (default `SynchronizeViewRangeNatively`,
  which calls msync and returns the errno). The flag choice, page alignment and the throw on failure (the old
  line 74) run on Windows too. Tests: `BlockFileRangedFlushTests.SynchronizeRange_UnalignedStart_SynchronizesFromItsPageBoundaryWithThePlatformFlag`
  (DataRow LINUX/4 and OSX/0x10) and `SynchronizeRange_CallFails_ThrowsWithTheErrno` (the fake returns 5 and the
  message is "msync failed with errno 5.").
- `FileEntry.OpenById(bool isWindows, ...)`, `UsnJournalSettingsQuery.Query(char, bool isWindows)`,
  `JournalCheckpointCheck.ReadLiveJournal(char, bool isWindows)`: internal forms that are told whether the host is
  Windows. The public or default entry passes `OperatingSystem.IsWindows()`. The guard reads
  `!isWindows || !OperatingSystem.IsWindows()` so CA1416 still sees the platform check. Tests that run on every
  platform: `FileEntryOpenTests.OpenById_HostIsNotWindows_ThrowsPlatformNotSupported`,
  `UsnJournalSettingsQueryLiveTests.Query_HostIsNotWindows_ThrowsPlatformNotSupported`,
  `UsnJournalVolumeInteropTests.ReadLiveJournal_HostIsNotWindows_ReportsNothing`.
- Found while measuring: `FileIndex.RescanRestart.cs:115` (a rescan that meets a still-retiring instance adds its
  drain) was covered in runs 1 to 3 and uncovered in run 4, so it depended on how the suite interleaved. New
  deterministic test `FileIndexPerDriveWatchTests.Rescan_WhileAStoppedWatchIsStillRetiring_ScansOnlyAfterItDrains`
  (it holds the old pump's batch and stops with a cancelled token). Checked with a scratch mutation that replaced
  `drains.Add(alreadyRetiring.Drained)` with a discard. Under the mutation `Assert.IsFalse failed. the rescan waits
  for the retiring instance` (Failed: 1). Reverted, the test passes.

### Windows (final)

`run-coverage.ps1 -NonInteractive` at 89332bb: Total 1956, Passed 1950, Skipped 6, Failed 0. nscov:
`MFTLib.Index 3381 3390 99.73`, `MFTLib(other) 2766 2782 99.42`. 6a6c978 only rewrites one test assertion
without a closure; I ran that test class alone and did not repeat the whole suite: BlockFileRangedFlushTests plus
FileIndexPerDriveWatchTests, Passed 55, Failed 0. aislop at 6a6c978: `99 / 100 ... 0 errors · 5 warnings`, the
four baseline warnings plus the ruled JournalBrokerHost one.

Windows uncovered lines in MFTLib.Index (9):

| File:line | What | Linux coverage expected from |
|---|---|---|
| BlockFile.Flush.cs:71, 72 | `FlushRange`'s non-Windows call into `SynchronizeRange` (and its closing brace) | every Linux `Flush` test in BlockFileRangedFlushTests |
| BlockFile.Flush.cs:93, 94 | `SynchronizeViewRangeNatively` body (the real msync call) | the same Linux flush tests |
| CacheDirectory.cs:373, 375, 378, 380, 383 | `EnsureCreated` Unix owner-only mode | CacheDirectoryTests.EnsureCreated_OnUnix* (3 tests), plus every Linux FileIndex open |

`File.SetUnixFileMode` and `libc` msync have no Windows implementation, so no Windows test can reach these lines.

### Linux: not measured (blocked)

1. Run 1 on llamabox, `~/scratch/ci2` detached at c6df78c: `./init.sh --build && scripts/coverage-linux.sh` under
   nohup to `~/scratch/ci2-run1.log`. The managed run ended
   `Failed!  - Failed: 1, Passed: 1597, Skipped: 84, Total: 1682`. The one failure is
   `FileIndexWatchRecoveryTests.ChannelFault_NoRecovery` ("Assert.IsFalse failed. a channel fault queues no
   recovery", FileIndexWatchRecoveryTests.cs:145), the Linux failure linux-final-report.md already records. That file
   belongs to the fx3 lane. Coverlet writes no cobertura output unless the run is green (the script says so), so
   Linux produced no coverage report.
2. To get a report anyway I tried to add that one test to the script's filter in my scratch clone only. The auto-mode
   classifier denied it as "[CI Bypass]". Its ruling also covers equivalent routes to a Linux coverage reading that
   leaves the failing test out, so I stopped there. I also did not read the rest of the run-1 log after that denial.
3. So I cannot report the Linux uncovered list, whether the 9 lines above are covered on Linux, or a Linux run of
   the cross-platform `ReadLiveJournal` tests (item 3). `ReadLiveJournal_HostIsNotWindows_ReportsNothing` now covers
   the non-Windows `return null` of `ReadLiveJournal` on Windows as well, so that line is off the Windows list.

What unblocks it: fx3's fix for `ChannelFault_NoRecovery` merged under this branch (or rebased onto), then the
unchanged `scripts/coverage-linux.sh` rerun. Or the owner explicitly allows a scratch-only measurement that filters
that test out. That is the owner's call.

Intersection of the Windows and Linux uncovered lists: unknown until Linux measures. Linux tests exist for all 9
Windows-uncovered lines. None of them has a Linux coverage reading yet.

### Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Fix round 1

Commits (on top of 6a6c978, pushed to gitea `task/265-CI2`):

- aca2832 BlockFile.Open reports a path it cannot describe as no usable block
- 2dcf797 The retired-sibling listing seam comes in through the options
- 4b23a3b Default cache path and journal read resolve through instances
- b2df6e0 New watch tests bound every await with the hang guard
- 31a3839 CacheDirectory's default path moves to its own partial file; tests capture locals

### 1. BlockFile.Open failure handling restored

The try around `new FileInfo(path)`, `Exists` and `Length` is back. It has one handler,
`catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)`, which returns
WrongMagic and a null block. That is the same outcome both old catches gave, in one handler, so the single line is
covered.

A spike (scratch console app, net10.0, Windows) checked which failures are reachable:

- `new FileInfo(<temp> + 40,000 chars)` throws PathTooLongException.
- A 300-character segment: `Exists` returns false, and `Length` throws IOException.
- `Length` after the file was deleted following `Exists` returns the cached value and does not throw.
- The CON device path and NUL: `Length` throws FileNotFoundException or IOException.

So the IOException arm can be reached through the real API with an over-long path. No seam was needed.

Test: `BlockFileTests.Open_PathTheFileSystemCannotDescribe_IsReportedWithoutThrowing`. It uses a 40,000-character
path and asserts WrongMagic and a null block.

RED 1 (the test written before the restore, committed code without any catch). Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.BlockFileTests.Open_PathTheFileSystemCannotDescribe_IsReportedWithoutThrowing"`

       at System.IO.FileInfo..ctor(String fileName)
       at MFTLib.Index.BlockFile.Open(String path, UInt32 expectedVolumeSerial, BlockValidationResult& validation) in ...\MFTLib\Index\BlockFile.cs:line 226
       at MFTLib.Tests.Index.BlockFileTests.Open_PathTheFileSystemCannotDescribe_IsReportedWithoutThrowing() in ...\MFTLib.Tests\Index\BlockFileTests.cs:line 110
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 21 ms - MFTLib.Tests.dll (net10.0)

RED 2 (scratch mutation of the restored filter to `when (exception is UnauthorizedAccessException)`, not
committed; restored from a saved copy afterwards). Same command:

      Failed Open_PathTheFileSystemCannotDescribe_IsReportedWithoutThrowing [8 ms]
      Error Message:
       Test method MFTLib.Tests.Index.BlockFileTests.Open_PathTheFileSystemCannotDescribe_IsReportedWithoutThrowing threw exception:
      System.IO.PathTooLongException: The path 'C:\Users\mtsch\AppData\Local\Temp\aaaa... (truncated)
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 17 ms - MFTLib.Tests.dll (net10.0)

GREEN: the same test passes on the committed code.

The UnauthorizedAccessException arm of the same filter has no reachable input on .NET 10 that I could find. The
FileInfo constructor documents it, but the spike did not produce it. It shares the covered line, so it adds no
uncovered line.

### 2 and 3. No unsettled index is observable; instance-owned seams

- `OpenCoreAsync` is deleted. `OpenAsync` is again the single async open with no callback and no null branch.
- The retired-sibling listing seam is now the internal init property
  `FileIndexOptions.EnumerateCacheFilesForTest`. The constructor copies it once into the readonly instance field
  `_enumerateCacheFiles` (`?? Directory.EnumerateFiles`). The test sets it with `Options() with { ... }`.
- `CacheDirectory.ResolveDefaultPath(bool, Func)` is replaced by the instance class
  `CacheDirectory.DefaultPathResolver(Func<bool> isForbidden, Func<SpecialFolder,string> folderPath).Resolve()`.
  This is now in `CacheDirectory.DefaultPath.cs`, which keeps `CacheDirectory.cs` under the 400-line limit.
- `JournalCheckpointCheck.ReadJournal(char, bool)` is replaced by
  `JournalCheckpointCheck.JournalReader(Func<bool> liveReadsForbidden, Func<char, JournalWindow?> readLive).Read(char)`.
- Guard strength is unchanged. `ResolveDefaultPath()` and `Check` go through one static readonly instance each
  (`ProcessDefaultPath`, `ProcessJournal`). Each instance's guard is a lambda that reads the process's one-way flag.
  Nothing can reassign either instance or its guard, and no reset exists. Tests build their own instances; they
  never touch the process's instance or the flags. The existing tests that the process guards are on still pass:
  `ResolveDefaultPath_InGuardedProcess_*`, `ForbidDefaultCacheDirectory_*`, `OpenAsync_OmittedCacheDirectory_*`,
  and `LiveJournalReadsAreForbiddenInThisTestProcess`.

Why the blockless-status invariant cannot now be violated, so `DescribeBlocklessDrive` keeps
`_blocklessDriveStatuses.First(...)`:

- The constructor is private. The only creator is `OpenAsync`, and it hands the index to no code before
  `SettleDrivesAsync` finishes and the snapshot is published. Only its own reentrancy marker passes `this` anywhere,
  and that is for Changed delivery, which cannot run during an open. Options callbacks (Progress, OpenProgress,
  Diagnostics, producers, the watch source) receive values, never the index.
- Every drive's settle ends in one of three ways: `RecordOfflineDrive` and `RecordFailedDrive` add a blockless
  status, and adoption adds a block. The alternative is a throw, which fails the open, so the index is never
  returned.
- `SettleDriveAsync`'s own read (`FileIndex.Scanning.cs:61`) runs after that drive's `AddDriveAsync` returned.
- After open, `_driveBlocks` entries are only replaced or added. `Publication.cs` removes a blockless status under
  the same `_stateLock` section that adds its block. The only `_driveBlocks.Clear()` calls are in the open's unwind
  (the index is never returned) and in disposal (the flag is set first, and `Drives` checks it under the lock).

So neither `Drives` nor the settle report can see a configured drive that has neither status.

### 4. Bounded awaits

The following awaits are now bounded by `WaitAsync(HangGuard)`:

- `FileIndexResilienceTests.RetiredSiblings`: the file write, the open, and the disposal. The disposal is now an
  explicit `DisposeAsync` in a finally, replacing `await using`.
- `FileIndexWatchRecoveryTests.RestartDecision`: the start and the final `ThrowsExceptionAsync`.
- My follow-up `FileIndexPerDriveWatchTests.RescanWhileRetiring`: the start, `CancelAsync` and `ThrowsAsync`.

The same round's aislop flagged the two watch tests for jb AccessToDisposedClosure. Both lambdas now capture locals
(`index`, `stopToken`).

### 5. Doc

`BlockFileCreateOptions.Diagnostics` now reads "Receives the log line when a block whose creation failed is deleted."

### Verification

- Touched classes three times. Filter: BlockFileTests, CacheDirectoryTests, UsnJournalVolumeInteropTests,
  FileIndexResilienceTests, FileIndexWatchRecoveryTests, FileIndexPerDriveWatchTests, FileIndexDriveStatusTests,
  JournalCheckpoint. Result, three times: `Passed!  - Failed: 0, Passed: 152, Skipped: 3, Total: 155`.
- Whole suite at 31a3839 through `run-coverage.ps1 -NonInteractive`: Total 1957, Passed 1951, Skipped 6, Failed 0.
- nscov: `MFTLib.Index 3392 3401 99.74`, `MFTLib(other) 2766 2782 99.42`.
- Windows uncovered in MFTLib.Index: `BlockFile.Flush.cs [71, 72, 93, 94]` (the non-Windows msync call and the native
  msync) and `CacheDirectory.cs [335, 337, 340, 342, 345]` (`EnsureCreated` Unix mode; the same lines as before, moved
  by the split). The restored `BlockFile.Open` handling added no uncovered line.
- aislop at 31a3839: `99 / 100 Healthy 0 errors · 5 warnings`, the four baseline warnings plus the ruled
  JournalBrokerHost one.
- Linux: not measured. This is still the blocker from "Follow-up: Linux": `ChannelFault_NoRecovery` fails on Linux,
  so coverlet writes no report, and a filtered scratch run was denied as a CI bypass. Linux MFTLib.Index
  covered/total is therefore unknown.

### Open point for the controller

The follow-up commit 9957da6 added the platform-as-input methods `FileEntry.OpenById(bool isWindows, ...)`,
`UsnJournalSettingsQuery.Query(char, bool isWindows)` and `JournalCheckpointCheck.ReadLiveJournal(char, bool isWindows)`
as you directed ("as Libc.SelectSynchronousFlag does"). They are static methods with a parameter, the same shape
review item 3 objects to for the guard overloads. I left them as they are. If they should also become instances,
that is a ruling for you.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Linux measured (after the authorized merge)

Merge: `git fetch gitea` then `git merge --no-edit gitea/impl/265-per-drive-channels`. Before merging,
`git log HEAD..gitea/impl/265-per-drive-channels` listed exactly bf9c50c ("ChannelFault_NoRecovery reads its own
handler's result instead of racing it") and 03139d7 ("Continuation-inline tests assert on the observed flag each
helper returns"). The merge had no conflicts and produced merge commit 6d4396a, which I pushed to gitea only. It
touched 3 test files and no production file. Fix round 1 was already complete before the merge (see above).

### Linux run

- Host llamabox, `~/scratch/ci2` detached at 6d4396a. Ran `nohup bash -c "./init.sh --build && scripts/coverage-linux.sh"`
  to `~/scratch/ci2-run2.log`, with the script unmodified and no filter added.
- Managed: `Passed!  - Failed: 0, Passed: 1606, Skipped: 84, Total: 1690`. Coverlet summary:
  `lines: 95.14% (8427/8857)`.
- Linux MFTLib.Index (the same scratch lister over `coverage-report/managed/coverage.cobertura.xml`): 4623 / 4847.
  Coverlet counts sequence points differently from Microsoft's collector, so the Linux and Windows totals are not
  comparable.
- Linux-uncovered MFTLib.Index lines are all Windows-only or Windows-hosted code:
  - `NamedBlockSection`, `WindowsFileById`, `UsnJournalVolumeInterop`, `IndexedDrive.Windows` in full.
  - The Windows ACL half of `CacheDirectory.EnsureCreated`.
  - The Windows retry flush in `BlockFile.Flush.cs`.
  - `UsnJournalSettingsQuery`'s interop body, `JournalCheckpointCheck`'s live Windows read (lines 170-185).
  - The Windows-only test paths `BlockFile.Mapping.cs`, `DriveBlock.cs`, `FileIndex.ScanCleanup.cs:193-201, 253-255`,
    `AggregateEngine.cs:30-31`, `FileEntry.Open.cs:45-46`, `FileIndex.Watch.cs:210`, `BlockFile.cs:207-209, 234-240`.
  - `JournalCheckpointCheck.cs:147` (`return readLive(...)` in `JournalReader.Read`, tested only by the
    Windows-only real-volume test).
  - Every one of these is covered in the Windows run.

### Item 3 of the Linux follow-up

The cross-platform `ReadLiveJournal` tests ran on Linux. `JournalCheckpointCheck.cs:166`, the `return null` when the
host is not Windows, has 2 Linux hits: `ReadLiveJournal_VolumeRootWillNotOpen_ReportsNothing` and
`ReadLiveJournal_HostIsNotWindows_ReportsNothing`. Both passed (the Linux run had 0 failures).

### Windows at the merged head

- `run-coverage.ps1 -NonInteractive` at 6d4396a: Total 1957, Passed 1951, Skipped 6, Failed 0.
- nscov: `MFTLib.Index 3392 3401 99.74`.
- aislop: `99 / 100 Healthy 0 errors · 5 warnings`, the four baseline warnings plus the ruled JournalBrokerHost one.

### Windows-uncovered lines, their Linux coverage, and the intersection

Scratch script `intersect.py`. It keys Windows `coverage.xml` and Linux cobertura by `Index/<file>` and line number,
then lists every Windows-uncovered line with its Linux hit count.

| Line | Linux hits |
|---|---|
| BlockFile.Flush.cs:71 | 2368 |
| BlockFile.Flush.cs:72 | 2368 |
| BlockFile.Flush.cs:93 | 2368 |
| BlockFile.Flush.cs:94 | 2368 |
| CacheDirectory.cs:335 | 598 |
| CacheDirectory.cs:337 | 213 |
| CacheDirectory.cs:340 | 385 |
| CacheDirectory.cs:342 | 298 |
| CacheDirectory.cs:345 | 385 |

`intersection (uncovered on both): []`. The CacheDirectory lines are the ones previously numbered 373-383 (and
383-393). They moved when the default-path code went to `CacheDirectory.DefaultPath.cs` in 31a3839.

Final head: 6d4396a on `task/265-CI2` (gitea). `git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Linux measurement

Already done at 6d4396a (merge of exactly bf9c50c and 03139d7, no conflicts, pushed to gitea only). The full
evidence is in the section "Linux measured (after the authorized merge)" above. In short:

- Linux (llamabox `~/scratch/ci2`, `./init.sh --build` then unmodified `scripts/coverage-linux.sh`): Passed 1606,
  Skipped 84, Failed 0. MFTLib.Index 4623 / 4847 (coverlet line counting). Every Linux-uncovered line is
  Windows-only code, and all of them are covered on Windows.
- Windows (6d4396a): Passed 1951, Skipped 6, Failed 0. MFTLib.Index 3392 / 3401.
- The nine Windows-uncovered lines are all covered on Linux. Flush.cs:71, 72, 93, 94 have 2368 hits each.
  CacheDirectory.cs:335, 337, 340, 342, 345 (the Unix file-mode lines, previously 373-383) have 598 / 213 / 385 /
  298 / 385 hits. Intersection: empty. No new Linux test was needed.
- The cross-platform JournalCheckpointCheck tests ran on Linux: line 166 (non-Windows `return null`) has 2 hits,
  and both tests passed.

isWindows ruling, checked: `FileEntry.OpenById`, `UsnJournalSettingsQuery.Query(char, bool)` and
`JournalCheckpointCheck.ReadLiveJournal(char, bool)` are internal, pure and stateless. Each has exactly one
production caller that passes `OperatingSystem.IsWindows()`: `FileEntry.DefaultOpenById` (FileEntry.Open.cs:30),
`UsnJournalSettingsQuery.Query(char)` (UsnJournalSettingsQuery.cs:19) and `ReadLiveJournal(char)`
(JournalCheckpointCheck.cs:156). A search of MFTLib and MFTLibTestExtensions finds no other caller. No change made.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Fix round 2

Commit 2a9e9fb "The platform-taking settings query reads only its arguments" (pushed to gitea `task/265-CI2`).

- `UsnJournalSettingsQuery.Query(char)` now reads `_queryOverride` itself and otherwise calls
  `Query(driveLetter, OperatingSystem.IsWindows())`. `Query(char, bool)` no longer reads any state: it uses only its
  arguments and the platform APIs.
- Behavior of the public entry point is unchanged: the override still takes precedence and the real query runs
  otherwise. Production has one caller, `FileIndex.JournalSettings.cs:15`, which goes through `Query(char)`.
- New test `UsnJournalSettingsQueryLiveTests.Query_OverrideInstalled_AnswersThePublicEntryAndNotThePlatformTakingQuery`.
  With an override installed, `Query('C')` returns the override's value and `Query('C', isWindows: false)` still throws
  PlatformNotSupportedException. This shows the non-Windows refusal test does not depend on the override being unset.
  The class is now `[DoNotParallelize]`, because this test installs the process-wide override, as
  `FileIndexJournalSettingsTests` already does.

RED: I stashed only the production file (`git stash push -- MFTLib/Index/UsnJournalSettingsQuery.cs`), rebuilt, and ran:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.UsnJournalSettingsQueryLiveTests.Query_OverrideInstalled_AnswersThePublicEntryAndNotThePlatformTakingQuery"`

      Failed Query_OverrideInstalled_AnswersThePublicEntryAndNotThePlatformTakingQuery [9 ms]
      Error Message:
       Assert.ThrowsException failed. No exception thrown. PlatformNotSupportedException exception was expected.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 18 ms - MFTLib.Tests.dll (net10.0)

Then `git stash pop` and a rebuild.

Verification:

- Classes that touch UsnJournalSettingsQuery, run three times (filter: UsnJournalSettingsQueryLiveTests,
  FileIndexJournalSettingsTests, UsnJournalVolumeInteropTests, UsnJournalLiveTests, UsnJournalSyntheticTests).
  Each run: `Passed!  - Failed: 0, Passed: 63, Skipped: 6, Total: 69`.
- Whole suite through `run-coverage.ps1 -NonInteractive` at 2a9e9fb: Total 1958, Passed 1952, Skipped 6, Failed 0.
- MFTLib.Index 3392 / 3401. Uncovered: `BlockFile.Flush.cs [71, 72, 93, 94]` and
  `CacheDirectory.cs [335, 337, 340, 342, 345]`. These are the same nine non-Windows lines, all covered on Linux per
  "Linux measured".
- aislop: `99 / 100 Healthy 0 errors · 5 warnings`, the four baseline warnings plus the ruled JournalBrokerHost one.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).
