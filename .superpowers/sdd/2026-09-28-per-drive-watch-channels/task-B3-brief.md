### Task B3: Port index watch tests: rescan interplay

**Files:** Create `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs`, `FileIndexWatchFailedRescanTests.cs`, `FileIndexWatchRescanFaultDuringProductionTests.cs`, `FileIndexWatchRescanCheckpointLossTests.cs`, `FileIndexWatchRecoveryFaultTests.cs` from their base-commit versions. The rescan-recovers-a-faulted-drive cases in `FileIndexWatchRecoveryFaultTests` become manual `RescanAsync(X)` cases now; B6 extends them with automatic recovery. `_swapGate` reflection (`FileIndexWatchRescanTests.cs:302`, `FileIndexWatchFailedRescanTests.cs:139`) stays until B5 replaces it.

- [ ] Port with the same rules as B2; add `Rescan_OfT_LeavesUsPumpRunning` (U applies a batch while T's producer is gated: spec 9, "Rescan of X leaves Y's pump running"); commit "Port FileIndex rescan-while-watching tests".

**Gate:** green. **Depends on:** B1.

