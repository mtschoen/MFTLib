### Plan Compliance
- Issues found (the requested behavior is present; these are the gaps):
  - Harness disposal: a host session fault is thrown out of `BrokerProcess.DisposeAsync` before the process finishes its own cleanup (Important 1).
  - Timeout tests poll with a real-time `Task.Delay` and do not pin the bound (Important 2). `MFTLib.Tests/BrokerProcessTests.Timeouts.cs:992-1007` (diff lines).
  - The named risk "disposal racing in-flight scans and channel opens" has no test, and on the harness's in-memory pipes the drive reader lets a read-after-close `InvalidOperationException` through (Important 3).
  - The spec bounds a started control write by the stall limit (spec :103-106). The brief and the implementation use `ControlReplyTimeout` (Minor 1).
- Cannot verify from diff:
  - aislop result. The report gives 99/100 with six warnings, which matches the base list: the four listed, the 8-parameter `JournalBrokerHost` constructor and `DriveStatus.CompactionNeeded`. The report also says no `BrokerLiveness` member was flagged. The controller should confirm this on the merged head.
  - The whole-suite result (1356 run, 1350 passed, 6 skipped, 96.1% line coverage) and the RED runs. The RED output quoted for the frame-length tests names the expected pre-fix failures (`OverflowException` and the huge allocation).
  - That `MFTLib` already lists `MFTLibTestExtensions` in `InternalsVisibleTo`. The harness constructs the internal `BrokerProcess` constructor and `IBrokerPipeFactory` (`MFTLibTestExtensions/BrokerTestHarness.cs:3249`). It compiles, according to the report.

Named-risk checks:
- **Channel open as one owned operation** (`BrokerProcess.Channels.cs:2099-2135`):
  - The listener is created first. The connection wait is linked to the caller token and `_lifetime`.
  - Every failure cancels the wait, disposes the listener (idempotent) and observes the abandoned connection task.
  - A failure after connection disposes the channel, which disposes the listener, and both factories release the connected stream with it (`BrokerPipes.cs:2072-2078`; `InMemoryBrokerPipes.cs:3412-3419` handles both orders of the connect/release race).
  - A cancellation before the write starts releases the id (`Control.cs:2370-2384`). A cancellation after it keeps the entry until the late reply arrives, which is the spec 2.2 rule.
  - `TrackChannel` refuses a channel once disposal has begun (`:2185-2197`).
  - All correct.
- **Started control write** (`Control.cs:2365-2407`):
  - The write is finished under `_lifetime` linked with a timer on the injected `TimeProvider`. On failure or timeout it calls `RequestEnd`.
  - The caller's token is observed only until the write lock is held. Correct against the brief.
- **Request ids** (`Control.cs:2319-2358`):
  - Allocation happens under `_gate` and skips 0 and every entry in the table. A full table throws the exact message.
  - Entries are removed only on a reply (`:2472-2481`), on a pre-write cancel, or on End (`:2486-2506`). Correct.
  - A test-only seam can spin forever if `MaximumRequestIdForTest` is lowered below ids already in the table. This is a negligible test-seam edge.
- **Control reader:**
  - A reply with no pending entry is dropped (`:2477`). An unknown kind ends the process (`:2463-2464`).
  - `End` runs once, because it runs only at the end of the single reader task. It sets `_endReason` under the lock, then fails and clears every pending entry, then fires `Ended` (`:2486-2506`). Correct.
  - Not tested: an unknown kind on the control pipe and `Stalled` on the control pipe (Minor 4).
- **Scan frame order** (`Scan.cs:2732-2810`):
  - The frames must arrive as Cursor, then ScanProgress*, then ScanReady, then a terminal frame, then EOF. Heartbeats are skipped. `Error` becomes `InvalidOperationException`, and any other out-of-order frame becomes `BrokerChannelLostException(drive)`.
  - `CatchUpLost` returns the block with `AdvancedCursor` null, empty entries and the loss (`:2715-2717`). The section is disposed on every failure (`:2720-2725`).
  - Correct, and tested (`BrokerProcessTests.Scan.cs`).
- **Frame length:**
  - One shared reader (`BrokerFrameStream.cs:3108-3140`). It checks `< 1` and `> MaximumFrameLength` before `new byte[4 + totalLength]`, with the reasoning in a comment. There are no unsigned or overflow gaps: `4 + (1 << 30)` fits.
  - On a drive pipe, the host's existing catch writes an `Error` and ends only that channel (checked outside the diff: `JournalBrokerHost.Channel.cs:171-177`).
  - On the control pipe, `ServeAsync` ends with `InvalidDataException` and writes no `Error` first (checked: `JournalBrokerHost.Session.cs:34-51`). See Minor 2.
  - Client: `BrokerChannelLostException` on both pipes.
  - Regression tests exist on both sides (`BrokerFrameLengthTests.cs`).
- **Elevation:**
  - Every `LaunchAsync` call in the tests passes a fake delegate (`BrokerProcessLaunchTests.cs:197-429`, `BrokerProcessTests.cs:1310-1328`). The end-to-end test runs a fake host over a real unelevated named pipe. Nothing reaches `BrokerLauncher.Launch` or `runas`.

### Strengths
- The frame-length defect is fixed in the right place. The host's duplicate `ReadExactAsync` was deleted and one reader now serves both sides. The maximum's comment argues from the largest legitimate frame, and the RED evidence names the exact pre-fix failures.
- The channel-open handshake is a single owned operation with one cleanup path. The in-memory registry's release is race-safe in both orders of connect and release.
- The request-id allocator matches R12 exactly, and its seams make the wrap and exhaustion tests cheap and deterministic.
- The scan collector is small, reads the protocol order directly, and leaves the section owned correctly on every exit. `BrokerMftBlockProducer` carries the proven loss through without making it look like a failure.
- The tests use real host code through the harness and scripted frames for orders a real host never produces. `TestGate` handles ordering, and every await has a timeout.
- Scope discipline: no forbidden files were touched. `HostChannelHarness` is unchanged. `BlockFile.DeleteOnClose` was correctly not restored, because nothing reads it.

### Issues
#### Critical (Must Fix)
None.

#### Important (Should Fix)
1. **Harness disposal throws before `BrokerProcess.DisposeAsync` finishes, and the throw can hide a test's own failure.** `BrokerProcess.cs:2901-2922` awaits `_control.DisposeAsync()` before it sweeps channels and awaits the reader. Under the harness, that call is `HostLifetimeStream.DisposeAsync` (`InMemoryDuplexStream.cs:3473-3486`), which rethrows a faulted `ServeAsync`.
   - So when the host session faulted, disposal throws with drive channels still open and the reader not awaited. `_disposed` is already set, so a second dispose does nothing.
   - In a consumer test written as `await using var process = BrokerTestHarness.StartInProcess(...)`, the disposal exception replaces an assertion failure already in flight. C# lets a `finally` exception win. `ControlWrite_FailsMidFrame_EndsProcessLoudly` (`BrokerProcessTests.cs:1223-1257`) shows the shape: any failed assertion there would be reported as `EndOfStreamException`.
   - On concern (4): surfacing a host fault is right, but not this way.
   - Fix: run the whole disposal (channels, reader, host end) in `finally` and rethrow the host fault last. Better, keep `DisposeAsync` non-throwing and expose the host session's completion (for example a harness-returned `Task HostCompleted`) for tests to assert on.
2. **The timeout tests poll the fake clock on a real-time `Task.Delay` and do not pin the limit.** `AdvanceUntilCompleteAsync` (`BrokerProcessTests.Timeouts.cs:992-1007`) advances 30 s at a time, with `Task.Delay(20 ms)` between steps, until the task completes.
   - This breaks the broker rule "No `Task.Delay` on real time in a test" (broker-common "Rules for every broker task").
   - It would also pass if the bound were 60 s or 10 minutes. Only the message text, which is built from the same constant, mentions 30.
   - For the control write, `Gate.Entered` fires after the timeout source exists (`Control.cs:2388` comes before the write). The test can therefore advance `ControlReplyTimeout - 1 tick`, assert that the process has not ended, then advance one tick and assert that it has.
   - For the connection-after-ack bound, a `TimeProvider` wrapper that signals `CreateTimer` gives the same determinism.
3. **Disposal racing an in-flight scan or channel open is untested, and the harness's drive reader can surface the wrong exception type.**
   - Nothing exercises `TrackChannel`'s disposed branch (`Channels.cs:2189-2193`) or `DisposeAsync` while `ScanDriveAsync` is reading.
   - The report says the control reader needed a catch-all because the in-memory pipe throws `InvalidOperationException` on read after close (`Control.cs:2433-2439`). `BrokerFrameReader` (`BrokerFrameReader.cs:1901-1902`) and `BrokerDriveChannel.WriteAsync` (`:1829`) catch only `IOException`, `InvalidDataException` and `ObjectDisposedException`.
   - Under the harness, then, a scan whose channel is closed by process disposal can throw a raw `InvalidOperationException`, which is the type the API reserves for a host `Error`. Consumer tests (file-wizard `BrokerDeathTests`, which the spec at :1176 moves onto this harness) would see a different failure than production does.
   - Fix: make `InMemoryDuplexStream` throw `ObjectDisposedException` after close, so the harness matches the named-pipe contract. Add a harness test that disposes the process mid-scan and asserts `BrokerChannelLostException('C')` with the section released.
   - I did not run this. The recommended test settles it either way.

#### Minor (Nice to Have)
1. The spec bounds a started control write by the stall limit (spec :103-106), while the brief and the code use `ControlReplyTimeout` (`Control.cs:2388`). Both are 30 s. Where they disagree the spec wins, so either switch to `BrokerLiveness.StallLimit` or have the orchestrator record the brief's reading. Update the `BrokerLiveness` doc comments (`BrokerLiveness.cs:1680-1692`) to match.
2. The frame-length ruling says "an Error frame on that pipe where one can still be written". On the control pipe the host writes none before ending the session (`JournalBrokerHost.Session.cs:34-51`), so the client's `Ended` reason is the generic "The broker closed its control pipe." (`Control.cs:2468`). This is defensible, because a malformed frame has no request id, but the orchestrator should confirm that this is the intended reading.
3. `RequestAsync` answers a reply of the wrong known kind with `BrokerChannelLostException(null, ...)` but leaves the process running (`Control.cs:2313-2316`). The exception's own contract (`BrokerChannelLostException.cs:1759-1760`) says a null-drive loss ends the process. Either call `RequestEnd` there or use a different exception.
4. Untested reader branches: an unknown kind on the control pipe (`Control.cs:2463`), `Stalled` on the control pipe (`:2460`), and the wrong-kind reply (`:2315`).
5. `Ended` is invoked synchronously inside the reader task (`Control.cs:2505`), which `DisposeAsync` awaits.
   - A handler that throws makes a later `DisposeAsync` rethrow the handler's exception.
   - A handler that blocks on `DisposeAsync` deadlocks.
   - Document both or isolate the handler invocation.
6. `LaunchAsync`'s connect timeout runs on the real clock (`Launch.cs:2617`), and `LaunchAsync_NeverConnects_TimesOut` / `LaunchAsync_DefaultTimeoutOverridden_TimesOut` wait on a real 50 ms timer. These are plan-mandated ports that keep the base shape. The global rule lists "connect timeout" among the limits that read a `TimeProvider`, so an internal clock overload would bring it into line.
7. Spec inconsistency, not the implementer's fault: spec :942 defines `HoldWrites` as holding host writes, while spec :1279 uses it to hold the client's control write. The implementer kept the option as the interface defines it and held the client write through an internal client-stream seam (`BrokerTestHarness.cs:3235-3251`). On concern (3), this is the right call. The orchestrator should fix the spec table row.
8. `MaximumFrameLength` is enforced only on read. A host that writes a larger terminal `JournalBatch` would be seen by the client as a lost channel rather than as a host error. The later bounded catch-up task closes this; note it there.

Concern (1) judged: the brief's R5 and R6 bullets place both limits in C2, and both read the injected clock (`Control.cs:2388`, `Channels.cs:2157`). C7's brief (`task-C7-brief.md:3`) lists only the stall limit, the per-request reply wait and `Stalled` on scan channels. The remainder is coherent and does not overlap.

Concern (2) judged: four `TimeSpan` constants, as the dispatch ruled, each with an enforcement doc. This is acceptable.

### Assessment
Task quality: Needs fixes
Reasoning: The production client is correct on every named risk I checked: channel ownership, request ids, the control reader, scan order and the frame-length bound. The consumer-facing harness is the problem. It throws a host fault out of a half-finished disposal, which can hide a test's own failure, and it may not match production's exception types when a channel is closed mid-read. The two timeout tests also break the no-real-time rule.
