### Finding Verdicts

1. ADDRESSED. `task-CI-report.md:63-65` applies CI-Q1 to `CacheDirectory.cs:33` and `JournalCheckpointCheck.cs:129`, classifies both as class (b)-guard, and gives the ruled reason: the guards are one-way in the test process and child-process hits are not collected by the in-process coverage run. That matches the guarded branches at `MFTLib/Index/CacheDirectory.cs:26-33` and `MFTLib/Index/JournalCheckpointCheck.cs:120-129`.

2. NOT ADDRESSED. The three race rows are now correctly class (b)-race in `task-CI-report.md:67-74`, and their no-existing-seam reasons hold:
   - `MFTLib/Index/BlockFile.cs:229-248` uses a local `FileInfo` and direct framework calls with no injection point around the `Exists` and `Length` access.
   - `MFTLib/Index/FileIndex.ScanCleanup.cs:161-177` calls `Directory.EnumerateFiles` directly inside the catch scope, with no seam between ownership acquisition and enumeration.
   - `MFTLib/Index/FileIndex.RescanRestart.cs:172-178` releases `_stateLock` and calls the restart. With `gateHeld: true`, `MFTLib/Index/FileIndex.WatchDrive.cs:112-150` reaches `PrepareStart` synchronously, so a concurrent stop can interleave but no existing seam can force that exact window. `RestartBeforeRegistrationForTest` is later at `FileIndex.WatchDrive.cs:163-166` and can cover only the registration re-check at `FileIndex.WatchDrive.cs:281-283`.

   However, not every remaining class (c) line in `task-CI-report.md:76` is unreachable:
   - `MFTLib/Index/FileIndex.Rescan.cs:218` is class (a), not class (c). A fake MFT producer can throw a custom exception whose overridden `Message` returns null. `MFTLib/Index/FileIndex.Scanning.cs:229-231` copies that null into `PendingDriveResult.ProducerFailureMessage`; `MFTLib/Index/FileIndex.Rescan.cs:153-157` passes it to `RecordRescanProducerFailure`; and an already-published drive takes `FileIndex.Rescan.cs:214-218`. This is deterministic, non-elevated, and in-process.
   - `MFTLib/Index/FileIndex.cs:371-372` is class (a), not class (c). `FileIndexOptions.Drives` is an `IReadOnlyList` property at `MFTLib/Index/FileIndexOptions.cs:9`, but the index retains the supplied object and `FileIndex.Drives` re-enumerates it at `MFTLib/Index/FileIndex.cs:116-120`. Supplying a mutable list, opening the index, then appending another configured drive makes that new letter have neither an online block nor a blockless status and reaches the throw.

   The other five class (c) executable lines remain unreachable on the inspected control flow: `BlockFile.cs:197` follows only a managed exception from primitive header writes at `BlockFile.cs:360-376`; `FileIndex.Watch.cs:213` follows `ExceptionDispatchInfo.Throw()` at line 212; every originally configured drive gets a runtime at `FileIndex.cs:96-100`, excluding `FileIndex.WatchCatchUp.cs:18`; `IndexNavigation.cs:146` repeats an equality already checked immediately after each parent assignment at lines 121-125; and `JournalMutator.cs:241` cannot see `meaningful == None` after the nonzero classification gate at `JournalMutator.cs:77-109`.

   The corrected classification is therefore a = 11 (8 covered and 3 still uncovered), b = 33, c = 5, not the reported 8/33/8.

### New Breakage in the Fix Diff

- None. Fix round 1 is report-only and adds no tests or code. W40-R1 RED evidence is therefore not applicable to this round, and no new test lacks an exact RED command or failing output. The five tests in the reference diff are existing-behavior coverage tests covered by the brief's exemption and CB-Q1.

### Out-of-Scope Observations

- None.

### Verdict

Findings remain open: Finding 2. The race classifications are corrected, but three deterministic non-elevated lines remain incorrectly classified as unreachable.
