### Plan Compliance

- Issues found: the committed fixes meet the test-defect scope and preserve the assertions, but the report calls sleep-based scratch mutations deterministic proof. Neither mutation forces the losing interleaving under arbitrary load (`task-FX5-report.md:72`, `:144`, `:231`). See Important I1.
- Cannot verify from diff: the remote Linux run counts, diagnostics, failing TRX files, and scratch RED/GREEN executions. The controller should inspect the retained Linux artifacts and the actual scratch build/run output identified in `task-FX5-report.md:15`, `:74`, `:147`, `:196`, and `:229`. I did not run tests, Git commands, remote commands, or mutate code.
- Existing local Windows artifacts support the reported suite totals (`.superpowers/run-coverage.log:2024`, `:2025`, `:2026`, `:2027`, `:2239`) and exactly the five accepted aislop warnings (`.superpowers/aislop.log:14`, `:15`, `:19`, `:24`, `:25`, `:30`). The scan's `EXIT=1` at `.superpowers/aislop.log:45` is consistent with its score of 99; the amended warning-set gate is satisfied. W2-2 is not a finding.

### Strengths

- The connection gate fixes the observation at its cause: the connector remains pending until the connect timer is created (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:64`, `MFTLib.Tests/JournalBrokerHostChannelTests.cs:302`).
- The cancellation fallback checks the real token and cannot turn normal completion into a cancellation observation (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:206`).
- The change is confined to four test files. It introduces no production seam, retry, widened timeout, coverage exclusion, or compatibility path (`review-8dd64bf..b5c4557.diff:6`).

### Answers to the seven questions

#### 1. CAUSE 1

The cause is right. Production starts the connector on a worker at `MFTLib/Broker/Host/JournalBrokerHost.Channel.cs:50` and only subsequently calls its timed `WaitAsync` at `:53`. An immediate connector can complete the connection task between those statements. The .NET 10 generic implementation returns the original task when `IsCompleted` is true, before constructing its timeout promise (`Future.cs:544`, `:558`, [official .NET 10 source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs#L542)). Thus a completed connection need not create a timer.

The first-request read then has its own 30 second wait at `MFTLib/Broker/Host/JournalBrokerHost.Channel.cs:168`. Both constants are 30 seconds (`MFTLib/Broker/Host/JournalBrokerHost.cs:18`, `:21`). In the base diff, both tests awaited occurrence 2 without holding the connector. If occurrence 1 was skipped, the first-request timer was occurrence 1 and the occurrence-2 signal never arrived. This explains a permanent missing observation, rather than a slow correct observation (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:289`; base deletions in the two timeout-test hunks).

#### 2. FIX 1

The ordinal is guaranteed for these two tests. Each uses a fresh clock, opens one channel, and supplies an initially incomplete `connectionsReleased` task (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:155`, `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:104`). The default connector waits on that task before returning a stream (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:64`). The helper waits for the connect timer's creation before releasing the connector (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:301`, `:302`, `:303`). No scheduler interleaving can complete the connection before that signal.

The first request cannot start until the connection succeeds and its channel task is launched (`MFTLib/Broker/Host/JournalBrokerHost.Channel.cs:23`, `:40`). With no request supplied, its read remains pending. Therefore the connect timer is the first 30 second creation and the first-request timer is the second. The clock records creations under a lock and retains their signals (`MFTLib.Tests/TestSupport/TimerSignalingClock.cs:38`, `:45`, `:51`). Waiting for due time alone cannot distinguish these two timers: the default occurrence-1 signal remains completed after the connect timer is disposed. Using it for the first request could advance fake time before the request timer exists. The forced ordinal is sound; I found no remaining wrong-selection interleaving in these tests.

Strictly, the helper advances the whole clock, not a selected timer (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:290`). Other due timers can fire too. Even if completion cleanup momentarily leaves the connect timer alive, its connection wait has already completed before the first-request timer can exist, so firing it cannot substitute for the required first-request timeout.

#### 3. CAUSE 2

The cause is right. The helper registers its cancellation observer before entering a cancellable channel read (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:187`, `:197`). The reader's later callback can wake the iterator on another thread, which exits and disposes the earlier registration before that callback is invoked. .NET executes callbacks in reverse registration order and permits removal of callbacks still awaiting execution (`CancellationTokenSource.cs:679`, `:699`, [official .NET 10 source](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/CancellationTokenSource.cs#L678)).

The fix covers the cooperative iterator's exit paths with a `finally` before disposal of the outer registration (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:202`, `:206`, `:208`). If cancellation wakes the read, the token is already cancelled, and the fallback completes the observation even if the callback loses the race. A pre-cancelled token is also observed. If the callback runs first, `TrySetResult` is idempotent. Exceptions or normal disposal with an uncancelled token do not report cancellation. A normal end alone cannot satisfy this test.

The stubborn branch retains its live registration while waiting for release (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:189`, `:191`). In the relevant tests, release happens only in cleanup after the cancellation observations (`:120`, `:121`, `:130`, `:154`, `:161`). Thus it cannot remove its observer before the cancellation being asserted. As with any registration, cancellation after the iterator has already ended and disposed is outside that observer's lifetime; the fix does not claim to observe future cancellation after normal disposal.

#### 4. ASSERTION STRENGTH

No changed assertion is weaker. There are no assertion edits in the diff.

- `NoFirstRequest` still requires an Error, request id zero, and closed channel (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:162`, `:163`, `:164`). Its added setup controls connection timing; it does not send a first request.
- `FirstRequestReadEndsNormallyAfterTimeout` still requires exactly one Error with the no-request message (`MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:117`, `:118`, `:119`). It still waits for actual read-token cancellation before releasing the uncancellable read (`:112`, `:113`).
- `AdvanceWhenTimerExistsAsync` is unchanged: it waits for creation, advances fake time, and joins the expected outcome (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:287`). The new opening helper retains `OpenChannelAsync`'s ChannelOpened and request-id checks from the harness diff and adds bounded joins (`:301`, `:304`).
- `ConnectInMemoryAsync` still returns the same wrapped or unwrapped host stream. Its optional gate controls when it returns; with no gate its asynchronous method completes directly (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:62`, `:69`, `:70`).
- `ScriptedWatch` still reports only real cancellation. The fallback does not satisfy the assertions merely because the source ended (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:206`). The unaffected-channel negative assertion also remains (`:99`).

#### 5. SWEEP

The requested checks outside the diff evaluated three named risks: skipped or reordered timer creations, repeated timer creations shifting ordinals, and cancellation observers removed by their own cancellable reads. Changed-file excerpts outside the provided hunks were limited to the call sites and helpers explicitly requested for this sweep.

| Ordinal call site | Ordering conclusion |
| --- | --- |
| `MFTLib.Tests/JournalBrokerHostChannelTests.cs:136` | Forced pending connection, then timer-creation signal. The connector returns the never-completed task shown in the diff at `:131`; the timer cannot be skipped. Occurrence 1 is the connect bound. |
| `MFTLib.Tests/JournalBrokerHostChannelTests.cs:160` | Forced by the new connection gate and connect-timer signal at `:302`, followed by the occurrence-2 signal in `AdvanceWhenTimerExistsAsync`. |
| `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:112` | Same explicit connection gate through `OpenChannelAfterConnectBoundAsync`; the request read is held until its cancellation signal. |
| `MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:125` | Guaranteed synchronous heartbeat creation first, then drain creation. Cooperative/stubborn entry and cancellation signals order the test. This is a production call-order invariant, not a scheduler assumption. |
| `MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:157` | Same heartbeat/drain invariant, plus entry, write-failure, and cancellation signals at `:149`, `:153`, `:154`. |

For the last two, `ControlSession` constructs the heartbeat sender before serving requests (`MFTLib/Broker/Host/JournalBrokerHost.Session.cs:34`, `:174`); its constructor creates one periodic 5 second timer (`MFTLib/Broker/Host/BrokerHeartbeatSender.cs:31`). Drain creates a separate 5 second delay after session termination (`MFTLib/Broker/Host/JournalBrokerHost.Session.cs:51`, `:52`, `:129`). The periodic timer does not create another timer per tick.

Other ordinal users are also ordered:

- `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:160` and `MFTLib.Tests/BrokerProcessTests.Disposal.cs:51` use the same synchronous heartbeat-before-drain invariant and wait for the drain timer before proceeding.
- `MFTLib.Tests/BrokerProcessTests.Timeouts.cs:46` waits for the initial stall timer before starting its gated write (`:42`, `:44`, `:45`). The write timeout is always created before writing (`MFTLib/Broker/Client/BrokerProcess.Control.cs:202`).
- `MFTLib.Tests/BrokerProcessTests.Timeouts.cs:74` observes the reply timer before acknowledging the channel; `:76` observes the connection timer while the broker deliberately never connects (`:75`). The client orders request write, reply wait, and connection wait (`MFTLib/Broker/Client/BrokerProcess.Control.cs:96`, `:105`, `MFTLib/Broker/Client/BrokerProcess.Channels.cs:25`, `:26`).
- `MFTLib.Tests/BrokerProcessLivenessTests.cs:190` withholds the slow reply until after its timeout, having observed initial read/stall readiness (`:183`, `:184`, `:208`). Its reply timer cannot be skipped.
- The nonordinal clock users at `MFTLib.Tests/BrokerProcessLivenessTests.cs:36` and `:229` observe the sole initial stall timer before advancing. The reader creates its timer once and re-arms that same timer (`MFTLib/Broker/Client/BrokerFrameReader.cs:109`, `:179`), so later reads do not shift occurrence counts.

Cancellation sweep: no remaining instance of the same race was found. `ScriptedWatchBrokerHarness` already has the conditional fallback around its full iterator (`MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs:258`, `:282`, `:286`, `:288`). The observer in `MFTLib.Tests/JournalBrokerHostTests.Watch.cs:20` is never disposed early. `UncancellableReadStream` cannot finish until the test releases it after observing cancellation (`MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:112`, `:113`, `:194`, `:195`). `ScriptedDriveWatch` has no separate cancellation-registration observer to lose (`MFTLib.Tests/TestSupport/ScriptedDriveWatch.cs:85`, `:95`).

The remaining registration matches are test-scope timeout cleanup or deliberate callback tests, not iterator-scope observers removed by their own reads: `MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs:116`, `:178`; `MFTLib.Tests/JournalBrokerHostRealSeamsTests.Operations.cs:416`; `MFTLib.Tests/Index/FileIndexBlockReleaseTests.cs:310`, `:343`; `MFTLib.Tests/ParseThreadAllocatorTests.cs:37`.

#### 6. LOAD

Every new committed ordering wait is signal-based. The connector waits for explicit release (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:66`); the helper waits for timer creation and actual channel opening (`MFTLib.Tests/JournalBrokerHostChannelTests.cs:302`, `:304`). The changed tests use fake time to trigger the production deadline and retain signal/outcome joins (`:160`, `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs:112`). The cancellation change adds no timing wait (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:202`).

Real time remains only in the existing 10 second hang guard (`MFTLib.Tests/TestSupport/HostChannelHarness.cs:18`). As any finite guard can expire if the scheduler makes no progress for its entire duration, these tests cannot promise success under unbounded starvation. They do not, however, rely on work completing within a real-time interval to establish the behavior under test, and no changed test widens a guard or adds a retry. The two reported missing-signal races are removed structurally. The scratch evidence, separately, still relies on a real interval being sufficient (question 7).

#### 7. EVIDENCE

The reported 0/210 fixed class runs and 0/20 whole runs are useful supporting observations (`task-FX5-report.md:196`, `:229`, `:230`), not construction proofs. The base comparison combines unmodified and diagnostic class runs (`:231`), so it should also retain that qualification.

Neither scratch mutation guarantees the losing order:

- Sleeping the host for 200 ms after `Task.Run` does not guarantee that the connector worker finishes before the host resumes, particularly on the mandated saturated host (`task-FX5-report.md:72`). Without a connection-completion observation, a reported failure could also be a hang-guard scheduling failure rather than proof that the connect timer was skipped. The first test's displayed stack is its disposal timeout, which masks its original observation (`:80`).
- Sleeping a middle cancellation callback for 200 ms does not guarantee that the reader continuation disposes the earlier registration before that callback resumes (`task-FX5-report.md:144`). Entry is reported before the read is installed (`MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs:188`, `:197`), so the mutation also needs to prove the reader is waiting when cancellation starts. The report itself says one sibling passed with this mutation (`task-FX5-report.md:159`).

The report supplies literal commands and failure excerpts (`task-FX5-report.md:74`, `:78`, `:147`, `:151`), but the cancellation excerpt omits the exception message, and the underlying scratch transcripts are not in the diff. I can verify that both described mutations enlarge the relevant windows. I cannot certify their claim to force the interleavings by construction. The committed fixes have an independent structural argument, detailed above; that does not make the scratch experiments deterministic.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

**I1. Replace sleep-based scratch reproducers with signal-controlled RED/GREEN evidence.**

Locations: `task-FX5-report.md:72`, `:144`, `:231`; requirements at `global-constraints.md:65`, `:68` and `lanes/FX5-dispatch.md:52`, plus the owner's load requirement and question 7.

The report claims deterministic proof, but both mutations allow the winning order if the other worker does not run within 200 ms. Their reported failures demonstrate observations from particular schedules; they do not force the required schedules or distinguish every displayed timeout from scheduler starvation. This leaves the requested construction-based validation incomplete, despite the apparently sound committed fixes.

Use scratch gates that establish the precise preconditions and hold the interfering actor until the losing event has occurred. For the connection case, record and control completed-versus-held connector state at the timed-wait boundary, demonstrating that the base skips its timer while the gated fix necessarily creates it. For cancellation, establish a pending read, then hold the middle callback until the earlier registration's disposal is observed. Preserve the actual literal build/test commands and full failing exception/output for each regression, followed by GREEN on the corresponding fixed variant. Update the report to distinguish observed stress results from forced-interleaving proof. Do not increase sleeps, hang guards, or retries. This finding concerns evidence, not a discovered residual defect in the committed synchronization.

#### Minor (Nice to Have)

None.

### Assessment

Task quality: Needs fixes

Reasoning: The test-only changes address both causes, preserve assertion strength, and leave no identified equivalent race in the requested sweep. The claimed deterministic scratch proof remains schedule-dependent and must be replaced with signal-controlled evidence before this task gate is trusted.
