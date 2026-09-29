### Plan Compliance
- Issues found: the residual coverage table does not follow the brief's reachability classes. It leaves non-elevated paths in class (b) or (c), and labels admitted races class (c), at `.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-CI-report.md:28`, `:31`, `:34-35`, `:37`, and `:40`.
- Cannot verify from diff: the required `Co-Authored-By: Claude <noreply@anthropic.com>` commit trailer. The review package lists only the commit subject.

### Strengths

- Every added test asserts an observable contract rather than merely executing a line: the lost-block retry preserves status and failure detail (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.Open.cs:58`), repeated catch-up stays fault-free (`MFTLib.Tests/Index/FileIndexWatchCatchUpTests.cs:266`), pre-cancelled waiting leaves the watch usable (`MFTLib.Tests/Index/FileIndexWatchCatchUpTests.cs:281`), failed handle disposal still drains and permits restart (`MFTLib.Tests/Index/FileIndexWatchPumpTests.cs:290`), and an unopenable volume reports no journal evidence (`MFTLib.Tests/Index/UsnJournalVolumeInteropTests.cs:157`).
- The live-journal test does not assume T or U is absent. It snapshots mounted letters and selects the first absent letter from D through Z before calling the real reader (`MFTLib.Tests/Index/UsnJournalVolumeInteropTests.cs:164-168`). A letter already mounted at selection time cannot be chosen, satisfying the brief's runtime proof requirement.
- The changed tests introduce no delay or polling sleep. File-index operations use the test token, potentially blocking pump work uses `HangGuard`, cache directories are owned by `WatchHarness` or `LossScriptedCache`, and the classes touching journal or other process-wide state are nonparallel (`MFTLib.Tests/Index/UsnJournalVolumeInteropTests.cs:18-20`, `MFTLib.Tests/Index/FileIndexCatchUpLossTests.cs:16-18`).
- `FailProductionNumber` is nullable and defaults to null, so existing `LossScriptedCache` users retain their prior production behavior; only the new test assigns it (`MFTLib.Tests/Index/FileIndexCatchUpLossTests.Open.cs:62`, `:98`, `:136`).

### Issues
#### Critical (Must Fix)
- None.

#### Important (Should Fix)
- Reachable non-elevated paths remain uncovered. `CacheDirectory.ResolveDefaultPath` reaches `ComputeDefaultPath` at `MFTLib/Index/CacheDirectory.cs:33`, and the live checkpoint path reaches `ReadLiveJournal` at `MFTLib/Index/JournalCheckpointCheck.cs:129`; the report calls both class (b) only because module initialization permanently enables guards in the test host (`task-CI-report.md:28`, `:31`). Both guard contracts explicitly say activation is process-local and not inherited by child processes (`MFTLibTestExtensions/CacheDirectoryIsolation.cs:16`, `MFTLibTestExtensions/JournalIsolation.cs:36`), so a non-elevated isolated helper can exercise them, with an unmounted letter avoiding a real NTFS dependency for the journal path. Separately, `MFTLib/Index/FileIndex.Rescan.cs:216-218` is reachable with the existing fake producer by throwing a custom exception whose `Message` returns null: `ProduceDriveBlockAsync` copies that value at `MFTLib/Index/FileIndex.Scanning.cs:229-231`. These are class (a) paths under the brief and need behavior assertions, so the reported 8/22/19 classification and claim that every class (a) line is covered cannot be trusted.
- Reachable race handling is mislabeled dead or unreachable. The report itself describes the `FileInfo.Exists` to `Length` delete/ACL race guarded by `MFTLib/Index/BlockFile.cs:238-248` and the directory enumeration race guarded by `MFTLib/Index/FileIndex.ScanCleanup.cs:169-177`, yet assigns both class (c) (`task-CI-report.md:34-35`). It also assigns `MFTLib/Index/FileIndex.WatchDrive.cs:191-193` class (c), although `StopWatchingAsync` can clear `WatchRequested` between the first check in `MFTLib/Index/FileIndex.RescanRestart.cs:172-178` and `PrepareStart`; stop deliberately takes no lifecycle gate. The brief explicitly requires reachable races without a deterministic seam to be class (b)-race, not class (c). Correct these rows and counts so the controller does not treat live defensive branches as deletion candidates.

#### Minor (Nice to Have)
- None.

### Assessment
Task quality: Needs fixes
Reasoning: The five tests are focused and well asserted, but the central deliverable is an exhaustive, reliable classification of the remaining lines. Several reachable paths are still uncovered or mislabeled unreachable, so this task cannot yet support the final coverage decision.
