# Task C3a report: port host watch tests

Status: DONE_WITH_CONCERNS (merge reconciliation with C3b needed, see Concerns)
Commit: 874c951 "Port host watch tests to one channel per watch" on task/265-C3a (base cb13c86e verified)

## Implemented
Five ported partials of `JournalBrokerHostTests` plus `JournalBrokerHostTests.WatchSupport.cs`, all on
`HostChannelHarness` (one watch channel per drive). The main partial `JournalBrokerHostTests.cs` does NOT
exist on the base, so WatchSupport declares `[TestClass] [DoNotParallelize] public partial class
JournalBrokerHostTests` (no base class) and these members: `WatchVolume`, `CreateWatchHost`, `WatchRecord`,
`WatchEntry`, `LiveWatch`, `FiniteWatch`, `ThrowingWatchMidStream`, `GatedWatch`, `FaultingAfterGate`,
`AssertControlStillServesAsync`, nested `BreakableDrivePipe`. Names are deliberately distinct from the old
main partial's (`CreateHost`, `SampleEntry`, `FakeWatch`, ...) so C3b's partial should merge without clashes.
No production code and no existing harness member changed (no harness additions).
Rewrites: sentinel/zero-cursor tests use cursor `default`; frame ordering now proven by `ReadToEndAsync` over a
finite source (channel closes itself) instead of EndWatchAck; sibling-channel and control-pipe liveness asserted
after each failure. Diagnostics tests route log lines to a discarding writer (`ReplaceWriterForTest`), so
nothing writes to `C:\broker-diag-tests`. `ArmAndScan_WithDiagnostics_*` (2 scan catch-up cases in the base
`.WatchDiagnosticsFilter.cs`) are ported here via `OpenScanChannelAsync` + `RecordingBlockSectionWriter`.

## Dropped / renamed / moved
See commit message (built by comparing `public async Task` names). Renamed 4 (per brief). Dropped:
EndWatch_StopsWatchTasks_AndWritesEndWatchAck; two DataRows of NoQueryableCachedCursor (drive spec strings
no longer exist). Moved to C3b: 4 `ServeAsync_*` control-loop cases, and the two `ServeOnce_ScanProgress_*`
cases that lived in the base `.WatchRecovery.cs`.

## Evidence
Ports are not written test-first. Targeted: `dotnet test MFTLib.Tests ... --filter FullyQualifiedName~JournalBrokerHostTests`
-> Passed 39/39, run 6 times with no flake. Whole suite (`run-coverage.ps1 -NonInteractive`, before a
one-token lint fix in WatchSupport that was re-verified by rebuild + targeted run): Total 1343, Passed 1337,
Skipped 6, Failed 0. Build: 0 warnings, 0 errors (test project built with `dotnet build MFTLib.Tests`; the
native project was built with MSBuild; solution-level `dotnet build` cannot load the vcxproj).
aislop: 99/100, 6 warnings, none in my files: the 4 baseline ones plus `MFTLib/Broker/Host/JournalBrokerHost.cs:43`
(too-many-params, 8 params, C1 constructor) and `MFTLib/Index/DriveStatus.cs:42` (UnusedAutoPropertyAccessor
`CompactionNeeded.get`), both pre-existing on the base and outside this task. My one finding
(RedundantArgumentDefaultValue in WatchSupport) was fixed.

## Concerns
- Merge with C3b: if its main partial declares `[TestClass]`/`[DoNotParallelize]` (it will), delete those two
  attributes from WatchSupport (duplicate attribute is CS0579). The main partial likely also gives a base class
  (`BrokerBlockTestBase`); mine lists none, which is legal for a partial.
- Two aislop findings above (C1) are not mine and were left alone.
- No suspected production defects found; every ported assertion passed unweakened.
