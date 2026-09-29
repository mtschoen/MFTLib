# Task CB2 report: flat MFTLib namespace to 100 percent

Status: DONE. Commit `17f68bb` on `task/265-CB2` (base `f1a53db`, verified first).

## First measurement (run-coverage.ps1 -NonInteractive, nscov): MFTLib(other) 2766/2782 = 99.42, 16 uncovered lines

| File, line | What | Class | Test or deletion |
|---|---|---|---|
| BrokerDiagnostics.cs 60, 62, 65 | non-Windows arms of `LogPath` | i (platform as input) | `LogPath` now calls `ResolveLogPath(combined, isWindows)`; `PlatformBranchTests.ResolveLogPath_*` (3 tests). `BrokerDiagnosticsLogFilter.TryGetDriveLetter` gained a platform overload so the relative-path arm is reachable on Windows |
| BrokerDiagnosticsLogFilter.cs 116 | `!IsWindows` return in `ResolveFileReference` | i | `ResolveFileReference(path, isWindows)`; `ResolveFileReference_NonWindowsHost_CannotResolveAndReturnsNull` |
| BrokerDiagnosticsLogFilter.cs 208 | non-Windows return in `NormalizePath` | i | `NormalizePath(path, isWindows)`; `NormalizePath_NonWindowsHost_ReturnsThePathAsGiven` |
| NtfsVolumeInformation.cs 56, 57 | non-Windows throw in `Query` | i | `Query(driveLetter, isWindows)`; `NtfsVolumeInformationQuery_NonWindowsHost_ThrowsPlatformNotSupported` |
| JournalBrokerHost.Sources.cs 46, 47 | duplicate non-Windows throw in `ScanDriveRecordBatches` | iii | deleted: the iterator now reads the volume through `QueryVolumeInfo`, which already throws the same `PlatformNotSupportedException` (now covered via the `Query` overload) |
| BrokerProcess.Control.cs 195, 196 | cancel after the write lock is won | ii | seam `AfterControlWriteLockAcquiredForTest`; `ControlWrite_CallerCancelsAfterWinningTheWriteLock_SendsNothingAndReleasesItsId` (asserts cancelled, id released, next frame on the pipe is the next request) |
| BrokerProcess.Scan.cs 70 | `}` of try after an always-returning `await using` block | iii | body moved to `ScanOnChannelAsync`, `ReleaseOnce` ref-bool helper became a small class; no unreachable brace remains |
| MftVolume.Journal.cs 156, 168 | wrong-state throws in `CancelWatch`/`ReadWatchBatch` | iii | deleted with the untyped callback state: new `UsnWatchSession` owns handle and event, `Register(session.Cancel)`, `session.ReadBatchAsync(...)`; both watch loops use it |
| MftRecord.cs 121, 166 | `_fileName`/`_fullPath` non-null on an unmaterialized record | iii | deleted: the pointer constructor sets both null and the string constructor sets `_materialized`, so no record reaches them |

Class iv (admin-only or other-platform-only lines left uncovered): none.

## Seams added
Instance internal `BrokerProcess.AfterControlWriteLockAcquiredForTest` (Action?), invoked after the write lock is held. No static seams, no public members. Internal platform-parameter overloads: `BrokerDiagnostics.ResolveLogPath`, `BrokerDiagnosticsLogFilter.TryGetDriveLetter/ResolveFileReference/NormalizePath`, `NtfsVolumeInformation.Query(string, bool)`.

## Defects found
None.

## Results (quoted from command output)
- `run-coverage.ps1 -NonInteractive` (final): Total tests 1948, Passed 1942, Skipped 6, Failed 0.
- nscov: MFTLib(other) 2774/2774 100.0; MFTLib.Index 3361/3401 98.82 (other lane's); MFTLibTestExtensions 131/163 80.37; Benchmark 547/554 98.74; TestProgram 58/58; Program 4/4. (Denominator moved from 2782 to 2774 as dead code was deleted.)
- Three measure-and-fix cycles were enough (Scan.cs brace needed the extraction; aislop flagged `!` and captured-disposed-variable forms, replaced by `UsnWatchSession`).
- aislop scan: 99/100, exactly the four baseline warnings (NativeSeamIsolationFixtures 73, 79; CachedBlockDeletionOutcome 8, 10) plus the ruled 8-parameter `JournalBrokerHost` constructor warning.
- Post-final-edit (unused using removed): build clean, targeted Journal/PlatformBranch/BrokerProcess tests 435 passed, aislop unchanged.
- `git -C C:\Users\mtsch\MFTLib status --short`: (empty)

## Files
New: `MFTLib.Tests/PlatformBranchTests.cs`, `MFTLib/Journal/UsnWatchSession.cs`. Changed: BrokerDiagnostics.cs, BrokerDiagnosticsLogFilter.cs, BrokerProcess.Control.cs, BrokerProcess.Scan.cs, JournalBrokerHost.Sources.cs, MftVolume.Journal.cs, MftRecord.cs, NtfsVolumeInformation.cs, BrokerProcessTests.ControlPipe.cs.

## Fix round 1

Commit 930faae. `BrokerProcessTests.ControlPipe.cs` line 48: `CloseControlAsync()` now `.AsTask().WaitAsync(HangGuard)`. Other awaits my commit added were checked: `ReadRequestAsync` bounds itself, `AssertCancelledAsync` bounds with `WaitAsync(HangGuard)`, `next.WaitAsync(HangGuard)` is bounded; `PlatformBranchTests` has no awaits. (Line 28's unbounded close predates this work and was not touched.)
Verify: BrokerProcessTests x3: 67 passed each. Whole suite `run-coverage.ps1 -NonInteractive`: Total 1948, Passed 1942, Skipped 6, Failed 0. MFTLib(other) 2774/2774 100.0. aislop: same four baseline warnings plus the ruled constructor warning.
