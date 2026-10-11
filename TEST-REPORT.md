# MFTLib - Test Report

2026-10-11

| Field | Value |
| --- | --- |
| Status | PASS for local Windows (unelevated) and Linux (WSL) coverage runs; platform CI not run locally |
| Mode | best-effort; documentation-only change, pre-existing fixture lint warnings |
| Git | docs/test-report-0-3-0; tested code at [MFTLib main (0.3.0 release candidate)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/commit/331b740d7f11ef8d82c3b28ed8ebb6b93d84b4f2), with TEST-REPORT.md updated |
| Tests (Windows) | Whole suite excluding RequiresAdmin: 2945 total, 2936 passed, 0 failed, 9 skipped |
| Tests (Linux) | Suite as scripts/coverage-linux.sh runs it, excluding UsnJournalSyntheticTests (filtered) and SystemInfoWmiTests (not compiled off Windows): 2893 total, 2825 passed, 0 failed, 68 skipped; native smoke test: 29 passed, 0 failed |
| Contract tests | AgentInstructionsTests: 3 passed, 0 failed, 0 skipped |
| Coverage (Windows) | Managed: 9149/9231 lines (99.1%), 82 uncovered; 3701/3840 branches (96.3%); 1877/1887 methods (99.4%) |
| Coverage (Linux) | Same scope as the Linux test row. Managed: 12091/12676 lines (95.38%); 3608/3886 branches (92.84%). Native gcov: 1480/1649 lines (89.8%); 868/1235 branches (70.3%); 184/205 functions (89.8%) |
| Test extensions | Whole-suite line, branch and method coverage: 100% on Windows and Linux |
| Lint | aislop 0.16.0 CI: score 100/100, 0 errors, 2 pre-existing warnings, 0 fixable; exit 0 |
| Build | Windows: native and managed Release x64 passed; all six managed builds: 0 warnings, 0 errors; native restore NU1503 warning. Linux: native and managed builds passed; 4 SourceLink warnings (no git repository located from the worktree path) |
| Linux package floors | build-linux.sh and check-linux-native.sh: passed; highest imported glibc 2.33, GLIBCXX 3.4.22 |
| Platform | Windows unelevated and Linux in WSL (Ubuntu-24.04) ran; the Linux run excludes UsnJournalSyntheticTests (51 cases, filtered) and SystemInfoWmiTests (8 cases, not compiled off Windows); 21 RequiresAdmin elevated live-volume tests excluded by the noninteractive filter and not run; native instrumentation (native-coverage.ps1) not run; package smoke (smoke-package-linux.sh) not run |
| Exclusions | No coverage exclusions or analyzer suppressions changed |

## Lint

| Engine | Findings | Location |
| --- | --- | --- |
| format | 0 | None |
| lint | 2 | Unchanged MFTLib.Tests/CallerGateFixtureConstructors.cs:35 (jb/RedundantExplicitPositionalPropertyDeclaration); MFTLib.Tests/CallerGateFixtureRecord.cs:50 (jb/EmptyConstructor) |
| code-quality | 0 | None |
| ai-slop | 0 | None |
| security | 0 | None |

## Coverage

Windows (scripts/run-coverage.ps1 -NonInteractive -Configuration Release):

| Assembly | Whole-suite line coverage | Branch coverage | Method coverage |
| --- | --- | --- | --- |
| MFTLib | 99.73% | 97.69% | 99.92% |
| MFTLibTestExtensions | 100% | 100% | 100% |
| SampleProgram.Direct | 96.13% | 94.09% | 95.83% |
| SampleProgram.Watch | 92.38% | 84% | 94.56% |
| Benchmark | 98.82% | 96.36% | 100% |

Linux (scripts/coverage-linux.sh):

| Assembly | Whole-suite line coverage | Branch coverage | Method coverage |
| --- | --- | --- | --- |
| MFTLib | 96.02% | 93.79% | 98.22% |
| MFTLibTestExtensions | 100% | 100% | 100% |
| SampleProgram.Direct | 96.42% | 93.77% | 95.83% |
| SampleProgram.Watch | 91.14% | 83.55% | 94.56% |
| Benchmark | 82.87% | 85.71% | 87.75% |

## Skipped tests

| Platform | Class | Count | Test names |
| --- | --- | --- | --- |
| Windows | Platform (Unix/Linux-only) | 9 | EnsureCreated_OnUnixLeavesOwnerOnlyPermissions; EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory; EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory; Find_OnLinux_LeavesBackslashesInsideFileNamesUntouched; ParseMFTRecords_OffWindowsWithoutAnOverride_TakesTheRecordSizeFromTheBootSector (3 rows); Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported; Query_NonWindowsHost_ThrowsPlatformNotSupported |
| Linux | Platform (Windows-only) | 68 | BlockFlush_EachRange_RestartsScanPipeProgressClock; BlockScan_WithTheOption_WritesTheFreedRecordAsADeletedRow; CheckpointCheck_AgainstTheRealVolume_ReadsItsLiveJournal; CheckpointCheck_JournalQueryRefused_ReportsNothing; CreateThenOpenExisting_SeesTheSameRows; Create_MappingConstructionFails_DeletesTheFileAndDisposesTheStream; EnsureCreated_OnWindowsProtectsTheAccessRulesOfANewDirectory; EnsureCreated_OnWindowsReprotectsADirectoryThatAlreadyExists; EveryPublicMember_HasASampleOrReasonedConsumerCaller; FromLocalVolumes_AllocatedRecordWithAnInvalidFixup_IsReportedAsASkippedRecord; FromWindowsVolume_ReadsARealSerialForTheSystemDrive; GetVolumeHandle_InvalidVolume_Throws; GetVolumeHandle_ValidVolume_ReturnsValidHandle; GrowUsnJournal_OnRealVolume_GrowsAndReadsBack; JournalReader_NoOverride_TheGuardAloneDecidesWhetherTheLiveJournalIsRead; LaunchAsync_EndToEnd_UsesRealPipeAndRealBlockSeams; LiveBlockScan_AllocatedRecordWithAnInvalidFixup_IsAddedToTheSkippedRecordCount; LiveBlockScan_NoInvalidFixup_SkipsNothing; LiveJournalReadsAreForbiddenInThisTestProcess; LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition; Log_DifferentRoles_HeldFileDoesNotDropOtherRoleRecords; MftResult_Properties_MatchRecordCounts; OpenExisting_UnknownSectionName_ThrowsFileNotFound; Open_InvalidVolumeHintPath_ThrowsIOExceptionNamingThePath; Open_InvalidVolume_Throws; Open_OnAnMftEntryWithNoOverride_UsesTheRealByIdOpenOnWindows; Open_RoundTripsARealFileThroughItsFileReference; Open_ThrowsIOExceptionNamingTheRecordWhenTheSequenceNumberIsWrong; Open_UnrecognizedFileAccessValue_ThrowsArgumentOutOfRangeException; Open_ValidDriveLetter_Succeeds; Open_WithBackslash_Succeeds; Open_WithColon_Succeeds; Open_WithCustomBufferSize_Succeeds; Open_WithReadWriteAccess_ReadsAndWritesThroughTheSameHandle; Open_WithWriteAccess_OpensAndAcceptsAWrite; OversizedJournalName_ReportsCopiedLength (2 rows); QueryJournalData_ReadsEveryFieldTheCheckpointCheckNeeds; QueryUsnJournal_OnRealVolume_ReturnsCursor; Query_AcceptsSameDriveFormatsAsMftVolumeOpen; Query_ForADriveLetterThatDoesNotExist_ThrowsIOExceptionNamingTheRoot; Query_OnRealVolume_ReturnsSettingsUnelevated; Query_SystemDrive_ReturnsPlausibleMftSizing; Query_WhenTheIoctlFails_ThrowsWin32Exception; Query_WithSyntheticVolumeData_MapsBothSizingFields; ReadAllRecords_ReturnsNonEmpty; ReadAllRecords_WithTimings_PopulatesTimings; ReadRecordBatches_RealVolume_TakesTwoBatches_DisposesEarly; ReadUsnJournal_AfterTempFileCreate_ContainsEntry; ReadUsnJournal_CurrentPosition_ReturnsEmptyOrFew; RealBlockSectionWriter_WritesBlock_ClientCanReadItBack; RescanAsync_FailedScanWithALockedReplacementFile_RecordsTheFailureAndLeavesTheRetiredFileAside; ResetToDefaults_RestoresTheRealIoctl; ResolveFileReference_RealFile_MatchesThatFilesJournalEntries; Run_Index_MftCache_OpensWithJournalIsolationEnabled; ScanDriveRecordBatches_KeepsFreedRecordsOnlyWhenAsked (2 rows); ServeAsync_DefaultSource_AdaptsNativeProgressWithoutFreedRows; ServeAsync_DefaultSource_IncludesRootDirectoryRow; ServeAsync_QueryVolume_UsesRealNtfsVolumeInformationSeam; ServeAsync_ScanAndCatchUp_UseRealMftVolumeSeams; SettingsQuery_MetadataIoctlFails_SurfacesTheWin32Error; StreamRecords_CanEnumerate; StreamRecords_ToArray_MaterializesAll; TryGetDriveLetter_ForARelativePath_ResolvesAgainstTheCurrentDrive; VolumeRecordWithoutDataRuns_ReturnsError (2 rows); WatchUsnJournal_DetectsNewFile |

## Excluded tests (not run)

| Platform | Class | Count | Test names |
| --- | --- | --- | --- |
| Windows | Admin/live volume (TestCategory=RequiresAdmin, excluded by the noninteractive filter) | 21 | LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition; Query_SystemDrive_ReturnsPlausibleMftSizing; Query_AcceptsSameDriveFormatsAsMftVolumeOpen; QueryUsnJournal_OnRealVolume_ReturnsCursor; ReadUsnJournal_AfterTempFileCreate_ContainsEntry; ReadUsnJournal_CurrentPosition_ReturnsEmptyOrFew; WatchUsnJournal_DetectsNewFile; GrowUsnJournal_OnRealVolume_GrowsAndReadsBack; Open_ValidDriveLetter_Succeeds; Open_WithColon_Succeeds; Open_WithBackslash_Succeeds; Open_WithCustomBufferSize_Succeeds; ReadAllRecords_ReturnsNonEmpty; ReadAllRecords_WithTimings_PopulatesTimings; StreamRecords_CanEnumerate; StreamRecords_ToArray_MaterializesAll; Open_InvalidVolume_Throws; GetVolumeHandle_ValidVolume_ReturnsValidHandle; GetVolumeHandle_InvalidVolume_Throws; MftResult_Properties_MatchRecordCounts; ReadRecordBatches_RealVolume_TakesTwoBatches_DisposesEarly |
| Linux | UsnJournalSyntheticTests (filtered out by scripts/coverage-linux.sh with FullyQualifiedName!~UsnJournalSyntheticTests: the Linux native library has neither the USN exports nor the USN test hooks) | 51 | Every case in the class; 51 is the count the class runs on Windows |
| Linux | SystemInfoWmiTests (MFTLib.Tests.csproj removes the file from non-Windows compilation: System.Management is Windows-only) | 8 | Every case in the class; never discovered on Linux |

## Commands

```bash
pwsh -NoProfile -File scripts/run-coverage.ps1 -NonInteractive -Configuration Release
MSYS_NO_PATHCONV=1 wsl -d Ubuntu-24.04 -- bash /mnt/c/Users/mtsch/MFTLib-worktrees/test-report/scripts/coverage-linux.sh
MSYS_NO_PATHCONV=1 wsl -d Ubuntu-24.04 -- bash /mnt/c/Users/mtsch/MFTLib-worktrees/test-report/scripts/build-linux.sh
bash scripts/check-linux-native.sh build/linux/libMFTLibNative.so
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~AgentInstructionsTests
aislop ci .
```
