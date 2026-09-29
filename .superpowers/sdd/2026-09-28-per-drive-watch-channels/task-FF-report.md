# Task FF report: final-review fix lane

Status: DONE_WITH_CONCERNS. The only concern is the Linux compile, which is still owed (see item 3).

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FF`, branch `task/265-FF`. The base was
`38fbca32307ec11f0792c2665583542be04da1bd`, and HEAD matched it before any work started.
I took no lock and did not push.

## Commits (base..HEAD)

| SHA | Item | Subject |
|---|---|---|
| bf0468e | 1 | Diagnostics writer counts a record only once it is queued, so a flush always completes |
| 00a9ffe | 2 | A known control reply of the wrong kind ends the broker process |
| fd9f7d5 | 3 | MftParseControl is naturally aligned on both sides of the interop boundary |
| c9382dd | 4 | Tests and analyzer settings name only the current broker API |
| fa9ee27 | 5 | Launch timeout and cancelled rescan tests are bounded by a hang guard |
| d59dc6d | 6 | Protocol rejection tests assert the message names the rejected kind and cause |
| 7b10891 | 7 | ParseThreadAllowance documents that it serves one running parse at a time |
| bbebf88 | 8 | Three test comments describe the behavior their tests assert |
| e3266a4 | 2 (aislop) | Wrong-reply test records Ended reasons in a queue instead of a captured counter |

## RED command (W40-R1a)

Fixed prefix, completed per test by the test's full name:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~<full test name>"`

I ran all of the names below in one invocation, joined with `|`, against the unfixed production
code. For item 1, the unfixed code carried only the `BeforeDropAccountedForTest` hook, placed in
`OnDropped` before the decrement. Its output is in `.superpowers\red.log` in the worktree.

## Item 1: diagnostics writer enqueue and flush accounting

- `MFTLib/Broker/BrokerDiagnosticsWriter.cs`:
  - The channel uses `BoundedChannelFullMode.Wait`, so `TryWrite` returns false when the buffer is full or the writer is completed, and there is no drop callback. The `[ThreadStatic]` flag is removed.
  - `TryEnqueue` calls `TryWrite` and then increments `_accepted` under `_flushLock`, which `FlushAsync` also takes to snapshot its target. As a result:
    - The counted records are exactly the queued records, in queue order.
    - A dropped record is never counted.
    - A completed writer refuses a record (returns false and counts a drop) and no longer counts it as accepted.
    - A flush always completes.
  - `_flushLock` is now a `Lock`.
  - Added the internal seam `BeforeDropAccountedForTest`, which runs under the lock when a record is dropped.
- New test class `MFTLib.Tests/BrokerDiagnosticsWriterFlushTests.cs`. It touches no process-global state.
  - RED `MFTLib.Tests.BrokerDiagnosticsWriterFlushTests.FlushAsync_TakenWhileARecordIsBeingDropped_CompletesOnceTheQueuedRecordsAreAppended`:
    `Failed ... [10 s] System.TimeoutException: The operation has timed out. at ...BrokerDiagnosticsWriterFlushTests.cs:line 38`.
    The flush was taken between the increment and the drop's decrement, and it stranded, as the review describes. Now `Monitor` reentrancy lets the same-thread flush see the correct count.
  - RED `MFTLib.Tests.BrokerDiagnosticsWriterFlushTests.TryEnqueue_AfterComplete_RefusesTheRecordAndFlushCompletes`:
    `Failed ... Assert.IsFalse failed. at ...BrokerDiagnosticsWriterFlushTests.cs:line 50`.
    A completed writer reported the record as accepted, and its count would have stranded every later flush.
  - GREEN: both tests pass. The existing `BrokerDiagnosticsTests` and `BrokerDiagnosticsWriterReplacementTests` also pass.
- Choice made outside the review's instructions: I did not change the stale-writer window in `BrokerDiagnostics.ReplaceWriterForTest`. The dispatch's item 1 covers only accounting. With this fix, a line that a logger enqueues into a writer a swap has just completed is refused and counted as a drop, and no flush can strand on it. The line is lost, but only in the test-only swap.

## Item 2: a wrong known control reply ends the process

- `MFTLib/Broker/Client/BrokerProcess.Control.cs` (`RequestAsync`): builds the mismatch reason, calls `RequestEnd(reason)`, then throws `BrokerChannelLostException(null, reason)`.
- New test `BrokerProcessTests.ControlPipe_KnownReplyOfTheWrongKind_EndsProcessAndFailsEveryRequest` in `MFTLib.Tests/BrokerProcessTests.ControlPipe.cs`. It checks that:
  - The exception has a null drive and a message naming the mismatch.
  - `Ended` fires with the same reason, and fires exactly once, including after disposal.
  - `HasEnded` is true.
  - A second pending request fails with the same reason.
  - A later request fails with `BrokerChannelLostException`.
- RED `MFTLib.Tests.BrokerProcessTests.ControlPipe_KnownReplyOfTheWrongKind_EndsProcessAndFailsEveryRequest`:
  `Failed ... [10 s] System.TimeoutException: The operation has timed out. at ...BrokerProcessTests.ControlPipe.cs:line 72`.
  That line is the wait for `Ended`, which the unfixed code never raised.
- GREEN: passes, and all 66 `BrokerProcessTests` pass.
- Commit e3266a4 answers an aislop finding (jb `AccessToModifiedClosure`) on this test's counter. It changes the test's bookkeeping only.

## Item 3: MftParseControl alignment

- `MFTLibNative/mft_api.h`:
  - `MftParseControl` moved above `#pragma pack(push, 1)`, so it has natural alignment.
  - Added `static_assert`s: size 8, `cancelRequested` at offset 0, `parseThreadAllowance` at offset 4, and `alignof == alignof(int32_t)`.
  - Added `#include <cstddef>` for `offsetof`.
- `MFTLib/Interop/MftParseControl.cs`: `Pack = 1` removed. The layout is still 8 bytes, with fields at 0 and 4.
- New test class `MFTLib.Tests/MftParseControlLayoutTests.cs`:
  - `MftParseControl_MatchesTheNativeLayout` checks size 8 and offsets 0 and 4. It passed before and after the change, which pins that the layout is unchanged.
  - `MftParseControl_IsAlignedLikeA32BitField` embeds the struct after a byte in a probe struct and expects offset 4.
- RED native: I added the asserts before moving the struct, then ran
  `MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64`.
  It failed with exit 1:
  `mft_api.h(82,40): error C2338: static_assert failed: 'MftParseControl fields are shared 32-bit loads and need natural alignment'`
- RED managed `MFTLib.Tests.MftParseControlLayoutTests.MftParseControl_IsAlignedLikeA32BitField`:
  `Failed ... Assert.AreEqual failed. Expected:<4>. Actual:<1>.`
- GREEN: the native Release x64 build exits 0, and both layout tests and `ParseThreadAllowanceTests` pass.
- The repository has no native test program for this header. The only native test is `MFTLibNative/test/linux_smoke_test.cpp`, which is Linux-only.
- The Linux compile is owed. WSL Ubuntu 24.04 is installed but has no `g++`, and nothing can be built on Linux without leaving the worktree.

## Item 4: deleted-identifier cleanup (the plan and specification deletion is deferred to V1 by FR-Q1)

- `.editorconfig`: removed the two `JournalBrokerScanSession.Start.cs` and `.Rescan.cs` sections.
- `MFTLib.Tests/BrokerProcessTests.BlockSections.cs` renames:
  - Line 11: `ScanDriveAsync_ScanReady_ReleasesThatDrivesSectionWhileAnUnreadDriveStaysLive`
  - Line 63: `ScanDriveAsync_ErrorFrame_DisposesSectionLifetimeImmediately`
  - Line 101: `ScanDriveAsync_ScanProgressFrames_ReachTheOptionsProgressCallback`
  - Line 135: `ScanDriveAsync_ScanProgressFrame_DoesNotCompleteTheScan`
- `MFTLib.Tests/VolumeQueryClientTests.cs` renames:
  - Line 10: `QueryVolumeAsync_OneDriveSucceedsAndOneErrors_ReturnsTheVolumeAndThrowsTheHostMessage`
  - Line 32: `QueryVolumeAsync_ControlPipeClosesAfterOneReply_AnsweredQuerySucceedsAndPendingQueryLosesTheProcess`
- `MFTLib.Tests/BenchmarkRunnerTests.cs:1131`: the comment no longer names a test class.
- The renamed tests are a rename only; there is no behavior change and no RED.
- Deleted-identifier grep. One `git grep -F -n "<id>" -- . ':(exclude)docs/superpowers/' ':(exclude)CHANGELOG.md' | wc -l` per identifier, run on the final tree. Every count is 0:
  - JournalBrokerClient 0
  - JournalBrokerScanSession 0
  - ScanSessionTestHarness 0
  - WatchStreamNotRunningException 0
  - DriveWatchFailure 0
  - ReadyOnFirstMoveWatchStream 0
  - WatchSession 0
  - _swapGate 0
  - _rescanGate 0
  - ArmEpoch 0
  - EndWatchAck 0
  - SendStartWatchAsync 0
  - StopLiveWatchAsync 0
  - QueryVolumesAsync 0
  - ArmScanAndCatchUpAsync 0
  - BrokerScanResult 0
  - BrokerDied 0
  - WriteWarning 0
  - _cacheOnlyUnresumableCheckpointOrdinals 0
  - _unreportedWatchFaults 0
  - ResumeDriveAfterRescanAsync 0
  - IndexDriveOpened.Ordinal 0
  - ReplaceWatchCursors 0
  - WatchCursors 0

## Item 5: bounded timing tests

- `MFTLib.Tests/BrokerProcessLaunchTests.cs` (`LaunchAsync_DefaultTimeoutOverridden_TimesOut`): the launch is awaited under `.WaitAsync(HostChannelHarness.HangGuard)`.
  - Why a clock cannot be injected: `BrokerProcess.LaunchAsync` is a public static entry point that takes no `TimeProvider`. Its connect timeout is a `CancellationTokenSource(connectTimeout)` on the system timer, so injecting a clock would mean changing production code, which is outside this lane.
  - A hang-guard timeout cannot pass the test by accident. It is also a `TimeoutException`, but the test's `StringAssert` on "Timed out waiting 50ms" rejects its message.
  - A comment in the test states this.
- `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs` (the cancelled rescan, around line 330): now `ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard))`.
- These changes harden existing tests, with no production change and no RED.

## Item 6: protocol rejection messages

- `MFTLib.Tests/BrokerProtocolTests.cs` `ReadFrame_UnknownKind_ThrowsInvalidDataException`: asserts `"Unknown frame kind: {kind}"`.
- `MFTLib.Tests/BrokerProtocolTests.Scan.cs` `ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException`: asserts `"Unknown checkpoint loss cause: 42"`.
- The production messages were already correct. As W40-R1 allows, I produced RED with a scratch mutation instead:
  - I replaced both messages in `MFTLib/Broker/Protocol/BrokerProtocol.cs` with the unrelated `"Broker frame payload is truncated"`.
  - I built, then ran the prefix with `ReadFrame_UnknownKind_ThrowsInvalidDataException|ReadFrame_CatchUpLost_UnknownCause`. All 4 cases failed:
    - `Failed ReadFrame_UnknownKind_ThrowsInvalidDataException (0) ... Expected:<Unknown frame kind: 0>. Actual:<Broker frame payload is truncated>.`
    - The same failure for cases (18) and (99).
    - `Failed ReadFrame_CatchUpLost_UnknownCause_ThrowsInvalidDataException ... Expected:<Unknown checkpoint loss cause: 42>. Actual:<Broker frame payload is truncated>.`
  - The mutation is reverted: `git diff --quiet` on the file is clean.
  - GREEN: all four cases pass.

## Item 7: ParseThreadAllowance documentation

- `MFTLib/Mft/ParseThreadAllowance.cs` summary now states the rules:
  - One allowance attaches to one running parse at a time.
  - Passing it while another parse holds it throws `InvalidOperationException`.
  - It can be reused once that parse returns, and parses that run concurrently each need their own allowance.
- `MFTLib/Mft/MftVolume.cs`:
  - The `StreamRecords` `parseThreads` parameter doc states the rule, and the method gains an `<exception cref="InvalidOperationException">`.
  - `ReadRecordBatches` gains the same `<exception>`, noting that the parse starts on first enumeration.
- Documentation only; there is no RED.

## Item 8: stale comments

- `FileIndexWatchRescanTests.cs:272-273`: the comment now says the rescan throws both failures, the scan failure left the old block, and the restart failure is what the drive reports.
- `FileIndexCatchUpLossTests.cs:176-178`: the comment now states the target-state count rule and no longer mentions future task B6.
- `JournalBrokerHostLivenessTests.StalledPipe.cs:78`: the comment now says sixth interval, with six 5-second visits against the 30-second limit.

## Verification

- Native: `MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64` exits 0.
- `dotnet build -c Release -p:Platform=x64` of the solution fails in this worktree with MSB4278, because the dotnet CLI cannot load the `.vcxproj`, as AGENTS.md says. I built `MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64` instead: 0 warnings, 0 errors.
- Targeted tests:
  - Classes: BrokerDiagnosticsWriterFlushTests, BrokerDiagnosticsTests, BrokerDiagnosticsWriterReplacementTests, BrokerProcessTests, MftParseControlLayoutTests, ParseThreadAllowanceTests, BrokerProtocolTests, BrokerProcessLaunchTests, FileIndexWatchRescanTests, FileIndexCatchUpLossTests, JournalBrokerHostLivenessTests, VolumeQueryClientTests, BenchmarkRunnerTests and MftVolumeTests.
  - Result: `Passed! - Failed: 0, Passed: 331, Skipped: 0, Total: 331`.
  - After e3266a4, BrokerProcessTests passed 66 of 66.
  - The native DLL has to be copied into `MFTLib.Tests\bin\x64\Release\net10.0` by hand, as lane-common notes.
- Whole suite: one run of `pwsh -NoProfile -File C:\Users\mtsch\MFTLib-worktrees\265-FF\scripts\run-coverage.ps1 -NonInteractive`, at bbebf88, exit 0:
  - Total tests: 1941, Passed: 1935, Failed: 0, Skipped: 6 (the admin-only tests)
  - Line coverage 98.6%, branch coverage 96.5% (2513 of 2602)
  - The run came before e3266a4, which is a test-only bookkeeping change covered by the targeted rerun above.
- aislop: `aislop scan C:\Users\mtsch\MFTLib-worktrees\265-FF` scores 99/100 with 0 errors and 5 warnings. These are exactly the baseline plus the ruled warning:
  - `MFTLib.Tests/NativeSeamIsolationFixtures.cs:73` and `:79` (AsyncFixer01)
  - `MFTLib/Index/CachedBlockDeletionOutcome.cs:8` and `:10` (redundant doc comment)
  - `MFTLib/Broker/Host/JournalBrokerHost.cs:46` (8 parameters, ruled)
  - The first scan also flagged jb AccessToModifiedClosure at `BrokerProcessTests.ControlPipe.cs:57`. Commit e3266a4 fixed it.
- Line endings: CRLF is preserved in every edited file, and the two new files were written as CRLF.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short` printed nothing (empty), and the checkout is on `main`.

## Adjacent, not fixed

- `BrokerDiagnostics.ReplaceWriterForTest` stale-writer window (see item 1).
- `QueryVolumeAsync` and `GrowUsnJournalAsync` document `BrokerChannelLostException` as "The process ended first". A wrong-kind reply now ends the process and throws the same exception. The docs still read correctly, but they could name that case.
