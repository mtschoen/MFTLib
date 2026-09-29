### Finding Verdicts

1. ADDRESSED - N-2/W40-R1 evidence. The fix report now gives a file, line, before/after mutation, literal filtered `dotnet test` command, and failing output for every scenario (`task-C8-report.md:67`, `:87`, `:107`, `:127`, `:147`, `:157`, `:177`, `:187`). The archived transcript contains the corresponding commands and full failures. Each mutation plausibly causes the quoted failure: disabling the client stall branch prevents the OneDrive fault (`MFTLib/Broker/Client/BrokerFrameReader.cs:172`); extending host processing indefinitely prevents the wedged-loop `Stalled` frame (`MFTLib/Broker/Host/HostPipeWriter.cs:190`); removing heartbeat visits makes the idle tests fail in their bounded heartbeat wait (`MFTLib/Broker/Host/BrokerHeartbeatSender.cs:73`, `MFTLib.Tests/TestSupport/CrossDriveScenario.cs:178`); disabling recovery strands HostError (`MFTLib/Index/FileIndex.WatchPump.cs:217`); changing the catch-up limit or stopping after one loss produces the reported scan counts and exception (`MFTLib/Index/FileIndex.CatchUp.cs:9`, `:67`); and treating a retained cursor as lost produces the reported three-loss exception (`MFTLib/Index/JournalCheckpointCheck.cs:79`). The repeated heartbeat-window mutations are also consistent with their failures: retaining the first read timestamp makes later reads inherit an expired stall window (`MFTLib/Broker/Client/BrokerFrameReader.cs:106`). The fix diff adds no tests, so no new round-1 test lacks W40-R1 RED evidence.

2. NOT ADDRESSED - bounded cleanup. `DisposeAsync` itself now bounds index and broker disposal and uses nested `finally` blocks to reach broker disposal and cache deletion after an index timeout or exception (`MFTLib.Tests/TestSupport/CrossDriveScenario.cs:196`, `:203`, `:206`, `:210`, `:212`, `:214`). However, the open-failure path wraps that entire cleanup in another `WaitAsync(HangGuard)` (`MFTLib.Tests/TestSupport/CrossDriveScenario.cs:88`). The inner index wait can consume the full same `HangGuard` before its `finally` begins broker disposal at line 210. The outer wait can therefore time out at that point, so `OpenAsync` stops awaiting while broker disposal and cache deletion continue in the background. It can also replace the original open exception with that outer `TimeoutException`. The open-failure path consequently does not await every bounded disposal through cleanup completion.

### New Breakage in the Fix Diff

- No separate item. The premature whole-cleanup timeout at `MFTLib.Tests/TestSupport/CrossDriveScenario.cs:88` is the unresolved Important finding 2 above and is counted once.

### Out-of-Scope Observations

- None.

### Verdict

Findings remain open: finding 2.
