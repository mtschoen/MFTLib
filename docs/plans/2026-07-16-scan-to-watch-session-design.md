# Scan-to-watch session API: design and implementation plan

Status: design, ready for a fresh MFTLib implementer fleet.
Branch: feat/scan-to-watch-session-api.
Governing plan: docs/plans/2026-07-15-scan-to-watch-session-api.md (its "Compatibility
constraints" are binding). Investigation: .superpowers/sdd/session-api/investigation.md.

## 1. Decision

Add a new owned abstraction: `JournalBrokerScanSession : IAsyncDisposable`. A
documentation-only fix cannot repair the mistake GitWizard made, because the mistake is
expressible in the type system: `ArmScanAndCatchUpAsync` returns a `BrokerScanResult`
that has no reference back to the `JournalBrokerClient` that produced it, so a consumer
can hold `result.AdvancedCursors` while disposing the client, and nothing (compiler,
runtime, or event) flags the orphaned cursors (investigation Q1, Q2). The session closes
this by construction: it owns the connected client privately, never hands it out, exposes
the scan result as `LatestScan` on the still-live session object, sources the watch
cursors from its own `LatestScan` (so a caller cannot pass mismatched or stale cursors),
latches broker death into a queryable `IsFaulted`/`FaultReason` state, and makes disposal
idempotent so the underlying (non-idempotent) client is disposed exactly once. The
existing `JournalBrokerClient` API is preserved unchanged for callers who already run
elevated or want the low-level primitive.

### Why a new type is materially safer (answering plan Q1)

- The correct scan-to-watch pattern is currently only enforceable by caller discipline
  across four steps on two objects, and the object that must survive (`client`) is not
  the object the consumer cares about (`result`). Binding both into one owned object makes
  "keep the thing you scanned with alive" the only spelling available.
- `SendStartWatchAsync` accepts any cursor dictionary with no tie to a prior scan
  (investigation Q6). The session removes the cursor parameter from the consumer-facing
  surface entirely, so a volume/cursor mismatch is unrepresentable on the happy path and
  an explicit `ArgumentException` on the one remaining per-drive selector.
- Broker death that happens while a consumer is parked reading `Records` is observable
  today only if that consumer had already subscribed to the `BrokerDied` event during the
  scan (investigation Q4). The session exposes a latch (`IsFaulted`/`FaultReason`) plus an
  event whose `add` accessor fires immediately if death already happened, so a consumer
  that attaches after discovery cannot miss it.
- `JournalBrokerClient.DisposeAsync` is not idempotent (investigation Q3, Risk notes): a
  second call re-enters the whole teardown and can throw `ObjectDisposedException` from
  `_demuxCts.CancelAsync()`. The session's single-disposer ownership plus an
  `Interlocked` dispose guard means the client is disposed exactly once regardless of how
  the consumer treats the session, sidestepping that latent bug.

### Prior art consulted

- Microsoft's dispose guidance: a `Dispose`/`DisposeAsync` method must be idempotent -
  callable repeatedly, later calls doing nothing
  (https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose,
  https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync).
  This drives the session's `Interlocked` dispose guard rather than relying on the
  underlying client being safe to double-dispose.
- `SafeHandle`'s `ownsHandle` model
  (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle):
  a wrapper takes sole ownership of a resource and is the only thing that releases it.
  The session is the sole owner and sole disposer of its `JournalBrokerClient`; the client
  is never exposed, mirroring "ownership transfer to a single owning wrapper."
- dotnet/runtime move-semantics / ownership-annotation discussions
  (https://github.com/dotnet/runtime/issues/1163,
  https://github.com/dotnet/runtime/issues/29631): the language has no compiler-enforced
  ownership transfer, so a wrapper that never surfaces the owned object is the practical
  way to guarantee a single disposer. The session follows this by keeping the client
  `private` with no accessor.
- The `await using`/`IAsyncDisposable` consumption pattern
  (https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-iasyncdisposable):
  the session is designed for `await using var session = await JournalBrokerScanSession.StartAsync(...)`
  so the common path is a single scope that owns discovery, watch, and teardown together.

## 2. Design

### 2.1 Public API surface

All types live in namespace `MFTLib` (the existing public broker namespace,
JournalBrokerClient.cs:1). File layout:

- `MFTLib/Broker/Client/JournalBrokerSessionState.cs` - the state enum.
- `MFTLib/Broker/Client/JournalBrokerScanSession.cs` - factory, discovery, fault latch,
  rescan, dispose.
- `MFTLib/Broker/Client/JournalBrokerScanSession.Watch.cs` - watch/stop/per-drive
  consumption (partial, keeps each file well under the 500-line guideline).

```csharp
namespace MFTLib;

/// <summary>
/// Lifecycle state of a <see cref="JournalBrokerScanSession"/>. A session begins
/// <see cref="Parked"/> after its initial scan, moves to <see cref="Watching"/> on
/// <see cref="JournalBrokerScanSession.StartWatchAsync"/> and back on
/// <see cref="JournalBrokerScanSession.StopWatchAsync"/>. <see cref="Faulted"/> latches
/// once the broker dies; <see cref="Disposed"/> latches once the session is disposed.
/// Both terminal states reject every operation except queries and disposal.
/// </summary>
public enum JournalBrokerSessionState
{
    Parked,
    Watching,
    Faulted,
    Disposed,
}
```

```csharp
namespace MFTLib;

/// <summary>
/// An owned scan-to-watch session over one elevated journal broker. Wraps a single
/// connected <see cref="JournalBrokerClient"/> and its most recent
/// <see cref="BrokerScanResult"/> so that discovery and live watching share one elevated
/// process (one UAC prompt) and one pipe. The session is the sole owner and sole disposer
/// of the underlying client; the client is never exposed, which prevents the discovery
/// layer and the watch layer from both disposing it.
/// </summary>
/// <remarks>
/// Typical use is a single <c>await using</c> scope that scans, watches, and disposes:
/// <see cref="StartAsync(System.Func{string,bool},System.Collections.Generic.IReadOnlyList{string},System.Threading.CancellationToken)"/>
/// scans up front, <see cref="LatestScan"/> is the discovery snapshot,
/// <see cref="StartWatchAsync"/> begins live watching from the scan's advanced cursors,
/// and <see cref="WatchDriveAsync"/> yields per-drive batches. Rescan without a second
/// UAC prompt via <see cref="StopWatchAsync"/> then <see cref="RescanAsync()"/>.
/// </remarks>
public sealed partial class JournalBrokerScanSession : IAsyncDisposable
{
    /// <summary>Current lifecycle state. Safe to read at any time, including after fault or disposal.</summary>
    public JournalBrokerSessionState State { get; }

    /// <summary>
    /// The most recent scan result. Set by the initial scan and replaced by each
    /// <see cref="RescanAsync()"/>. Immutable between rescans; exposes per-drive
    /// records, armed and advanced cursors, catch-up entries, and per-drive errors.
    /// </summary>
    public BrokerScanResult LatestScan { get; }

    /// <summary>True once the broker has died. Never reverts.</summary>
    public bool IsFaulted { get; }

    /// <summary>The reason the broker died, or null while <see cref="IsFaulted"/> is false.</summary>
    public string? FaultReason { get; }

    /// <summary>
    /// Raised once when the broker dies. If a handler is added after death already
    /// occurred, it is invoked immediately with <see cref="FaultReason"/>, so a consumer
    /// that attaches after discovery cannot miss a death that happened while parked.
    /// </summary>
    public event Action<string>? Faulted;

    /// <summary>
    /// Spawn one elevated broker (single UAC prompt via <paramref name="launchBroker"/>),
    /// arm and scan <paramref name="drives"/> with <see cref="BrokerScanProfile.Full"/>,
    /// and return a session parked on the result. Throws
    /// <see cref="System.InvalidOperationException"/> if the broker declines to launch or
    /// dies before the scan completes.
    /// </summary>
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="StartAsync(System.Func{string,bool},System.Collections.Generic.IReadOnlyList{string},System.Threading.CancellationToken)"/>
    /// but with an explicit <paramref name="profile"/> and, under
    /// <see cref="BrokerScanProfile.DirectoryIndex"/>, an optional set of non-directory
    /// <paramref name="keepFileNames"/> to keep alongside every directory record.
    /// </summary>
    public static Task<JournalBrokerScanSession> StartAsync(
        Func<string, bool> launchBroker,
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rescan the same drives, profile, and <c>keepFileNames</c> the session was started
    /// with, on the same elevated broker (no second UAC prompt), replacing
    /// <see cref="LatestScan"/>. Legal only in <see cref="JournalBrokerSessionState.Parked"/>;
    /// call <see cref="StopWatchAsync"/> first if watching. Throws
    /// <see cref="System.InvalidOperationException"/> if the broker dies during the rescan.
    /// </summary>
    public Task RescanAsync(CancellationToken cancellationToken = default);

    /// <summary>Rescan a different set of drives (same profile and keepFileNames) on the same broker.</summary>
    public Task RescanAsync(IReadOnlyList<string> drives, CancellationToken cancellationToken = default);

    /// <summary>Rescan a different set of drives with a different profile and keepFileNames on the same broker.</summary>
    public Task RescanAsync(
        IReadOnlyList<string> drives,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin live watching every successfully armed drive in <see cref="LatestScan"/>,
    /// resuming from its advanced cursor. Legal only in
    /// <see cref="JournalBrokerSessionState.Parked"/>. Throws
    /// <see cref="System.InvalidOperationException"/> if no drive armed successfully.
    /// The consumer never supplies cursors, so a volume or cursor mismatch is not
    /// representable.
    /// </summary>
    public Task StartWatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Yield live journal batches for one armed drive, resuming from its advanced cursor.
    /// Legal only in <see cref="JournalBrokerSessionState.Watching"/>. Throws
    /// <see cref="System.ArgumentException"/> if <paramref name="driveLetter"/> was not
    /// successfully armed in <see cref="LatestScan"/>. The enumerable faults with
    /// <see cref="System.InvalidOperationException"/> if that drive's journal is
    /// invalidated mid-watch or the broker dies. One consumer per drive.
    /// </summary>
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDriveAsync(
        string driveLetter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop live watching and return the session to
    /// <see cref="JournalBrokerSessionState.Parked"/>, keeping the elevated process alive
    /// for a subsequent <see cref="RescanAsync()"/> or <see cref="StartWatchAsync"/>.
    /// No-op if already parked. Takes no cancellation token, mirroring
    /// <see cref="JournalBrokerClient.StopLiveWatchAsync"/>, which bounds itself with its
    /// own ack timeout.
    /// </summary>
    public Task StopWatchAsync();

    /// <summary>
    /// Dispose the session: stop any live watch, send the broker <c>Shutdown</c>, close
    /// the pipe, and release memory maps. Idempotent - the underlying client is disposed
    /// exactly once no matter how many times this is called.
    /// </summary>
    public ValueTask DisposeAsync();
}
```

Internal factory seam (for deterministic non-admin tests; `MFTLib.Tests` already has
`InternalsVisibleTo`, MFTLib.csproj:39):

```csharp
// The public StartAsync overloads delegate here with
// connectAsync = ct => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, ct).
// Tests inject a fake client built over an in-memory DuplexStream.
internal static Task<JournalBrokerScanSession> StartAsync(
    Func<CancellationToken, Task<JournalBrokerClient>> connectAsync,
    IReadOnlyList<string> drives,
    BrokerScanProfile profile,
    IReadOnlyCollection<string>? keepFileNames = null,
    CancellationToken cancellationToken = default);
```

Note that `StartWatchAsync`, `WatchDriveAsync`, and the cursor sourcing are fully internal
to the session, so `JournalBrokerClient.SendStartWatchAsync` /
`CreateBatchSource` / `StopLiveWatchAsync` keep their exact current signatures for
low-level callers (constraint preserved).

### 2.2 State machine

States: `Parked`, `Watching`, `Faulted` (terminal for operations), `Disposed` (terminal).

Legal transitions:

| From | Operation | To | Notes |
| --- | --- | --- | --- |
| (factory) | `StartAsync` | `Parked` | throws instead if launch fails or broker dies during initial scan |
| `Parked` | `RescanAsync` | `Parked` | replaces `LatestScan`; throws if broker dies during rescan |
| `Parked` | `StartWatchAsync` | `Watching` | throws if no drive armed successfully |
| `Watching` | `WatchDriveAsync` | `Watching` | per-drive enumerable; `ArgumentException` for unarmed drive |
| `Watching` | `StopWatchAsync` | `Parked` | reclaims the pipe as the scan reader |
| `Parked` | `StopWatchAsync` | `Parked` | no-op |
| `Parked`/`Watching` | broker death | `Faulted` | latched; see 2.4 |
| any | `DisposeAsync` | `Disposed` | idempotent |

What throws:

- `StartWatchAsync` while `Watching`: `InvalidOperationException` (mirrors the client's
  `_watchStartGuard`, LiveWatch.cs:46-47).
- `StartWatchAsync` with an empty armed set: `InvalidOperationException`.
- `RescanAsync` while `Watching`: `InvalidOperationException` ("stop the live watch before
  rescanning") - preserves the documented "call StopLiveWatchAsync before rescanning"
  ordering (broker-integration.md:136-144) as an enforced guard rather than a convention.
- `WatchDriveAsync` for a drive not in `LatestScan.AdvancedCursors`: `ArgumentException`.
- Any operation (except queries and `DisposeAsync`) while `Faulted`:
  `InvalidOperationException` carrying `FaultReason`. Recovery is dispose plus a fresh
  `StartAsync`, matching the existing "create a new client if the user reconnects" guidance
  (broker-integration.md:156-158).
- Any operation (except `DisposeAsync`) while `Disposed`: `ObjectDisposedException`.

What latches: `Faulted` (never reverts, survives across what would otherwise be a
stop/rescan cycle) and `Disposed`.

The state field is guarded by a single lock so transitions and the fault latch cannot
race; this replaces the ad-hoc mutable fields scattered across the client's three partials
(investigation Risk notes) with one central owner.

### 2.3 Ownership and disposal semantics

- The session owns exactly one `JournalBrokerClient`, held in a `private readonly` field
  with no public accessor. Ownership is transferred to the session at construction and
  never handed back, so there is structurally only one disposer (answers plan Q2, Q3).
- `DisposeAsync` uses `Interlocked.Exchange(ref _disposed, 1)`; the first caller sets
  `State = Disposed` and calls `_client.DisposeAsync()` once, later callers return a
  completed `ValueTask` without touching the client. This guarantees the client's
  non-idempotent `DisposeAsync` (investigation Q3) runs exactly once.
- Dispose while `Watching`: `_client.DisposeAsync()` already cancels the demux CTS and
  awaits the demux task before closing the pipe (Transport.cs:30-36); the session simply
  delegates.
- Dispose while `Parked`: `_client.DisposeAsync()` sends `Shutdown` and closes the pipe
  and MMFs (the parked broker is shut down - answers "disposal before live start shuts
  down the parked broker").
- Dispose after `Faulted`: `_client.DisposeAsync()`'s best-effort `Shutdown` write may
  throw on the dead pipe and is swallowed by the client (Transport.cs:18-26); the session
  still transitions to `Disposed`.
- Double dispose: second call is a no-op (see above); no second `Shutdown` frame reaches
  the wire, which is the deterministic assertion for the "disposal is exactly once" test.

### 2.4 Broker-death latching (answers plan Q4)

- The session subscribes to `_client.BrokerDied` in the factory. The handler takes the
  state lock, sets `IsFaulted = true` and `FaultReason = reason`, transitions `State` to
  `Faulted` (unless already `Disposed`), captures the current `Faulted` handler list, and
  invokes them outside the lock.
- The `Faulted` event uses explicit `add`/`remove` accessors: on `add`, under the lock,
  it appends the handler and, if `IsFaulted` is already true, invokes the handler
  immediately with `FaultReason`. This closes the "late subscriber misses the event" gap
  the investigation identified for a consumer that attaches only after discovery.
- Death detected during a scan: `ArmScanAndCatchUpAsync` returns an incomplete result and
  fires `BrokerDied` on the same EOF (investigation Q4). `StartAsync` and `RescanAsync`
  therefore check `IsFaulted` immediately after the scan returns and throw
  `InvalidOperationException(FaultReason)` rather than surfacing a half-populated
  `LatestScan`.
- Death during live watch surfaces through the existing per-drive channel completion
  (LiveWatch.cs latch), so `WatchDriveAsync` enumerables throw; the session's latch and
  `State = Faulted` are set by the same `BrokerDied` handler.

### 2.5 Journal wrap and volume mismatch (answers plan Q5, Q6)

Volume mismatch is designed out of the consumer surface: `StartWatchAsync` sources cursors
from `LatestScan.AdvancedCursors` (only successfully armed drives), and `WatchDriveAsync`
validates its `driveLetter` against that same set, throwing `ArgumentException` otherwise.
A caller cannot pass a cursor for an unarmed or failed drive because the caller passes no
cursor at all.

Journal wrap needs a client-level fix first. Today the host turns a wrapped or recreated
journal during live watch into a per-drive `Error` frame (JournalBrokerHost.cs:95-100),
but the client's `DemuxLoopAsync` drops `Error` frames on the floor (LiveWatch.cs:185), so
the affected drive's batch source neither completes nor throws - a silent stall
(investigation Q5). Task 1 fixes the demux to route a live-watch `Error` frame to that
drive's channel as a faulting completion, so `WatchDriveAsync` throws
`InvalidOperationException(message)` for the wrapped drive while other drives keep
streaming. Wrap during the parked interval (before `StartWatchAsync`) surfaces the same
way: the stale cursor is sent, the host's native watch call throws, and the resulting
`Error` frame faults that drive's stream at watch start. The session does not attempt to
detect parked-interval wrap eagerly (that would require a pipe reader during the parked
window - see 2.6).

### 2.6 Cancellation ownership of the parked period (answers plan Q8)

The session holds no ambient cancellation token or background reader during the parked
interval. This is deliberate: the single-pipe-reader invariant (investigation Risk notes)
means a background watchdog reading frames while parked would race the next scan or watch
reader and corrupt frames. Instead:

- Each operation (`RescanAsync`, `StartWatchAsync`, `DisposeAsync`) owns its own
  `CancellationToken`. `StopWatchAsync` takes none, matching
  `StopLiveWatchAsync`'s self-timeout (investigation cancellation note).
- Broker death during the parked window is discovered lazily on the next pipe operation
  and then latched into `IsFaulted` (2.4). A consumer that wants to know sooner subscribes
  to `Faulted`; a consumer that comes back later reads `IsFaulted`.
- "Cancellation during the parked state" means: a token already cancelled when the next
  operation is invoked yields a clean `OperationCanceledException` and leaves the session
  in `Parked`, still disposable. That is the deterministic parked-cancellation test.

### 2.7 Stop, rescan, restart mapping (answers plan Q7)

`StopWatchAsync` delegates to `_client.StopLiveWatchAsync()`, which tears down only the
watch generation and resets the client's live-watch state so the pipe returns to scan mode
with the broker (and its elevation) alive (LiveWatch.cs:74-78, JournalBrokerHost.Session.cs:30-39).
`RescanAsync` then delegates to `_client.ArmScanAndCatchUpAsync(...)` and replaces
`LatestScan`; `StartWatchAsync` starts a fresh watch from the new advanced cursors. The
session enforces the `Parked`-only precondition on `RescanAsync` and `StartWatchAsync`, so
the "call stop before rescan" rule becomes a guard instead of a convention, while never
re-spawning the broker (no second UAC prompt - constraint preserved).

## 3. Alternatives considered

- **Documentation only (rewrite the split example into one continuous snippet, add
  "do not dispose the client between scan and watch" to the XML docs).** Rejected: the
  ownership mistake is a type-system affordance (a detached `BrokerScanResult` plus a
  freely disposable client), and docs do not remove the affordance. The plan explicitly
  permits this conclusion only if the existing API prevents or clearly flags the mistake;
  it does neither (investigation Q1, Q2). We still do the doc rewrite, but as part of
  shipping the session, not instead of it (Task 5).
- **Return a session struct/record from `ArmScanAndCatchUpAsync` without owning the
  client** (a value that pairs `BrokerScanResult` with a back-reference to the client).
  Rejected: it still lets the consumer dispose the client directly, and it changes the
  return type of an existing public method, violating source compatibility.
- **Extend `JournalBrokerClient` in place** with an ownership flag and a start-guard on
  `ArmScanAndCatchUpAsync`. Rejected: it grows the already three-partial client, cannot
  remove the misuse-prone `SendStartWatchAsync(cursors)` overload without a breaking
  change, and offers no place to host the fault latch as queryable state without adding
  public surface to the primitive. A separate owned type keeps the primitive intact and
  concentrates the safety in one opt-in abstraction.
- **`IJournalBrokerClient` interface + mockable client for tests.** Rejected as
  unnecessary scope: the existing suite already tests a real `JournalBrokerClient` over an
  in-memory `DuplexStream` with faked MMF seams, and the session's guarantees
  (single Shutdown frame, single arm frame, cursor sourcing) are all observable on the
  wire. Kept in reserve only if a wire assertion proves flaky.

## 4. Implementation plan

Dependency order. Each task is sized for one fresh mid-tier implementer, writes its tests
first (TDD), and must leave the repo at 100% managed line/branch/method coverage and
aislop score 100 before handing off. New `.cs` files arrive LF-terminated from agent Write
tools but this repo enforces CRLF for `.cs` (format gate fails otherwise) - convert every
new `.cs` file (for example `sed -i 's/$/\r/' <file>` or unix2dos) before running the gate.

### Task 1 - Route live-watch Error frames to the per-drive channel

Foundation for journal-invalidation behavior; independent of the session.

- Files: `MFTLib/Broker/Client/JournalBrokerClient.LiveWatch.cs` (modify
  `DemuxLoopAsync`, add a `FaultLiveChannel(string drive, Exception error)` helper beside
  `GetOrAddLiveChannel`). Read `BrokerProtocol.WriteError` / the `Error` frame reader to
  learn the frame's drive-letter and message fields before wiring.
- Behavior: when `DemuxLoopAsync` reads a `BrokerFrameKind.Error` frame during live watch,
  parse its drive letter and message and complete that drive's channel with
  `new InvalidOperationException(message)` (leave `Heartbeat` still ignored). Other drives
  keep streaming. Do not change any public signature.
- Tests first (in `MFTLib.Tests/JournalBrokerClientTests.cs` or a focused
  `BrokerLiveWatchErrorTests.cs`), driven by a host/DuplexStream emitting an `Error` frame
  mid-watch:
  - `LiveWatch_ErrorFrameForDrive_FaultsThatDrivesBatchSource` - the affected drive's
    enumerable throws `InvalidOperationException` with the host message.
  - `LiveWatch_ErrorFrameForOneDrive_OtherDrivesKeepStreaming` - a second drive still
    yields a subsequent batch.
  - `LiveWatch_ErrorFrameBeforeSubscribe_LateSubscriberGetsFault` - subscribing after the
    error still surfaces it (latched channel completion), mirroring the existing
    death-before-subscribe test.
- Coverage seam: none new; reuse the existing `DuplexStream`/host harness.

### Task 2 - Session skeleton: state, factory, discovery, fault latch, dispose

- Files: create `MFTLib/Broker/Client/JournalBrokerSessionState.cs` (enum) and
  `MFTLib/Broker/Client/JournalBrokerScanSession.cs` (factory, `State`, `LatestScan`,
  `IsFaulted`, `FaultReason`, `Faulted` event, `DisposeAsync`; no watch/rescan yet).
- Signatures: the enum; `State`, `LatestScan`, `IsFaulted`, `FaultReason`, `Faulted`; both
  public `StartAsync` overloads (the profile-taking overload also carries an optional
  `keepFileNames`); the internal `StartAsync(connectAsync, drives, profile, keepFileNames,
  ct)` seam; `DisposeAsync`. Store `_drives`, `_profile`, and `_keepFileNames` for later
  rescan.
- Factory: public overloads delegate to the internal seam with
  `connectAsync = ct => JournalBrokerClient.SpawnAndConnectAsync(launchBroker, ct)` (the
  no-profile overload passes `BrokerScanProfile.Full` and no `keepFileNames`). The internal
  seam connects, wires the `BrokerDied` handler, calls
  `ArmScanAndCatchUpAsync(drives, profile, keepFileNames, ct)`, and - if `IsFaulted` -
  disposes and throws `InvalidOperationException(FaultReason)`; otherwise stores
  `LatestScan` and sets `State = Parked`.
- Tests first (new `MFTLib.Tests/JournalBrokerScanSessionTests.cs`), using a fake client
  built with the existing `MakeFakeClient`/`DuplexStream`/`NullMmfReader` helpers, passed
  through the internal `StartAsync` seam:
  - `StartAsync_Scans_ParksWithLatestScanResult` - `State == Parked`, `LatestScan` carries
    records, armed/advanced cursors, catch-up entries, and per-drive errors (covers "cursors,
    catch-up entries, and per-drive errors survive the handoff").
  - `StartAsync_ConnectsExactlyOnce` - the `connectAsync` seam is invoked once.
  - `StartAsync_BrokerDiesDuringInitialScan_Throws` - EOF during scan yields
    `InvalidOperationException`, and the session is disposed (no leaked client).
  - `StartAsync_Cancelled_Throws` - a cancelled token during the scan surfaces
    `OperationCanceledException`.
  - `Dispose_WhileParked_SendsSingleShutdownFrame` - one `Shutdown` frame on the server side
    (covers "disposal before live start shuts down the parked broker").
  - `Dispose_CalledTwice_DisposesClientOnce` - exactly one `Shutdown` frame across two
    `DisposeAsync` calls (covers "disposal is exactly once").
  - `Operation_AfterDispose_ThrowsObjectDisposed` - a query/operation after dispose throws
    `ObjectDisposedException`.
  - `BrokerDeath_LatchesIsFaultedAndFaultReason` - after a mid-scan or explicit death,
    `IsFaulted` is true and `FaultReason` is set.
  - `Faulted_LateSubscriber_FiresImmediately` - adding a `Faulted` handler after death
    invokes it at once with the reason (covers "broker death between scan and watch is
    observable to a later consumer").
  - `PublicStartAsync_InProcessBroker_EndToEnd` - drives the public `launchBroker` overload
    with an in-process `Task.Run` broker (the `SpawnAndConnectAsync_EndToEnd` template),
    asserting `Parked` with records, so both public overloads and the `SpawnAndConnectAsync`
    delegation are covered without admin.
- Coverage seams: the internal `connectAsync` `Func` (client injection); the fault handler
  and event `add` accessor are exercised by the death and late-subscriber tests.

### Task 3 - Watch surface: StartWatch, WatchDrive, StopWatch

- Files: create `MFTLib/Broker/Client/JournalBrokerScanSession.Watch.cs` (partial). Touch
  `JournalBrokerScanSession.cs` only for shared state (the state lock, cached batch-source
  delegate).
- Signatures: `StartWatchAsync(ct)`, `WatchDriveAsync(driveLetter, ct)`, `StopWatchAsync()`.
  `StartWatchAsync` validates `Parked` and a non-empty `LatestScan.AdvancedCursors`
  (else `InvalidOperationException`), calls `_client.SendStartWatchAsync(LatestScan.AdvancedCursors, ct)`,
  caches `_client.CreateBatchSource()`, and sets `State = Watching`. `WatchDriveAsync`
  validates `Watching` and drive membership in `AdvancedCursors` (else `ArgumentException`),
  and returns the cached batch source seeded with `AdvancedCursors[driveLetter]`.
  `StopWatchAsync` delegates to `_client.StopLiveWatchAsync()` and returns to `Parked`
  (no-op if already `Parked`).
- Tests first (append to `JournalBrokerScanSessionTests.cs`):
  - `StartWatch_UsesSameClientAsScan_NoSecondArmOrSpawn` - the fake client receives a
    `StartWatch` frame and no additional `ArmAndScan` frame, and `connectAsync` was not
    called again (covers "scan result and live start use the same underlying
    client/transport" and "no second spawn or arm occurs during the transition").
  - `StartWatch_WhenAlreadyWatching_Throws` - second `StartWatchAsync` throws
    `InvalidOperationException`.
  - `StartWatch_NoDriveArmed_Throws` - empty advanced cursors yields
    `InvalidOperationException` (a volume-mismatch explicit behavior).
  - `WatchDrive_HappyPath_YieldsBatchesFromAdvancedCursor` - batches flow for an armed
    drive from its advanced cursor.
  - `WatchDrive_UnarmedDrive_ThrowsArgumentException` - a drive absent from
    `AdvancedCursors` throws (volume-mismatch explicit behavior).
  - `WatchDrive_JournalInvalidatedMidWatch_ThrowsInvalidOperation` - relies on Task 1: an
    `Error` frame for the drive faults its enumerable (journal-invalidation explicit
    behavior).
  - `StartWatch_Cancelled_Throws` and `WatchDrive_Cancelled_StopsCleanly` - cancellation
    during live start and during active watch is safe and leaves the session usable/disposable.
  - `Dispose_WhileWatching_TearsDownDemux` - dispose during an active watch completes and
    leaves `State == Disposed`.
- Coverage seam: reuse the existing `CancelAfterReadsStream` for the mid-watch cancellation
  test.

### Task 4 - Rescan and restart

- Files: `MFTLib/Broker/Client/JournalBrokerScanSession.cs` (add the three `RescanAsync`
  overloads and the `Parked`-only guard).
- Signatures: `RescanAsync(ct)`, `RescanAsync(drives, ct)`, `RescanAsync(drives, profile,
  keepFileNames, ct)`. The no-argument overload reuses stored `_drives`/`_profile`/
  `_keepFileNames`; the others update `_drives`/`_profile` (and `_keepFileNames` where
  supplied). Each validates `Parked` (else `InvalidOperationException` guiding to
  `StopWatchAsync`), calls `_client.ArmScanAndCatchUpAsync(_drives, _profile,
  _keepFileNames, ct)`, replaces `LatestScan`, and throws
  `InvalidOperationException(FaultReason)` if the broker died during the rescan.
- Tests first (append):
  - `StopThenRescanThenStartWatch_ReusesOneBroker` - full stop/rescan/restart on one fake
    client with no re-`connectAsync` and no new `SpawnAndConnect` (covers "stop, rescan, and
    restart still work on one broker").
  - `Rescan_WhileWatching_Throws` - `InvalidOperationException` before a stop.
  - `Rescan_NoArgs_ReusesInitialDrivesAndProfile` - the arm frame repeats the original
    drives and profile token.
  - `Rescan_BrokerDiesDuringScan_Throws` - EOF during rescan yields
    `InvalidOperationException` and latches `IsFaulted`.
  - `Rescan_Cancelled_Throws` - a cancelled token during rescan surfaces
    `OperationCanceledException` and leaves the session `Parked`.
- Coverage seam: none new.

### Task 5 - Documentation, changelog, and gate verification

- Files: `docs/broker-integration.md`, XML docs on the new type (already specified in 2.1),
  `CHANGELOG.md`, and a coverage/aislop run.
- `docs/broker-integration.md`: replace the split sections 2-4 (spawn / scan / watch) with
  one continuous example built on `JournalBrokerScanSession` -
  `await using var session = await JournalBrokerScanSession.StartAsync(BrokerLauncher.Launch, new[] { "C", "D" }, ct);`
  through `LatestScan` discovery, `StartWatchAsync`, one `WatchDriveAsync` consumer per
  drive, `StopWatchAsync` + `RescanAsync`, and `IsFaulted`/`Faulted` handling - so no reader
  can copy a fragment that drops the owning object. Keep a short "low-level primitive"
  pointer to `JournalBrokerClient` for elevated-only callers. Update the deployment
  checklist ("keep one session per elevated session", "dispose the session on shutdown").
- `CHANGELOG.md`: add an entry for `JournalBrokerScanSession` and the live-watch
  `Error`-frame routing fix. 0.3.0 is not yet published (tag and NuGet are user-held), so
  the implementer folds these into the existing `## 0.3.0` block or opens a new heading -
  flag the choice to the reviewer rather than deciding the version unilaterally.
- Verify: run `scripts/run-coverage.ps1` (or `-NonInteractive`) to confirm 100% managed
  line/branch/method, and `aislop scan .` (then `aislop ci .`) for score 100. Address every
  finding; do not suppress rules.
- Also confirm existing `JournalBrokerClient` tests still pass unchanged (covers "existing
  client APIs remain source-compatible").

Plan test-list coverage check: same underlying client (Task 3), no second spawn/arm
(Task 3), cursors/catch-up/errors survive handoff (Task 2), single-use ownership + dispose
once (Task 2), dispose before live start shuts down parked broker (Task 2), cancellation in
scan/parked/live-start/active-watch (Tasks 2/3/4), death between scan and watch observable
to a later consumer (Task 2), volume mismatch and journal invalidation explicit (Tasks 1/3),
stop/rescan/restart on one broker (Task 4), existing client APIs source-compatible (Task 5).

## 5. Global constraints block (verbatim-quotable for implementer and reviewer prompts)

- Preserve every existing `JournalBrokerClient` public API and default, including the
  `BrokerScanProfile.Full` default overload behavior. The session is additive.
- Source the watch cursors inside the session from `LatestScan.AdvancedCursors`; never add
  a consumer-facing cursor parameter.
- Preserve per-drive errors: one drive failing to arm or wrap must not fail the others.
- Preserve arm-before-scan and catch-up ordering; never start the demux reader before the
  scan collect loop has drained (single pipe reader at all times).
- No second UAC prompt on any scan-to-watch, stop, rescan, or restart transition; never
  re-spawn the broker within a session.
- No GitWizard concepts (repository roots, UI state, `IVolumeChangeSource`) and no
  consumer-specific callback interface; the session stays a generic MFT/USN primitive.
- `DisposeAsync` is idempotent; the owned `JournalBrokerClient` is disposed exactly once
  and is never exposed publicly.
- 100% managed line, branch, and method coverage and aislop score 100 are hard gates. Use
  `Func` indirection (the internal `connectAsync` seam) for deterministic non-admin tests;
  do not suppress rules or add `[ExcludeFromCodeCoverage]` to real code.
- Every test is deterministic and non-admin (fake client over `DuplexStream`, in-process
  broker for the one end-to-end path); no test may require elevation.
- Style: terse, no unnecessary comments, full-word identifiers (no abbreviations), unsigned
  types for sizes, no em-dashes anywhere (use " - " or parentheses).
- New `.cs` files must be converted to CRLF before the format/aislop gate.
