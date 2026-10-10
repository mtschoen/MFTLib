# MFTLib - Test Report

2026-10-09

| Field | Value |
| --- | --- |
| Status | PASS for local Windows checks; platform CI not run locally |
| Mode | best-effort; documentation-only change, pre-existing coverage gaps and fixture warnings |
| Git | docs/changelog-api-rulings; tested code at [integrated API-ruling base (all eight slices)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/commit/7e40576c1a8a46f8d53295bae196b51a93434dc7), with CHANGELOG.md and TEST-REPORT.md updates |
| Tests | Whole suite: 2840 total, 2813 passed, 0 failed, 27 skipped |
| Contract tests | AgentInstructionsTests and PublicSurfaceTests: 6 passed, 0 failed, 0 skipped |
| Coverage | Managed whole suite: 8973/9045 lines (99.2040%), 72 uncovered; 3620/3748 branches (96.5848%) |
| Test extensions | Whole-suite line, branch and method coverage: 100% |
| Lint | aislop 0.16.0 CI: score 100/100, 0 errors, 2 pre-existing warnings, 0 fixable; exit 0 |
| Build | Native and managed Release x64 passed; all six managed builds: 0 warnings, 0 errors; native restore NU1503 warning |
| Platform | Windows unelevated; Linux, Linux package and elevated live-volume tests not run locally; native instrumentation not run |
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

| Assembly | Whole-suite line coverage | Branch coverage | Method coverage |
| --- | --- | --- | --- |
| MFTLib | 99.73% | 97.67% | 99.92% |
| MFTLibTestExtensions | 100% | 100% | 100% |
| SampleProgram.Direct | 97.73% | 96.19% | 97.77% |
| SampleProgram.Watch | 90.10% | 81.66% | 93.15% |
| Benchmark | 98.82% | 96.36% | 100% |

## Skipped tests

| Class | Count | Test names |
| --- | --- | --- |
| Platform (Unix/Linux-only) | 6 | EnsureCreated_OnUnixLeavesOwnerOnlyPermissions; EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory; EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory; Find_OnLinux_LeavesBackslashesInsideFileNamesUntouched; Open_OnLinuxWithNoOverride_ThrowsPlatformNotSupported; Query_NonWindowsHost_ThrowsPlatformNotSupported |
| Admin/live volume | 21 | LiveWatch_CreateModifyDeleteCycle_ReportsOneChangePerRealTransition; Query_SystemDrive_ReturnsPlausibleMftSizing; Query_AcceptsSameDriveFormatsAsMftVolumeOpen; QueryUsnJournal_OnRealVolume_ReturnsCursor; ReadUsnJournal_AfterTempFileCreate_ContainsEntry; ReadUsnJournal_CurrentPosition_ReturnsEmptyOrFew; WatchUsnJournal_DetectsNewFile; GrowUsnJournal_OnRealVolume_GrowsAndReadsBack; Open_ValidDriveLetter_Succeeds; Open_WithColon_Succeeds; Open_WithBackslash_Succeeds; Open_WithCustomBufferSize_Succeeds; ReadAllRecords_ReturnsNonEmpty; ReadAllRecords_WithTimings_PopulatesTimings; StreamRecords_CanEnumerate; StreamRecords_ToArray_MaterializesAll; Open_InvalidVolume_Throws; GetVolumeHandle_ValidVolume_ReturnsValidHandle; GetVolumeHandle_InvalidVolume_Throws; MftResult_Properties_MatchRecordCounts; ReadRecordBatches_RealVolume_TakesTwoBatches_DisposesEarly |

## Commands

```bash
pwsh -NoProfile -File scripts/build-windows.ps1
VSTEST_TESTHOST_SHUTDOWN_TIMEOUT=60000 dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura -p:CoverletOutput=../.lane/cl-coverage
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~AgentInstructionsTests|FullyQualifiedName~PublicSurfaceTests"
aislop ci .
aislop scan .
aislop scan --staged
```
