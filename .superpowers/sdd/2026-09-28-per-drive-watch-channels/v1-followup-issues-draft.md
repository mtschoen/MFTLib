# Drafts of follow-up issues for the per-drive watch channels work

Branch impl/265-per-drive-channels at 66bdb6d. Ledger paths are relative to
.superpowers/sdd/2026-09-28-per-drive-watch-channels/. Nothing here has been filed.

Not drafted because already fixed on the branch (checked against the code): the Frames.cs huge
length prefix (BrokerFrameStream.cs:41-45 now throws InvalidDataException), the
WaitForCatchUpAsync-while-Recovering fault (FileIndex.WatchCatchUp.cs:17-19 and :58), the
AGENTS.md names of removed fields, the queued-diagnostics-lost-at-exit item (flush at
DefaultElevatedEntryRunner.cs:100), and the negative-bound guard test.

# Issue: a failed rescan followed by disposal after the restart decision throws AggregateException instead of cancelling

## Problem

`FileIndex.RescanAsync` promises that disposal cancels an admitted operation with
`OperationCanceledException`. One path breaks that. When a rescan's scan fails and the drive's
watch was healthy, `ResumeAfterFailedScanAsync` (MFTLib/Index/FileIndex.RescanRestart.cs:59)
decides to restart the watch. The restart enters `StartWatchingWithGateHeldAsync`, whose first
line calls `ThrowIfCancelledByDisposal` (MFTLib/Index/FileIndex.WatchDrive.cs:144). If disposal
began after the restart decision, that throws `OperationCanceledException`. The catch at
FileIndex.RescanRestart.cs:68-72 wraps it, together with the scan failure, in an
`AggregateException`. The caller sees an `AggregateException` holding the scan failure and the
disposal cancellation, not a cancellation.

The failed-scan caller passes `scanPublished: false` on purpose, so a disposal before the decision
only skips the restart. Only a disposal that lands after the decision reaches the wrapped path.

## Evidence

- task-FX4-report.md:314-315 (Concerns): "Pre-existing, not changed: on the failed-scan path, a
  disposal that begins after the restart decision (checkpoints C4/C5) makes
  ResumeAfterFailedScanAsync wrap the scan failure and the OCE in an AggregateException."
- task-FX4-rereview-r1.md, "Concern 2: separate follow-up": classified as pre-existing and outside
  the FX4 scope; the fix diff supplies `false` on this path and leaves the aggregation alone.
- Present at 66bdb6d; no test pins either outcome.

## Acceptance

- A test disposes the index after the restart decision of a rescan whose scan failed, and asserts
  the outcome the admitted-operation contract requires. It fails before the change.
- The `RescanAsync` remarks say which exception a caller sees in this case.
- Decision for the owner: when both a scan failure and a disposal are true, does the caller get
  the scan failure, the cancellation, or an aggregate carrying both?

# Issue: BrokerDiagnostics.Log can drop a line when the test writer is replaced between acquiring the writer and enqueueing

## Problem

`BrokerDiagnostics.Log` reads the static `Writer` property (MFTLib/Broker/BrokerDiagnostics.cs:137-138)
and then calls `TryEnqueue` on it (BrokerDiagnostics.cs:165). `ReplaceWriterForTest`
(BrokerDiagnostics.cs:143-146) exchanges the field and completes the previous writer at line 145.
A `Log` call that acquired the old writer before the exchange enqueues onto a completed writer, so
`TryEnqueue` returns false and the line is lost. There is no retry and no test for the
acquire-then-replace window. It is a test seam only: production never replaces the writer.

## Evidence

- final-rereview.md, "Important 1: NOT ADDRESSED": the flush-accounting half was fixed by the FF
  round; this acquisition and replacement half was not.
- task-FF-report.md:51 records the omission.
- The reviewer cited BrokerDiagnostics.cs:138, :144, :159; at 66bdb6d the same lines are
  :138, :145, :165.

## Acceptance

- A test forces the interleaving (acquire, replace, enqueue) by signal, not by timing, and asserts
  the line reaches the replacement writer or is reported as dropped. It fails before the change.
- `Log` and `ReplaceWriterForTest` no longer let a line vanish silently in that window.

# Issue: BrokerProcessLaunchTests waits on a real 50 ms system-clock timeout

## Problem

`LaunchAsync_DefaultTimeoutOverridden_TimesOut` sets a 50 ms connect timeout and waits for the real
timer to fire (MFTLib.Tests/BrokerProcessLaunchTests.cs:190-195). The cause is production:
`BrokerProcess.WaitForBrokerAsync` builds `new CancellationTokenSource(connectTimeout)` on the
system timer (MFTLib/Broker/Client/BrokerProcess.Launch.cs:99). `LaunchAsync` accepts no clock, so
a test cannot drive the timeout by signal. The hang guard added by the fix wave bounds the wait but
does not remove the dependence on wall-clock time. This breaks the rule that tests never wait on
real time and must pass at any machine load.

## Evidence

- final-rereview.md, "Important 5: NOT ADDRESSED": the launch test still waits for the real timer;
  the reviewer requires an injected `TimeProvider` or a timer-signaling seam.
- orchestrator-rulings.md, row LOAD (owner, 2026-09-29): every test must pass under extreme load;
  widening a bound is not a fix.
- progress.md, session 4 resume point: the deterministic fix needs a `TimeProvider` on the public
  `BrokerProcess.LaunchAsync`.

## Acceptance

- The connect timeout is driven through an injected `TimeProvider` or an equivalent timer-signaling
  seam, and the launch test fires it by advancing a fake clock with no real delay.
- No test in MFTLib.Tests waits on a real timer for this path.
- Decision for the owner: put the `TimeProvider` on the public `LaunchAsync` signature, or keep
  the seam internal? There is no backward-compatibility constraint either way.

# Issue: spec gaps left open by the per-drive watch channels work

## Problem

The per-drive watch channels specification is silent on three points. The code follows the letter
of the spec in each case, and each needs the owner's answer, recorded in the spec, so tests can
pin it.

1. What `StopWatchingAsync` does after a failed restart. A rescan that replaced a faulted drive
   and then failed to restart its watch throws the restart failure to its caller and clears the
   drive's current instance (spec 2.6.4). Stop takes the outstanding fault from the instance it
   retires, so with no current instance the earlier fault is gone. The spec does not say whether
   stop then throws `InvalidOperationException` (no instance) or returns quietly.
   Question: after a restart fails, must stop rethrow the fault the old instance carried, throw
   for the missing instance, or return without throwing?
2. Whether a fresh start discards a faulted instance's outstanding fault.
   Question: when a start retires a Faulted instance, does its outstanding fault survive for the
   next stop to rethrow, or does the start discard it?
3. Ruling C5-Q1, made by the controller and open to the owner's overrule: a `Processing` pipe
   whose step makes progress but writes no frame (the bounded catch-up after ScanReady, the block
   flush) writes `Heartbeat` on a visit while its processing clock is within `ProcessingLimit`,
   and `Stalled` past the limit. The merged spec lists only Idle, WaitingOnVolume and Queued as
   heartbeating states.
   Question: does the owner confirm that a Processing pipe within its limit heartbeats, so that
   client silence always means a wedged host or pipe?

The two restart-failure tests get their stop assertion once item 1 is decided (B3 deferred minor
in progress.md).

## Evidence

- task-B3-review.md, "Concern 1": spec gap, code follows the spec's letter.
- task-B2-review.md, minor 5, and task-B1-review.md, minor 7: start over a Faulted instance.
- orchestrator-rulings.md, row C5-Q1: controller ruling, owner may overrule.
- progress.md, OWNER DECISIONS OPEN and the SPEC GAPS lines.
- Code: MFTLib/Index/FileIndex.RescanRestart.cs (restart after a rescan),
  MFTLib/Index/FileIndex.WatchDrive.cs (start helper), MFTLib/Broker/Host/HostPipeWriter.cs:136
  and :191 (heartbeat selection).

## Acceptance

- The owner's answer to each question is written into the specification and the AGENTS.md
  architecture notes, with one test per answer.
- The restart-failure tests assert the stop outcome chosen for item 1.

# Issue: deferred minors from the per-drive watch channels review rounds

## Problem

Small defects and test gaps found during review and deliberately left out of the pull request.
Each item was re-checked against 66bdb6d and still applies. Items already fixed were dropped.

- [ ] The diagnostics writer's drain loop runs with `TaskCreationOptions.LongRunning` although it is
  an async method that yields at every await, so it holds a dedicated thread for nothing
  (MFTLib/Broker/BrokerDiagnosticsWriter.cs:46).
- [ ] The Linux `msync` call passes `MS_SYNC` alone, while the runtime's own Unix flush also passed
  `MS_INVALIDATE` (MFTLib/Internal/Libc.cs, `SelectSynchronousFlag`; MFTLib/Index/BlockFile.Flush.cs:94).
  Decide whether the omission matters, and either add the flag or say why not in a comment.
- [ ] A throwing `Ended` handler escapes session teardown: `Ended?.Invoke(reason)` runs unguarded at
  the end of the end-of-session path (MFTLib/Broker/Client/BrokerProcess.Control.cs:324), so it can
  propagate into disposal, which the `DisposeAsync` documentation says never throws. Guard the call
  or narrow the documentation.
- [ ] A `TimeoutException` from the test helper's cleanup can mask the body's original exception,
  since it is thrown from a `finally` at four call sites
  (MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs:317-320, called from :74, :146, :205, :264).
- [ ] The native test-hook chunk-count recorder is process-global on the production path and takes a
  mutex per chunk. Its comment should say that tests using the hooks must not run in parallel
  (MFTLibNative/core/test_hooks.cpp:59-72).
- [ ] Two parse-thread tests compare the native `hardware_concurrency` with
  `Environment.ProcessorCount`, which differ under processor affinity, job limits or
  `DOTNET_PROCESSOR_COUNT` (MFTLib.Tests/MftVolumeTests.ParseControl.cs:15,
  MFTLib.Tests/NativeParserCoverageTests.ParseControl.cs:17, native side
  MFTLibNative/core/test_hooks.cpp:51).
- [ ] A cold scan cancelled during `OpenAsync` leaves its partial canonical cache file
  (`DeleteOnClose` is false for canonical targets; MFTLib/Index/FileIndex.CatchUp.cs:119 and
  MFTLib/Index/FileIndex.Rescan.cs:145). The next open rejects it as Incomplete, deletes it and
  cold-scans. Same on main 3597586. Decide whether to delete the partial file on cancellation.
  This item comes from the ledger and was not reproduced during drafting.

## Evidence

- deferred-minors.md: the A2 (drain loop), A1 (test hooks, processor count), C2 (Ended handler),
  C3b (cleanup helper) and W40 (msync) entries.
- progress.md:359: "KNOWN PRE-EXISTING" cold-scan entry.
- task-C2-review.md and task-C2-rereview-r1.md: the Ended handler.

## Acceptance

- Each box is either fixed, with a failing-first test where a test can fail first, or closed with a
  one-line reason in a comment on this issue.

# Issue: MFTLibTestExtensions line coverage is 80.37 percent and was never inside the coverage gate

## Problem

The coverage gate covers MFTLib, MFTLib.Index, TestProgram and Benchmark (AGENTS.md, "Test
coverage"). MFTLibTestExtensions, the public consumer-facing test harness, is not in that list.
The standing owner directive is 100 percent line, branch and method coverage. The extension
assembly measured 80.37 percent at the end of this work, so it is the one shipped assembly outside
that bright line.

## Evidence

- progress.md, OWNER DECISIONS OPEN: "MFTLibTestExtensions coverage is 80.37% and was never in the
  gate's scope."
- AGENTS.md lists the tested namespaces and says to extend the list and its regression fixtures
  when another tested executable namespace is added.
- Per-file uncovered lines were not captured in the ledger; the first step is to produce them.

## Acceptance

- Every uncovered line of MFTLibTestExtensions is covered by a test, or deleted if it cannot run,
  with no exclusions or attribute-based opt-outs.
- MFTLibTestExtensions is added to the publisher's tested-namespace list in
  scripts/test-coverage-status.ps1 and its regression fixtures, so the gate fails when it drops.
- Decision for the owner: none expected; the 100 percent directive already answers it.

# Filed

ISSUE 293 a failed rescan followed by disposal after the restart decision throws AggregateException instead of cancelling
ISSUE 294 BrokerDiagnostics.Log can drop a line when the test writer is replaced between acquiring the writer and enqueueing
ISSUE 295 BrokerProcessLaunchTests waits on a real 50 ms system-clock timeout
ISSUE 296 spec gaps left open by the per-drive watch channels work
ISSUE 297 deferred minors from the per-drive watch channels review rounds
ISSUE 298 MFTLibTestExtensions line coverage is 80.37 percent and was never inside the coverage gate
ISSUE 299 JournalMutator reports a Modified change for the data write inside a file's create cycle, so a live create-write-close raises Created plus Modified
ISSUE 308 Post-merge simplification pass over the per-drive watch channels change (PR 301): FileIndex partials and test duplication
