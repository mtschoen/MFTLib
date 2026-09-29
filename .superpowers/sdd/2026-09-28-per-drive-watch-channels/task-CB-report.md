# Task CB report: broker coverage tests (flat MFTLib namespace)

Status: DONE. Commit `f4cdf46` on `task/265-CB` (base `ca649bba`, verified first). Tests only; no production code changed.

## Totals (nscov.py, "MFTLib(other)")

- Before: 2723 / 2779 covered = 97.98 percent (56 uncovered lines).
- After: 2760 / 2779 covered = 99.32 percent (19 uncovered lines; meets main's 99.32).
- Final `run-coverage.ps1 -NonInteractive`: Total tests 1905, Passed 1899, Failed 0, Skipped 6.
- aislop: 99/100, 6 warnings = the four baseline warnings (NativeSeamIsolationFixtures 73 and 79, CachedBlockDeletionOutcome 8 and 10) plus the ruled 8-parameter `JournalBrokerHost` constructor warning (`JournalBrokerHost.cs:46`) and nothing else.

## Uncovered lines at the start (56) and their disposition

Class a = covered by a new test; b = needs elevation/real NTFS or the other platform; c = unreachable or race-only.

| Line(s) | Class | Covering test or reason |
|---|---|---|
| JournalBrokerHost.Channel.cs 17-19 | a | `JournalBrokerHostChannelTests.OpenChannel_InvalidDriveLetter_RepliesErrorWithRequestId` |
| Channel.cs 33, 35, 36 | a | `OpenChannel_ChannelOpenedReplyCannotBeWritten_DisposesTheConnectedPipeAndEndsSession` |
| Channel.cs 67, 68 | a | `OpenChannel_ConnectionArrivesAfterSessionEnded_IsDisposed` |
| Channel.cs 196 | a | `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` |
| Channel.cs 222, 225 | a | `WatchChannel_ClientPipeReadFails_CancelsTheWatchAndClosesTheChannel` |
| JournalBrokerHost.Session.cs 96, 97 | a | `ControlPipe_DriveOperationFrame_RepliesErrorNamingTheKind` |
| Session.cs 105 | a | `ControlRequest_SourceCancelsItselfWhileSessionLives_RepliesError` |
| Session.cs 132, 146 | c | Second drain iteration needs a task tracked after the session ended; every tracking path checks `session.Token` first, so only a check-then-act race reaches it |
| BrokerProcess.Control.cs 179-182, 217-222 | a | `BrokerProcessTests.ControlWrite_CallerCancelsWhileWaitingForTheWriteLock_SendsNothingAndReleasesItsId` |
| Control.cs 189, 190 | c | Caller token cancelled after the write lock was won: `SemaphoreSlim.WaitAsync` throws for an already-cancelled token, so only a scheduling race between lock hand-off and cancel reaches it |
| Control.cs 270 | a | `ControlPipe_StalledFrame_EndsProcessWithHostMessage` |
| BrokerProcess.Channels.cs 81, 82, 84, 85 | a | `OpenChannel_AcknowledgedThenProcessEnds_FailsWithChannelLostNamingTheProcess` |
| BrokerProcess.Scan.cs 70 | c | Closing brace after a `try` whose body always returns; no instruction reachable |
| BrokerProtocol.Payload.cs 143, 149, 174, 184 | a | `BrokerProtocolTests.MalformedPayloads`: fixed field past end, name length past payload, negative name length, count of entries the names leave no room for |
| DefaultElevatedEntryRunner.cs 86, 88, 89 | a | `DefaultElevatedEntryRunnerConnectTests.ConnectDrivePipeAsync_CancelledBeforeAnyServerListens_ThrowsOperationCancelled` |
| HostPipeWriter.cs 108 | a | `JournalBrokerHostLivenessTests.StalledPipe_FrameQueuedBehindTheStalledWrite_IsNotWritten` (Stalled write held, next frame queued behind it; ordering signalled by the diagnostics line, no sleep) |
| HostPipeWriter.cs 175 | a | `StalledPipe_OperationIgnoresCancellation_WritesNothingMoreAfterStalled` |
| ParseThreadAllocator.cs 89 | a | `ParseThreadAllocatorTests.AdmitAsync_WaitCancelledInTheMomentItIsAdmitted_ReturnsTheRegistration` (cancellation callbacks run last-registered first, so the test's callback admits the waiter before the allocator's runs) |
| ParseThreadAllocator.cs 105 | a | `ParseThreadAllocatorTests.Release_SameRegistrationTwice_...` |
| BrokerDiagnostics.cs 147 | c (unresolved) | Null branch of `Interlocked.Exchange(...)?.Complete()` in `ReplaceWriterForTest`. `BrokerDiagnosticsWriterReplacementTests` resets, replaces first, then enables, yet coverage still shows the branch untaken; another test's thread may recreate the writer in that window. The test is kept as a behavior test. |
| BrokerDiagnostics.cs 60, 62, 65 | b (Linux only) | Non-Windows branch of `LogPath`; covered by the Linux job |
| BrokerDiagnosticsLogFilter.cs 116, 208 | b (Linux only) | `!IsWindows` returns; covered by the Linux job |
| JournalBrokerHost.Sources.cs 46, 47 | b (Linux only) | `PlatformNotSupportedException` branch; covered by the Linux job |
| NtfsVolumeInformation.cs 56, 57 | b (Linux only) | Same |
| MftVolume.Journal.cs 156, 168 | c | Defensive `InvalidOperationException` when the callback state has the wrong type; the state is always built by the two private callers |
| MftRecord.cs 121, 166 | c (dead code) | `FileName`/`FullPath` branch `_materialized == false && _fileName/_fullPath != null`: the pointer constructor sets both to null and the string constructor sets `_materialized = true`, so it cannot be reached. Controller decision: delete. |

Counts (start): a = 37 lines, b = 9 lines (all Linux-only branches), c = 10 lines (Session 132/146, Control 189/190, Scan 70, BrokerDiagnostics 147, MftVolume.Journal 156/168, MftRecord 121/166). Remaining uncovered at the end: 19 = the 9 b + 10 c.

Class (b) in the strict sense (elevation or real NTFS) contributed no lines in this namespace; the b lines here are platform branches.

## Suspected defects

None. All new tests passed against the code.

## Adjacent notes

- `MftRecord` dead branch above (lines 121 and 166).
- Session.cs drain loop second iteration (132, 146) may be removable if the controller confirms nothing can be tracked after end.

## Files (all new, CRLF)

`MFTLib.Tests/`: `JournalBrokerHostChannelTests.ChannelLifetime.cs`, `JournalBrokerHostLivenessTests.StalledPipe.cs`, `ParseThreadAllocatorTests.cs`, `BrokerProcessTests.ControlPipe.cs`, `BrokerProtocolTests.MalformedPayloads.cs`, `DefaultElevatedEntryRunnerConnectTests.cs`, `BrokerDiagnosticsWriterReplacementTests.cs`.

## Commands

- `git -C C:\Users\mtsch\MFTLib-worktrees\265-CB rev-parse HEAD` = `ca649bba0a69bf9dc83aeaa60ebb522485063fe6` before work.
- `pwsh -NoProfile -File scripts\run-coverage.ps1 -NonInteractive` (baseline), then `python ...\nscov.py MFTLib.Tests\coverage.xml "MFTLib(other)"`: 2723/2779 97.98.
- Targeted: `dotnet test ... --no-build --filter "FullyQualifiedName~JournalBrokerHostChannelTests|...LivenessTests|...BrokerProcessTests|...BrokerProtocolTests|...ParseThreadAllocatorTests|..."`: all passed.
- Final coverage run: Total 1905, Passed 1899, Failed 0, Skipped 6; nscov 2760/2779 99.32.
- `aislop scan .`: 99/100, 6 warnings (baseline four plus the ruled constructor warning; the sixth counted line is the second doc-comment finding).
- Lock released. `git -C C:\Users\mtsch\MFTLib status --short`: empty.

## Fix round 1

Commit: see `git log task/265-CB -1` ("Broker coverage tests drive races deterministically and poll nothing"). Tests only.

1. aislop: the scan is exactly the four baseline warnings (NativeSeamIsolationFixtures 73 and 79, CachedBlockDeletionOutcome 8 and 10) plus the ruled 8-parameter constructor warning (5 warnings, 99/100). The earlier "sixth" was the doc-comment finding counted per line (2 lines, 1 rule), not an extra finding. Unused usings and a closure warning that this round briefly introduced were removed.
2. Classification: the drain re-snapshot loop (Session.cs 132/146) is now covered by `ControlSessionEnds_ChannelTrackedWhileDrainIsWaiting_DrainWaitsForItToo` (control reply held mid-write after the end check, client closes, drain's grace timer signalled through `TimerSignalingClock`, reply released, channel disposal gated so the second snapshot sees an unfinished task). BrokerDiagnostics.cs 147 was the non-null arm (every earlier test reset the writer first): covered by `ReplaceWriterForTest_ReplacingALiveWriter_RoutesLaterLinesOnlyToTheReplacement`. BrokerProcess.Control.cs 189/190 is class (b)-race: `SemaphoreSlim.WaitAsync` throws for an already-cancelled token even when the lock is free, so the branch needs a cancel landing between the lock hand-off and the next statement; `_controlWriteLock` is private and there is no seam to order that.
3. Test rules: the 20 ms real `Task.Delay` poll helper `AdvanceUntilCompleteAsync` (it predated CB; used by 5 tests) is replaced by `AdvanceWhenTimerExistsAsync` over `TimerSignalingClock` (wait for the timer by due time and occurrence, advance once, bounded wait). The Stalled-write test's interval loop is bounded by 20 intervals and fails with a message.

Updated table of remaining uncovered lines (16 of 2779; MFTLib(other) 2763/2779 = 99.42 percent):

| Line(s) | Class | Reason |
|---|---|---|
| BrokerDiagnostics.cs 60, 62, 65 | b (Linux only) | non-Windows `LogPath` branch |
| BrokerDiagnosticsLogFilter.cs 116, 208 | b (Linux only) | `!IsWindows` returns |
| JournalBrokerHost.Sources.cs 46, 47 | b (Linux only) | `PlatformNotSupportedException` branch |
| NtfsVolumeInformation.cs 56, 57 | b (Linux only) | same |
| BrokerProcess.Control.cs 189, 190 | b-race | see item 2; no seam |
| BrokerProcess.Scan.cs 70 | c | closing brace after a try whose body always returns |
| MftVolume.Journal.cs 156, 168 | c | defensive throws on callback state the private callers always build correctly |
| MftRecord.cs 121, 166 | c | dead: pointer constructor sets `_fileName`/`_fullPath` null, string constructor sets `_materialized` |

Verification: targeted classes (JournalBrokerHostChannelTests, JournalBrokerHostLivenessTests, BrokerDiagnosticsWriterReplacementTests) 64 passed; final `run-coverage.ps1 -NonInteractive`: Total 1907, Passed 1901, Failed 0, Skipped 6 (one earlier run had an unrelated `FileIndexWatchRecoveryTests.DisposeDuringRecovery_CancelsIt` failure in the Index namespace that did not recur; not touched by this lane). nscov: MFTLib(other) 2763/2779 99.42, MFTLib.Index 3289/3339 98.5. aislop 99/100 as above. Primary checkout status empty.
