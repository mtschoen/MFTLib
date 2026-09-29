### Task C7: Client liveness: stall limit and control reply timeouts

**Files:** Modify `MFTLib/Broker/Client/BrokerFrameReader.cs` (stall limit from `BrokerLiveness.StallLimit` on the injected `TimeProvider`: a pipe with no frame of any kind for that long is closed and the read throws `BrokerChannelLostException(drive, "No frame from the broker for 30 seconds")`), `BrokerProcess.Control.cs` (the control reader uses the same limit; a stall ends the process: `Ended` fires; each control request waits at most `ControlReplyTimeout` and then throws `TimeoutException`, leaving its id to be dropped when a late reply arrives), `BrokerProcess.Scan.cs` (a `Stalled` frame on a scan channel throws `BrokerChannelLostException(drive, hostMessage)`). Create `MFTLib.Tests/BrokerProcessLivenessTests.cs`.

- [ ] **Failing tests:** `ControlSilentPastStallLimit_EndsProcess`; `ControlHeartbeats_KeepProcessAlive`; `ScanChannelSilent_FailsScanWithChannelLost_OtherScanUnaffected`; `ScanProgressKeepsScanAlive`; `ScanChannelStalledFrame_FailsWithHostMessage`; `ControlReply_TimesOut_LateReplyDropped_NextRequestSucceeds`. See them fail; implement; verify; commit "BrokerProcess closes any pipe that goes silent past the stall limit".

**Gate:** green. **Depends on:** C2. **Parallel with:** B5, C4, C5.

---

