### Task C5: Host liveness: operation state, heartbeat thread, watchdog (Opus)

Opus because the heartbeat sender is a dedicated thread racing channel writes, and the watchdog must measure time without progress, not time spent working, under a fake clock.

**Files:**
- Create: `MFTLib/Broker/Host/ChannelOperationState.cs`, `MFTLib/Broker/Host/HostPipeWriter.cs` (one per pipe: write lock, last-write time, state), `MFTLib/Broker/Host/BrokerHeartbeatSender.cs`, `MFTLib/Internal/Libc.cs` (`msync` for non-Windows ranged flush), `MFTLib.Tests/JournalBrokerHostLivenessTests.cs`
- Modify: `JournalBrokerHost.Session.cs`, `.Channel.cs`, `.Scan.cs` (progress pump throttle on `TimeProvider`; bounded catch-up loop; state around volume open), `JournalBrokerHost.cs` (watch loop publishes `WaitingOnVolume` before each read and `Processing` per batch), `JournalBrokerHost.Sources.cs` (`ReadJournal` uses `ReadUsnJournalBounded`), `MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs`, `MFTLib/Index/BlockWriter.cs` and `MFTLib/Index/BlockFile.cs` (ranged flush), `MFTLib/Internal/Kernel32.cs` (add `FlushViewOfFile`)

**Interfaces produced:**

```csharp
internal enum ChannelOperationKind { Idle, WaitingOnVolume, Queued, Processing } // Idle: the control loop waiting for a client request
internal readonly record struct ChannelOperationState(ChannelOperationKind Kind, string Step, DateTimeOffset Since);
internal static class BrokerLiveness
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ProcessingLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);    // read by C7
    public const int CatchUpBufferReadsPerCall = 256;                          // 16 MB per bounded read
}
public void Flush(Action<long>? rangeFlushed);          // BlockFile: 64 MB ranges through FlushViewOfFile (Windows) or msync (elsewhere), on the view pointer plus offset
public void Complete(DateTime scanTimestampUtc, Action<long>? rangeFlushed); // BlockWriter; every caller updated
```

Behavior (spec 2.2, with amendments S1, S2, S3, R11, L1): every pipe (control included) has a `HostPipeWriter`; a frame write or a state republish restarts its progress clock, and the `IBrokerOperationReporter` C1 threads into each source is that writer's state. `BrokerHeartbeatSender` runs on one `Thread` (`IsBackground = true`), waking every `HeartbeatInterval` through `TimeProvider` (`timeProvider.CreateTimer` signalling a `ManualResetEventSlim` the thread waits on, so a fake clock drives it). On each visit, for each pipe:

- A pipe whose previous write (heartbeat or any frame) has not completed is skipped (S2). The sender never awaits a write: it starts `HostPipeWriter.TryStartHeartbeat()`, which returns false without writing when a write is in flight, and otherwise starts the write asynchronously and returns. A pipe whose write is blocked therefore gets no further heartbeats and its client stall limit ends it; every other pipe is unaffected.
- The control pipe is heartbeated whenever it wrote nothing for `HeartbeatInterval`, unconditionally (S1).
- A drive pipe that wrote nothing since the last visit: `WaitingOnVolume` or `Queued` (a scan waiting for admission by the allocator, S3) writes `Heartbeat`; `Processing` past `ProcessingLimit` without progress writes `Stalled` naming the step (through the same non-blocking start) and cancels the channel.

The progress pump restarts the clock on every frame it writes. Scan steps republish: volume open (`WaitingOnVolume`), parse chunk callback, each 4096-record batch, each flushed range, each bounded catch-up call. A bounded catch-up call that throws ends catch-up: the loop stops, does not retry the call, and the host applies C1's journal check to choose between `CatchUpLost` and `Error`.

- [ ] **Step 1: Failing tests** (`FakeTimeProvider`, `HostChannelHarness`):
  - `IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls` (spec 9, "Idle watch stays alive"): the fake watch source never yields; advance 120 s in 5 s steps; 24 heartbeats observed, no `Stalled`.
  - `WedgedProcessing_WritesStalledNamingStepAndCloses` (spec 9 host half): a scan source holds `Processing` on a gate; advance past 30 s; the pipe receives `Stalled` with the step name, then EOF.
  - `ProcessingWithProgress_NeverStalls`: a source reporting progress every 10 s for 120 s.
  - `QueuedScan_WaitingForAdmission_Heartbeats` (S3, the heartbeat half of the queue test).
  - `IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit` (S1): a harness with a real client over `BrokerTestHarness` and a fake clock on both sides; no control requests and no channels; advance 300 s in 5 s steps; the control pipe receives a heartbeat every 5 s and `BrokerProcess.HasEnded` stays false.
  - `BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults` (S2): `HoldWrites` holds every write to `X`'s drive pipe; `X` and `Y` are idle watches; advance well past the stall limit; `Y` receives a heartbeat every interval and keeps watching; `X` receives none after the held write, and only `X` faults (client stall limit, `Channel`).
  - `HeartbeatSkipped_WhilePreviousWriteInFlight` (S2): the sender visits a pipe with a held write three times; exactly one heartbeat write was started for it.
  - `HeartbeatSender_RunsOnDedicatedThread`: the sender's `ManagedThreadId` differs from every thread-pool thread id recorded while the pool is saturated by blocked work items.
  - `CatchUp_BoundedReads_RepublishesPerCall`: a catch-up source returning three chunks; the final `JournalBatch` holds all entries; the progress clock was restarted three times (observed through a state-change hook).
  - `CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch` (L1): the source returns one chunk then throws and the synthetic window proves the loss; the pipe receives `CatchUpLost` then EOF, no `JournalBatch`, the progress clock was restarted once, and the fake clock was never advanced by the host.
  - `BlockFile_Flush_ReportsEachRange`.
- [ ] **Step 2: See them fail;** implement.
- [ ] **Step 3: Verify** targeted; host suites; whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "Host heartbeats idle pipes and reports a wedged operation as Stalled".

**Gate:** green. **Depends on:** C2 (the harness's `HoldWrites` and client clock), C3a, C3b. **Parallel with:** B5, C4, C7.

