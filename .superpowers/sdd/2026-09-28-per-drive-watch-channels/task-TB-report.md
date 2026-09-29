# Task TB report: account for every base test method the per-drive redesign removed

Status: DONE. Branch `task/265-TB`, base `d1a20a999a45ad67557ea9536e759d8a1da73e98` (verified first), commit `a732d47`.

## Counts

The audit's two not-accounted tables hold 245 rows (195 tranche B, 50 tranche I; the audit summary's 44 counts the tranche I
rows inside deleted files plus 6 outside, the table itself lists 50). Every row is assigned exactly once:

| list | rows |
| --- | ---: |
| ported (9 new test methods) | 10 |
| covered by | 122 |
| dropped | 101 |
| left to B7 | 12 |
| total | 245 |
| suspected defects | 0 |

Tranche split: B 195, I 50.

## Method

- Rows were extracted mechanically from the audit tables (`tranche-B-audit.md`), and each row's method name was checked against the
  `[TestMethod]` names of `git show 3597586:<path>` (all 245 found). HEAD names were extracted the same way from `HEAD`; every
  'covered by' target was asserted to exist at HEAD by the classification script before any list was written.
- Behavior checks for the dropped groups, one focused grep each over `MFTLib/` and `MFTLibTestExtensions/`: `ArmEpoch`,
  `DisarmDrive`, `EndWatch`, `EndWatchAck`, `WriteWarning`, `BrokerFrameKind.Shutdown`, `JournalBrokerScanSession`,
  `JournalBrokerClient`, `ScanSessionTestHarness`, `ReplaceWatchCursors`, `StartFromCursors`, `StopLiveWatch`, `CreateBatchSource`,
  `reportStreamReady`, `BrokerDied`, `WatchSpec`, `ParseWatchSpec` all return 0 files. `BrokerFrameKind` has no Warning or
  Shutdown kind. `BrokerProcess.Ended` is a plain event (no late-subscriber replay); `IIndexWatchSource` has only `StartAsync`.
- Several audit rows were already accounted for by rename at HEAD but missed by the audit's exact-substring search (the host tests lost
  their `ServeOnce_` prefix, the index rescan tests say Restart instead of ReArm, the ABI tests say Two, `EndWatch_IdleNativeRead_
  Acknowledges` became `CloseWatchPipe_IdleNativeRead_EndsChannel`, and a commit message abbreviated `..._RestartsIt`); those are
  listed under covered by.
- One row deserves a note: `RescanAsync_WhenTheEndedSessionRecordedASubscriberFault_CarriesItIntoAFreshSession` is dropped, not ported,
  because under the per-drive contract a subscriber fault is the outstanding fault of its own watch instance and goes with that
  instance when a rescan replaces it (spec 2.6; `FileIndex.DriveRuntime.cs` `WatchInstance.OutstandingFault`, taken only by stop at
  `FileIndex.WatchDrive.cs:74`; `RecoveredDrive_StopDoesNotRethrowItsFault`). Porting the base assertion (stop still rethrows it)
  would pin the old contract.

## New test methods (all in existing files of their area)

- `BrokerProcessTests.cs`: `ControlExchange_DemuxExit_CompletesPendingQuery` (3 data rows: unroutable frame, truncated frame, eof).
- `BrokerProcessTests.Scan.cs`: `ScanDrive_TruncatedFrame_IsChannelLost`, `ScanDrive_HeaderOnlyThenEof_IsChannelLost`,
  `ScanDrive_WithoutKeepFileNames_SendsAnEmptyList`, `Producer_ConnectsExactlyOnce`.
- `BrokerLiveWatchErrorTests.cs`: `LiveWatch_TruncatedFrame_LosesThatDrivesChannelWithTheTruncationMessage`.
- `FileIndexPerDriveWatchTests.Lifecycle.cs`: `RescanAsync_CancelledWhileTheWatchIsStarting_LeavesTheDriveUntouched`,
  `StartWatchingAsync_WhileARescanProduces_WatchesOnceFromTheFreshCursor`.
- `FileIndexWatchRescanTests.cs`: `RescanAsync_OfAHealthyDrive_WhenTheOtherDriveFaultsMidScan_RestartsItAndLeavesTheOtherFaulted`.

All ride existing harnesses (`ScriptedBroker`, `InProcessBroker`, `ScriptedWatchBrokerHarness`, `WatchHarness`); no test-support change,
no production change, no real-time sleep or elapsed assertion, every await bounded (`HangGuard`/`WaitAsync`), no `FileIndex` opened
without the harness's owned cache directory. Host faults reach tests only through production surfaces (closed pipes, raw frames on
the scripted pipes). The first run of `LiveWatch_TruncatedFrame_...` failed on my own omission (the scripted host never sent
`CaughtUp`, so the first read hung to the hang guard); fixed in the test, not a product finding.

## Suspected defects

None. Every ported case passed against the code at full assertion strength.

## Commands and outputs

- `git -C C:\Users\mtsch\MFTLib-worktrees\265-TB rev-parse HEAD` -> `d1a20a999a45ad67557ea9536e759d8a1da73e98` (matches).
- `.\init.ps1`, then `.\init.ps1 -Build`: `Solution built (Release|x64)`.
- Targeted: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerLiveWatchErrorTests|FullyQualifiedName~FileIndexPerDriveWatchTests|FullyQualifiedName~FileIndexWatchRescanTests"`
  -> `Passed!  - Failed: 0, Passed: 121, Skipped: 0, Total: 121` (final run, after the last edit).
- Whole suite, once after the last edit: `.\scripts\run-coverage.ps1 -NonInteractive` -> exit 0, `Total tests: 1844`, `Passed: 1838`,
  `Failed: 0` (none reported), `Skipped: 6`, `Test Run Successful.`
- `aislop scan .` once after it -> `99 / 100 Healthy, 0 errors, 5 warnings, 0 fixable`: exactly the four baseline warnings
  (`NativeSeamIsolationFixtures.cs:73`, `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8`, `:10` redundant doc) plus the ruled
  8-parameter `JournalBrokerHost` constructor (`JournalBrokerHost.cs:46`). Nothing else. (An earlier scan reported one extra
  `AccessToModifiedClosure` at `BrokerProcessTests.Scan.cs:413`, a captured counter in `Producer_ConnectsExactlyOnce`; fixed by
  counting through a `ConcurrentQueue`, and the suite and scan were re-run in full afterwards.) The scan exits 1 because the
  score is below `failBelow: 100`, as it does on the untouched base.
- CRLF preserved in all five touched files (`file` reports CRLF).

## Files changed (5, tests only)

- `MFTLib.Tests/BrokerProcessTests.cs`
- `MFTLib.Tests/BrokerProcessTests.Scan.cs`
- `MFTLib.Tests/BrokerLiveWatchErrorTests.cs`
- `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.Lifecycle.cs`
- `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs`

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short` -> (empty)

## Lists (identical to the commit message body)

Ported (10 base methods, 9 new test methods; base name -> new name, file):
- ControlExchange_DemuxExit_CompletesPendingQuery -> same name, BrokerProcessTests.cs (data rows now unroutable frame, truncated frame, eof)
- CreateBatchSource_DemuxReadThrows_SignalsBrokerDeathWithExceptionMessage -> LiveWatch_TruncatedFrame_LosesThatDrivesChannelWithTheTruncationMessage (renamed), BrokerLiveWatchErrorTests.cs
- ArmScanAndCatchUpAsync_HeaderOnlyThenEof_ThrowsEndOfStreamException -> ScanDrive_HeaderOnlyThenEof_IsChannelLost (renamed), BrokerProcessTests.Scan.cs
- ArmScanAndCatchUpAsync_ProfileOverload_WithoutKeepFileNames_SendsEmptyList -> ScanDrive_WithoutKeepFileNames_SendsAnEmptyList (renamed), BrokerProcessTests.Scan.cs
- ArmScanAndCatchUpAsync_TruncatedFrame_ThrowsEndOfStreamException -> ScanDrive_TruncatedFrame_IsChannelLost (renamed), BrokerProcessTests.Scan.cs
- StartAsync_ConnectsExactlyOnce -> Producer_ConnectsExactlyOnce (renamed), BrokerProcessTests.Scan.cs
- StartAsync_WithoutKeepFileNames_SendsEmptyNameList -> ScanDrive_WithoutKeepFileNames_SendsAnEmptyList (renamed), BrokerProcessTests.Scan.cs
- RescanAsync_OfAHealthyDrive_WhenTheOtherDriveFaultsMidScan_ReArmsOnTheSameSession -> RescanAsync_OfAHealthyDrive_WhenTheOtherDriveFaultsMidScan_RestartsItAndLeavesTheOtherFaulted (renamed), FileIndexWatchRescanTests.cs
- RescanAsync_CancelledWhileTheSessionIsStarting_LeavesTheDriveUntouched -> RescanAsync_CancelledWhileTheWatchIsStarting_LeavesTheDriveUntouched (renamed), FileIndexPerDriveWatchTests.Lifecycle.cs
- RescanAsync_OntoASessionThatStartedWhileItsProducerRan_ArmsOnlyOnceThatSessionIsReady -> StartWatchingAsync_WhileARescanProduces_WatchesOnceFromTheFreshCursor (renamed), FileIndexPerDriveWatchTests.Lifecycle.cs

Covered by (122 base methods; base name -> HEAD name):
- ScanProgressFrame_RoundTrips_AllFields -> ScanProgressFrame_RoundTrips_AllFieldsWithEmptyDrive
- VolumeInfoFrame_RoundTrips_AllFields -> VolumeInfoFrame_RoundTrips_RequestIdAndAllFields
- ArmAndScanFrame_RoundTrips_DrivesSpec -> ArmAndScanFrame_RoundTrips_SectionAndProfile
- CursorFrame_RoundTrips_DriveAndCursor -> CursorFrame_RoundTrips_Cursor
- ErrorFrame_RoundTrips_PerDriveMessage -> ErrorFrame_RoundTrips_RequestIdAndMessage ; ErrorFrame_OnDrivePipe_RoundTripsRequestIdZero
- ScanReadyFrame_RoundTrips_MmfHandshake -> ScanReadyFrame_RoundTrips_Counts
- StartWatchFrame_RoundTrips_DrivesSpec -> StartWatchFrame_RoundTrips_Cursor
- Factory_GrowUsnJournal_PopulatesKindDriveAndSizes -> Factory_GrowUsnJournal_PopulatesKindRequestIdDriveAndSizes
- Factory_UsnJournalSettings_PopulatesKindDriveAndSizes -> Factory_UsnJournalSettings_PopulatesKindRequestIdAndSizes
- GrowUsnJournalFrame_RoundTrips_DriveAndSizes -> GrowUsnJournalFrame_RoundTrips_RequestIdDriveAndSizes
- UsnJournalSettingsFrame_RoundTrips_DriveAndSizes -> UsnJournalSettingsFrame_RoundTrips_RequestIdAndSizes
- DisposeAsync_PipeAlreadyClosed_SwallowsShutdownWriteFailure -> DisposeAsync_ControlPipeAlreadyClosed_DoesNotThrow ; Dispose_ControlCloseThrows_CompletesTeardownWithoutThrowing
- SpawnAndConnectAsync_BrokerNeverConnects_TimesOutAndDisposesServer -> LaunchAsync_NeverConnects_TimesOut
- SpawnAndConnectAsync_CallerCancellationRequested_ThrowsOperationCanceledException -> LaunchAsync_CallerCancellationRequested_ThrowsOperationCanceled
- SpawnAndConnectAsync_DefaultTimeoutOverridden_TimesOutAndDisposesServer -> LaunchAsync_DefaultTimeoutOverridden_TimesOut
- SpawnAndConnectAsync_LaunchDeclined_ThrowsAndDisposesServer -> LaunchAsync_LaunchDeclined_Throws
- SpawnAndConnectAsync_NegativeTimeout_ThrowsArgumentOutOfRangeException -> LaunchAsync_NegativeTimeout_ThrowsArgumentOutOfRange
- SpawnAndConnectAsync_NullLaunchBroker_ThrowsArgumentNullException -> LaunchAsync_NullLaunchBroker_ThrowsArgumentNull
- StopLiveWatchAsync_NoAckWithinTimeout_ForcesDemuxDown -> TimedOutStop_ThenNewWatch_RunsUndisturbed
- TryNormalizeDriveLetter_InvalidDrive_ReturnsFalseAndAnEmptyNormalizedDrive -> NormalizeDriveLetter_InvalidInputs_ThrowsArgumentException
- TryNormalizeDriveLetter_Null_ThrowsArgumentNullException -> NormalizeDriveLetter_InvalidInputs_ThrowsArgumentException
- TryNormalizeDriveLetter_ValidDrive_ReturnsTrueAndUppercaseLetter -> NormalizeDriveLetter_ValidDriveFormats_ReturnsUppercaseLetter
- ControlExchange_ScanWhileWatching_RoutesQueryAndCatchUpWithoutAnotherReader -> RescanOfT_OverBroker_ReopensOnlyTsChannel ; ScanDrive_TwoDrivesConcurrently_BothComplete
- ControlExchange_UnexpectedFrameKindDuringScan_ThrowsInvalidData -> ScanDrive_CatchUpLostBeforeScanReady_IsProtocolError ; ScanDrive_JournalBatchAfterCatchUpLost_IsProtocolError
- ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation -> HostFault_WithPendingRequest_FailsRequestEndsProcessOnceAndDisposesQuietly ; Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe ; HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce
- CreateBatchSource_CancelledBetweenFrames_ChannelCompletesCleanly -> WatchSource_CompletesWhenTheTokenIsCancelled ; ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal
- CreateBatchSource_CancelledBetweenFrames_CompletesAllLiveChannels -> WatchSource_CompletesWhenTheTokenIsCancelled ; WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle
- CreateBatchSource_ChannelCompletesCleanly_EnumerationEndsWithoutError -> WatchSource_CompletesWhenTheTokenIsCancelled ; WatchSource_HostClosingThePipeIsAChannelLossNotAnEnd
- ArmScanAndCatchUpAsync_ErrorFrame_RecordsErrorAndCompletesForThatDrive -> ScanDrive_HostError_ThrowsInvalidOperation ; ArmScanAndCatchUpAsync_ErrorFrame_DisposesSectionLifetimeImmediately
- ArmScanAndCatchUpAsync_ReturnsBlock_ArmedCursor_AndCatchUpEntries -> ScanDrive_ReturnsArmedAndAdvancedCursorsAndBlock
- ArmScanAndCatchUpAsync_WarningFrame_RecordsWarning_AndDriveStillCompletes -> ScanDrive_CatchUpLost_ReturnsBlockWithLossAndArmedCursor
- ArmScanAndCatchUpAsync_WithKeepFileNames_SendsThemOnTheWire -> ScanDrive_ForwardsProfileAndKeepFileNames
- ServeOnce_BlockFormat_ForwardsDirectoryIndexProfileAndKeepNamesToSectionWriter -> ScanChannel_BlockFormat_ForwardsDirectoryIndexProfileAndKeepNamesToSectionWriter
- ServeOnce_BlockFormatWithoutSectionWriterReportsNamedError -> ScanChannel_BlockFormatWithoutSectionWriterReportsNamedError
- ServeOnce_BlockFormatWritesRowsAndArmedCursorWithoutPayload -> ScanChannel_BlockFormatWritesRowsAndArmedCursorWithoutPayload
- ServeOnce_BlockProgressReportsParsingThenTransferring -> ScanChannel_BlockProgressReportsParsingThenTransferring
- ServeOnce_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady -> ScanChannel_BlockSourceFailureLeavesIncompleteBlockAndEmitsNoScanReady
- ServeOnce_CancelledBlockScanLeavesIncompleteBlockWithoutErrorOrScanReady -> ScanChannel_CancelledBlockScanLeavesIncompleteBlockWithoutErrorOrScanReady
- ServeOnce_ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame -> ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame
- ServeOnce_ScanProgress_MaxRecordsProcessedExceedsReportedTotals_ClampsFinalCounts -> ScanProgress_MaximumRecordsProcessedExceedsReportedTotals_ClampsFinalCounts
- ServeOnce_ScanProgress_MonotonicAcrossParseAndWritePhases -> ScanProgress_MonotonicAcrossParseAndWritePhases
- ServeOnce_ScanProgress_WritePhaseByteProgressFlowsThroughBeforeCompletion -> ScanProgress_WritePhaseByteProgressFlowsThroughBeforeCompletion
- ServeOnce_ArmAndScan_EmitsCursorScanReadyAndCatchUp -> ArmAndScan_EmitsCursorScanReadyAndCatchUp
- ServeOnce_DirectoryIndexProfile_EmptyKeepFileNames_YieldsDirectoriesOnly -> DirectoryIndexProfile_EmptyKeepFileNames_YieldsDirectoriesOnly
- ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive -> DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive
- ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_KeepsTheNamedFile -> DirectoryIndexProfile_KeepFileNameMatch_KeepsTheNamedFile
- ServeOnce_DirectoryIndexProfile_NonMatchingFiles_AreDropped -> DirectoryIndexProfile_NonMatchingFiles_AreDropped
- ServeOnce_DirectoryIndexProfile_NullKeepFileNames_YieldsDirectoriesOnly -> DirectoryIndexProfile_NullKeepFileNames_YieldsDirectoriesOnly
- ServeOnce_DriveFailure_EmitsErrorFrameAndContinues -> DriveFailure_EmitsErrorFrameAndSessionContinues
- ServeOnce_MftRecordBatchSource_StreamsBatchesToBlockSectionWriter -> MftRecordBatchSource_StreamsBatchesToBlockSectionWriter
- ArmAndScan_WritesItsPerDriveErrorWithNoArmEpoch -> DriveFailure_EmitsErrorFrameAndSessionContinues ; ErrorFrame_OnDrivePipe_RoundTripsRequestIdZero
- StartWatch_TagsAPerDriveErrorWithTheArmEpochThatProducedIt -> WatchChannel_SourceThrows_WritesErrorAndCloses
- StartWatch_WithNoWatchSource_TagsItsErrorWithTheArmEpoch -> StartWatch_NoWatchSourceConfigured_EmitsErrorFrame
- DisarmDrive_StopsOnlyThatDrivesTaskAndTheOtherDriveKeepsStreaming -> WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected
- StartWatch_ForADriveThatIsNotArmed_AddsItToTheLiveGenerationAndLeavesTheOthersRunning -> WatchChannel_StreamsBatchesAndCaughtUp_NoDriveFields ; WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected
- DisposeAsync_DisposesTheBlocksOfTheScanTheSessionStillHolds -> DisposeAsync_WithAScanStillInFlight_DisposesTheLeftoverBlockAndLifetime
- BrokerDeath_SecondDeathSignal_ReasonUnchanged -> HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce
- EnsureOperable_WhileFaulted_ThrowsInvalidOperationException -> ControlOperation_AfterProcessDeath_ThrowsChannelLost
- PublicStartAsync_InProcessBroker_EndToEnd -> OpenAsync_AdoptsBrokerBlockAndAppliesCatchUpAtRecordNumbers
- PublicStartAsync_WithOptions_InProcessBroker_ParksWithRequestedProfile -> OpenAsync_BlockProgressAndDirectoryProfileSurviveTheProtocol
- StartWatch_UsesSameClientAsScan_NoSecondArmOrSpawn -> WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle ; RescanOfT_OverBroker_ReopensOnlyTsChannel
- StartWatch_WhenAlreadyWatching_Throws -> StartWatching_AlreadyWatching_DoesNotRestart
- WatchDrive_HappyPath_YieldsBatchesFromAdvancedCursor -> WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle
- Rescan_BrokerDiesDuringScan_Throws -> HostFault_WithOpenScanChannel_FailsScanWithChannelLost
- Rescan_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotOverwriteLatestScan -> Dispose_DuringScan_FailsScanWithChannelLostAndReleasesSection
- Rescan_FaultedDuringHandshake_ThrowsEndOfStream_DoesNotOverwriteLatestScan -> HostFault_WithOpenScanChannel_FailsScanWithChannelLost
- Rescan_MidResponseCancellation_MakesSessionTerminalBeforeNextOperation -> ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation ; ScanDrive_CancelOne_OtherDriveCompletes
- Rescan_PreSendCancellation_LeavesSessionParked -> OpenChannel_CancelledBeforeHostConnects_HostSeesNoChannel ; ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds
- Rescan_WithDestinationsAndDrives_ForwardsProfileAndKeepFileNames -> ScanDrive_ForwardsProfileAndKeepFileNames
- RescanAsync_WithDrivesAndOptions_DispatchesProgressCallbackAndUpdatesDrives -> ScanDrive_ReportsProgress
- RescanAsync_WithOptions_DispatchesProgressCallback -> ScanDrive_ReportsProgress
- BrokerDeath_LatchesIsFaultedAndFaultReason -> ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce ; HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce
- Dispose_CalledTwice_DisposesClientOnce -> DisposeAsync_CalledTwice_DoesNotThrow
- Faulted_SubscribeBeforeDeath_InvokedOnDeath -> WatchChannel_ProcessDeath_FiresEndedOnce_AndReadThrowsChannelLost
- Operation_AfterDispose_ThrowsObjectDisposed -> HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce ; ControlOperation_AfterProcessDeath_ThrowsChannelLost
- StartAsync_BrokerDiesDuringInitialScan_Throws -> HostFault_WithOpenScanChannel_FailsScanWithChannelLost
- StartAsync_Cancelled_Throws -> LaunchAsync_CallerCancellationRequested_ThrowsOperationCanceled ; ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation
- StartAsync_Scans_ParksWithLatestScanResult -> ScanDrive_ReturnsArmedAndAdvancedCursorsAndBlock
- StartAsync_WithKeepFileNames_ForwardsNamesToArmAndScanFrame -> ScanDrive_ForwardsProfileAndKeepFileNames
- StartAsync_WithOptions_DispatchesProgressCallback -> ScanDrive_ReportsProgress
- StartFromCursors_BrokerDiesWhileParked_LatchesFaultAndBlocksWatch -> WatchChannel_DeathBeforeRead_LateReadsThrow_EndedFiresOnce ; ControlOperation_AfterProcessDeath_ThrowsChannelLost
- StartFromCursors_RescanAfterWarmStart_PopulatesLatestScanAndRewatchesFromScan -> RescanOfT_OverBroker_ReopensOnlyTsChannel ; Rescan_RestartsWatchFromFreshCursor
- StartFromCursors_SentinelCursor_WatchesFromCurrent -> StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching ; StartWatch_ZeroCursorSentinel_WritesCaughtUpImmediately
- StartFromCursors_StartWatchMidTransmissionCancellation_MakesSessionTerminal -> ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds
- StartFromCursors_WatchesFromSuppliedCursors_EventsFlow -> WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle ; StartWatchingAsync_ResumesEachDriveFromItsHeaderCursor
- StartWatch_MidTransmissionCancellation_MakesSessionTerminal -> ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds
- StartWatch_PreSendCancellation_CanBeRetried -> WatchSource_DoesNotConnectWhenTokenIsAlreadyCancelled ; OpenChannel_CancelledBeforeHostConnects_HostSeesNoChannel
- StopWatch_WhileWatching_ReturnsToParked_AndCanRestart -> WatchSource_LeavesTheBorrowedProcessReadyForAnotherWatch
- WatchDrive_Cancelled_StopsCleanly -> WatchSource_CompletesWhenTheTokenIsCancelled
- WatchDrive_JournalInvalidatedMidWatch_ThrowsInvalidOperation -> LiveWatch_ErrorFrameForDrive_FaultsThatDrivesHandle
- WatchDrive_StaleCursorError_FaultsThatDriveWithTheRescanMessage -> StartWatch_StaleCachedCursor_EndsThatDrivesStreamWithARescanErrorAndTheOtherDriveKeepsStreaming ; LiveWatch_ErrorFrameForOneDrive_OtherDrivesKeepStreaming
- Dispose_WhileWatching_TearsDownDemux -> Dispose_EndsHost
- StartWatch_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotResurrectWatching -> Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe
- StartWatch_FaultedDuringHandshake_ThrowsInvalidOperation_DoesNotResurrectWatching -> OpenChannel_FailConnection_ThrowsWithHostMessage ; StartWatchingAsync_WhenTheBrokerConnectionFails_ThrowsItAndLeavesTheIndexStartable
- StopThenRescanThenStartWatch_ReusesOneBroker -> WatchSource_LeavesTheBorrowedProcessReadyForAnotherWatch ; RescanOfT_OverBroker_ReopensOnlyTsChannel
- QueryVolumes_DoesNotEndSession_SubsequentArmAndScanStillWorks -> QueryVolume_DoesNotEndSession_SubsequentArmAndScanStillWorks
- QueryVolumes_NoSeamConfigured_EmitsErrorPerDrive -> QueryVolume_NoSeamConfigured_EmitsError
- QueryVolumes_TwoDrives_OneSeamThrows_EmitsVolumeInfoAndError -> QueryVolume_TwoRequests_OneSeamThrows_EmitsVolumeInfoAndError
- RescanAsync_CancelledDuringQueryVolumes_DisposesClientAndSession -> ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds ; ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation
- RescanAsync_PlansBlocksOnTheSameBroker -> Scan_SendsBlockSpecificationAndPlansFromVolumeQuery
- RescanAsync_OnTheUnresumableDriveWhileWatching_WhenTheScanFailsWithoutThrowing_LeavesTheRefusalIntact -> RescanAsync_OnTheUnresumableDrive_WhenTheProducerReturnsNoBlock_ThrowsAndLeavesTheRefusalIntact
- RescanAsync_WhenTheWatchsCallerTokenEndedTheSession_ReclaimsItWithoutAFault -> CallerTokenCancellation_AfterStart_LeavesTheWatchRunning
- RescanAsync_AfterASubscriberFaultAndEveryDriveFaulted_StopStillRethrowsTheSubscriberFault -> RecoveringOneDrive_PreservesAnotherDrivesSubscriberFault
- RescanAsync_OfTheOtherDriveAfterARestart_RecoversItsCarriedFault -> RescanAsync_AfterEveryDriveFaulted_RestartsOnlyTheRescannedDriveAndLeavesTheOthersFaulted ; RecoveredDrive_StopDoesNotRethrowItsFault
- RescanAsync_AfterEveryDriveFaulted_ReclaimsTheSessionAndStartsAFreshOne -> RescanAsync_AfterEveryDriveFaulted_RestartsOnlyTheRescannedDriveAndLeavesTheOthersFaulted
- RescanAsync_CacheDeclinedDriveWhileWatching_ArmsItWithoutADisarmAndAppliesItsBatches -> RescanAsync_CacheDeclinedDrive_AdoptsItWithoutAWatchAndALaterStartAppliesItsBatches
- RescanAsync_CacheDeclinedDriveWhileWatching_RescannedASecondTime_DisarmsAndRearmsIt -> RescanAsync_CacheDeclinedDriveWhileWatching_RescannedASecondTime_RestartsIt
- RescanAsync_WhileWatching_DropsABatchAlreadyQueuedOnTheMergedStreamWhenTheDriveWasDisarmed -> RescanAsync_WhileWatching_DropsABatchThePumpHadAlreadyAcceptedWhenTheDriveWasRetired
- RescanAsync_WhileWatching_ReArmsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure -> RescanAsync_WhileWatching_RestartsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure
- RescanAsync_WhoseReArmFailsAfterTheSwapSucceeded_AnnouncesTheStoppedDrive -> RescanAsync_WhoseWatchRestartFailsAfterTheSwapSucceeded_ThrowsAndReadsFaulted
- RescanAsync_WhoseSwapAndReArmBothFail_AnnouncesTheFreezeAndLeavesTheOtherDriveStreaming -> RescanAsync_WhoseSwapAndWatchRestartBothFail_ThrowsBothAndReadsFaultedWhileTheOtherDriveKeepsStreaming
- RescanAsync_WhoseSwapFails_ReArmsTheDriveFromItsUnchangedCursorAndRethrows -> RescanAsync_WhoseSwapFails_RestartsTheDriveFromItsUnchangedCursorAndRethrows
- RescanAsync_WithNoWatchRunning_ArmsNothingAndDisarmsNothing -> RescanAsync_WithNoWatchRunning_StartsNoWatch
- FailedRearm_PreservesPreviouslyOutstandingDriveFault -> FailedRestart_OfARecoveredDrive_RaisesRecoveryAndReadsFaulted
- DisposeAsync_DuringStartup_CancelsTheStart -> DisposeDuringStart_CancelsTheSourceStart_AndCompletes
- StartWatchingAsync_CancelledBeforeReady_ThrowsAndReleasesTheSession -> CallerTokenCancellation_DuringStart_EndsTheStartWithNoFault
- StartWatchingAsync_WhenTheSourceFailsBeforeReady_ThrowsThatFailureAndReleasesTheSession -> StartWatching_SourceStartThrows_ThrowsAndFaultsSlot
- StopWatchingAsync_DuringStartup_CancelsTheStart -> StopDuringStart_CancelsTheSourceStart
- ExpectedAbiVersion_IsOne -> ExpectedAbiVersion_IsTwo
- NativeLibrary_ReportsAbiVersionOne -> NativeLibrary_ReportsAbiVersionTwo
- GetMftNativeAbiVersion_ReturnsVersion1 -> GetMftNativeAbiVersion_ReturnsVersion2
- EndWatch_IdleNativeRead_Acknowledges -> CloseWatchPipe_IdleNativeRead_EndsChannel ; Watch_IdleNativeRead_CancellationCompletes

Dropped (101; name - reason):
- Demux_CreateBatchSourceSkipsCaughtUpMarkersAndKeepsStreaming - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DeliversACaughtUpFrameToTheDrivesLiveItemStream - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DeliversAJournalBatchTaggedWithTheDrivesCurrentArmEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DropsACaughtUpFrameTaggedWithASupersededArmEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DropsAFrameTaggedWithTheNoArmEpochSentinel - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DropsAJournalBatchForADriveThatIsNotArmedWhateverEpochItCarries - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DropsAJournalBatchTaggedWithASupersededArmEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_DropsAnErrorTaggedWithASupersededArmEpochAndKeepsTheFreshChannelStreaming - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- Demux_FaultsTheDriveAWarningNamesWhateverEpochIsCurrent - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- SendStartWatchAsync_InvalidDriveLeavesTheExistingArmDeliveringItsCurrentEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- QueryVolumesAsync_AfterAStopThatTimedOut_DrainsAStaleCaughtUpFrameWithoutKillingTheBroker - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendDisarmDriveAsync_WhileAnArmHoldsTheOrderingGate_ReachesTheWireAfterThatArm - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendDisarmDriveAsync_WithNoWatchRunning_StillThrowsWithoutAwaiting - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_AfterAStopThatTimedOut_NeverReissuesAnEarlierEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_ConcurrentArmsOfOneDrive_PutTheHigherEpochLastOnTheWire - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_EndedGenerationValidationLeavesEarlierArmsIntact - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_IssuesAHigherEpochForEveryLaterArmOfTheSameDrive - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_IssuesDistinctEpochsAcrossDrivesInOneCall - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendStartWatchAsync_IssuesEpochOneForTheFirstArmSoZeroIsNeverALiveEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch; the arm-ordering gate is gone with them
- SendDisarmDriveAsync_WhenDisposalCancelsItMidWrite_ThrowsObjectDisposed - no DisarmDrive request exists; a control write cut off by disposal ends the process (Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe)
- StopLiveWatchAsync_AfterDispose_IsANoOp - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopLiveWatchAsync_DemuxCtsInconsistentWithDemuxTask_ThrowsInvalidOperationException - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopLiveWatchAsync_NotWatching_IsNoOp - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopLiveWatchAsync_PipeAlreadyClosed_SwallowsEndWatchWriteFailure - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopLiveWatchAsync_WithALatchedControlFailure_JoinsTheEndedDemuxAndReturns - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- ControlExchange_CancelledTransmittedQuery_RejectsLaterRequests - the contract is reversed: a cancelled request no longer poisons the control pipe; its frame is finished and the next request succeeds (ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds, ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds)
- ControlExchange_LiveErrorIsNotAVolumeQueryErrorForTheSameDrive - watch errors travel on their drive pipe, never the control pipe; control replies are matched by request id, so the two cannot be confused
- ControlExchange_QuerySerializesWithASecondQuery - control requests are no longer serialized: they run concurrently under request ids (ControlRequests_RunConcurrently on the host, HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce on the client)
- ControlExchange_WatchHandoff_WaitsForQuery - there is no watch handoff on the control pipe; a watch is a drive pipe of its own
- SendStartWatchAsync_CalledTwiceWithoutStop_ArmsTheSecondCallsDrives - a second watch of any drive is a second drive pipe; there is no StartWatch that re-arms a running generation
- WatchSpecArmEpochs_ForDrive_MatchesDriveLetterCaseInsensitively - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- WatchSpecArmEpochs_ForDrive_MissingDrive_ThrowsWithDriveAndSpec - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- CreateBatchSource_YieldsBatchesForMatchingDrive_SkipsOtherDrives - there is no shared stream to filter by drive: each drive's batches arrive on that drive's own handle (WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle)
- DisposeAsync_SendsShutdownFrame - no Shutdown frame exists: disposal closes the control pipe, which ends every host channel (Dispose_EndsHost)
- StopLiveWatchAsync_ResetsState_SoWatchCanRestart - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes (restart after stop: WatchSource_LeavesTheBorrowedProcessReadyForAnotherWatch)
- StopLiveWatchAsync_StrayBatchBeforeAck_StillStopsAndCanRestart - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes; a stale frame cannot precede an ack that does not exist (TimedOutStop_ThenNewWatch_RunsUndisturbed)
- DisarmDrive_ForADriveSpelledAsAPath_RetiresTheDriveThatWasArmed - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- ParseWatchSpec_NormalizesTheDriveLetterItArmsUnder - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- ParseWatchSpec_ReadsTheArmEpochAsTheFourthField - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- ParseWatchSpec_RejectsATokenWithNoArmEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- StartWatch_ReArmingADriveTagsItsBatchesWithTheNewEpochOnly - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- StartWatch_TagsEveryJournalBatchForADriveWithThatArmsEpoch - arm epochs and the shared demux are gone: each drive has its own pipe and no frame carries an epoch
- DisarmDrive_ForADriveThatWasNeverArmed_IsIgnoredAndTheSessionKeepsServing - no DisarmDrive/EndWatch and no shared watch generation: each watch is one drive pipe the host ends when that pipe closes
- EndWatch_AfterAPerDriveDisarm_StopsEveryRemainingDriveAndStillAcks - no DisarmDrive/EndWatch and no shared watch generation: each watch is one drive pipe the host ends when that pipe closes
- StartWatch_ForAnArmedDrive_StopsItsTaskAndRestartsItFromTheSuppliedCursor - no DisarmDrive/EndWatch and no shared watch generation: each watch is one drive pipe the host ends when that pipe closes
- Rescan_SecondScan_DisposesSupersededBlocksAndLeavesTheNewOnesLive - BrokerProcess returns each block to its caller and holds none; superseded blocks are the index's (DisposeAsync_AfterARescan_ReleasesTheRetiredBlockToo)
- EnsureOperable_WhileParked_DoesNotThrow - there is no parked state: a BrokerProcess is operable until it ends
- Faulted_Unsubscribe_StopsReceivingNotifications - Ended is a plain event with no subscription registry to test
- StartWatch_NoDriveArmed_Throws - there is no armed-drive set: the index starts a watch for a named drive (WaitForCatchUpAsync_ThrowsForADriveThatIsNotInTheIndex)
- ReplaceWatchCursors_WarmSession_WatchesReplacedSetNotBaseline - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- WatchCursors_ReflectsReplacement_AndReflectsRescanAfterwards - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- WatchCursors_SafeToReadWhenDisposedOrFaulted - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- Harness_StartFromCursorsAsync_ParksWithoutScanning - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_DefensiveCopy_CallerMutationDoesNotAffectSession - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_MalformedDriveKeys_ThrowsArgumentException - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_NormalizesKeys_LastWriterWinsOnDuplicates - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_NullArgument_ThrowsArgumentNullException - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_ReplacesRatherThanMerges_DropsOmittedDrives - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_StartWatchAsync_SendsReplacedSetOverWire - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_WhileDisposed_ThrowsObjectDisposed - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_WhileFaulted_ThrowsInvalidOperationWithFaultReason - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_WhileOperationInFlight_ThrowsInvalidOperation - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_WhileParked_UpdatesWatchCursors - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- ReplaceWatchCursors_WhileWatching_ThrowsInvalidOperation - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- StartFromCursorsAsync_MalformedDriveKeys_ThrowsArgumentException - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- WatchDriveAsync_NormalizesDrivePath_FindsWatchedDrive - the session's replaceable cursor set is gone: each watch starts from the drive block header cursor
- Rescan_WithNewDrives_StoresDrivesAndProfileReusedByTheNextExplicitRescan - BrokerProcess stores no drive set or profile: each ScanDriveAsync call carries its own target and options
- StartWatch_WhileRescanInFlight_ThrowsWithoutTouchingPipe - a watch and a scan run on separate drive pipes at once; nothing rejects the overlap (ScanDrive_TwoDrivesConcurrently_BothComplete, Rescan_HoldsLifecycleGateThroughProduction)
- Dispose_WhileParked_SendsSingleShutdownFrame - no Shutdown frame exists: disposal closes the control pipe (Dispose_EndsHost)
- Faulted_LateSubscriber_FiresImmediately - Ended is a plain event that fires once at the end; a late reader uses HasEnded (asserted by HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce)
- ScanSessionTestHarness_StartScannedAsync_WithOptions_DispatchesProgressCallback - ScanSessionTestHarness is deleted; the consumer harness is BrokerTestHarness (progress: ScanDrive_ReportsProgress)
- StartFromCursorsAsync_WithANullDriveKey_ThrowsArgumentExceptionBeforeConnecting - there is no warm-start cursor dictionary: each drive is named by a char
- Harness_StartScannedAsync_ForwardsArgumentsAndParksOnScan - ScanSessionTestHarness is deleted; the consumer harness is BrokerTestHarness
- PublicStartFromCursors_InProcessBroker_EndToEnd - there is no warm-start session: the index resumes each drive from its block header (StartWatchingAsync_ResumesEachDriveFromItsHeaderCursor)
- Rescan_ConcurrentDoubleCall_ExactlyOneProceeds - the process refuses nothing: two rescans of one drive queue on that drive's lifecycle gate in the index (Rescan_HoldsLifecycleGateThroughProduction)
- Rescan_WhileStartWatchInFlight_ThrowsWithoutTouchingPipe - a watch and a scan run on separate drive pipes at once; nothing rejects the overlap
- StartFromCursors_DisposeWhileParked_SendsSingleShutdownFrame - no Shutdown frame exists: disposal closes the control pipe (Dispose_EndsHost)
- StartFromCursors_ParksWithoutScanning_LatestScanNull - there is no parked session and no latest scan
- StopWatch_WhenClientStopCompletesSynchronously_AwaitsCapturedTask - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopWatch_WhileParked_IsNoOp - stopping a drive that is not watching throws InvalidOperationException (Stop_DriveNeverStarted_ThrowsInvalidOperation)
- WatchDrive_UnarmedDrive_ThrowsArgumentException - there is no armed-drive set to be missing from
- WatchDrive_WhileParked_ThrowsInvalidOperation - there is no parked session
- Rescan_WhileWatching_Throws - a rescan of a watched drive is legal: it retires that drive's watch and restarts it (RescanAsync_WhileWatching_RestartsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure)
- Rescan_WithDestinations_ReusesInitialDrives - BrokerProcess stores no drive set: each ScanDriveAsync call carries its own target
- StopWatch_ConcurrentCalls_SendOneEndWatch_AndRestartReceivesBatches - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- StopWatch_DisposedDuringHandshake_ThrowsObjectDisposed_DoesNotResurrectParked - no EndWatch/EndWatchAck exchange exists: a watch ends when its own drive pipe closes
- WatchDrive_AfterStopWatch_ThrowsInvalidOperation_NotNullReference - there is no session watch state to read after a stop
- WatchDrive_BatchSourceInvariantBroken_ThrowsInvalidOperation - there is no batch-source delegate whose invariant could break
- RescanAsync_WhenTheEndedSessionRecordedASubscriberFault_CarriesItIntoAFreshSession - a subscriber fault is the outstanding fault of its watch instance and goes with that instance when a rescan replaces it (spec 2.6, WatchInstance.OutstandingFault; RecoveredDrive_StopDoesNotRethrowItsFault); no session exists to carry it into
- RescanAsync_AfterASourceFault_WhenTheReplacementStreamEndsCleanly_StopStillRethrowsTheSourceFault - there is no Source fault kind and no source-level fault: every fault names a drive and goes with its watch instance (RecoveredDrive_StopDoesNotRethrowItsFault)
- RescanAsync_AfterASourceFaultEndedTheSession_StopStillRethrowsTheSourceFault - there is no Source fault kind and no source-level fault: every fault names a drive and goes with its watch instance (RecoveredDrive_StopDoesNotRethrowItsFault)
- RescanAsync_OfAFaultedDrive_WhenTheLastHealthyDriveFaultsMidScan_StartsAFreshSession - there is no shared session to end when the last healthy drive faults: the rescanned drive restarts alone and the other stays faulted
- RescanAsync_QueuedFromTheLastDrivesFaultHandler_WhenTheSessionEndsMidScan_StartsAFreshSession - there is no shared session to end: a rescan restarts only its own drive (RescanAsync_AfterEveryDriveFaulted_RestartsOnlyTheRescannedDriveAndLeavesTheOthersFaulted)
- RescanAsync_WhoseDisarmFailsBecauseTheSessionAlreadyEnded_StartsAFreshSessionInsteadOfFailing - there is no disarm and no shared session; retirement cannot fail
- RescanAsync_WhoseDisarmIsRejectedAfterTheStreamEndedButBeforeThePumpFinished_StartsAFreshSession - there is no disarm and no shared session; retirement cannot fail
- StartWatchingAsync_CompletesOnlyOnceTheSourceReportsReady_WithNoItemDelivered - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle
- StartWatchingAsync_IsNotSatisfiedByAnEarlierSessionsReadiness - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle; there is no shared session whose readiness could be mistaken
- StartWatchingAsync_WithASourceThatReportsNoReadiness_CompletesAtTheStreamsFirstIncompleteAwait - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle; there is no first-move readiness rule
- StartWatchingAsync_WithASourceThatReportsNoReadiness_CompletesOnceTheSourceIsRunning - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle; there is no first-move readiness rule
- StartWatchingAsync_WithASourceThatReportsNoReadiness_WhoseFirstMoveEndsSynchronously_Throws - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle; there is no first-move readiness rule
- StartWatchingAsync_WithASourceThatReportsNoReadiness_WhoseFirstMoveFaultsSynchronously_ThrowsTheFault - the source reports no readiness: StartWatchingAsync completes when the source returns the drive's live handle; there is no first-move readiness rule

Left to B7 (12; all-drives WaitForCatchUpAsync cases):
- WaitForCatchUpAsync_AllDrives_CanceledWaitsReleaseTheirCoordinatorsOnceThePendingDriveCatchesUp
- WaitForCatchUpAsync_AllDrives_CanceledWaitsReleaseTheirCoordinatorsWhileADriveIsPending
- WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUpThenOtherDriveCatchesUp_FaultsAggregateWait
- WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUpWhileOtherDriveIsCatchingUp
- WaitForCatchUpAsync_AllDrives_DriveFaultsWhileCatchingUpThenOtherDriveCatchesUp_FaultsAggregateWait
- WaitForCatchUpAsync_AllDrives_DriveReArmedAfterCatchUpWhileOtherDriveIsCatchingUp_CancelsAggregateWait
- WaitForCatchUpAsync_AllDrives_DriveReArmedWhileCatchingUpThenOtherDriveCatchesUp_CancelsAggregateWait
- WaitForCatchUpAsync_AllDrives_FaultsImmediatelyWhenFirstDriveFaultsWhileSecondIsCatchingUp
- WaitForCatchUpAsync_AllDrives_ThrowsWhenNoWatchIsRunning
- WaitForCatchUpAsync_AllDrives_WaitsForTheSlowestDrive
- WaitForCatchUpAsync_AllDrives_WhenEveryDriveAlreadyCaughtUp_IsCompleteAtIssue
- WaitForCatchUpAsync_AllDrives_WithASingleWatchedDrive_CompletesWithItsCatchUp


## Fix round 1

New commit (a732d47 not amended): see `git log task/265-TB -1`. Subject "Tranche accounting: reversed contracts are drops and lifecycle cases are ported".

Corrected counts over the 245 rows: ported 14 (13 new test methods), covered by 116, dropped 103, left to B7 12, suspected defects 0.

- Covered by -> Dropped (reversed contracts): `TryNormalizeDriveLetter_InvalidDrive_ReturnsFalseAndAnEmptyNormalizedDrive`, `RescanAsync_CancelledDuringQueryVolumes_DisposesClientAndSession`.
- Covered by -> Ported: `ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation` (same name, BrokerProcessTests.Disposal.cs; the watch=true row is covered by `Dispose_EndsHost`), `StartWatchingAsync_CancelledBeforeReady_...` -> `StartWatching_AfterACancelledStart_StartsFresh`, `StartWatchingAsync_WhenTheSourceFailsBeforeReady_...` -> `StartWatching_AfterASourceStartFailure_StartsFresh`, `StopWatchingAsync_DuringStartup_CancelsTheStart` -> `StartWatching_AfterAStopDuringStart_StartsFresh` (last three in FileIndexPerDriveWatchTests.cs).
- `RescanAsync_CancelledWhileTheWatchIsStarting_LeavesTheDriveUntouched` now awaits `rescan.WaitAsync(HangGuard)`.
- Targeted (BrokerProcessTests, FileIndexPerDriveWatchTests, FileIndexWatchRescanTests): 118/118 passed. Whole suite after the last edit: Total 1848, Passed 1842, Failed 0, Skipped 6.
- aislop: 99/100, five warnings = four baseline plus the ruled 8-parameter `JournalBrokerHost`; nothing else.
- Primary checkout `git status --short`: empty. Suspected defects: none.

## Fix round 2

Commit f49d3d9 "Tranche accounting: the control queue row is a drop".

- `ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation` moved from Ported to Dropped (no serialized control queue: requests register independently, only the frame write is serialized; BrokerProcess.Control.cs).
- The test is renamed `Dispose_WithTwoPendingControlRequests_FailsBothWithChannelLost` and its last request await is bounded with `WaitAsync(HangGuard)`.
- Corrected counts over 245 rows: ported 13 base rows (12 new tests), covered by 116, dropped 104, left to B7 12, defects 0.
- The ported tests are ports of existing behavior, exempt from RED (broker-common port rules); no RED evidence exists or is claimed.
- `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerProcessTests"` -> Passed 62, Failed 0, Total 62.
- `aislop scan .` -> 99/100, 5 warnings (four baseline plus the ruled 8-parameter JournalBrokerHost); nothing else.
