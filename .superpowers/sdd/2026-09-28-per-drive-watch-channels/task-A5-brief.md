### Task A5: Delete the broker watch source

The old `BrokerIndexWatchSource` implements the old `IIndexWatchSource` (so B1 cannot compile with it) and calls the old `JournalBrokerClient` live-watch members (so C1 cannot compile with it). Deleting it once, before either trunk, means neither trunk touches the other's files; C6 rebuilds it over the new contract. Nothing inside MFTLib other than its own tests uses it (Grep `BrokerIndexWatchSource` and `CreateWatchSource` across `MFTLib`, `MFTLibTestExtensions`, `MFTLib.Tests`, `TestProgram`, `Benchmark`; the hits outside the files below are doc crefs in `MFTLib/Index/IIndexWatchSource.cs` and `FileIndex.Watch.cs`, which B1 rewrites).

**Files:**
- Delete: `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `.PerDrive.cs`, `.AbandonedStart.cs`; `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`, `BrokerIndexWatchSourceCaughtUpTests.cs`, `BrokerIndexWatchSourceFaultTests.cs`, `BrokerIndexWatchSourceArmingTests.cs`, `BrokerIndexWatchSourceArmingTests.LastDrive.cs`, `BrokerPerDriveArmTests.cs`, `BrokerPerDriveArmTests.Recovery.cs`, `BrokerWatchSourceAbandonedStartTeardownTests.cs`, `BrokerWatchStartSendCancellationTests.cs`, `BrokerFileIndexRescanTests.cs`, `MFTLib.Tests/Index/WatchFailureObservationTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` (remove `CreateWatchSource`)

- [ ] **Step 1:** Grep as above; delete and edit. No new tests: this removes a feature with no production caller inside MFTLib. C6 ports the deleted cases from the base commit (`git show c1d43784:<path>`).
- [ ] **Step 2: Verify** build, whole suite, `aislop scan .`.
- [ ] **Step 3: Commit:** "Delete the broker watch source". The commit message lists every deleted test file and that C6 ports it.

**Gate:** green (build, tests, aislop); the coverage comparison is made at the tranche end (see "Coverage tranches"). **Depends on:** none. **Parallel with:** A1, A2, A3.

---

