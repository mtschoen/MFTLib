# Task C6 report: watch channels and BrokerIndexWatchSource

Branch task/265-C6, base 9725d0b8a246cd56b9a4cefa3e17fd1e5c024d65 (verified as HEAD first), commit 49f5f23 "Each drive watch runs on its own broker pipe". Worktree C:\Users\mtsch\MFTLib-worktrees\265-C6.

## Implemented

- MFTLib/Broker/Client/BrokerProcess.Watch.cs: OpenWatchChannelAsync(IndexWatchTarget, CancellationToken) over OpenChannelAsync (which already closes its pipe on a failed or cancelled open), writing StartWatch.
- BrokerWatchChannel.cs: IIndexDriveWatch over BrokerDriveChannel. ReadAsync reads straight off the pipe: JournalBatch -> JournalBatch, CaughtUp -> DriveCaughtUp, Heartbeat skipped, Error -> DriveWatchFaultException(drive, message), Stalled -> BrokerChannelLostException(drive, hostMessage) (thrown by BrokerDriveChannel.ReadAsync), EOF / I/O failure / any other frame -> BrokerChannelLostException(drive, ...). Cancelling the read token returns promptly and never disposes the channel. DisposeAsync closes the pipe.
- BrokerIndexWatchSource.cs: thin StartAsync (null check, ThrowIfCancellationRequested so an already-cancelled start connects to nothing, connect, open watch channel). No per-drive state.
- BrokerMftBlockProducer.CreateWatchSource().
- Test support: TestSupport/ScriptedWatchBrokerHarness.cs rewritten over InProcessBroker (BrokerTestHarness): per-drive ScriptedHostWatch / ScriptedWatchRun (Push, Fail, End, Release, IgnoreCancellationInNextRun, Entered/Cancelled/Finished), EndHostAsync (next control write is an unreadable frame, so the host session fails and closes every pipe, same route as BrokerProcessTests.HostFault), ChangeSignal and WatchReads helpers. No harness member observes host failures (C2-Q1). WatchHarness gained a second constructor taking an IIndexWatchSource (null keeps its fake). The A5-deleted members ForwardedFrameKinds and WrapClientTransport were not needed and not restored.
- Rulings: W5-1 (post-fault state assertions use Channel faults; Error-frame cases assert only WatchFault(Drive, X)), W5-3 (HostError_IsDriveWatchFault), C2-Q1, C5-Q1, N-3 followed.

## Base-to-new method table (13 base files at c1d43784; 63 methods: 27 ported, 36 dropped)

BrokerIndexWatchSourceTests
- WatchSource_StartsOneWatchForEveryTargetAndTagsEachBatch -> WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle
- WatchSource_LeavesTheBorrowedClientReadyForAnotherWatch -> WatchSource_LeavesTheBorrowedProcessReadyForAnotherWatch
- WatchSource_CompletesWhenTheTokenIsCancelled -> same name
- WatchSource_CancellationLeavesBorrowedClientReadyForRestart -> WatchSource_CancellationLeavesBorrowedProcessReadyForRestart
- WatchSource_DoesNotFaultTheStreamForOneDrivesError -> WatchSource_OneDrivesErrorDoesNotEndAnotherDrivesHandle
- WatchSource_CompletesNormallyWhenAllDriveSourcesComplete -> WatchSource_HostClosingThePipeIsAChannelLossNotAnEnd
- WatchSource_DoesNotConnectWhenTokenIsAlreadyCancelled -> same name
- WatchSource_RejectsNullTargetsBeforeConnecting -> WatchSource_RejectsANullTargetBeforeConnecting
- DROPPED WatchSource_ReportsReadyOncePublished_BeforeAnyItemAndWithAPerDriveCallAccepted: no readiness report or per-drive arm/disarm
- DROPPED WatchSource_EmptyTargetsCompletesNormally: a start opens one drive, no target list
- DROPPED StartWatching_WhileAStreamIsAlreadyRunning_ThrowsInvalidOperationException: no shared stream or session

BrokerIndexWatchSourceCaughtUpTests
- WatchSource_SurfacesACaughtUpFrameAsADriveCaughtUpItem -> same name
- WatchSource_AfterAReArm_SurfacesOnlyTheFreshArmsMarker -> WatchSource_AfterARestart_SurfacesOnlyTheFreshWatchsMarker

BrokerIndexWatchSourceFaultTests
- WatchSource_YieldsAPerDriveFaultItemAndKeepsTheOtherDriveFlowing -> WatchSource_OneDrivesFaultIsThatHandlesAndTheOtherDriveKeepsFlowing
- WatchSource_CompletesAfterEveryDriveHasFaulted -> WatchSource_EveryDriveFaultsOnItsOwnHandle
- WatchSource_FaultsTheWholeStreamWhenItCannotConnect -> WatchSource_StartFailsWhenItCannotConnect
- DROPPED WatchSource_RejectsTwoTargetsForOneDriveBeforeConnecting: no target list
- DROPPED DisarmDriveAsync_ThatThrows_LeavesNoMarkerBlockingTheStreamFromCompleting: no disarm or merged stream

BrokerLiveWatchErrorTests
- LiveWatch_ErrorFrameForDrive_FaultsThatDrivesBatchSource -> LiveWatch_ErrorFrameForDrive_FaultsThatDrivesHandle
- LiveWatch_ErrorFrameForOneDrive_OtherDrivesKeepStreaming -> same name
- LiveWatch_ErrorFrameBeforeSubscribe_LateSubscriberGetsFault -> LiveWatch_ErrorFrameBeforeRead_LateReaderGetsFault
- LiveWatch_WarningFrameForDrive_FaultsThatDrivesBatchSourceAndLeavesTheOthers -> LiveWatch_FrameOutsideAWatch_LosesThatDrivesChannelAndLeavesTheOthers (Warning kind no longer exists)
- DROPPED LiveWatch_WarningFrameForDrive_DisarmsThatDriveSoALaterBatchForItIsDropped: no demux, routing or disarm

BrokerFileIndexRescanTests
- Rescan_UsesTheSharedBrokerAndRearmsOnlyItsDrive -> RescanOfT_OverBroker_ReopensOnlyTsChannel
- StartWatchingAsync_WaitsForTheBrokerConnection_AndARescanNeedsNoDeliveredItem, ..._WhenTheBrokerConnectionFails_ThrowsItAndLeavesTheIndexStartable, ..._CancelledWhileTheBrokerConnects_ThrowsAndLeavesTheIndexStartable -> same names
- DROPPED StartWatchingAsync_WaitsForTheBrokerStreamToBePublished_SoAnImmediateRescanRearmsOnlyItsDrive: no stream publication gate or re-arm

BrokerDeathTests
- BatchSource_PipeDeath_FiresBrokerDiedOnce_AndThrowsInvalidOperation -> WatchChannel_ProcessDeath_FiresEndedOnce_AndReadThrowsChannelLost
- BatchSource_DeathBeforeSubscribe_LateSubscribersThrow_BrokerDiedFiresOnce -> WatchChannel_DeathBeforeRead_LateReadsThrow_EndedFiresOnce
- ControlOperation_AfterBrokerDeath_ThrowsConnectionEnded -> ControlOperation_AfterProcessDeath_ThrowsChannelLost

Index/WatchFailureObservationTests: BrokerFailure_DoesNotLeaveUnobservedInternalTasks (4 data rows), BrokerReader_ReportsFailureWithoutAnUnobservedTask, CollectionProbe_DetectsAnAbandonedFaultedTask -> same names.

Six files the brief did not list, every method DROPPED (no arm/disarm, merged stream, arm epoch, shared control-pipe demux, StopLiveWatch, StartWatch send gate or abandoned-send teardown exists; a cancelled start closes its own pipe, and TimedOutStop_ThenNewWatch_RunsUndisturbed covers a late close):
- BrokerIndexWatchSourceArmingTests (9): ArmDriveAsync_OnALiveStream_AddsThatDrivesBatchesWithoutRestartingTheOthers; ArmDriveAsync_OnADriveWhoseReaderIsStillLive_ReplacesItWithoutHanging; ArmDriveAsync_OnADriveWhoseReaderIsStillLive_LeavesTheOtherDriveStreaming; DisarmDriveAsync_OnALiveStream_StopsThatDrivesItemsAndLeavesTheOtherFlowing; ArmDriveAsync_AfterADisarm_DropsAnItemTheOldReaderHadAlreadyQueuedAndYieldsTheOnesAfter; ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm; ArmDriveAsync_WithNoStreamRunning_ThrowsInvalidOperationException; DriveFaulting_WhileAnotherDriveIsBetweenDisarmAndReArm_KeepsTheStreamAlive; ReaderFaulting_WhileItsOwnDisarmIsInFlight_DoesNotCompleteTheMergedStream
- BrokerIndexWatchSourceArmingTests.LastDrive (2): ArmDriveAsync_FailingWithNoOtherReaderLeft_EndsTheMergedStreamWithTheFailure; DisarmDriveAsync_FailingWithNoOtherReaderLeft_EndsTheMergedStreamWithTheFailure
- BrokerPerDriveArmTests (8): SendStartWatchAsync_ForASecondDrive_ArmsItWithoutStartingASecondDemux; SendStartWatchAsync_ForAnArmedDrive_ReplacesItsChannelSoTheEarlierSubscriberEnds; SendStartWatchAsync_ForADriveWhoseChannelWasFaulted_GivesTheNextSubscriberALiveChannel; SendDisarmDriveAsync_CompletesOnlyThatDrivesChannelAndWritesTheFrame; SendDisarmDriveAsync_WithNoWatchRunning_ThrowsInvalidOperationException; Demux_DropsAJournalBatchForADriveThatIsNotArmed; Demux_ErrorFrameDisarmsThatDriveSoALaterBatchForItIsDropped; StopLiveWatchAsync_ClearsEveryArmedDriveSoTheNextGenerationStartsClean
- BrokerPerDriveArmTests.Recovery (4): SendStartWatchAsync_WhenFirstWriteFailsAfterTransmissionStarts_LaterStartCreatesWorkingDemux; SendStartWatchAsync_WhenConcurrentClaimantIsCancelledBeforeTransmission_OtherStartCreatesWorkingDemux; StopLiveWatchAsync_WithLeakedClaimAndNoDemux_AllowsFreshGenerationAndDropsStaleDriveBatch; SendStartWatchAsync_AfterDemuxEndedWithoutStop_ThrowsInvalidOperationException
- BrokerWatchSourceAbandonedStartTeardownTests (2): StartWaitingForAnAbandonedStart_WhoseStopIsNeverAcknowledged_FailsInsteadOfWatching; AbandonedStart_WhoseStopIsNeverAcknowledged_FailsTheNextStartEvenAfterItsTeardownFinished
- BrokerWatchStartSendCancellationTests (4): StartWatchingAsync_CancelledWhileTheStartWatchSendIsBlocked_ThrowsAndTheNextStartWatches; StopWatchingAsync_WhileTheStartWatchSendIsBlocked_CompletesAndTheNextStartWatches; DisposeAsync_WhileTheStartWatchSendIsBlocked_CompletesAndTheSourceWatchesForTheNextIndex; StartWatchingAsync_IssuedWhileAnAbandonedSendIsBlocked_WaitsForItsTeardownAndThenWatches

New tests with no base method: HostError_IsDriveWatchFault, LiveWatch_HeartbeatsBetweenFrames_AreSkipped, LiveWatch_StalledFrame_LosesTheChannelWithTheHostsMessage, OneChannelLost_OnlyThatDriveFaults, ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce, ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal, TimedOutStop_ThenNewWatch_RunsUndisturbed, WatchSource_SurfacesTheMarkerOnlyOnceTheBacklogReachesTheTip.

## RED evidence (W40-R1): scratch mutations, none committed, tree restored and verified with cmp

Command form: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~<Test>" after rebuilding the test project with the mutation.

| Test | Mutation | Failing output |
|---|---|---|
| ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal | BrokerWatchChannel.ReadAsync disposes the channel on cancellation | Failed! Failed: 1, Passed: 0; at WatchReads.NextBatchAsync from BrokerIndexWatchSourceTests.cs:211 (re-read of the same handle after cancel) |
| TimedOutStop_ThenNewWatch_RunsUndisturbed | BrokerWatchChannel.DisposeAsync returns without closing the pipe | Failed TimedOutStop_ThenNewWatch_RunsUndisturbed [10 s]; threw System.TimeoutException (held host watch's pipe never closes); Failed: 1 |
| OneChannelLost_OnlyThatDriveFaults | EOF thrown as DriveWatchFaultException instead of BrokerChannelLostException | Failed [10 s]; TimeoutException waiting for WatchFault(Channel, T); Failed: 1 |
| HostError_IsDriveWatchFault | Error frame thrown as BrokerChannelLostException | Failed [10 s]; TimeoutException waiting for WatchFault(Drive, T); Failed: 1 |
| ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce | lost channel thrown with null drive letter | Failed [175 ms]; Assert.AreEqual failed. Expected:<T>. Actual:<(null)>. |
| RescanOfT_OverBroker_ReopensOnlyTsChannel | OpenWatchChannelAsync calls CloseChannelsAsync first | Failed [10 s]; TimeoutException; Failed: 1 |

The three other new tests are not named in the brief and have no RED evidence. During authoring, ScriptedWatchRun.Cancelled lost a callback race (LIFO cancellation callbacks); fixed. The 38 targeted tests ran 8 times consecutively, all green.

## Verification

- Targeted: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerIndexWatchSource|FullyQualifiedName~BrokerLiveWatchErrorTests|FullyQualifiedName~BrokerFileIndexRescanTests|FullyQualifiedName~BrokerDeathTests|FullyQualifiedName~WatchFailureObservationTests" -> "Passed!  - Failed: 0, Passed: 38, Skipped: 0, Total: 38". Adding NativeSeamIsolationTests and NamespaceBoundaryTests: 54 passed.
- Whole suite: .\scripts\run-coverage.ps1 -NonInteractive -> "Total tests: 1793 / Passed: 1787 / Skipped: 6", "Line coverage: 97.7%", Test Run Successful. It ran before the aislop fixes below (test-code only); targeted classes re-run green after them.
- aislop: aislop scan . -> "99 / 100  Healthy  0 errors  5 warnings": NativeSeamIsolationFixtures.cs:73 and :79 (AsyncFixer01), CachedBlockDeletionOutcome.cs:8 and :10 (redundant doc), plus the ruled 8-param JournalBrokerHost warning. First scan had 18 (10 AccessToDisposedClosure, 3 AsyncFixer01, 1 missing summary); all fixed in the tests, no rule disabled.
- CRLF kept on all new and modified .cs files; no em-dashes or en-dashes.

## Files changed

Production: MFTLib/Broker/Client/BrokerProcess.Watch.cs, BrokerWatchChannel.cs, BrokerIndexWatchSource.cs (new); BrokerMftBlockProducer.cs (CreateWatchSource).
Tests (MFTLib.Tests): BrokerIndexWatchSourceTests.cs, BrokerIndexWatchSourceCaughtUpTests.cs, BrokerIndexWatchSourceFaultTests.cs, BrokerLiveWatchErrorTests.cs, BrokerFileIndexRescanTests.cs, BrokerDeathTests.cs, Index/WatchFailureObservationTests.cs, TestSupport/ScriptedWatchBrokerHarness.cs (all new); TestSupport/WatchHarness.cs (second constructor).

## Concerns

- The WatchFailureObservationTests index scenarios use ScriptedBroker plus a Stalled frame (the only channel-loss frame carrying text); the old all-drives WaitForCatchUpAsync(token) assertion is dropped because that overload arrives in B7.
- LiveWatch_FrameOutsideAWatch_... replaces the two Warning-frame cases (the Warning kind no longer exists).
- Client stall limit is not reached in these tests (millisecond runs on the system clock); C7 covers it.
- B6 changes post-fault assertions only for Drive faults; post-fault-state cases here use Channel faults.

## Primary checkout

git -C C:\Users\mtsch\MFTLib status --short -> (empty); primary HEAD is main.

## Fix round 1

Commit 069def5 "Broker watch tests prove a timed-out stop and observe faults without polling".

1. RED evidence: all six mutations re-run against the final code; real output below (from .superpowers/red3.log in the worktree). Production restored byte-for-byte afterwards.

### ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal: ReadAsync disposes the channel when the read is cancelled
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal"
  Failed ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal [56 ms]
  Error Message:
   at MFTLib.Tests.BrokerIndexWatchSourceTests.ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal() in C:\Users\mtsch\MFTLib-worktrees\265-C6\MFTLib.Tests\BrokerIndexWatchSourceTests.cs:line 211
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 65 ms - MFTLib.Tests.dll (net10.0)
```

### TimedOutStop_ThenNewWatch_RunsUndisturbed: DisposeAsync leaves the pipe open
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~TimedOutStop_ThenNewWatch_RunsUndisturbed"
  Failed TimedOutStop_ThenNewWatch_RunsUndisturbed [10 s]
  Error Message:
System.TimeoutException: The operation has timed out.
      at MFTLib.Tests.BrokerIndexWatchSourceTests.TimedOutStop_ThenNewWatch_RunsUndisturbed() in C:\Users\mtsch\MFTLib-worktrees\265-C6\MFTLib.Tests\BrokerIndexWatchSourceTests.cs:line 240
   at MFTLib.Tests.BrokerIndexWatchSourceTests.TimedOutStop_ThenNewWatch_RunsUndisturbed() in C:\Users\mtsch\MFTLib-worktrees\265-C6\MFTLib.Tests\BrokerIndexWatchSourceTests.cs:line 260
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

### OneChannelLost_OnlyThatDriveFaults: an ended pipe reads as a drive fault, not a lost channel
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OneChannelLost_OnlyThatDriveFaults"
  Failed OneChannelLost_OnlyThatDriveFaults [10 s]
  Error Message:
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

### HostError_IsDriveWatchFault: an Error frame reads as a lost channel, not a drive fault
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~HostError_IsDriveWatchFault"
  Failed HostError_IsDriveWatchFault [10 s]
  Error Message:
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

### ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce: the lost channel does not name its drive
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce"
  Failed ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce [101 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<T>. Actual:<(null)>.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 111 ms - MFTLib.Tests.dll (net10.0)
```

### RescanOfT_OverBroker_ReopensOnlyTsChannel: opening a watch channel closes every other channel first
```
COMMAND: dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~RescanOfT_OverBroker_ReopensOnlyTsChannel"
  Failed RescanOfT_OverBroker_ReopensOnlyTsChannel [126 ms]
  Error Message:
   Assert.IsTrue failed.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 136 ms - MFTLib.Tests.dll (net10.0)
```

2. TimedOutStop_ThenNewWatch_RunsUndisturbed now wraps the source (HoldFirstDisposalSource) so the first handle's DisposeAsync waits on a TestGate. Flow: the stop starts; the test waits for the teardown to reach the gate; asserts the stop has not completed; cancels the stop's token; asserts OperationCanceledException; asserts the host watch's pipe is still open; releases the gate; starts the replacement watch and applies its batch; releases the held host task with a stale batch; asserts only fresh.txt and after.txt applied and no faults. The empty catch is gone.
3. WatchFailureObservationTests.CountUnobservedAsync: Task.Delay(25 ms) x40 replaced by Task.Yield() under a 400-iteration bound; no wall-clock wait remains.

### Verification after the last edit
- Targeted (5 consecutive runs): Passed!  - Failed: 0, Passed: 38, Skipped: 0, Total: 38.
- Whole suite: .\scripts\run-coverage.ps1 -NonInteractive -> Test Run Successful. Total tests: 1793, Passed: 1787, Skipped: 6, Line coverage: 97.7%.
- aislop scan . -> 99 / 100  Healthy  0 errors  5 warnings (NativeSeamIsolationFixtures.cs:73 and :79, CachedBlockDeletionOutcome.cs:8 and :10, ruled 8-param JournalBrokerHost).
- Primary checkout: git -C C:\Users\mtsch\MFTLib status --short is empty.
