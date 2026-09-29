### Finding Verdicts

1. NOT ADDRESSED (Important): The production handshake now uses per-read state and a single lock, so the stall path claims only an unsettled current read and the frame path cannot return a frame after that claim (`MFTLib/Broker/Client/BrokerFrameReader.cs:101-127`, `MFTLib/Broker/Client/BrokerFrameReader.cs:162-193`). The deterministic race test does force the stall claim before releasing a cancellation-ignoring frame read (`MFTLib.Tests/BrokerProcessLivenessTests.cs:263-281`). However, W40-R1 requires an exact command plus real failing output for every new test. The report gives failing output but abbreviates the command as `dotnet test ... --filter ...`, so it is not an exact command (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C7-report.md:36`). The required RED evidence is therefore incomplete.

2. NOT ADDRESSED (Important): `BrokerFrameReader.DisposeAsync` does await `ITimer.DisposeAsync`, and drive-channel teardown awaits it before closing the listener pipe (`MFTLib/Broker/Client/BrokerFrameReader.cs:81-99`, `MFTLib/Broker/Client/BrokerDriveChannel.cs:89-100`). The control path does not preserve that ordering: `ReadControlAsync` calls `End(reason)`, which disposes the control stream and raises `Ended`, before the method scope exits and the `await using` reader is disposed (`MFTLib/Broker/Client/BrokerProcess.Control.cs:224-248`, `MFTLib/Broker/Client/BrokerProcess.Control.cs:293-312`). Process teardown can therefore be observed complete before the timer drain completes. The regression test also gates `DrainGatedTimer.DisposeAsync` itself without ever running or gating the timer callback, so it does not prove that disposal waits for an executing callback (`MFTLib.Tests/BrokerProcessLivenessTests.cs:283-300`, `MFTLib.Tests/BrokerProcessLivenessTests.cs:405-433`). Its report entry supplies failing output but no exact test command, which also fails W40-R1 (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C7-report.md:37`).

3. NOT ADDRESSED (Important): `ScriptedBroker.ReadRequestAsync` is now bounded (`MFTLib.Tests/TestSupport/ScriptedBroker.cs:32-36`), and the previously cited scan setup operations are bounded. The promised all-await audit is incomplete. The new race stream still awaits both its release gate and inner stream read without a hang guard (`MFTLib.Tests/BrokerProcessLivenessTests.cs:393-402`); the drain timer similarly awaits its gate and inner disposal without a hang guard (`MFTLib.Tests/BrokerProcessLivenessTests.cs:422-433`); and session cleanup reaches an unbounded host-control disposal (`MFTLib.Tests/TestSupport/ScriptedBroker.cs:63-72`). These are externally progressing awaits in the test helpers and can outlive or hang a failed test.

4. ADDRESSED (Minor): `ScanDriveAsync` now documents `TimeoutException` for the volume-query and channel-open reply timeout (`MFTLib/Broker/Client/BrokerProcess.Scan.cs:18-22`).

5. ADDRESSED (controller-assigned): The change is in the actual in-process harness stream, not production. It translates `InvalidOperationException` only from an in-flight `ReadAsync` after that same stream end has been marked closed (`MFTLibTestExtensions/InMemoryDuplexStream.cs:32-44`, `MFTLibTestExtensions/InMemoryDuplexStream.cs:59-68`). The stream is internal to the public test-support package and the translation preserves its documented closed-stream contract, while unrelated reads before own disposal still expose `InvalidOperationException` unchanged (`MFTLibTestExtensions/InMemoryDuplexStream.cs:3-10`, `MFTLibTestExtensions/InMemoryDuplexStream.cs:34-44`). This is narrow and safe for package consumers.

### New Breakage in the Fix Diff

None separate from the still-open findings above.

### Out-of-Scope Observations

None.

### Verdict

Findings remain open: 1, 2, and 3.
