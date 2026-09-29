### Task C3b: Port host tests: scan, control and entry point

**Files:** Create `MFTLib.Tests/BrokerProtocolTests.cs`, `.Frames.cs`, `.Scan.cs`, `.GrowUsnJournalFrames.cs` (round-trip every kind in the table in C1, including `RequestId` and the `CatchUpLost` kind with every loss field present and with `BytesBehind` and `SizeThatWouldHaveRetained` null, and the read rejection of an unknown kind), `JournalBrokerHostTests.cs`, `.RequestDisconnect.cs`, `.Scan.cs`, `.Progress.cs`, `.ControlLoop.cs` (new: the four `ServeAsync_*` control-loop cases from `JournalBrokerHostTests.Watch.cs:134-199` at the base commit, on `HostChannelHarness`), `JournalBrokerHostRealSeamsTests.cs`, `.Operations.cs`, `JournalBrokerHostBlockScanTests.cs`, `GrowUsnJournalHostTests.cs`, `VolumeQueryHostTests.cs` from their base-commit versions. Finish `DefaultElevatedEntryRunnerTests.cs` (the real-pipe test becomes `RunBroker_ValidControlPipe_ServesUntilControlCloses_ExitsWithCode0`; update both lines in `scripts/coverage-linux.sh`) and `ElevatedEntryPointTests.cs` (`--once` cases deleted; `RunBroker(controlPipeName)` recorded).

- [ ] Port; commit "Port host scan, control, protocol and entry-point tests".

**Gate:** green. **Depends on:** C1.

---

