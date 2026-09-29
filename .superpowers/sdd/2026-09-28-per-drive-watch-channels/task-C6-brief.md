### Task C6: Watch channels and BrokerIndexWatchSource

**Files:**
- Create: `MFTLib/Broker/Client/BrokerProcess.Watch.cs` (`OpenWatchChannelAsync`), `MFTLib/Broker/Client/BrokerWatchChannel.cs`, `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`, `BrokerIndexWatchSourceCaughtUpTests.cs`, `BrokerIndexWatchSourceFaultTests.cs`, `BrokerLiveWatchErrorTests.cs`, `BrokerFileIndexRescanTests.cs`, `BrokerDeathTests.cs`, `Index/WatchFailureObservationTests.cs` (base-commit versions ported), `TestSupport/ScriptedWatchBrokerHarness.cs` (rewritten over `BrokerTestHarness`: per-drive scripted watch sources the test drives)
- Modify: `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` (`public IIndexWatchSource CreateWatchSource() => new BrokerIndexWatchSource(_connectAsync);`; `_connectAsync` already has the `BrokerProcess` factory type from C2; A5 removed the old method)

```csharp
internal Task<BrokerWatchChannel> OpenWatchChannelAsync(IndexWatchTarget target, CancellationToken cancellationToken);
internal sealed class BrokerWatchChannel : IIndexDriveWatch
{
    public char DriveLetter { get; }
    public IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
    public ValueTask DisposeAsync(); // closes the pipe; completes once nothing further can be read
}
public sealed class BrokerIndexWatchSource : IIndexWatchSource
{
    public BrokerIndexWatchSource(Func<CancellationToken, Task<BrokerProcess>> connectAsync);
    public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}
```

`ReadAsync` reads straight off the pipe (no queue): `JournalBatch` frame to `new JournalBatch(entries, journalId, nextUsn)`, `CaughtUp` to `new DriveCaughtUp()`, `Heartbeat` skipped, `Error` throws `DriveWatchFaultException(drive, message)`, `Stalled` throws `BrokerChannelLostException(drive, hostMessage)`, EOF or I/O throws `BrokerChannelLostException(drive, ...)`. `StartAsync` returns after the channel is connected and `StartWatch` is written; a cancelled start closes its own pipe. The source keeps no per-drive maps.

- [ ] **Failing tests:**
  - Ports of the watch-source tests onto one handle per drive (arming, catch-up, fault).
  - `TimedOutStop_ThenNewWatch_RunsUndisturbed` (spec 9 and section 10, [MFTLib issue 252 (a late EndWatchAck ends the next watch)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/252)): the host holds `T`'s first watch pipe open and silent; `StopWatchingAsync(T)` with a token that times out closes the pipe; `StartWatchingAsync(T)` opens a fresh pipe and receives batches; releasing the held host task delivers nothing to the new watch.
  - `ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal` (B1's single-ownership contract: the pump cancels the token and only then disposes the handle).
  - `OneChannelLost_OnlyThatDriveFaults` (spec 9): the host closes `T`'s pipe; `WatchFault(Channel, 'T')`; `U` keeps applying.
  - `HostError_IsDriveWatchFault_TriggersRecovery` (once B6 is merged in wave 6 this asserts recovery; in C6's worktree assert `WatchFault(Drive, 'T')` only, and C8 extends it).
  - `ProcessDeath_FaultsEveryDriveByName_EndedFiresOnce` (spec 9): the harness ends the host; each watched drive raises exactly one `WatchFaulted(Channel, letter)`; each `WatchFailureMessage` is set; `Ended` fires once.
  - `RescanOfT_OverBroker_ReopensOnlyTsChannel` (port of `BrokerFileIndexRescanTests`).
- [ ] Implement; verify; commit "Each drive watch runs on its own broker pipe".

**Gate:** green. **Depends on:** B2, C2, C7. **Parallel with:** B6.

---

