# Task FX report: flaky OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock

Status: DONE. Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FX`, base `3b21d6b` (verified), branch `task/265-FX`.

## Cause (the test was wrong, not production)

`MFTLib.Tests/Index/FileIndexResilienceTests.cs:136-139` (before the fix) cancelled from the second drive's first scan progress report and assumed the first drive "is guaranteed to have already been added to _driveBlocks". That held for the sequential open. `OpenAsync` now settles drives concurrently (B9), so U's first progress report can precede T's scan completing or even starting; T is then cancelled mid-scan, its cache file never exists, and the exclusive reopen at `:150` throws `FileNotFoundException`. Production behaves as ruled (B9-Q2: a cancelled drive settles nothing; blocks adopted earlier are released; the open throws OperationCanceledException).

## Reproduction

Natural: `bash ..\265-FX-loop.sh 40` (a loop of `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock"`, counting `Failed!`) gave `failures 0 of 40` solo, and 0 of 90 with six loops in parallel: the window is narrow (needs T's scan to be slower than U's first report). Deterministic RED by scratch mutation (uncommitted, reverted): `await Task.Delay(500, cancellationToken)` for drive T at the top of the enumeration branch of `ProduceDriveBlockAsync`, then the loop of the ORIGINAL test:
`failures 10 of 10`, and
`System.IO.FileNotFoundException: Could not find file '...\mftlib-cache-9c38a76e27db472a94d63f8cf3bfe650\T-0BADF00D.mlix'.` / `Failed!  - Failed: 1, Passed: 0`.

## Fix (test only)

The first drive T now warm-starts from a cache a first open leaves behind (so its adoption needs no scan and no walk slot); the open is cancelled from T's `OpenProgress` report (`CancelOnDriveSettled`), which fires after T is adopted; U's scan parks in its first progress report until the token is cancelled (`ParkUntilCancelled`, bounded 30 s), then observes the cancellation mid-scan. The assertion (exclusive reopen of T's block succeeds) is unchanged and now always has an adopted block to prove released. No sleep; every wait bounded; the open's await bounded. Unused `CancelOnDriveReport` removed, parameters ordered for CA1068.

After the fix: same loop, `failures 0 of 40`; with a scratch mutation delaying T's settle by 500 ms (`AddDriveAsync`, reverted) the new test still passes: `failures 0 of 10`; final tree loop `failures 0 of 10`.

## Verification (after the last edit)

- `dotnet test ... --filter "FullyQualifiedName~Index"`: `Passed! Failed: 0, Passed: 897, Skipped: 6, Total: 903`.
- `.\scripts\run-coverage.ps1 -NonInteractive`: Total tests 1841, Passed 1835, Skipped 6, Failed 0.
- `aislop scan . -d`: 99/100, 5 warnings = 4 baseline + ruled `JournalBrokerHost` 8-parameter warning.

## Files

`MFTLib.Tests/Index/FileIndexResilienceTests.cs` only. Scratch loop script `C:\Users\mtsch\MFTLib-worktrees\265-FX-loop.sh` (outside the tree). Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: (empty output).

## Fix round 1

1. **Deterministic ordering.** `CancelOnDriveSettled` (cancelled from T's OpenProgress, possibly before U parked) replaced by `DriveSettledSignal` (T settled) and `ParkUntilCancelled.Parked` (U is inside its first scan progress report). The test cancels from its own thread only after `Task.WhenAll(firstSettled.Reported, secondParked.Parked).WaitAsync(30 s)` and asserts the open is not completed then (U mid-scan). Loop, literal command: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock"` x40 (`bash fxloop.sh 40`, scratch script outside the tree) -> `failures 0 of 40`.
2. **U's partial canonical file is pre-existing, not introduced here.** At `3597586`, `FileIndex.ScanCleanup.cs:128-129` gives a canonical target `DeleteOnClose: false`; `FileIndex.Scanning.cs:427-429` (`CreateAndPopulateBlock` finally) only `Dispose`s the block, and `BlockFile.Dispose` deletes only delete-on-close files (BlockFile.cs:263-300), so a cancelled cold scan at open left the canonical file on main too. What the next open does: `TryOpenExistingBlock` -> `BlockFile.Open` -> `BlockHeader.Validate` returns `Incomplete` when `BlockFlags.Complete` is unset (BlockHeader.cs:74-76, or `WrongMagic`), the file is deleted (`existedBeforeOpen`) and the drive cold-scans with `DiscardedBlock = Incomplete`; garbage cannot be adopted. No production change. The test now asserts exactly that and nothing false: if U's file exists, `BlockFile.Open` returns null for it.

Verification after the last edit: Index filter `Passed! Failed: 0, Passed: 897, Skipped: 6`; `run-coverage.ps1 -NonInteractive` Total tests 1841, Passed 1835, Skipped 6, Failed 0; aislop 99/100, 5 warnings (4 baseline + ruled JournalBrokerHost). Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: empty.

## Fix round 2

`await cancellationTokenSource.CancelAsync()` in `OpenAsync_SecondDriveCancelledMidScan_...` now ends in `.WaitAsync(TimeSpan.FromSeconds(30))` (`FileIndexResilienceTests.cs:161`). Targeted run: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock"` -> `Passed! Failed: 0, Passed: 1`. `aislop scan . -d`: 99/100, 5 warnings (4 baseline + ruled JournalBrokerHost). One-line test-only change; whole suite not re-run.

## Fix round 3

All awaits in `OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock` and its helper `AssertThrowsCancellation` (FileIndexResilienceTests.cs), each bounded by `HangGuard` (30 s, new static field):
- `AssertThrowsCancellation`: `await action().WaitAsync(HangGuard)` (also bounds every other caller of the helper).
- `File.WriteAllTextAsync(...)`: `.WaitAsync(HangGuard)`.
- Warm-up `FileIndex.OpenAsync(...)`: `.WaitAsync(HangGuard)`; its disposal is now explicit, `warmingOpen.DisposeAsync().AsTask().WaitAsync(HangGuard)` (the implicit `await using` is gone). The measured open never yields an index (it throws), so there is nothing to dispose.
- Inside the action: `Task.WhenAll(firstSettled.Reported, secondParked.Parked).WaitAsync(HangGuard)`, `CancelAsync().WaitAsync(HangGuard)`, `opening.WaitAsync(HangGuard)`.
- Non-async synchronous wait in `ParkUntilCancelled.Report`: `WaitHandle.WaitOne(30 s)`.
Targeted: `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexResilienceTests"` -> `Passed! Failed: 0, Passed: 22`; single test filter `...~OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock` -> `Passed: 1`. aislop 99/100 (4 baseline + ruled JournalBrokerHost).
