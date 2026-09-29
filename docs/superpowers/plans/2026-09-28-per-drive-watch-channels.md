# Per-Drive Watch Channels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking. Each task runs in its own sibling worktree; see "Waves".

**Goal:** Give every drive its own broker pipe, its own watch handle, its own pump, its own rescan gate and its own write gate, so that nothing index-wide exists except the snapshot and the short step that publishes it, and nothing connection-wide exists except the elevated process and its control pipe. A per-drive fault recovers quietly by rescanning that drive (a scan that loses its catch-up is one such fault and stops after three in a row on the same drive); connection-level uncertainty (a cancelled or timed-out operation, a closed or stalled pipe) closes that drive's channel and fails loudly. The work closes MFTLib issue 252 and ships as the 0.3.0 watch architecture that file-wizard and git-wizard migrate to in their pin-bump pull requests.

**Architecture:** Specification `docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md` (the merged specification on `main`, commit `3597586`, is the governing version). Section references below ("spec 2.4") point into it; this plan does not restate its rationale. Where the plan and the spec disagree, the spec wins; Appendix A lists the plan's corrections of the spec's citations.

**Tech stack:** C# / .NET 10 (MFTLib, MFTLibTestExtensions, MFTLib.Tests with MSTest), C++ (MFTLibNative, MSVC on Windows, cmake/Ninja on Linux), named pipes, memory-mapped block sections.

**Base commit:** `c1d43784eda997e419cfc3aab693d5cefd68593d` (MFTLib main `692820f` plus the specification). Consumers read at file-wizard `gitea/main` `f4f994082b87e120f26d7f72300b3a05ee178d40` and git-wizard `gitea/main` `15be5f3cf88acb3f80397954334f4b65b5ab62ed`; both pin MFTLib at `88e97a3`.

## NO BACKWARD COMPATIBILITY (owner directive)

On MFTLib, file-wizard and git-wizard there is NO compatibility surface of any kind. These have never shipped to an external consumer; the two wizards are dogfooding projects migrated in their pin-bump pull requests. Breaking changes are the purpose of 0.3.0.

- A breaking change is never a cost, a risk, a trade-off, or a reason to prefer one design over another.
- Forbidden: retained old overloads, `[Obsolete]` members, opt-in flags or enum members that preserve old behavior, transitional wire fields, dual paths, adapters from the old shape to the new one, version-bump ceremony, "keep X in case a caller needs it".
- Delete in place. The implementer traces call sites and rewrites the caller.
- Do not ask the owner about any of this. The answer is no.

This plan applies the rule to its own structure. No phase runs old and new watch paths side by side. Where a new layer replaces an old one that other code still compiles against, the task that changes the layer deletes the dependents it breaks, and a later task rebuilds them on the new shape. A dependent that is temporarily absent is not a shim; it is a gap on the integration branch that closes before the pull request. Every task in this plan is green (build, tests, aislop) at its gate except the git-wizard production task (G1), marked red by design; coverage is compared at the tranche ends defined under Waves.

## Owner rulings (binding)

Distilled from the spec and the 2026-09-28 comments on [MFTLib issue 265 (per-drive watch channels)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265).

1. One elevated broker process per consumer session, one elevation prompt, many pipes. Each drive operation gets its own pipe; connection-scoped control operations (volume query, journal grow, channel open) use one control pipe. No process per drive, no supervisor.
2. Watches are per drive all the way up through `FileIndex`: no shared watch session, no merged stream, no index-wide pump. `IIndexWatchSource` starts and stops one drive.
3. Batched entry points are calls, not lifetimes: thin concurrent fan-outs over the per-drive operation, one result per drive, throwing only for caller errors (bad arguments, disposed index, cancellation). The no-list forms mean every drive. Nothing shared outlives the call.
4. Every watch fault names a drive. The null-drive `WatchFault` is removed; a broker process failure is reported by each drive's own channel.
5. `_rescanGate` becomes per drive, so drives rescan concurrently. `_swapGate` splits into a per-drive write gate and a short index-wide publish step under `_stateLock`.
6. Failure policy: a per-drive fault (journal wrapped, drive removed, a batch that cannot be applied) recovers by rescanning that drive; connection-level uncertainty closes that drive's channel and fails loudly. A channel is never repaired.
7. Liveness: known failures are reported by an explicit signal; heartbeats, a host watchdog and request timeouts are the backstop. `Stalled` is its own frame kind and is reported as a loud `Channel` fault with no recovery rescan.
8. Concurrent scans are permitted and limited to no more than one scan per core. It is enforced by amendment S3 below: the broker owns one parse thread per processor, every running scan holds at least one, and a scan that cannot get one waits. This replaces the spec's separate scan cap and per-call thread budget.
9. One diagnostics log per process, each line tagged by channel.
10. `JournalBrokerScanSession` and `ScanSessionTestHarness` are deleted, not reshaped; `BrokerTestHarness` replaces the harness.
11. The spec's two readings are binding: a scan runs on its own drive pipe with only the channel-open handshake on the control pipe; a recovery rescan keeps the drive's `LiveWatch` checkpoint-loss report.
12. [MFTLib issue 264 (0.3.0 ships no TestExtensions package)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/264): ship `MFTLib.TestExtensions` as its own nuget.org package pinned to the exact `MFTLib` version.
13. Every place drives are scanned is concurrent and limited by core count through the broker's one parse-thread allocator, with no thread count passed by any caller: open, batched rescan, single-drive rescan, recovery rescan. `FileIndex.OpenAsync` scans its drives concurrently; `FileIndexOptions.OpenProgress` reports each drive as it finishes, from whichever thread finished it, and both consumers' progress handlers migrate. The impact is measured (M1). (Owner, 2026-09-28: "I thought we settled on concurrent, limited by core count, and we'd measure the impact.")
14. A running scan's parse-thread count is rebalanced at every chunk ([owner ruling 6 on MFTLib issue 265](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265#issuecomment-97869)). The broker owns the process-wide budget of one parse thread per processor and divides it among running scans; no caller computes a share, and no thread count crosses the wire or the producer contract.
15. A scan that loses its catch-up is a per-drive fault, never a resume from the current journal position ([owner ruling 7 on MFTLib issue 265](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265#issuecomment-97972)). Recovery is a rescan of that drive. Three consecutive lost catch-ups on one drive stop automatic recovery, leave the drive faulted, and carry the journal-size suggestion through the existing `CheckpointLoss` and `JournalSizeArithmetic` path. A scan whose catch-up succeeds resets the count.

## Specification amendments folded into the plan

Two reviews of the first version of the spec found gaps: a pr-crew review of [MFTLib pull request 266 (the spec)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/266) at `c1d4378` (S1 to S3) and a second independent review (R1 to R13). Owner rulings 6 and 7 on issue 265, made after those reviews, are folded into S3 and L1. The merged specification (commit `3597586`) contains every amendment below; the ids stay as the plan's traceability labels, and a task that cites one follows the merged spec's text for it. None reopens an owner ruling.

| Id | Direction | Tasks |
|---|---|---|
| S1 | The host heartbeats the control pipe whenever it has been idle for the heartbeat interval, unconditionally. The control pipe has no operation loop, and losing it ends the process and every watch. | C5 |
| S2 | Heartbeat writes are independent and bounded: the sender starts each pipe's write asynchronously and skips a pipe whose previous write (heartbeat or otherwise) has not completed, so a blocked write on one pipe never delays another pipe's heartbeat. | C5 |
| S3 | Parse threads are rebalanced at every chunk (owner ruling 6 on issue 265). The broker owns a process-wide budget of one parse thread per processor. Each running scan has a shared allowance that the broker updates whenever a scan starts, finishes or is cancelled; the native parser reads it at the start of each chunk and before path resolution (A1). Allowances divide the budget among running scans, remainders handed out in admission order, never fewer than 1 per scan; with more scans than processors the extras queue in arrival order, heartbeating as `Queued` (C5). A finished or cancelled scan's share returns to the others only after its native work has stopped (R7). No caller computes a share: `FileIndex` passes no thread count for open, batched rescan, single-drive rescan or recovery rescan, and no thread count exists on `ArmAndScan`, `BrokerScanOptions` or `MftBlockProduceRequest`. Accepted limit: a chunk already in progress finishes with the count it started with, so for the length of one chunk after a scan starts the total can exceed the processor count. | A1, C1, C2, C5 |
| SM, R1 | Per-drive state machine with watch-instance identity (next section). A removed handle is not a barrier: start and rescan await the previous instance's drain before publishing a successor or replacing the block; batch application, catch-up completion, fault bookkeeping and cleanup are scoped to the instance. | B1, B5, B6 |
| R2 | Stop and disposal cancel an unpublished start: the pending start and its cancellation source are registered under `_stateLock` before the source is invoked. | B1 |
| R3 | A queued recovery is tied to the failed watch instance and block, revalidates after taking the lifecycle gate, and is dropped when a manual rescan, stop or disposal superseded it. `Recovering` is published before subscribers are notified. | B6 |
| R4 | Drive X's lifecycle gate is held through the whole rescan: suspension, production, commit or rollback, restart. Production holds neither the write gate nor `_stateLock`. Internal restart helpers do not reacquire the lifecycle gate. | B1, B5 |
| R5 | Channel open is one owned operation (server creation, request registration, host connection, acknowledgement, first request) with bounded host-side waits for the connection and the first request, and disposal of both sides on every failed branch. | C1, C2 |
| R6 | A control request that has started writing finishes its frame (under a process-owned bounded token) or ends the control connection loudly; only a fully sent request uses the late-reply discard rule. | C2 |
| R7 | Native scan cancellation is in scope as an early task; channel teardown keeps ownership of the volume, the section and the scan's thread allowance until native work has actually stopped. | A1, C1 |
| R8 | Blockless adoption: every unpublished scan result and diagnostic is local to the operation or keyed by drive letter; the ordinal and all associated status move together at publication. | B5 |
| R9 | From inside any `Changed` or `WatchFaulted` handler of an index, stop, rescan, start, dispose, their batched forms and an unsettled `WaitForCatchUpAsync` fail immediately with an actionable exception, whichever drive they name (an `AsyncLocal` marker with an `Active` flag). | B8 |
| R10 | Diagnostics records are enqueued without waiting for disk; a separate writer with a bounded buffer and a stated overflow policy does the I/O. | A2 |
| R11 | Test seams: host and client take an injected `TimeProvider` and processor count; sources report operation state (waiting on volume versus processing) through a seam fakes can drive; `BrokerTestHarness` exposes these plus controlled connection and write failures. | C1, C2, C5 |
| R12 | Request ids are allocated atomically, never zero, never an outstanding or abandoned id; retained entries are released on reply or process end; exhaustion fails explicitly. | C2 |
| R13 | Scan frame order is `Cursor`, `ScanProgress`\*, `ScanReady`, then one terminal frame: `JournalBatch` when the catch-up held or `CatchUpLost` when it failed and the live journal proves the armed cursor lost (L1); or `Error` at any point, including a catch-up failure the journal does not prove. `ScanReady` is written before catch-up runs (`JournalBrokerHost.Scan.cs:208-217`); the spec's table put `Warning` before `ScanReady`. | C1, C2 |
| L1 | A scan that loses its catch-up (owner ruling 7 on issue 265; spec 2.3 and 2.6.6). When catch-up fails the host runs `JournalCheckpointCheck.Check` on the armed cursor; only a proven loss becomes a terminal `CatchUpLost` frame carrying the `JournalCheckpointLoss`, while a retained cursor or an unanswerable journal is an `Error` frame after `ScanReady` and the scan fails with no block. The client returns the complete block with `CatchUpLoss` set (`BrokerDriveScanResult`, then `MftBlockProduceResult`); `FileIndex` publishes it marked unresumable, counts it in `DriveRuntime.ConsecutiveLostCatchUps`, raises `WatchFaulted(CatchUpLost, X)` with a `JournalCatchUpLostException`, rescans that drive at once, and stops after `FileIndex.LostCatchUpRecoveryLimit` (3) consecutive losses, leaving the drive `Faulted` with its watch refused and a `ScanCatchUp` `CheckpointLoss` carrying the journal-size suggestion; a scan whose catch-up holds resets the count. | C1, C2, C5, B5, B6, B9, C8 |

## Per-drive state machine (amendment SM)

This restates spec 2.6 in the terms the tasks use. Everything below is per drive X; `_stateLock` guards every field named.

**Watch instance.** Each start creates a `WatchInstance` with its own identity (the object reference, plus a per-drive `long Generation` for diagnostics). It owns: the start cancellation source (linked to the disposal token), the `IIndexDriveWatch` handle once returned, the pump task and the pump's stop source, the catch-up slot for this instance, the instance's outstanding fault, the block it was armed against (`DriveBlock ArmedBlock`), and a `Drained` task that completes when teardown has finished (handle disposed, pump returned). `DriveRuntime.Current` holds at most one instance; `DriveRuntime.Retiring` holds the previous one until its `Drained` completes.

**Instance states:** `Starting` (source invoked, handle not yet returned), `Running` (handle published, pump running), `Faulted` (pump ended with a fault; recovery may be queued against this instance), `Retiring` (stop requested or superseded; teardown in progress), `Drained` (terminal).

**Drive states** (reported through `DriveStatus.WatchCatchUp`): `NotStarted` (no current instance), `CatchingUp` and `CaughtUp` (current instance `Running`), `Recovering` (a recovery is queued or running for the current faulted instance, or a lost catch-up is being rescanned), `Faulted` (current instance `Faulted` with no recovery pending, or a drive whose watch is refused because its block is unresumable).

**Scoping rule.** Every mutation a pump makes is conditioned on its instance: a batch takes X's write gate, then checks under `_stateLock` that `runtime.Current` is still this instance, still `Running`, and that `instance.ArmedBlock` is still X's published block; otherwise the batch is dropped, not applied. Catch-up completion, fault recording, checkpoint-loss recording and cleanup all check `ReferenceEquals(runtime.Current, instance)` the same way. A retiring pump's accepted batch therefore never reaches a successor's block.

**Linearization points** (each under `_stateLock`):

| Operation | Takes effect when | Before | After |
|---|---|---|---|
| `StartWatchingAsync(X)` | the returned handle is published and the instance moves `Starting` to `Running` | lifecycle gate taken; any `Retiring` instance's `Drained` awaited; the `Starting` instance and its cancellation source registered (R2); the source invoked outside the lock | pump started outside the lock; a handle returned after the instance stopped being `Current` is disposed by the start path, and the start throws `OperationCanceledException` |
| `StopWatchingAsync(X)` | `WatchRequested` is cleared and `Current` moves to `Retiring` (a `Starting` instance has its start source cancelled here) | no gate | teardown outside the lock; returns after `Drained`, bounded by the caller's token; the instance's catch-up slot is cancelled; its outstanding fault is rethrown once |
| `RescanAsync(X)` | the commit that publishes the new block (X's write gate, then `_stateLock`) | lifecycle gate held from entry to exit (R4); the current instance retired and its `Drained` awaited before production | restart through an internal helper that assumes the gate is held, only if `WatchRequested` is still set |
| Recovery | the same commit as a rescan | queued against a faulted instance and its block; after taking the lifecycle gate it revalidates that `Current` is still that instance, still `Faulted`, its block still published, and `WatchRequested` set; otherwise it is dropped (R3) | as rescan |
| `DisposeAsync` | `_disposed` set and the disposal token cancelled | nothing | every drive: current and starting instances retired and drained, queued recoveries cancelled and awaited, then lifecycle gates and write gates in ascending drive-letter order, then snapshot release |

**Lost catch-up (spec 2.6.6).** A scan whose catch-up is lost publishes its block, marks it unresumable and is retried by the same operation under X's lifecycle gate (B5); `Recovering` is published for it when `WatchRequested` is set, and X reads `Faulted` with `RecoveryStopped` after the third consecutive loss.

## Build and test (from AGENTS.md)

Windows, from the worktree root. `MSBuild.exe` is the 64-bit MSBuild that `init.ps1` resolves through `vswhere`.

```powershell
.\init.ps1                                                    # restore and generated agent files
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64
dotnet build -c Release -p:Platform=x64
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~<ClassName>"
.\scripts\run-coverage.ps1 -NonInteractive                   # whole suite plus managed coverage, no UAC
pwsh -NoProfile -File scripts/test-coverage-status.ps1        # offline coverage-publisher regression checks
.\scripts\native-coverage.ps1                                 # native coverage (Debug|x64 /PROFILE build)
aislop scan .                                                 # quality gate, failBelow: 100
```

Linux (tasks that touch native code or test platform exclusions):

```bash
./init.sh --build
scripts/coverage-linux.sh
```

The targeted `dotnet test` line is the iteration command. `run-coverage.ps1 -NonInteractive` is the one whole-suite run per task.

**Standard verification** (what "verify" means in every task below, in this order): rebuild what the task touched (the native line when native code changed, then `dotnet build -c Release -p:Platform=x64`); the targeted `dotnet test` line with a filter naming every test class the task created or ported (`--filter "FullyQualifiedName~ClassA|FullyQualifiedName~ClassB"`); `.\scripts\run-coverage.ps1 -NonInteractive` in the background; `aislop scan .`. Tasks name any extra command (native coverage, Linux build, namespace or seam guards) explicitly.

## Test rules every task inherits

- Tests that reference the process-wide native delegate seams in `MFTLibNative` or `FileUtilities`, including calls to either `ResetToDefaults`, carry class-level `[DoNotParallelize]`. `NativeSeamIsolationTests` enforces this from IL; do not add exclusions to hide a race. The same applies to any test that reads or writes a native test-hook global (`SetMaxThreads`, the new `GetChunkThreadCounts` and `GetResolveThreadCount`) and to `DefaultElevatedEntryRunner._exitProcess`.
- Never assert on wall-clock time. Heartbeat, stall, processing limit, reply timeout, connect timeout and grace period all read an injected `TimeProvider`; tests use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` and advance it. Ordering is proven with `TestSupport/TestGate.cs` signals.
- Async waits poll a condition with a timeout (`Task.WaitAsync(timeout)` on a signal task), never a fixed sleep.
- Every test that opens a `FileIndex` sets `FileIndexOptions.CacheDirectory` to an owned temporary directory; the module initializer rejects the default cache.
- Every bug fix gets a regression test that is seen failing before the fix and passing after.
- Test-driven order inside a task: write the named tests, run them and see each fail for the stated reason, implement, run them green.
- A task that deletes an old test file whose cases a later task ports writes nothing to replace it; the porting task reads the old file from the base commit with `git show c1d43784:<path>`.

## Lane rules every task inherits

- Work only in the task's own worktree `~/MFTLib-worktrees/<task-id>` (sibling of the checkout, never nested), branched from the integration branch head the orchestrator names. FIRST ACTION: confirm `git rev-parse HEAD` equals the named SHA; on mismatch stop and report, never merge, rebase or reset to self-correct.
- Do not commit on the integration branch; commit only on the task branch. The orchestrator merges.
- Any command over about 30 seconds (MSBuild, `dotnet build` of the solution, `run-coverage.ps1`, `aislop scan`) runs with `run_in_background`, teed to a scratch file; keep working and read the file on the completion notice. Never re-run a command to see more output.
- Targeted tests while iterating; one whole-suite run and one `aislop scan .` at the end; at most three fix-and-rerun rounds, then report.
- Do not launch GUI apps and do not use computer use. Do not run anything that needs elevation; the measurement tasks M1 to M3 are the owner's.
- Identifiers use full words (`maximum`, not `max`; `configuration`, not `config`).
- No em-dashes or en-dashes in anything written (code, comments, docs, commit messages). Use ` - `, colons or parentheses.
- Keep code files under about 500 lines; split partial classes by responsibility as the codebase already does.
- Native error text goes through `SetErrorMessage` in `MFTLibNative/internal.h`.
- Do not edit `.aislop/config.yml`. Fix aislop findings; explain false positives in the report.
- Describe the target state in comments and docs; no "no longer" history notes.

## Order and why

The dependency graph has two trunks that meet at the watch source:

- Index trunk: the `IIndexWatchSource` contract and the per-drive state machine in `FileIndex` (B1), then the gate split and publication (B5), then automatic recovery (B6) beside the concurrent open (B9), then the batched entry points (B7), then the callback reentrancy guard (B8).

Task boundaries on the index trunk, re-examined against the state machine: B1 owns the machine itself (instances, start, stop, pump scoping, the rescan's retire-and-drain, disposal's drain), because every row of the linearization table depends on instance identity and none can land without it. B5 owns what the machine publishes into (per-drive write gates, publication under `_stateLock`, the pending-result type, the lost-catch-up rule, disposal gate order); it is separable because B1 runs correctly under the old index-wide gates. B6 owns recovery tickets, the only row B1 leaves out. B9 is new: the concurrent open consumes B5's pending results and nothing from B6, so it runs beside B6. Merging any two of these would put a single lane past 1500 lines.
- Broker trunk: the wire protocol and channel host with the parse-thread allocator (C1), then the `BrokerProcess` client and scan channels (C2), then watch channels and the thin `BrokerIndexWatchSource` (C6), with host liveness (C5) and client liveness (C7) beside them.

Five foundations touch neither trunk's files: A1 (native per-chunk thread allowance and parse cancellation), A2 (asynchronous diagnostics), A3 (deleting the scan session layer), A4 (bounded journal reads) and A5 (deleting the old broker watch source). A1, A2, A3 and A5 run in wave 1; A4 runs in wave 2 beside the two trunks because it edits `MFTLib/Internal/MFTLibNative.cs` after A1, and nothing in the trunks needs it before C5. Native parse cancellation (R7) is a foundation because C1's channel teardown and thread-allocation release depend on it. A5 exists because the old broker watch source cannot compile under the new `IIndexWatchSource` (B1) nor over the new wire protocol (C1); deleting it once, before either trunk, leaves B1 and C1 with no file in common, and rebuilding it waits for both trunks (C6). This is the only ordering that stays green without an adapter from the old broker watch to the new contract, which the no-compatibility rule forbids.

Test porting is split from the core tasks because the core tasks must delete every test file that no longer compiles, and rewriting all of them in one lane would put a single lane past 3000 lines. The port tasks run in parallel on disjoint files right after their core task merges.

## Waves

Every wave branches each task worktree from the integration branch head at the start of the wave. The integration branch is `impl/265-per-drive-channels`, created from the commit that adds this plan. Review happens at every merge point: the orchestrator runs the two-stage review from superpowers:subagent-driven-development (spec compliance, then code quality) on each task branch, merges the approved branches in the listed order, builds, runs `run-coverage.ps1 -NonInteractive` once on the merged head, and only then opens the next wave.

| Wave | Tasks (parallel, disjoint files) | Merge notes |
|---|---|---|
| 1 | A1, A2, A3, A5 | None expected |
| 2 | A4, B1, C1 | None expected: A4 touches only the journal read (the native export, its P/Invoke in `MFTLibNative.cs`, `MftVolume.Journal.cs`, `UsnJournalSyntheticTests.cs`); B1 owns the index watch contract; C1 owns the client and host and edits `UsnJournalSyntheticTests.Cancellation.cs`, a different file; A5 already deleted the old broker watch source in wave 1 |
| 3 | B2, B3, B4, C2, C3a, C3b | None expected; C2 edits `MFTLib/Index/MftBlockProducer.cs` (adds `CatchUpLoss`), which no other wave 3 task touches |
| 4 | B5, C4, C5, C7 | C5 and C7 both add a frame-kind handling test file; names are disjoint |
| 5 | B6, B9, C6 | B6 modifies `FileIndexWatchRecoveryFaultTests.cs` and `FileIndexWatchFaultTests.cs`; B9 rewrites `FileIndexOpenProgressTests.cs`; the new test files are their own |
| 6 | B7, C8 | None expected |
| 7 | B8 | Touches the entry points B7 just changed, so it runs alone |
| 8 | D1, D2, D3, then V1 | V1 is the orchestrator's final verification and the pull request |
| After merge | M1, M2, M3 (owner, attended) | Not lane work |
| After MFTLib lands | F1; G1 then G2 | Consumer repositories, against the MFTLib commit the implementation lands as |

**Coverage tranches.** The CI coverage publisher fails a drop of more than 10 percentage points from main's latest measured coverage, or zero covered executable lines in a tested namespace (AGENTS.md, "Test coverage"). The publisher is not an absolute 100 percent gate and it runs on the pull request, not per task; the project standard of 100 percent line coverage for managed code is enforced at V1. A5, B1 and C1 delete established tests that later tasks restore, so the comparison is meaningful only at tranche ends. Tranche I is B1 to B4: compare `MFTLib.Index` on the merged head after wave 3. Tranche B is A5, C1 to C4, C6 and C7: compare the `MFTLib` namespace on the merged head after wave 5. V1 requires 100 percent line coverage for `MFTLib` and `MFTLib.Index` (the project standard for managed code; any residual uncovered line needs a written unreachability reason), beyond the publisher's drop check. Each task's own gate is build, targeted tests, whole-suite tests and aislop; its `run-coverage.ps1` run reports coverage, and a dip inside a tranche is expected and is not a failure. The tranche end is also where the porting lists are audited: every base-commit test method in a file a core task deleted either has a counterpart or is named in a porting commit as dropped, with the reason.

## Dependency table

| Task | Name | Depends on | Parallel with | Tier |
|---|---|---|---|---|
| A1 | Native per-chunk thread allowance and parse cancellation | none | A2, A3, A5 | Opus: an allowance and a cancellation flag read by the chunk loop and every parse worker, and a safe unwind that frees every buffer |
| A2 | Asynchronous diagnostics with channel tags | none | A1, A3, A5 | Sonnet |
| A3 | Delete the scan session layer | none | A1, A2, A5 | Sonnet |
| A5 | Delete the broker watch source | none | A1, A2, A3 | Sonnet |
| A4 | Bounded journal reads | A1 (shared file `MFTLibNative.cs`) | B1, C1 | Sonnet |
| B1 | Per-drive watch contract and `FileIndex` state machine | A3, A5 | A4, C1 | Opus: watch-instance identity, pending-start cancellation and drain barriers under concurrency |
| C1 | Wire protocol, channel host and parse-thread allocator | A1, A2, A3, A5 | A4, B1 | Opus: concurrent control dispatch, owned channel open, the allocator, teardown that waits for native work |
| B2 | Port index watch tests: watch, pump, faults, catch-up | B1 | B3, B4, C2, C3a, C3b | Sonnet |
| B3 | Port index watch tests: rescan interplay | B1 | B2, B4, C2, C3a, C3b | Sonnet |
| B4 | Port index watch tests: checkpoint loss and isolation | B1 | B2, B3, C2, C3a, C3b | Sonnet |
| C2 | `BrokerProcess` client, scan channels, `BrokerTestHarness` | C1 | B2, B3, B4, C3a, C3b | Opus: owned channel-open handshake, mid-frame control write cancellation, request id retirement |
| C3a | Port host tests: watch operation | C1 | B2, B3, B4, C2, C3b | Sonnet |
| C3b | Port host tests: scan, control and entry point | C1 | B2, B3, B4, C2, C3a | Sonnet |
| B5 | Gate split, publish under `_stateLock`, lost catch-up, disposal order | B2, B3, B4 | C4, C5, C7 | Opus: lock order, blockless ordinals, catch-up retry bookkeeping and disposal ordering |
| C4 | Port client-side scan tests | C2 | B5, C5, C7 | Sonnet |
| C5 | Host liveness: operation state, heartbeat thread, watchdog | A4, C2, C3a, C3b | B5, C4, C7 | Opus: dedicated heartbeat thread and watchdog timing under a fake clock |
| C7 | Client liveness: stall limit and control reply timeouts | C2 | B5, C4, C5 | Sonnet (careful) |
| B6 | Automatic recovery | B5 | C6 | Opus: recovery rescans racing stop, disposal and second faults |
| C6 | Watch channels and `BrokerIndexWatchSource` | B2, C2, C7 | B6 | Sonnet (careful) |
| B9 | Concurrent open | B5 | B6, C6 | Sonnet (careful) |
| B7 | Batched entry points | B6, B9, C6 | C8 | Sonnet |
| C8 | Cross-drive liveness scenarios | C5, C6, B6 | B7 | Sonnet |
| B8 | Callback reentrancy guard | B7 | none | Sonnet (careful) |
| D1 | AGENTS.md and CHANGELOG | B8, C8 | D2, D3 | Sonnet |
| D2 | Broker docs | B8, C8 | D1, D3 | Sonnet |
| D3 | README and index-format | B8, C8 | D1, D2 | Sonnet |
| V1 | Final verification and pull request | D1, D2, D3 | none | Orchestrator |
| M1 to M3 | Attended measurements | V1 merged | none | Owner |
| F1 | file-wizard migration | V1 merged | G1 | Sonnet |
| G1 | git-wizard production migration | V1 merged | F1 | Sonnet |
| G2 | git-wizard test migration | G1 | F1 | Sonnet |

## Scan pipeline audit (spec 2.2, "The processing clock")

A healthy scan must never go 30 seconds (the processing limit) without a progress step: a frame written on its channel or a republished operation state. Traced at `c1d4378` for a 10 million record volume on slow storage.

| Phase | Where | Cost | Progress today | Verdict | Plan |
|---|---|---|---|---|---|
| Channel open, request read | new (C1) | O(1) | n/a | SAFE | none |
| Wait for admission by the parse-thread allocator (S3) | new (C1, C5) | unbounded by design | n/a | SAFE: state `Queued` heartbeats | C5 |
| Cursor query | `JournalBrokerHost.cs:311-315` | O(1) FSCTL | `Cursor` frame after it | SAFE | none |
| Volume open | `JournalBrokerHost.Sources.cs:9` | O(1), can block on a locked volume | none | UNKNOWN (rare) | C5 publishes `WaitingOnVolume` around the open |
| Boot sector, record 0, data runs | `mft.parse.cpp:301-352` | O(1) reads | none | SAFE | none |
| Chunk read and parallel fixup and parse | `mft.parse_core.cpp:316-377` | O(records), I/O bound | native callback per chunk (`:358-361`), chunk = 262144 records (`MftVolume.cs:36`) = 256 MB at 1 KB records, 1 GB at 4 KB records | AT RISK: one chunk plus the overlapped next read at 30 MB/s is 9 s (1 KB records) or 34 s (4 KB records); the first callback comes only after two chunk reads | C1 caps the broker's chunk at 64 MB (65536 records at 1 KB, 16384 at 4 KB), so a callback lands at least every 64 MB read |
| Single-thread parse (allowance of 1) | same | O(chunk) CPU | per chunk | SAFE after C1's chunk cap | none |
| Materialize batches, filter, write rows | `MftBlockRowWriter.cs:29-53` | O(records) | `BlockWriteProgress` per 4096-record batch | SAFE | none |
| Host progress pump | `JournalBrokerHost.Scan.cs:87-137` | throttle 250 ms | frame per 250 ms | SAFE; uses `Stopwatch` | C5 moves the throttle to the injected `TimeProvider` |
| Stamp cursor, complete, flush | `RealBlockSectionWriter.cs:17-18`, `BlockWriter.cs:184-196`, `BlockFile.cs:257-261` | one `MemoryMappedViewAccessor.Flush()` over the whole view (`BlockFile.cs:257-260`; the repository declares neither `FlushViewOfFile` nor `msync` today) (1 to 3 GB for a large volume) | none | AT RISK on slow cache storage | C5 flushes in 64 MB ranges through new P/Invokes (`FlushViewOfFile` in `MFTLib/Internal/Kernel32.cs`, `msync` in a new `MFTLib/Internal/Libc.cs` for non-Windows), called on the view's base pointer plus offset from `BlockFile.Flush(Action<long>? rangeFlushed)`, and republishes state per range |
| Final progress, `ScanReady` | `JournalBrokerHost.Scan.cs:193-211` | O(1) | frames | SAFE | none |
| Catch-up journal read | `JournalBrokerHost.Scan.cs:217`, native `ReadUsnJournal` loops to the tip in one call (`usn_journal.cpp:220-309`) | O(backlog); consumers grow journals to GBs through `GrowUsnJournalAsync` | none until the terminal batch | AT RISK: a multi-GB backlog is one silent call | A4 adds a per-call buffer-read bound to the native export; C5 loops in bounded calls (256 buffers, 16 MB) and republishes state per call |
| Catch-up `JournalBatch` write | `JournalBrokerHost.Scan.cs:249-251` | O(backlog) serialization | a frame | SAFE | none |
| Watch read loop | `JournalBrokerHost.cs:86-111` | blocked in `FSCTL_READ_USN_JOURNAL` | per batch | SAFE: `WaitingOnVolume` while blocked, `Processing` per batch | C5 |

Cancellation (R7, confirmed in code): at `c1d4378` a closed scan pipe cancels only the managed token. `ScanDriveRecordBatches` checks it after `ReadRecordBatches` yields (`JournalBrokerHost.Sources.cs:9-17`), `ReadRecordBatches` completes `StreamRecords` before its first batch (`MftVolume.cs:65-72`), and the native export takes no cancellation (`MftVolume.cs:129-130`, `mft.parse.cpp:288`). The whole volume is parsed before cancellation is seen, holding the volume, the section and (under S3) the scan's thread allocation. A1 adds native cancellation checked per chunk and per worker slice; C1 keeps the channel's resources until the native call has returned.

---

## Phase A: foundations (waves 1 and 2)

### Task A1: Native per-chunk thread allowance and parse cancellation (Opus)

Opus because the thread allowance and the cancellation flag are read by the chunk loop, the I/O thread and every parse worker, and a cancelled parse must free the double buffers, the worker slices and the partial result on every exit path. Implements amendments S3 (native side: the parser takes its thread count from a shared allowance, once per chunk) and R7.

**Where the rebalance happens (read at `c1d4378`).** The parser already creates its workers fresh for every chunk and joins them before the chunk ends: `ParseChunkParallel` sizes its slices and spawns `numThreads` workers (`MFTLibNative/mft/mft.parse_core.cpp:110-137`) and joins them (`:138-140`), and `ParseAllChunks` calls it once per chunk from its loop (`:323-369`, the call at `:331-336`). The count is a plain `unsigned` fixed once per scan at `:405` (`EffectiveThreadCount()`, `MFTLibNative/core/test_hooks.cpp:36-43`), passed to `ParseAllChunks` (`:420`) and to `ResolveAllPaths` (`:443`). Nothing in the parser has to be restructured to change the count between chunks. The plan changes two read points:

- The rebalance point is the top of the `while (currentChunkSize > 0)` iteration in `ParseAllChunks` (`:323`), before the I/O thread starts (`:326`) and before the `numThreads > 1` branch (`:331`): each iteration calls `EffectiveThreadCount(control)` and uses that value for this chunk only. A chunk in progress finishes with the count it started with, which is the accepted limit of owner ruling 6.
- Path resolution reads the control block again immediately before `ResolveAllPaths` (`:443`), so a scan late in its life resolves paths with its current share.

**How the next chunk's count is obtained.** The export takes one pointer to a caller-owned control block, `MftParseControl { int32_t cancelRequested; int32_t parseThreadAllowance; }` (spec 2.3), which the managed caller keeps pinned for the call; the broker's `ParseThreadAllowance` writes through to it while the parse runs, and the parser only reads it. The native code reads both fields atomically. `EffectiveThreadCount(const MftParseControl* control)` reads `parseThreadAllowance` once: 0, or a null control block, means every processor; any other value is clamped to `[1, hardware_concurrency]`; `SetMaxThreads` still caps the result (`test_hooks.cpp:39-41`). `cancelRequested` is read at the same loop top (before each chunk read), after each chunk's parse, by each worker between 4096-record sub-slices, and between path-resolution slices.

**Files:**
- Modify: `MFTLibNative/internal.h` (`unsigned EffectiveThreadCount(const MftParseControl* control)` replaces the declaration at `:44`; `void RecordChunkThreadCount(unsigned)` and `void RecordResolveThreadCount(unsigned)`), `MFTLibNative/core/test_hooks.cpp` (the new `EffectiveThreadCount`, the two record functions, the `GetChunkThreadCounts` and `GetResolveThreadCount` hooks, reset in `ResetTestState`), `MFTLibNative/mft/mft.parse_core.cpp` (`ParseMFTImpl` and `ParseAllChunks` take the control block in place of the fixed `numThreads`; the per-chunk read and the pre-resolution read above; a cancelled parse frees everything and returns a result whose `errorMessage` is `L"Parse cancelled"` and whose new `cancelled` field is 1), `MFTLibNative/mft/mft.parse.cpp` (both platform `ParseMFTRecordsWithProgress` exports gain the control-block parameter; `ParseMFTRecords` and `ParseMFTFromFile` pass null), `MFTLibNative/mft/mft_synthetic.cpp` (`:314` passes null), `MFTLibNative/mft_api.h:48` (`struct MftParseResult`: add `uint32_t cancelled` at the end of `MftParseResult` and declare `MftParseControl` beside it, bump `MFT_NATIVE_ABI_VERSION` because the stride check reads the struct), `MFTLib/Interop/MftParseResult.cs`, `MFTLib/Internal/MFTLibNative.cs` (P/Invoke and seam), `MFTLib/Mft/MftVolume.cs` (`StreamRecords` and `ReadRecordBatches` gain the allowance and a `CancellationToken`; a token registration sets `cancelRequested` in the pinned control block; a cancelled result throws `OperationCanceledException` from managed code)
- Create: `MFTLib/Mft/ParseThreadAllowance.cs`, `MFTLib.Tests/ParseThreadAllowanceTests.cs`
- Test: `MFTLib.Tests/NativeParserCoverageTests.cs`, `MFTLib.Tests/MftVolumeTests.cs`, `MFTLib.Tests/MftResultTests.cs`

**Interfaces produced:**

```cpp
struct MftParseControl { int32_t cancelRequested; int32_t parseThreadAllowance; }; // caller-owned, pinned for the call, read atomically
unsigned EffectiveThreadCount(const MftParseControl* control); // null or allowance 0: hardware_concurrency; else the value read once, clamped to [1, hardware_concurrency]; SetMaxThreads still caps
EXPORT MftParseResult* ParseMFTRecordsWithProgress(HANDLE volumeHandle, const wchar_t* filter, uint32_t matchFlags,
    uint32_t bufferSizeRecords, const MftParseControl* control, MftProgressCallback callback, void* context);
EXPORT unsigned GetChunkThreadCounts(unsigned* counts, unsigned capacity); // test hook: the count each chunk of the last ParseMFTImpl used, in order; returns how many were recorded (at most capacity)
EXPORT unsigned GetResolveThreadCount();                                   // test hook: the count path resolution of the last ParseMFTImpl used; 0 when it did not run
```

```csharp
namespace MFTLib;
public sealed class ParseThreadAllowance
{
    public ParseThreadAllowance(int count);            // count >= 1
    public int Count { get; set; }                     // below 1 throws ArgumentOutOfRangeException; while a parse runs, a write goes through to its control block
}
public MftResult StreamRecords(string? filter, MatchFlags matchFlags, IProgress<MftScanProgress>? progress,
    ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);      // null: every processor
public IEnumerable<MftRecord[]> ReadRecordBatches(bool resolvePaths, int batchSize,
    IProgress<MftScanProgress>? progress, ParseThreadAllowance? parseThreads, CancellationToken cancellationToken);
```

`StreamRecords` and `ReadRecordBatches` pin one `MftParseControl` for the call, attach the allowance to it (an internal attach and detach on `ParseThreadAllowance`) and detach it before the block is freed; a null allowance leaves `parseThreadAllowance` 0. The broker's allocator is the only production writer of `Count`. The existing optional-parameter shapes of `StreamRecords` and `ReadRecordBatches` are replaced, not kept beside the new ones; every caller in `MFTLib`, `TestProgram`, `Benchmark` and the tests passes the new arguments (Grep each method name). README examples are updated in D3.

- [ ] **Step 1: Failing tests.** The native cases use a synthetic NTFS file as in `ParseMFTRecordsWithProgress_RealCallback_ReportsPerChunkProgress` (`:299`) with `bufferSizeRecords` 64 so several chunks run; the progress callback fires after each chunk is parsed (`mft.parse_core.cpp:358-361`), so a change it makes can only affect later chunks.
  - `NativeParserCoverageTests.ParseMFTRecordsWithProgress_AllowanceNull_EveryChunkUsesEveryCore`.
  - `..._AllowanceTwo_EveryChunkUsesAtMostTwoThreads`: every entry of `GetChunkThreadCounts` equals `Math.Min(2, Environment.ProcessorCount)`.
  - `..._AllowanceAboveCores_ClampsToCores`, `..._AllowanceZero_MeansEveryCore` (native level; the managed type rejects 0), `..._SetMaxThreadsStillCaps` (`SetMaxThreads(1)`, allowance 4, every chunk 1).
  - `..._AllowanceLoweredFromProgressCallback_LaterChunksUseTheNewCount`: the allowance starts at `Environment.ProcessorCount`; the callback sets it to 1 on its first invocation; the first recorded count is `Environment.ProcessorCount` (the chunk that callback followed keeps its count) and every later one is 1.
  - `..._AllowanceRaisedFromProgressCallback_LaterChunksUseTheNewCount`: the reverse.
  - `..._PathResolutionReadsAllowanceAfterLastChunk`: `MatchFlags.ResolvePaths`; the callback sets the allowance to 1 on its last parsing invocation; `GetResolveThreadCount()` is 1 while the last chunk's recorded count was `Environment.ProcessorCount`.
  - `..._CancelledDuringPathResolution_StopsBetweenSlices` (the flag is set from the resolution progress callback; the result is cancelled and freed).
  - `..._CancelledBeforeFirstChunk_ReturnsCancelledWithoutReading`: flag set before the call; the progress callback never fires; `cancelled` is 1.
  - `..._CancelledFromProgressCallback_StopsAfterThatChunk`: the callback sets the flag on its first invocation; exactly one parsing callback fires before return; `cancelled` is 1; `FreeMftResult` succeeds (no leak detector exists, so the native coverage run must cover the cancel exits).
  - `MftVolumeTests.ReadRecordBatches_AllowanceLoweredBetweenChunks_LaterChunksUseTheNewCount` (through the managed seam, `ParseThreadAllowance.Count` written from the progress callback) and `ReadRecordBatches_TokenCancelledDuringParse_ThrowsBeforeFirstBatch`.
  - `ParseThreadAllowanceTests.Count_WrittenDuringParse_IsVisibleToTheNativeControlBlock` (including after a garbage collection), `Constructor_CountBelowOne_Throws`, `Count_SetBelowOne_Throws`.
  - `MftResultTests.AbiStride_MatchesCancelledField`.
  - Classes touching the hooks keep or gain class-level `[DoNotParallelize]`.
- [ ] **Step 2: See them fail.**
- [ ] **Step 3: Implement.** Workers exit their slice loop when the flag is set; the chunk loop joins the I/O thread before returning; every allocation made by `ParseMFTImpl` is freed on the cancel path, as on the existing `!parsedOk` path (`mft.parse_core.cpp:425-434`).
- [ ] **Step 4: Verify** (standard) plus native Release and Debug builds, `.\scripts\native-coverage.ps1` in the background (the cancel branches must be covered), and `./init.sh --build` on Linux for the `#ifndef _WIN32` stub at `mft.parse.cpp:394`.
- [ ] **Step 5: Commit:** "Native parse rebalances its thread count at every chunk and stops promptly when cancelled".

**Gate:** green. **Depends on:** none.

### Task A4: Bounded journal reads

Runs in wave 2, after A1 merges, because it edits `MFTLib/Internal/MFTLibNative.cs`, which A1 edits in wave 1.

**Files:** Modify `MFTLibNative/usn/usn_journal.cpp` (`ReadUsnJournal` gains `uint32_t maximumBufferReads`, 0 reads to the tip), `MFTLib/Internal/MFTLibNative.cs` (that P/Invoke only), `MFTLib/Journal/MftVolume.Journal.cs` (internal `ReadUsnJournalBounded`; public `ReadUsnJournal(since)` passes 0). Test: `MFTLib.Tests/UsnJournalSyntheticTests.cs`.

```cpp
EXPORT UsnJournalResult* ReadUsnJournal(HANDLE volumeHandle, int64_t startUsn, uint64_t journalId, uint32_t maximumBufferReads);
```
```csharp
internal (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournalBounded(UsnJournalCursor since, int maximumBufferReads);
```

- [ ] **Failing tests:** `ReadUsnJournal_MaximumBufferReadsOne_StopsAfterOneRead` (three buffers queued with `SetUsnIoSuccess`; a bounded read of 1 returns the first buffer's entries and `NextUsn`; a second call returns the second's); `ReadUsnJournal_MaximumBufferReadsZero_ReadsToTip`. See them fail; implement the loop bound (`for (uint32_t reads = 0; maximumBufferReads == 0 || reads < maximumBufferReads; reads++)`); verify (standard, plus native builds); commit "Journal reads take a buffer bound".

**Gate:** green. **Depends on:** A1 (shared file). **Parallel with:** B1, C1. C1 owns the broker's 64 MB scan chunk.

### Task A2: Asynchronous diagnostics with channel tags

Implements amendment R10: a slow diagnostics write must not block any drive's frame path (today `LogFrame` runs inside the frame-write path, `JournalBrokerHost.cs:190-198`, and `Log` appends synchronously, `BrokerDiagnostics.cs:133-137`).

**Files:**
- Modify: `MFTLib/Broker/BrokerDiagnostics.cs`, callers `MFTLib/Broker/Host/JournalBrokerHost.cs` (`:192`, `:264`), `MFTLib/Broker/Client/JournalBrokerClient.Transport.cs` (its `LogFrame` and `Log` calls)
- Create: `MFTLib/Broker/BrokerDiagnosticsWriter.cs`
- Test: `MFTLib.Tests/BrokerDiagnosticsTests.cs`

**Interfaces produced:**

```csharp
internal const string ControlChannel = "control";
internal static string DriveChannel(char driveLetter, int sequence); // "C#3"
public static void Log(string channel, string message);             // line: "{utc:O}  [{role}:{pid}:{channel}]  {message}"
internal static void LogFrame(string channel, string direction, byte kind, int length);
internal static Task FlushForTestAsync(CancellationToken cancellationToken);
internal sealed class BrokerDiagnosticsWriter // one per process
{
    public const int Capacity = 8192;                       // records
    public BrokerDiagnosticsWriter(Action<string> appendLine); // production: File.AppendAllText to LogPath
    public bool TryEnqueue(string line);                    // never blocks
}
```

`Log` and `LogFrame` format the line (timestamp taken at the call) and `TryEnqueue` it into a bounded `Channel<string>` (`BoundedChannelFullMode.DropWrite`, single reader). One background task drains the channel and appends. Overflow policy: a record that finds the buffer full is dropped and counted; when the writer next appends, it first writes `[{role}:{pid}:diagnostics]  {count} records dropped: buffer full`. A failing append is counted the same way and never throws. The existing callers pass `BrokerDiagnostics.ControlChannel`; C1 and C2 pass real channel tags.

- [ ] **Step 1: Failing tests:** `Log_CarriesChannelTag`; `Log_ConcurrentWritersFromEightChannels_LoseNoLine` (eight tasks released on a `TestGate`, 200 lines each; after `FlushForTestAsync`, 1600 lines, each tag 200); `Log_BlockedSink_DoesNotBlockCaller` (the writer's `appendLine` waits on a `TestGate`; a second channel's 100 `Log` calls all return while the gate is closed); `Log_BufferFull_DropsAndReportsCount` (capacity reached behind a closed gate; after release the log holds a "records dropped" line with the right count); `Log_SinkThrowsThenRecovers_CountsFailureAndReportsOnNextAppend` (the writer's `appendLine` throws on the first two lines, then succeeds; no `Log` call throws; the next successful append is preceded by a "records dropped" line counting both failures).
- [ ] **Step 2: See them fail;** implement; verify (standard).
- [ ] **Step 3: Commit:** "Broker diagnostics tag every line and write through a bounded background queue".

**Gate:** green. **Depends on:** none.

### Task A3: Delete the scan session layer

**Files:**
- Delete: `MFTLib/Broker/Client/JournalBrokerScanSession.cs`, `.Rescan.cs`, `.Start.cs`, `.Watch.cs`, `MFTLib/Broker/Client/JournalBrokerSessionState.cs`, `MFTLibTestExtensions/ScanSessionTestHarness.cs`, `MFTLib.Tests/JournalBrokerScanSessionTests.cs` and its eight partials (`.BlockLifetime`, `.Connection`, `.CursorReplacement`, `.Rescan`, `.Start`, `.WarmStart`, `.Watch`, `.WatchTransitions`), `MFTLib.Tests/VolumeQueryScanSessionTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerScanResult.cs` (drop the `JournalBrokerScanSession.LatestScan` cref sentence), `MFTLibTestExtensions/MFTLibTestExtensions.csproj` (comment lines 5 to 9 name the session; describe the assembly as the consumer test harness), `MFTLib.Tests/MftProducerEndToEndTests.cs` (delete the cases that construct a `JournalBrokerScanSession`; keep the rest; list the deleted method names in the commit message for C4)

- [ ] **Step 1:** Grep each of `JournalBrokerScanSession`, `JournalBrokerSessionState`, `ScanSessionTestHarness` separately across `MFTLib`, `MFTLibTestExtensions`, `MFTLib.Tests`, `TestProgram`, `Benchmark`; the only hits must be the files above.
- [ ] **Step 2:** Delete and edit. No new tests: this task removes a feature with no production caller (spec section 4).
- [ ] **Step 3: Verify** build, whole suite, `aislop scan .`. Docs that describe the session are rewritten in D2.
- [ ] **Step 4: Commit:** "Delete JournalBrokerScanSession and ScanSessionTestHarness".

**Gate:** green. **Depends on:** none.

### Task A5: Delete the broker watch source

The old `BrokerIndexWatchSource` implements the old `IIndexWatchSource` (so B1 cannot compile with it) and calls the old `JournalBrokerClient` live-watch members (so C1 cannot compile with it). Deleting it once, before either trunk, means neither trunk touches the other's files; C6 rebuilds it over the new contract. Nothing inside MFTLib other than its own tests uses it (Grep `BrokerIndexWatchSource` and `CreateWatchSource` across `MFTLib`, `MFTLibTestExtensions`, `MFTLib.Tests`, `TestProgram`, `Benchmark`; the hits outside the files below are doc crefs in `MFTLib/Index/IIndexWatchSource.cs` and `FileIndex.Watch.cs`, which B1 rewrites).

**Files:**
- Delete: `MFTLib/Broker/Client/BrokerIndexWatchSource.cs`, `.PerDrive.cs`, `.AbandonedStart.cs`; `MFTLib.Tests/BrokerIndexWatchSourceTests.cs`, `BrokerIndexWatchSourceCaughtUpTests.cs`, `BrokerIndexWatchSourceFaultTests.cs`, `BrokerIndexWatchSourceArmingTests.cs`, `BrokerIndexWatchSourceArmingTests.LastDrive.cs`, `BrokerPerDriveArmTests.cs`, `BrokerPerDriveArmTests.Recovery.cs`, `BrokerWatchSourceAbandonedStartTeardownTests.cs`, `BrokerWatchStartSendCancellationTests.cs`, `BrokerFileIndexRescanTests.cs`, `MFTLib.Tests/Index/WatchFailureObservationTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` (remove `CreateWatchSource`)

- [ ] **Step 1:** Grep as above; delete and edit. No new tests: this removes a feature with no production caller inside MFTLib. C6 ports the deleted cases from the base commit (`git show c1d43784:<path>`).
- [ ] **Step 2: Verify** build, whole suite, `aislop scan .`.
- [ ] **Step 3: Commit:** "Delete the broker watch source". The commit message lists every deleted test file and that C6 ports it.

**Gate:** green (build, tests, aislop); the coverage comparison is made at the tranche end (see "Coverage tranches"). **Depends on:** none. **Parallel with:** A1, A2, A3.

---

## Phase B1 and C1: the two trunks begin (wave 2)

### Task B1: Per-drive watch contract and FileIndex state machine (Opus)

Opus because it builds the per-drive state machine (amendment SM, with R1, R2 and R4) that replaces the session pump, stop, catch-up and the rescan's watch interplay at once, and every one of those runs concurrently with the others.

This task is larger than the 600-line guideline (about 1100 lines of new production and support code, plus deletions). It cannot be split green: the contract change breaks every watch caller at once, and the only alternative is an adapter from the merged stream to per-drive handles.

**Files:**
- Create: `MFTLib/Index/IIndexDriveWatch.cs`, `MFTLib/Index/DriveWatchFaultException.cs`, `MFTLib/Index/FileIndex.DriveRuntime.cs`, `MFTLib/Index/FileIndex.WatchDrive.cs` (start, stop), `MFTLib.Tests/Index/FileIndexPerDriveWatchTests.cs`
- Rewrite: `MFTLib/Index/IIndexWatchSource.cs`, `MFTLib/Index/WatchStreamItem.cs`, `MFTLib/Index/JournalBatch.cs`, `MFTLib/Index/WatchFault.cs`, `MFTLib/Index/FileIndex.WatchPump.cs` (per-drive pump), `MFTLib/Index/FileIndex.WatchCatchUp.cs` (per-drive slots, single-drive wait only), `MFTLib/Index/WatchCatchUpState.cs` (slot stays; `CatchUpCoordinator` goes), `MFTLib/Index/FileIndex.Rescan.cs` (watch interplay: stop X, scan, commit, start X), `MFTLib/Index/FileIndex.Watch.cs`, `MFTLib/Index/FileIndex.WatchTargets.cs`, `MFTLib/Index/FileIndexOptions.cs` (`WatchSource` doc), `MFTLib.Tests/TestSupport/FakeIndexWatchSource.cs`, `MFTLib.Tests/TestSupport/WatchHarness.cs`
- Modify: `MFTLib/Index/FileIndex.cs` (drop `_watchSession`; add `_driveRuntimes`), `MFTLib/Index/FileIndex.Disposal.cs` (stop every drive's watch first, never throw a fault), `MFTLib/Index/DriveStatus.cs`, `FileIndex.Scanning.cs`, `BlockSource.cs` and `DriveFailureKind.cs` (doc comments that describe the session watch or rescan)
- Delete: `MFTLib/Index/ReadyOnFirstMoveWatchStream.cs`, `MFTLib/Index/WatchStreamNotRunningException.cs`, `MFTLib/Index/FileIndex.WatchSession.cs`, `MFTLib/Index/FileIndex.WatchStart.cs`, `MFTLib/Index/FileIndex.WatchFaults.cs`, `MFTLib/Index/FileIndex.Rescan.Watch.cs`
- Delete tests (ported by B2, B3, B4): `MFTLib.Tests/Index/FileIndexWatchTests.cs`, `FileIndexWatchPumpTests.cs`, `FileIndexWatchFaultTests.cs`, `FileIndexWatchCatchUpTests.cs`, `FileIndexWatchCatchUpLinkedWaitTests.cs`, `FileIndexWatchCatchUpRetentionTests.cs`, `FileIndexWatchRescanTests.cs`, `FileIndexWatchFailedRescanTests.cs`, `FileIndexWatchRescanFaultDuringProductionTests.cs`, `FileIndexWatchRescanCheckpointLossTests.cs`, `FileIndexWatchRecoveryFaultTests.cs`, `FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexMidSessionCheckpointLossTests.cs`, `ConsumerJournalIsolationTests.cs`, `FileIndexCheckpointLossDetectionTests.cs` (only if it no longer compiles; otherwise edit its `DriveWatchFailure` uses in place), `FileIndexWatchStartReadinessTests.cs`, `FileIndexWatchRescanEndedSessionTests.cs` (the last two are not ported: their subject is gone); `MFTLib.Tests/TestSupport/ReadinessScriptedWatchSource.cs` (the broker watch-source tests were deleted by A5)
- Modify tests that only need the new shape to compile: `Index/FileIndexRescanCleanupTests.cs` (keeps its `_swapGate` reflection until B5), any other file the build names

**Interfaces produced (public, from spec section 3):**

```csharp
namespace MFTLib.Index;
public interface IIndexWatchSource
{
    Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken);
}
// FileIndex disposes each handle exactly once (the pump's finally once the handle is published, the start path before that),
// and ReadAsync ends promptly when its token is cancelled.
public interface IIndexDriveWatch : IAsyncDisposable
{
    char DriveLetter { get; }
    IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken);
}
public abstract record WatchStreamItem { private protected WatchStreamItem() { } }
public sealed record JournalBatch(IReadOnlyList<UsnJournalEntry> Entries, ulong JournalId, long NextUsn) : WatchStreamItem;
public sealed record DriveCaughtUp : WatchStreamItem;
public sealed class DriveWatchFaultException : Exception
{
    public DriveWatchFaultException(char driveLetter, string message, Exception? innerException = null);
    public char DriveLetter { get; }
}
public enum WatchFaultKind { Subscriber, Drive, Apply, Channel } // CatchUpLost and Recovery are added by B5
public sealed record WatchFault(WatchFaultKind Kind, char DriveLetter, Exception Exception);

public sealed partial class FileIndex
{
    public Task StartWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task StopWatchingAsync(char driveLetter, CancellationToken cancellationToken);
    public Task WaitForCatchUpAsync(char driveLetter, CancellationToken cancellationToken);
    // The no-list forms StartWatchingAsync(CancellationToken), StopWatchingAsync(CancellationToken) and
    // WaitForCatchUpAsync(CancellationToken) are deleted here and return with batched results in B7.
}
```

**Internal shape:**

```csharp
sealed class DriveRuntime
{
    public DriveRuntime(char driveLetter);
    public char DriveLetter { get; }
    public SemaphoreSlim LifecycleGate { get; } = new(1, 1); // start, rescan, recovery (B6), disposal (B5)
    // Guarded by _stateLock:
    public WatchInstance? Current;
    public WatchInstance? Retiring;     // until its Drained completes
    public bool WatchRequested;
    public long NextGeneration;
}
sealed class WatchInstance
{
    public WatchInstance(char driveLetter, long generation, DriveBlock armedBlock, CancellationToken disposalToken);
    public long Generation { get; }
    public DriveBlock ArmedBlock { get; }
    public CancellationTokenSource StartCancellation { get; } // linked to the disposal token
    public CancellationTokenSource PumpStop { get; }
    public WatchInstanceState State;       // Starting, Running, Faulted, Retiring, Drained; under _stateLock
    public IIndexDriveWatch? Handle;       // under _stateLock
    public Task? Pump;                     // under _stateLock
    public bool SubscriberFaultAnnounced;
    public Exception? OutstandingFault;    // rethrown once by StopWatchingAsync(X)
    public WatchCatchUpSlot CatchUp { get; } = new();
    public Task Drained { get; }            // completes after the handle is disposed and the pump has returned
}
readonly Dictionary<char, DriveRuntime> _driveRuntimes; // one per configured drive, created in OpenAsync
```

Behavior: the "Per-drive state machine" section above (amendment SM) is the contract. B1 implements every row of its linearization table except recovery (B6) and the disposal gate order (B5), with these specifics:

- `StartWatchingAsync(X)`: `ArgumentException` for a letter not in the index; take X's lifecycle gate; `InvalidOperationException` when X has no MFT-backed block, or the existing `RecordUnresumableCheckpointWatchFailureLocked` message for a cache-only unresumable drive; succeed without restarting when `Current` is `Running`; await `Retiring?.Drained`; under `_stateLock` create the instance in `Starting`, register it as `Current`, set `WatchRequested`, clear X's failure message (R2: registration happens before the source is invoked); call `source.StartAsync(target, instance.StartCancellation.Token linked with the caller's token)` outside every lock; under `_stateLock`, if the instance is still `Current` and `Starting`, store the handle, move to `Running`, start the pump; otherwise dispose the handle outside the lock and throw `OperationCanceledException`. A throw from `StartAsync` records the failure message, faults the instance's slot, clears `Current`, completes `Drained`, and propagates.
- `StopWatchingAsync(X)`: no lifecycle gate. Under `_stateLock` clear `WatchRequested`; move `Current` to `Retiring` (cancel `StartCancellation` for a `Starting` instance, `PumpStop` for a running one) and take its outstanding fault; outside the lock await `Drained` bounded by the caller's token (the pump's `finally` is the only code that disposes a published handle: stop cancels `PumpStop` and `ReadAsync` returns when its token is cancelled); the slot is cancelled; rethrow the taken fault once with `ExceptionDispatchInfo`.
- Pump: `await foreach (var item in handle.ReadAsync(PumpStop.Token))`. A `JournalBatch` goes to `ApplyJournalEntriesCore(X, instance, ...)`, which after taking the gate (`_swapGate` in B1, X's write gate from B5) checks under `_stateLock` that the instance is still `Current`, `Running`, and `ArmedBlock` is X's published block, and drops the batch otherwise (R1); `RaiseChanged` runs with no gate held; a handler throw raises `WatchFaulted(Subscriber, X)` once per instance. `DriveCaughtUp` completes the instance's slot if the instance is still `Current`. Ends: `PumpStop` cancellation is a stop; `DriveWatchFaultException` is `Drive`; an apply throw is `Apply`; any other exception, or a normal end before the stop, is `Channel` ("The watch for drive X ended without being stopped."). A fault is recorded only if the instance is still `Current`: message, outstanding fault, slot faulted, state `Faulted`, `RecordCheckpointLossForFaultedDrive(X)` before `WatchFaulted` is raised. The handle is disposed and `Drained` completed on every exit. In B1 a `Drive` or `Apply` fault leaves X `Faulted`; B6 adds recovery.
- `WaitForCatchUpAsync(X)`: waits on the `Current` instance's slot: completes on catch-up, faults with its fault, cancelled by stop, rescan, or disposal. The caller's token and the disposal token reach the wait through a token registration that cancels a queued-continuation completion source, not through `Task.WaitAsync` (today `FileIndex.WatchCatchUp.cs:374` and `:388` use `Task.WaitAsync`), so the awaiter's continuation never runs inline on the stack that cancels the token (spec 2.6.8).
- `RescanAsync(X)`: takes X's lifecycle gate and holds it to the end (R4), then `_rescanGate` (removed in B5); retires `Current` and awaits `Drained` before production; production holds neither `_swapGate` nor `_stateLock`; after a committed replacement it restarts through `StartWatchingCoreAsync(runtime, gateHeld: true, ...)` if `WatchRequested` is still set; after a failed scan it applies today's rule (`FileIndex.Rescan.cs:145-158`): a drive that was healthy restarts from its old cursor, a faulted or unresumable one stays faulted. Everything in `FileIndex.Rescan.Watch.cs` (suspend, resume, reclaim, restart, unreported-fault ledger) is deleted, not ported.
- `DisposeAsync`: set `_disposed`, cancel the disposal token (which cancels every `StartCancellation`), then for every drive retire `Current` and await `Drained`; never throw a fault; then today's gate-and-release sequence (B5 replaces it).
- Asynchronous completion (spec 2.6.8): every completion source a handler can settle, directly (a fault it raises completes waiters and `Drained`) or through a token it cancels, is created with `TaskCreationOptions.RunContinuationsAsynchronously`: `WatchInstance.Drained`, the catch-up slot's `Waiter` and `FaultWaiter` (already so at the base commit, `WatchCatchUpState.cs:49` and `:56`), and the completion source `WaitForCatchUpAsync` cancels from its token registration. The promise is non-inline execution only: nothing about ordering relative to the handler's return and nothing about which thread. B1 adds the internal test hook `internal Action<Action>? PumpFaultSettlementWrapperForTest`, which the pump calls as `wrapper(settleFault)` around exactly the step that faults X's slot (before `WatchFaulted` is raised), so a test can hold a `[ThreadStatic]` flag for the duration of that step.

- [ ] **Step 1: Rewrite the test doubles.** `FakeIndexWatchSource`: `StartAsync(target)` returns a `ScriptedDriveWatch` per call and records the target; the test drives each handle with `Publish(WatchStreamItem)`, `FailDrive(Exception)` (the read throws `DriveWatchFaultException`), `LoseChannel(Exception)` (the read throws the given exception), `End()` (normal end), `HoldStart(TestGate)` (the start waits), and `FailStart(Exception)`; `DisposeCount`, `ThrowOnSecondDispose` (a second `DisposeAsync` throws, so a double dispose fails a test), `Starts` (targets in order). `WatchHarness`: builds a `FileIndex` over synthetic MFT blocks for letters `T`, `U`, `V` with a fake producer and the fake source, and exposes `Changes` and `Faults` collections.
- [ ] **Step 2: Failing tests** in `FileIndexPerDriveWatchTests` (each named, each asserting exactly this):
  - `StartWatching_OneDrive_StartsOnlyThatDrive`: start `T`; `Starts` is `[T at its block cursor]`; `DriveStatus(T).WatchCatchUp` is `CatchingUp`, `U` is `NotStarted`.
  - `StartWatching_AlreadyWatching_DoesNotRestart`: second call completes, `Starts.Count` is 1.
  - `StartWatching_SourceStartThrows_ThrowsAndFaultsSlot`: the exception propagates; `WatchCatchUp` is `Faulted`; `WatchFailureMessage` set.
  - `StopDuringStart_CancelsTheSourceStart` (R2): the fake's `StartAsync` waits for its token; `StopWatchingAsync(T)` cancels that token, the start throws `OperationCanceledException`, no pump runs, `Current` is empty.
  - `DisposeDuringStart_CancelsTheSourceStart_AndCompletes` (R2): same fake; `DisposeAsync` completes without the test releasing anything.
  - `StartReturnsAfterStop_HandleDisposedByStartPath`: the fake ignores its token and returns a handle after the stop; that handle's `DisposeCount` is 1 and no pump runs.
  - `RestartAfterStop_AwaitsOldDrain_BeforePublishing` (R1): `T`'s old pump is held inside `ApplyJournalEntriesCore` on a test seam gate; `StopWatchingAsync(T)` with an already-cancelled token returns; `StartWatchingAsync(T)` does not publish the new handle (`Starts.Count` stays 1) until the gate opens and the old instance drains.
  - `RetiringPumpBatch_IsDroppedNotApplied` (R1): a batch accepted by the old pump before its drain is not applied to the block the successor armed against (the block's generation and row are unchanged).
  - `OldInstanceFault_AfterRestart_DoesNotTouchNewInstance` (R1): the old handle throws after a restart; no `WatchFaulted` for it, the new instance's slot stays `CatchingUp`.
  - `Rescan_HoldsLifecycleGateThroughProduction` (R4): with `T`'s producer gated, a `StartWatchingAsync(T)` waits until the rescan finishes.
  - `Batch_AppliesAndRaisesChanged`: publish a batch on `T`; one `FileChange` observed; `T`'s block cursor advanced.
  - `DriveCaughtUp_CompletesWait`: `WaitForCatchUpAsync(T)` completes after `DriveCaughtUp`.
  - `DriveFault_RaisesDriveKindAndFaultsOnlyThatDrive`: `FailDrive` on `T`; one `WatchFault(Drive, 'T')`; `U` keeps applying a later batch.
  - `ApplyFailure_RaisesApplyKind`: a batch the mutator rejects raises `WatchFault(Apply, 'T')`.
  - `ChannelLoss_RaisesChannelKind`: `LoseChannel(new IOException())` raises `WatchFault(Channel, 'T')`, `WatchFailureMessage` set, catch-up `Faulted`.
  - `NormalEndBeforeStop_IsChannelFault`.
  - `SubscriberThrows_AnnouncedOncePerWatch_DriveKeepsWatching`: two batches, a throwing handler, one `Subscriber` fault, both batches applied.
  - `BlockedChangedHandlerOnT_DoesNotDelayU`: the handler for changes on `T` waits on a `TestGate`; a `U` batch is applied and observed before the gate opens (spec 9, row 2).
  - `Stop_RethrowsOutstandingFaultOnce`: after `FailDrive`, the first `StopWatchingAsync(T)` throws that exception, the second completes.
  - `Stop_DuringRescan_ReturnsWhileScanRuns_AndRescanDoesNotRestart`: `T` watching; the producer for `T` waits on a gate; `StopWatchingAsync(T)` completes while gated; release; the rescan commits and `Starts.Count` for `T` stays at 1 (spec 9, "Stop during rescan").
  - `Rescan_RestartsWatchFromFreshCursor`: after a rescan the second start of `T` carries the new block's cursor.
  - `Dispose_StopsEveryDriveAndNeverThrows`: two drives watching, one faulted; `DisposeAsync` completes without throwing; both handles disposed.
  - `HandleDisposedExactlyOnce_AfterStop`, `HandleDisposedExactlyOnce_AfterDriveFault`, `HandleDisposedExactlyOnce_WhenStartReturnsAfterStop` and `HandleDisposedExactlyOnce_AfterIndexDisposal`: each with a `ThrowOnSecondDispose` handle; `DisposeCount` is 1 and no exception surfaces (single ownership of the handle).
  - `WaitForCatchUp_CallerTokenCancelled_ThrowsOperationCanceled` and `WaitForCatchUp_DisposalTokenCancelled_ThrowsOperationCanceled`: the wait faults with `OperationCanceledException` through the token registration and the slot is unaffected.
  - `PumpFaultSettlesWaiter_ContinuationNotInline` (spec 9): a `WaitForCatchUpAsync(X)` is pending; X faults; `PumpFaultSettlementWrapperForTest` sets a test `[ThreadStatic]` flag for exactly the fault-settlement step and clears it after; the waiter's continuation records the flag on its own thread and then calls `StopWatchingAsync(Y)`; assert the recorded value is false and the stop completes. No thread ids, no ordering assertion, no sleeps.
  - `CacheOnlyUnresumable_StartThrowsWithRescanMessage`.
- [ ] **Step 3: See them fail**, then implement the types, runtime, start, stop, pump, catch-up, rescan interplay and disposal above; delete the listed files; fix every compile error the build names by rewriting the caller, not by restoring a member.
- [ ] **Step 4: Verify.** Targeted `FileIndexPerDriveWatchTests`; the namespace boundary test (`NamespaceBoundaryTests`) passes with the new types; `NativeSeamIsolationTests` passes; whole suite; `aislop scan .`.
- [ ] **Step 5: Commit:** "FileIndex watches each drive through its own handle and pump". The commit message lists every deleted test file and which task ports it.

**Gate:** green (the suite shrinks; B2, B3, B4 and C6 restore coverage). **Depends on:** A3, A5. **Parallel with:** A4, C1.

### Task C1: Wire protocol, channel host and parse-thread allocator (Opus)

Opus because the host becomes concurrent: requests dispatch to their own tasks, channels open through an owned handshake and live and die independently, the parse-thread allocator is shared by every scan, and control-pipe close must cancel every channel and bound the wait.

Larger than the guideline for the same reason as B1: the protocol change breaks the client and everything that wraps it, and the only alternative to deleting them here is a second protocol.

**Files:**
- Rewrite: `MFTLib/Broker/Protocol/BrokerFrame.cs`, `BrokerProtocol.cs`, `BrokerProtocol.Write.cs`, `MFTLib/Broker/Host/JournalBrokerHost.Session.cs` (control loop), `MFTLib/Broker/Host/JournalBrokerHost.Scan.cs` (one drive, no spec parsing), `MFTLib/Broker/Host/JournalBrokerHost.cs` (`StreamWatchAsync` for one channel, no arm epochs), `MFTLib/Broker/Host/JournalBrokerHost.VolumeQuery.cs` (singular), `JournalBrokerHost.GrowUsnJournal.cs` (request id), `MFTLib/Broker/Launch/IElevatedEntryRunner.cs`, `DefaultElevatedEntryRunner.cs`, `ElevatedEntryPoint.cs`, `MFTLib/Broker/Sources/MftRecordBatchSource.cs`, `JournalBatchSource.cs` and `UsnJournalCatchUpSource.cs` (new shapes below), `MFTLib/Broker/Host/JournalBrokerHost.Sources.cs`
- Create: `MFTLib/Broker/Host/JournalBrokerHost.Channel.cs` (serve one drive pipe), `MFTLib/Broker/Host/ParseThreadAllocator.cs`, `MFTLib/Broker/Sources/IBrokerOperationReporter.cs`, `MFTLib/Broker/Host/BrokerChannelConnector.cs` (the public delegate), `MFTLib/Broker/BrokerDriveLetter.cs` (internal `Normalize` and `TryNormalize`, moved from `JournalBrokerClient`), `MFTLib.Tests/TestSupport/InMemoryPipePair.cs`, `MFTLib.Tests/TestSupport/HostChannelHarness.cs` (runs `ServeAsync` over in-memory control and drive pipes and speaks raw frames), `MFTLib.Tests/JournalBrokerHostChannelTests.cs`, `MFTLib.Tests/JournalBrokerHostSourcesTests.cs`
- Delete: `MFTLib/Broker/Client/JournalBrokerClient*.cs` (all 12), `LiveWatchItem.cs`, `NtfsVolumeQueryResult.cs`, `BrokerScanResult.cs`, `BrokerMftBlockProducer.cs`, `BrokerProgressAdapter.cs` (C2 restores what it needs), `MFTLib/Broker/Host/ClientDisconnectedException.cs` if the control loop no longer needs it (keep only if control-reply failure still uses it)
- Delete tests (ported by C3a, C3b, C4, C6): `BrokerProtocolTests.cs` and partials `.ArmEpochFrames`, `.DisarmDriveFrame`, `.Frames`, `.GrowUsnJournalFrames`, `.Scan` (C3b rewrites), every `JournalBrokerHostTests*.cs`, `JournalBrokerHostRealSeamsTests*.cs`, `JournalBrokerHostBlockScanTests.cs`, `GrowUsnJournalHostTests.cs`, `VolumeQueryHostTests.cs` (C3a, C3b), every `JournalBrokerClientTests*.cs`, `GrowUsnJournalClientTests.cs`, `VolumeQueryClientTests.cs`, `BrokerDeathTests.cs`, `BrokerLiveWatchErrorTests.cs`, `BrokerMftBlockProducerTests.cs`, `BrokerMftBlockProducerProtocolTests.cs`, `BrokerBlockContractTests.cs`, `BrokerArmEpochDemuxTests.cs`, `BrokerArmOrderingTests.cs`, `MftProducerEndToEndTests.cs` (C4), `TestSupport/InProcessBlockBrokerHarness.cs`, `ScriptedWatchBrokerHarness.cs`, `WatchSpecArmEpochs.cs`, `SingleReaderGuardStream.cs`, `GateFrameWriteStream.cs`, `CancellableGateFrameWriteStream.cs`, `BrokerBlockTestBase.cs` (C4 rewrites what it needs)
- Modify: `MFTLib.Tests/UsnJournalSyntheticTests.Cancellation.cs` (calls `ServeAsync(server, writer, false, token)` at `:37`; move it to `HostChannelHarness`), `MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs`, `ElevatedEntryPointTests.cs` (C3b finishes them; here only what compiles), `scripts/coverage-linux.sh` (`:72` and `:82` name the renamed real-pipe runner test), `MFTLib/Mft/NtfsVolumeInformation.cs` (`:20` cref), `MFTLib/Index/JournalCheckpointLoss.cs` (`:114` doc mention)

**Wire (spec 2.2).** Framing unchanged: 4-byte little-endian length, kind byte, payload. `BrokerFrameKind` renumbered densely:

| Value | Kind | Pipe | Payload |
|---|---|---|---|
| 1 | `OpenChannel` | control, to host | RequestId, Drive, PipeName |
| 2 | `ChannelOpened` | control, to client | RequestId |
| 3 | `QueryVolume` | control, to host | RequestId, Drive |
| 4 | `VolumeInfo` | control, to client | RequestId, volume fields as today |
| 5 | `GrowUsnJournal` | control, to host | RequestId, Drive, MaximumSize, AllocationDelta |
| 6 | `UsnJournalSettings` | control, to client | RequestId, settings as today |
| 7 | `Error` | any, to client | RequestId (0 on a drive pipe), Message |
| 8 | `Heartbeat` | any, to client | none |
| 9 | `Stalled` | any, to client | Message |
| 10 | `ArmAndScan` | drive, to host | SectionName, Profile, KeepFileNames |
| 11 | `Cursor` | drive, to client | JournalId, NextUsn |
| 12 | `ScanProgress` | drive, to client | as today without drive |
| 13 | `CatchUpLost` | drive, to client | Loss (the proven `JournalCheckpointLoss` fields: Cause, CheckpointUsn, FirstUsn, NextUsn, AllocationDelta, MaximumSize, BytesBehind, SizeThatWouldHaveRetained), Message |
| 14 | `ScanReady` | drive, to client | RowCount, NamePoolUsedBytes, SkippedRecordCount |
| 15 | `JournalBatch` | drive, to client | JournalId, NextUsn, entries |
| 16 | `StartWatch` | drive, to host | JournalId, NextUsn |
| 17 | `CaughtUp` | drive, to client | none |

`BrokerFrame` loses `ArmEpoch`, `NoArmEpoch`, `DrivesSpec` and every drive field on drive-pipe kinds; gains `RequestId`, `PipeName`, `SectionName`. `StartWatch` drive lists, `DisarmDrive`, `EndWatch`, `EndWatchAck`, `Shutdown` and plural `QueryVolumes` are gone.

**Scan frame order (amendments R13 and L1):** `Cursor`, `ScanProgress`\*, `ScanReady`, then exactly one terminal frame: `JournalBatch` when the catch-up held, or `CatchUpLost` when it failed and the live journal proves the armed cursor lost; or `Error` at any point, including a catch-up failure the journal does not prove. `ScanReady` is written before catch-up runs (`JournalBrokerHost.Scan.cs:204-251` at the base commit, the catch-up at `:213-238`); C2's collector accepts exactly this.

**Interfaces produced:**

```csharp
public delegate Task<Stream> BrokerChannelConnector(string pipeName, CancellationToken cancellationToken);
public delegate IEnumerable<IReadOnlyList<MftRecord>> MftRecordBatchSource(string driveLetter, ParseThreadAllowance parseThreads,
    IBrokerOperationReporter operation, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken);
public delegate (UsnJournalEntry[] Entries, UsnJournalCursor Updated) UsnJournalCatchUpSource(
    string driveLetter, UsnJournalCursor since, int maximumBufferReads);
public delegate IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> JournalBatchSource(
    string driveLetter, UsnJournalCursor since, IBrokerOperationReporter operation, CancellationToken cancellationToken);

// Amendment R11: how a source tells the host's watchdog what it is doing.
public interface IBrokerOperationReporter
{
    void WaitingOnVolume();            // blocked in a volume read; heartbeats, never stalls
    void Processing(string step);      // working; restarts the processing clock
}

public sealed partial class JournalBrokerHost
{
    public JournalBrokerHost(UsnJournalCursorQuery queryCursor, MftRecordBatchSource scanDrive,
        UsnJournalCatchUpSource readJournal, JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null, GrowUsnJournalQuery? growUsnJournal = null,
        int? processorCount = null, TimeProvider? timeProvider = null); // null: Environment.ProcessorCount, TimeProvider.System
    public Task ServeAsync(Stream control, BrokerChannelConnector connectChannel,
        IBlockSectionWriter? blockSectionWriter, CancellationToken cancellationToken);
    internal static readonly TimeSpan ControlClosedGracePeriod = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ChannelConnectTimeout = TimeSpan.FromSeconds(30);   // R5
    internal static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(30);     // R5
}
internal sealed class ParseThreadAllocator // amendment S3
{
    public ParseThreadAllocator(int processorCount);
    public int RunningScanCount { get; }
    // Admits the scan while fewer than processorCount scans run, otherwise queues it in arrival order; a cancelled wait leaves the queue.
    public ValueTask<ParseThreadRegistration> AdmitAsync(CancellationToken cancellationToken);
}
internal sealed class ParseThreadRegistration : IDisposable
{
    public ParseThreadAllowance Allowance { get; } // written by the allocator for as long as the scan runs
    public void Dispose();                         // ends the registration once, rebalances the remaining scans, admits queued ones
}
public interface IElevatedEntryRunner { void RunBroker(string? controlPipeName); }
```

The allocator's rule (S3): with `r` running scans and `P` processors (`r <= P` by admission), the scan at admission position `k` (from 0) has an allowance of `P / r`, plus 1 when `k < P % r`; every allowance is at least 1 and the allowances sum to `P`. Every admission and every `Dispose` recomputes the split under one lock and writes each running scan's `ParseThreadAllowance.Count`; the native parser reads it at its next chunk (A1). Nothing computes a share anywhere else.

The production sources report through `IBrokerOperationReporter`: `WatchAndDisposeAsync` reports `WaitingOnVolume` before each `MoveNextAsync` of the native watch and `Processing("journal batch")` after each batch; `ScanDriveRecordBatches` reports `WaitingOnVolume` around the volume open and `Processing` per native progress callback. It passes the scan's `ParseThreadAllowance` and the channel token to `MftVolume.ReadRecordBatches` (A1), which is what lets the rebalance reach the native parser. In C1 the reporter only records the state; C5 wires it to the watchdog.

Host behavior (spec 2.1, 2.3, with amendments S3, R5, R7, R11, L1):

- `ServeAsync` reads control frames; each request runs on its own task; control writes take a control-only `SemaphoreSlim`. A control reply write that finds the pipe gone ends the session; a drive-pipe write that finds its pipe gone ends only that channel quietly (today's `TryWriteFrameAsync`, `JournalBrokerHost.cs:216-229`).
- `OpenChannel` (R5, host half): `connectChannel(pipeName, token)` bounded by `ChannelConnectTimeout` on the injected clock; on success reply `ChannelOpened` and serve the channel on its own tracked task; on failure or timeout reply `Error` with the request id and dispose whatever was connected. The served channel waits for its first request bounded by `FirstRequestTimeout`, then disposes and ends if none arrives.
- A channel reads exactly one request frame (`ArmAndScan` or `StartWatch`; anything else is `Error` and close). A reader task then reads until EOF and cancels the channel token; the operation's completion cancels and joins that reader task.
- Scan (S3): `AdmitAsync(channelToken)` on the host's `ParseThreadAllocator` (a closed pipe leaves the queue), then today's pipeline for one drive with `parseThreads = registration.Allowance`. The registration, the volume and the section writer are held until the native parse has returned (R7: A1's cancellation makes that prompt), then released in that order; teardown never returns the scan's share to the others while native work runs. Frame order per R13.
- Scan chunk: `ScanDriveRecordBatches` queries `NtfsVolumeInformation.Query(drive).BytesPerFileRecordSegment` (the host scan is Windows-only) and opens the volume with the existing `MftVolume.Open(string, uint bufferSizeRecords)` overload, passing `HostScanChunkRecords(...)`, so a progress callback lands at least every 64 MB read. `internal const long HostScanChunkBytes = 64L * 1024 * 1024;` and `internal static uint HostScanChunkRecords(long bytesPerFileRecordSegment)` (`max(1, HostScanChunkBytes / size)`) live in `JournalBrokerHost.Sources.cs`.
- Lost catch-up, proven by the journal (L1, spec 2.3): a catch-up failure other than `OperationCanceledException` (the `catch ... when` at `JournalBrokerHost.Scan.cs:228`) is not classified from the exception, because `MftVolume.ReadUsnJournal` throws one `InvalidOperationException` for a null native result and for any native error text (`MFTLib/Journal/MftVolume.Journal.cs:76-88`). The host runs `JournalCheckpointCheck.Check(drive, armed.JournalId, armed.NextUsn, JournalCheckpointLossDetection.ScanCatchUp)` against the live journal, from the armed cursor of the `Cursor` frame. A returned loss: the host writes `CatchUpLost` carrying that loss and the failure message, and closes. A null answer (the cursor is still retained, or the journal cannot answer): the host writes `Error` with the failure message and closes. In neither case does it re-query the cursor (`:230`), substitute an empty batch (`:236-237`) or write a `JournalBatch`. The block that `ScanReady` announced is complete; the client delivers it after `CatchUpLost` and disposes it after `Error` (C2). The index trusts the frame and does not query the journal again.
- Watch: `StreamWatchAsync` with no epoch; `DescribeWatchFailure` keeps its wording. The host closes its end after a terminal frame.
- Control EOF: cancel every channel, wait for their tasks up to `ControlClosedGracePeriod` on the injected `TimeProvider`, return.
- Diagnostics: control frames log under `BrokerDiagnostics.ControlChannel`, drive frames under `BrokerDiagnostics.DriveChannel(drive, sequence)`; the log filter stays per drive (`BrokerDiagnostics.CreateLogFilter`).
- `DefaultElevatedEntryRunner.RunBroker(controlPipeName)`: connect the control `NamedPipeClientStream`; the connector opens a `NamedPipeClientStream(".", pipeName, InOut, Asynchronous)` and `ConnectAsync(token)`; `ServeAsync(control, connector, new RealBlockSectionWriter(), CancellationToken.None)`; exit 0. `ElevatedEntryPoint.TryHandle` drops `--once`.

- [ ] **Step 1: Failing tests** in `JournalBrokerHostChannelTests` (through `HostChannelHarness`, fake sources, `FakeTimeProvider`):
  - `QueryVolume_RepliesWithRequestId`; `GrowUsnJournal_RepliesWithRequestId`; `QueryVolume_SourceThrows_RepliesErrorWithRequestId`.
  - `ControlRequests_RunConcurrently`: a `QueryVolume` whose source waits on a gate does not delay a second `QueryVolume` reply.
  - `OpenChannel_ConnectsNamedPipeAndReplies`.
  - `ScanChannel_EmitsCursorProgressReadyAndCatchUpInOrderThenCloses`.
  - `ScanChannel_CatchUpFailsAndJournalProvesLoss_EmitsScanReadyThenCatchUpLostAndCloses` (R13, L1): the catch-up source throws `IOException`; the synthetic journal window (journal id 7 armed at next USN 1000; window journal id 7, first USN 5000, next USN 9000, allocation delta 4096, maximum size 32768, supplied through the internal journal override) proves the loss; the frames are `Cursor`, `ScanProgress`\*, `ScanReady`, `CatchUpLost` whose loss has `Cause` `CheckpointTrimmed`, `BytesBehind` 4000 and `SizeThatWouldHaveRetained` 12288, then EOF; no `JournalBatch`; the cursor query was called once.
  - `ScanChannel_CatchUpFailsAndCursorStillRetained_EmitsErrorAfterScanReady` (first USN 500), `ScanChannel_CatchUpFailsAndJournalCannotAnswer_EmitsErrorAfterScanReady` (the override answers null) and `ScanChannel_CatchUpFailsAndJournalRecreated_CatchUpLostHasNoSize` (window journal id 8: `Cause` `JournalRecreated`, `SizeThatWouldHaveRetained` null) (L1).
  - `ScanChannel_CatchUpCancelled_WritesNoCatchUpLost` (L1): an `OperationCanceledException` from the catch-up source ends the channel without a `CatchUpLost` frame.
  - `ScanChannel_AllowanceReachesSource` (S3): host `processorCount: 8`, one scan; the fake source receives a `ParseThreadAllowance` whose `Count` is 8.
  - `ParseThreadAllocator_TwoScans_DivideProcessors` (S3): `processorCount: 8` gives 4 and 4; three scans on 8 give 3, 3 and 2 in admission order (remainder to the earliest); the allowances always sum to 8.
  - `ParseThreadAllocator_ScanStarts_RunningScanIsReducedWithoutItsCooperation` (S3): one scan on `processorCount: 4` holds 4; a second is admitted and the first's `Allowance.Count` reads 2 with the first scan's source doing nothing.
  - `ParseThreadAllocator_ScanEndsCancelsOrFails_RemainingScansAreRaised` (S3): two scans at 2 and 2; the first ends by completing, then (separately) by cancellation, then by its source throwing; each time the other reads 4.
  - `ParseThreadAllocator_NeverOversubscribedAtRest` (S3): for every `processorCount` from 1 to 8 and every scan count from 1 to 12, a fixed sequence of admissions and disposals; after each step the running count is at most `processorCount`, every allowance is at least 1 and the allowances sum to `processorCount` (no random input).
  - `ParseThreadAllocator_MoreScansThanProcessors_ExtrasQueueInArrivalOrder` (S3): `processorCount: 2`, three scans; the third's source is not entered and each running scan holds 1; when the first ends the third is admitted with 1 and the queue order is preserved for a fourth.
  - `QueuedScan_PipeClosed_LeavesQueueAndSourceNeverRuns` (S3): the queued channel's source is never invoked; a later scan is admitted.
  - `ConcurrentScans_TwoChannels_BothInsideSourceAtOnce`: `processorCount: 4`; both entry signals arrive before either gate opens and the two allowances read 2 and 2.
  - `ScanChannel_PipeClosed_ShareReturnsOnlyAfterSourceReturns` (R7): the fake source ignores cancellation until a gate opens; closing the pipe does not raise the other scan's allowance, and a queued scan stays queued, until the gate opens.
  - `ScanChannel_PipeClosed_CancelsOnlyThatScan`: the other channel's scan completes.
  - `OpenChannel_ConnectorNeverConnects_RepliesErrorAfterTimeout` (R5), `OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout` (R5), `OpenChannel_ConnectorThrows_RepliesErrorWithRequestId` (R5).
  - `WatchChannel_StreamsBatchesAndCaughtUp_NoDriveFields`.
  - `WatchChannel_SourceThrows_WritesErrorAndCloses`.
  - `WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected`.
  - `ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod`: two watch channels blocked in their sources; close control; `ServeAsync` returns after the fake clock advances past the grace period even if a source ignores cancellation.
  - `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound`: a `QueryVolume` reply write throws `IOException`; two watch channels blocked in sources are cancelled; `ServeAsync` returns once the fake clock passes `ControlClosedGracePeriod` even if a source ignores cancellation.
  - `HostScanChunk_OneKilobyteRecords_Is65536` and `HostScanChunk_FourKilobyteRecords_Is16384` (in `JournalBrokerHostSourcesTests`).
  - `DriveChannel_UnknownFirstFrame_WritesErrorAndCloses`.
- [ ] **Step 2: See them fail;** implement; delete and edit the listed files; the build names every remaining caller of a deleted member; rewrite or delete it per the lists above.
- [ ] **Step 3: Verify.** Targeted `JournalBrokerHostChannelTests`; `bash scripts/coverage-linux.sh` locally is not required, but check the filter lines in `scripts/coverage-linux.sh` name only test methods that exist (Grep); whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "Broker host serves a control pipe and one pipe per drive operation". List deleted test files and their porting tasks.

**Gate:** green (the client, producer and broker watch source are absent until C2 and C6). **Depends on:** A1, A2, A3, A5. **Parallel with:** A4, B1.

---

## Wave 3: ports and the client

### Task B2: Port index watch tests: watch, pump, faults, catch-up

**Files:** Create `MFTLib.Tests/Index/FileIndexWatchTests.cs`, `FileIndexWatchPumpTests.cs`, `FileIndexWatchFaultTests.cs`, `FileIndexWatchCatchUpTests.cs`, `FileIndexWatchCatchUpLinkedWaitTests.cs` from their base-commit versions (`git show c1d43784:MFTLib.Tests/Index/<file>`). `FileIndexWatchCatchUpRetentionTests.cs` is not ported: its subject (`CatchUpCoordinator` collection, `_catchUpCoordinatorCreatedForTest`) is gone; B7 covers the batched wait.

Port every case that still describes the per-drive contract onto `FakeIndexWatchSource` and `WatchHarness`: a `DriveWatchFailure` item becomes `FailDrive`; `WatchFaultKind.Source` with a drive becomes `Drive` or `Channel` by what the case exercises; a null-drive case is dropped. Session, readiness, reclaim and ledger cases are dropped. The commit message lists every dropped method and why.

- [ ] Port; run each file's tests; whole suite; `aislop scan .`; commit "Port FileIndex watch, pump, fault and catch-up tests to per-drive watches".

**Gate:** green. **Depends on:** B1. **Parallel with:** B3, B4, C2, C3a, C3b.

### Task B3: Port index watch tests: rescan interplay

**Files:** Create `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs`, `FileIndexWatchFailedRescanTests.cs`, `FileIndexWatchRescanFaultDuringProductionTests.cs`, `FileIndexWatchRescanCheckpointLossTests.cs`, `FileIndexWatchRecoveryFaultTests.cs` from their base-commit versions. The rescan-recovers-a-faulted-drive cases in `FileIndexWatchRecoveryFaultTests` become manual `RescanAsync(X)` cases now; B6 extends them with automatic recovery. `_swapGate` reflection (`FileIndexWatchRescanTests.cs:302`, `FileIndexWatchFailedRescanTests.cs:139`) stays until B5 replaces it.

- [ ] Port with the same rules as B2; add `Rescan_OfT_LeavesUsPumpRunning` (U applies a batch while T's producer is gated: spec 9, "Rescan of X leaves Y's pump running"); commit "Port FileIndex rescan-while-watching tests".

**Gate:** green. **Depends on:** B1.

### Task B4: Port index watch tests: checkpoint loss and isolation

**Files:** Create `MFTLib.Tests/Index/FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexMidSessionCheckpointLossTests.cs`, `ConsumerJournalIsolationTests.cs`, and `FileIndexCheckpointLossDetectionTests.cs` if B1 deleted it, from their base-commit versions. `ConsumerJournalIsolationTests` must still prove that `JournalIsolation.OverrideJournalWindow` drives a watch-fault classification; the callback may now run on any drive's pump thread concurrently.

- [ ] Port; add `WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised` if the ported set does not already pin it; commit.

**Gate:** green. **Depends on:** B1.

### Task C2: BrokerProcess client, scan channels, BrokerTestHarness

**Files:**
- Create: `MFTLib/Broker/Client/BrokerProcess.cs` (fields, events, dispose), `BrokerProcess.Launch.cs`, `BrokerProcess.Control.cs` (control reader, request ids), `BrokerProcess.Channels.cs` (open a drive pipe), `BrokerProcess.Scan.cs`, `MFTLib/Broker/Client/BrokerFrameReader.cs` (reads frames off one pipe; C7 adds the stall limit here), `BrokerChannelLostException.cs`, `BrokerDriveScanResult.cs`, `BrokerBlockSectionFactory.cs`, `BrokerPipes.cs` (internal `IBrokerPipeFactory`, `BrokerPipeListener`, `NamedPipeBrokerPipeFactory`), `MFTLib/Broker/Client/BrokerMftBlockProducer.cs` (scan half), `BrokerProgressAdapter.cs` (restored for one drive), `MFTLibTestExtensions/BrokerTestHarness.cs`, `MFTLibTestExtensions/InMemoryBrokerPipes.cs`, `MFTLib.Tests/BrokerProcessTests.cs`, `MFTLib.Tests/BrokerProcessLaunchTests.cs`
- Modify: `MFTLib/Broker/Client/BrokerScanOptions.cs` (drop `BlockTargets`), `MftBlockCapacity.cs` (if its signature named the client), `MFTLib/Index/MftBlockProducer.cs` (`MftBlockProduceResult.CatchUpLoss`, an init property: set only with a loss the journal proved); create `MFTLibTestExtensions/BrokerTestHarnessOptions.cs`

**Interfaces produced (spec section 3, plus internals):**

```csharp
public sealed class BrokerProcess : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ControlReplyTimeout = TimeSpan.FromSeconds(30); // enforced by C7
    [SupportedOSPlatform("windows")] public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, CancellationToken cancellationToken);
    [SupportedOSPlatform("windows")] public static Task<BrokerProcess> LaunchAsync(Func<string, bool> launchBroker, TimeSpan connectTimeout, CancellationToken cancellationToken);
    public bool HasEnded { get; }
    public event Action<string>? Ended;          // fires once, from the control reader
    public Task<NtfsVolumeInformation> QueryVolumeAsync(char driveLetter, CancellationToken cancellationToken);
    public Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta, CancellationToken cancellationToken);
    public Task<BrokerDriveScanResult> ScanDriveAsync(char driveLetter, BlockScanTarget target, BrokerScanOptions options, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();

    internal BrokerProcess(Stream control, IBrokerPipeFactory pipes, BrokerBlockSectionFactory createBlockSection, TimeProvider timeProvider);
    internal Task<BrokerDriveChannel> OpenChannelAsync(char driveLetter, Action<ArrayBufferWriter<byte>> writeFirstRequest,
        CancellationToken cancellationToken);
}
internal interface IBrokerPipeFactory { BrokerPipeListener Listen(string pipeName); }
internal sealed class BrokerPipeListener : IAsyncDisposable
{
    public string PipeName { get; }
    public Task<Stream> WaitForConnectionAsync(CancellationToken cancellationToken);
}
internal sealed class BrokerDriveChannel : IAsyncDisposable // owns the pipe, the reader, the diagnostics tag
{
    public char DriveLetter { get; }
    public Task WriteAsync(Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken);
    public ValueTask<BrokerFrame?> ReadAsync(CancellationToken cancellationToken); // null at EOF
}
// AdvancedCursor is null and CatchUpEntries empty when CatchUpLoss is set. CatchUpLoss is the loss the host proved against the live journal (C1).
public sealed record BrokerDriveScanResult(char DriveLetter, UsnJournalCursor ArmedCursor,
    UsnJournalCursor? AdvancedCursor, IReadOnlyList<UsnJournalEntry> CatchUpEntries,
    JournalCheckpointLoss? CatchUpLoss, BlockScanOutcome Block);
public sealed record BrokerScanOptions
{
    public BrokerScanProfile Profile { get; init; } = BrokerScanProfile.Full;
    public IReadOnlyCollection<string>? KeepFileNames { get; init; }
    public IProgress<BrokerScanProgress>? Progress { get; init; }
}
public sealed class BrokerChannelLostException : IOException
{
    public BrokerChannelLostException(char? driveLetter, string message, Exception? innerException = null);
    public char? DriveLetter { get; }
}
public delegate (string SectionName, BlockFile Block, IDisposable Lifetime) BrokerBlockSectionFactory(
    char driveLetter, BlockFileCreateOptions options);
public sealed class BrokerMftBlockProducer
{
    public BrokerMftBlockProducer(Func<CancellationToken, Task<BrokerProcess>> connectAsync,
        BrokerScanOptions? scanOptions = null, Action<BrokerDriveScanResult>? scanCompleted = null);
    public MftBlockProducer CreateProducer();
    // CreateWatchSource returns in C6.
}
namespace MFTLib.Index;
// MftBlockProduceResult gains one init property (spec 2.6.6): a producer sets it only with a loss the journal proved.
//     public JournalCheckpointLoss? CatchUpLoss { get; init; }   // a proven loss only
namespace MFTLibTestExtensions;
public static class BrokerTestHarness
{
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection);
    public static BrokerProcess StartInProcess(JournalBrokerHost host, IBlockSectionWriter blockSectionWriter,
        BrokerBlockSectionFactory createBlockSection, BrokerTestHarnessOptions options);
}
// Amendment R11. The host's own clock and processor count go through the JournalBrokerHost constructor.
public sealed record BrokerTestHarnessOptions
{
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;        // client side: stall limit, reply timeouts
    public Func<string, Exception?>? FailConnection { get; init; }               // by pipe name: the host's connect throws this
    public Func<string, Task>? HoldWrites { get; init; }                         // by pipe name ("control" or a drive pipe): host writes wait on the returned task
}
```

`BrokerMftBlockProducer`'s `_connectAsync` field changes type here, from `Func<CancellationToken, Task<JournalBrokerClient>>` (`BrokerMftBlockProducer.cs:11` at the base commit) to `Func<CancellationToken, Task<BrokerProcess>>`; C6 restores `CreateWatchSource` over the same field.

Behavior (spec 2.1, 2.4, with amendments R5, R6, R12, R13, L1):

- Drive pipe names: `mftlib-broker-{guid}-{drive}-{sequence}`, the guid shared with the control pipe; the sequence is per process, starting at 1.
- Request ids (R12): allocated under the pending-request lock from a `uint` counter that skips 0 and every id still in the pending table (outstanding or abandoned); a full table (every nonzero id retained) throws `InvalidOperationException("No broker request id is free")`. An entry is removed when its reply arrives or when the process ends, never earlier. The allocator has two internal seams: `internal Func<uint>? StartingRequestIdForTest` so tests start near `uint.MaxValue`, and `internal uint? MaximumRequestIdForTest` so a test can shrink the id space to three ids instead of creating billions of live requests.
- Control writes (R6): a request's cancellation token is observed only before its frame starts writing. Once writing starts, the frame is finished under a process-owned token bounded by `ControlReplyTimeout`; if the write fails or times out, the control connection is ended loudly (`Ended` fires, every pending request fails with `BrokerChannelLostException(null, ...)`). Only a fully written request uses the late-reply discard rule when its caller stops waiting.
- `OpenChannelAsync` (R5, client half): one owned operation: create the listener, register the request id, write `OpenChannel`, then await the host connection and `ChannelOpened` in either order, then write the first request frame (the caller's `ArmAndScan` or `StartWatch`, passed in). Every failed branch (cancellation, `Error` reply, connection without acknowledgement, acknowledgement without connection within `ControlReplyTimeout`, first-write failure) disposes the listener and any connected stream, so the host observes EOF on whatever it holds; an `Error` reply throws `InvalidOperationException` with the host message; control loss throws `BrokerChannelLostException(drive, ...)`.
- Control reader: one task; replies complete the pending request whose id matches; a reply with no pending entry is dropped; `Heartbeat` is ignored here (C7 counts it for the stall limit); EOF or I/O error fails every pending request with `BrokerChannelLostException(null, ...)`, releases every retained entry, sets `HasEnded`, raises `Ended` once.
- `ScanDriveAsync`: `QueryVolumeAsync`, then today's `PrepareDriveBlock` (`JournalBrokerClient.BlockScan.cs:31-72` at the base commit), open a channel whose first frame is `ArmAndScan`, then read in the R13 order: `Cursor`, `ScanProgress`\* (to `options.Progress`), `ScanReady`, then one terminal frame; any other order is a protocol error (`BrokerChannelLostException`). A terminal `JournalBatch` closes the channel and returns the result with the block transferred to the caller. A terminal `CatchUpLost` (L1) closes the channel and returns the same result with the block transferred to the caller, `AdvancedCursor` null, `CatchUpEntries` empty and `CatchUpLoss` set to the proven loss; the block is complete, so the caller decides what to do with it. `BrokerMftBlockProducer.CreateProducer` copies the loss to `MftBlockProduceResult.CatchUpLoss`. An `Error` frame after `ScanReady` fails the scan like any other `Error`: the section is disposed and no block is returned. `Error` frame: `InvalidOperationException(message)`. EOF or I/O before the terminal frame: `BrokerChannelLostException(drive, ...)`. Cancellation: dispose the channel and the section, throw `OperationCanceledException`.
- `DisposeAsync`: close control (the host ends every channel), dispose open channels, wait for the control reader.
- `BrokerTestHarness.StartInProcess`: in-memory control pair plus an `InMemoryBrokerPipes` registry that implements both `IBrokerPipeFactory` (client side) and a `BrokerChannelConnector` (host side) by pipe name; runs `host.ServeAsync` on a background task; the returned process's disposal ends the host and awaits it.

- [ ] **Step 1: Failing tests** in `BrokerProcessTests` (in-process through the harness; fake host sources; `RecordingBlockSectionWriter` from `TestSupport`):
  - `QueryVolume_ReturnsVolumeInformation`; `GrowUsnJournal_ReturnsSettings`; `QueryVolume_HostError_ThrowsInvalidOperationWithMessage`.
  - `ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds` (spec 9, "Control request ids": the first query's source waits on a gate; cancel the caller; release; the next query returns its own answer).
  - `RequestIds_WrapAround_SkipZeroAndOutstanding` (R12): allocator started at `uint.MaxValue - 1` with id 1 still pending; the next ids are `uint.MaxValue`, then 2.
  - `RequestIds_SpaceExhausted_ThrowsInvalidOperationAndOtherRequestsUnaffected` (R12): `MaximumRequestIdForTest` 3 and three requests pending; the fourth throws `InvalidOperationException("No broker request id is free")`; releasing one reply lets a fifth succeed.
  - `RequestIds_ReleasedOnProcessEnd` (R12).
  - `ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds` (R6): `HoldWrites` holds the client's control write stream after the length prefix (through an in-memory stream wrapper); the caller cancels; the frame still completes when released; the next request is answered.
  - `ControlWrite_FailsMidFrame_EndsProcessLoudly` (R6): the wrapper throws after the length prefix; `Ended` fires; pending requests fail with `BrokerChannelLostException`.
  - `OpenChannel_CancelledBeforeHostConnects_HostSeesNoChannel` (R5), `OpenChannel_ConnectedButErrorReply_ClientDisposesStream_HostChannelEnds` (R5), `OpenChannel_FailConnection_ThrowsWithHostMessage` (R5), `OpenChannel_FirstRequestWriteFails_DisposesBothSides` (R5).
  - `ScanDrive_CatchUpLost_ReturnsBlockWithLossAndArmedCursor` (L1): the host's catch-up source throws and the synthetic window proves the loss; the result has the block, `ArmedCursor` equal to the `Cursor` frame, `AdvancedCursor` null, empty `CatchUpEntries` and a `CatchUpLoss` equal to the host's; the host channel ends and the section stays alive for the caller.
  - `ScanDrive_ErrorAfterScanReady_DisposesSectionAndReturnsNoBlock` (L1): the catch-up failure is not proven (cursor retained); the scan throws `InvalidOperationException` with the host message and the section is disposed.
  - `ScanDrive_CatchUpLostBeforeScanReady_IsProtocolError` and `ScanDrive_JournalBatchAfterCatchUpLost_IsProtocolError` (R13, L1).
  - `Producer_CatchUpLost_CopiesLossToProduceResult` (L1): through `BrokerMftBlockProducer.CreateProducer`; `MftBlockProduceResult.CatchUpLoss` equals the host's loss and the block is present.
  - `ScanDrive_ReturnsArmedAndAdvancedCursorsAndBlock`.
  - `ScanDrive_ReportsProgress`.
  - `ScanDrive_HostError_ThrowsInvalidOperation`.
  - `ScanDrive_Cancelled_ClosesChannelAndDisposesSection_SourceObservesCancellation`; `ScanDrive_CancelOne_OtherDriveCompletes` (spec 9, "Scan cancellation").
  - `ScanDrive_TwoDrivesConcurrently_BothComplete`.
  - `HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce`.
  - `Dispose_EndsHost`.
  - `LaunchAsync_LaunchDeclined_Throws`, `LaunchAsync_NeverConnects_TimesOut` (ported from `JournalBrokerClientTests.ConnectionAndWatchFailures.cs:12` and `:58` at the base commit; these open a real named pipe and are Windows-only).
  - In `BrokerProcessLaunchTests`, ported from `JournalBrokerClientTests.BlockSectionsAndProgress.cs:13-183` and `ConnectionAndWatchFailures.cs:30-139`: `LaunchAsync_DiagEnvVarSet_AppendsDiagFlag`, `LaunchAsync_BrokerDiagnosticsEnabledProgrammatically_AppendsDiagFlag`, `LaunchAsync_RelativeLogDirectory_ForwardsResolvedFullPath`, `LaunchAsync_IncludeSelfEnvVarSet_AppendsIncludeSelfFlag`, `LaunchAsync_IncludeSelfSetProgrammatically_AppendsIncludeSelfFlag`, `LaunchAsync_DiagEnvVarUnset_OmitsAllDiagnosticsFlags`, `LaunchAsync_EndToEnd_UsesRealPipeAndRealBlockSeams` (`:185`), `LaunchAsync_NullLaunchBroker_ThrowsArgumentNull`, `LaunchAsync_NegativeTimeout_ThrowsArgumentOutOfRange`, `LaunchAsync_DefaultTimeoutOverridden_TimesOut`, `LaunchAsync_CallerCancellationRequested_ThrowsOperationCanceled`, `DisposeAsync_ControlPipeAlreadyClosed_DoesNotThrow` and `DisposeAsync_CalledTwice_DoesNotThrow`.
- [ ] **Step 2: See them fail;** implement.
- [ ] **Step 3: Verify** targeted; whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "BrokerProcess owns the control pipe and scans each drive on its own channel".

**Gate:** green. **Depends on:** C1. **Parallel with:** B2, B3, B4, C3a, C3b.

### Task C3a: Port host tests: watch operation

**Files:** Create `MFTLib.Tests/JournalBrokerHostTests.Watch.cs`, `.WatchCatchUp.cs`, `.WatchRecovery.cs`, `.WatchFailureClassification.cs`, `.WatchDiagnosticsFilter.cs` from their base-commit versions, onto `HostChannelHarness` with one watch channel per drive. Cases about arm epochs, re-arm on one pipe, `DisarmDrive` and `EndWatch` are dropped (listed in the commit). `WatchFailureClassification` keeps `DescribeWatchFailure`'s rescan wording. These base-commit cases of `JournalBrokerHostTests.Watch.cs` must survive, rewritten for one watch channel each: `StartWatch_WatchSourceCompletesNaturally_EndsWithoutError` (`:228`, becomes `WatchChannel_SourceCompletesNaturally_ClosesWithoutError`: natural completion is not a fault and sends no `Error` frame), `ServeAsync_WatchBatchWriteHitsBrokenPipe_SessionEndsNormally` (`:256`), `ServeAsync_WatchFaultsWithBrokenPipe_ErrorFrameUnsendable_SessionEndsNormally` (`:299`) and `ServeAsync_LeadingCaughtUpWriteHitsBrokenPipe_SessionEndsNormally` (`:332`), which become `WatchChannel_BatchWriteHitsBrokenPipe_EndsQuietly`, `WatchChannel_FaultWithBrokenPipe_ErrorFrameUnsendable_EndsQuietly` and `WatchChannel_LeadingCaughtUpWriteHitsBrokenPipe_EndsQuietly`, each also asserting that a second channel and the control session keep running; and `StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching` and `StartWatch_NoWatchSourceConfigured_EmitsErrorFrame`. The four `ServeAsync_*` control-loop cases in that file (`:134-199`) belong to C3b.

- [ ] Port; commit "Port host watch tests to one channel per watch".

**Gate:** green. **Depends on:** C1.

### Task C3b: Port host tests: scan, control and entry point

**Files:** Create `MFTLib.Tests/BrokerProtocolTests.cs`, `.Frames.cs`, `.Scan.cs`, `.GrowUsnJournalFrames.cs` (round-trip every kind in the table in C1, including `RequestId` and the `CatchUpLost` kind with every loss field present and with `BytesBehind` and `SizeThatWouldHaveRetained` null, and the read rejection of an unknown kind), `JournalBrokerHostTests.cs`, `.RequestDisconnect.cs`, `.Scan.cs`, `.Progress.cs`, `.ControlLoop.cs` (new: the four `ServeAsync_*` control-loop cases from `JournalBrokerHostTests.Watch.cs:134-199` at the base commit, on `HostChannelHarness`), `JournalBrokerHostRealSeamsTests.cs`, `.Operations.cs`, `JournalBrokerHostBlockScanTests.cs`, `GrowUsnJournalHostTests.cs`, `VolumeQueryHostTests.cs` from their base-commit versions. Finish `DefaultElevatedEntryRunnerTests.cs` (the real-pipe test becomes `RunBroker_ValidControlPipe_ServesUntilControlCloses_ExitsWithCode0`; update both lines in `scripts/coverage-linux.sh`) and `ElevatedEntryPointTests.cs` (`--once` cases deleted; `RunBroker(controlPipeName)` recorded).

- [ ] Port; commit "Port host scan, control, protocol and entry-point tests".

**Gate:** green. **Depends on:** C1.

---

## Wave 4

### Task B5: Gate split, publish under _stateLock, lost catch-up, disposal order (Opus)

Opus because it removes both index-wide gates and changes lock order, blockless adoption (amendment R8) and disposal ordering (state machine, disposal row) together, and it adds the lost-catch-up retry bookkeeping (L1) that runs inside the scan step it restructures.

**Files:**
- Modify: `MFTLib/Index/FileIndex.cs` (delete `_rescanGate`, `_swapGate`), `FileIndex.DriveRuntime.cs` (add `WriteGate` and `ConsecutiveLostCatchUps`), `FileIndex.Watch.cs` (`ApplyJournalEntries` and `ApplyJournalEntriesCore` take X's write gate synchronously), `FileIndex.Rescan.cs` (lifecycle gate only; commit under X's write gate then `_stateLock`; rescan token linked to the disposal token; blockless adoption builds the `DriveBlock` and assigns its ordinal inside the publish step), `FileIndex.Scanning.cs` (`ProduceDriveBlockAsync` returns the produce result, including its `CatchUpLoss`, and the failure message; the `DriveBlock` is built by the caller), `FileIndex.ScanCleanup.cs` (`PublishSnapshot` runs inside `_stateLock` and owns `_retiredSnapshots` mutation), `FileIndex.Disposal.cs` (spec section 5 disposal), the doc remarks that name `_swapGate` (`FileIndex.cs:55`, `:80`, `:208`; `FileIndex.Watch.cs:233-244`, `:255`), `MFTLib/Index/JournalCheckpointLoss.cs` (add `ScanCatchUp` to `JournalCheckpointLossDetection`), `MFTLib/Index/WatchFault.cs` (add `CatchUpLost` and `Recovery` to `WatchFaultKind`), `MFTLib/Index/WatchCatchUpState.cs` (add `Recovering` between `CaughtUp` and `Faulted`), `MFTLib/Index/DriveStatus.cs` (`ConsecutiveLostCatchUps`; the remarks at `:136-146`: a `ScanCatchUp` report explains a fault the way `LiveWatch` does)
- Modify tests: `Index/FileIndexRescanCleanupTests.cs:323`, `Index/FileIndexWatchRescanTests.cs:302`, `Index/FileIndexWatchFailedRescanTests.cs:139` reflect on `_swapGate`; replace with an internal test seam `internal Task WaitForDriveWriteGateForTest(char driveLetter)` or with a gated producer, whichever the test needs
- Create: `MFTLib/Index/FileIndex.CatchUp.cs`, `MFTLib/Index/JournalCatchUpLostException.cs`, `MFTLib.Tests/Index/FileIndexConcurrentRescanTests.cs`, `MFTLib.Tests/Index/FileIndexDisposalOrderTests.cs`, `MFTLib.Tests/Index/FileIndexCatchUpLossTests.cs`

**Lock order (outermost first):** X's lifecycle gate; X's write gate; `_stateLock` (a `Lock`, never held across an await). No code holds two drives' lifecycle gates or write gates except disposal, which takes them in ascending drive-letter order. No code acquires a gate while holding `_stateLock`. `Changed` and `WatchFaulted` are raised with no write gate and no `_stateLock` held; a pump holds no gate at all when it raises, and the one raiser that holds a gate is a scan operation raising `WatchFaulted(CatchUpLost)` between attempts, which holds X's lifecycle gate (spec 5). Write this as the class remark on `FileIndex.DriveRuntime.cs`.

**Blockless adoption (amendment R8).** Before publication nothing is keyed by a tentative ordinal. Today the tentative ordinal (`FileIndex.Rescan.cs:252`) keys the producer failure message (`FileIndex.Scanning.cs:173-185`, consumed at `FileIndex.Rescan.cs:256-272`), and the open path writes discarded-block and checkpoint-loss state by ordinal before adoption (`FileIndex.Scanning.cs:31-39`, `:297-306`). B5 introduces a local `PendingDriveResult` (produce result, access-denied count, producer failure message, discarded-block reason, checkpoint loss, cache-slot state, unresumable flag, the result's `CatchUpLoss`) that `ProduceDriveBlockAsync` and the warm-start path return instead of writing shared dictionaries. The publish step (X's write gate, then `_stateLock`) assigns the ordinal (`_driveBlocks.Count` for a blockless drive, the existing ordinal otherwise), builds the `DriveBlock`, and writes every field of the pending result into the ordinal-keyed dictionaries together; a failed blockless scan writes its failure message onto the drive's blockless status, which is keyed by letter. The open path returns the same result type, which is what lets B9 open drives concurrently (ruling 13) without any ordinal collision.

**Lost catch-up (spec 2.6.6, owner ruling 7; amendment L1).** The signal is `MftBlockProduceResult.CatchUpLoss` (C2), a `JournalCheckpointLoss` detected during `ScanCatchUp` that the host proved against the live journal (C1); the index trusts it and never reads the journal for it. The block is a complete scan and is published like any other; the loss is what happens around the publish. At the base commit `ProduceDriveBlockAsync` (`FileIndex.Scanning.cs:154-189`) never sees a catch-up failure (the host turned it into a `Warning` frame, `JournalBrokerHost.Scan.cs:219-238`, which the producer ignored). B5's `PendingDriveResult` carries the result's `CatchUpLoss`.

- **State.** `DriveRuntime.ConsecutiveLostCatchUps`, guarded by `_stateLock`, is created at open and kept for the index's lifetime, so it spans every scan operation and watch instance of the drive. `DriveStatus.ConsecutiveLostCatchUps` reports it. `public const int FileIndex.LostCatchUpRecoveryLimit = 3`. `WatchFaultKind` gains `CatchUpLost` and `Recovery`, `WatchCatchUpState` gains `Recovering` (between `CaughtUp` and `Faulted`), `JournalCheckpointLossDetection` gains `ScanCatchUp`.
- **Publish step** (X's write gate, then `_stateLock`, with the rest of the pending result, R8). Count plus one for a lost catch-up, zero for a scan whose catch-up held, unchanged when the scan produced no block. A block whose catch-up was lost is marked unresumable in the same set that a cache-only open's unresumable block uses (`_cacheOnlyUnresumableCheckpointOrdinals`), so `StartWatchingAsync(X)` refuses it with a message that names the lost catch-ups and points at `RescanAsync`. The step records `CatchUpLoss` as X's `CheckpointLoss`; its `SizeThatWouldHaveRetained` is the existing `JournalSizeArithmetic` result from the armed cursor to the tip, null when the journal was recreated. No journal read happens in the index. A catch-up failure the journal did not prove reaches the index as a failed scan with no block (C2): the count is unchanged, no `ScanCatchUp` report is produced and no size is suggested.
- **Retry.** `FileIndex.CatchUp.cs` holds the loop every scan operation of X runs (the open's settle, `RescanAsync(X)`, a batched rescan's per-drive operation, a recovery): produce, publish. When a publish records a lost catch-up the operation publishes `Recovering` when `WatchRequested` is set, raises `WatchFaulted(CatchUpLost, X)` with a `JournalCatchUpLostException(driveLetter, consecutiveLostCatchUps, recoveryStopped, checkpointLoss, message)`, and, while the count is below `LostCatchUpRecoveryLimit`, scans X again at once while still holding X's lifecycle gate. A recovery (B6) re-checks `WatchRequested` before each further attempt and stops when it has been cleared. No delay, clock or timer is involved. Each attempt registers with the broker's allocator afresh and may queue behind other scans. During `OpenAsync` no handler can be subscribed, so the drive's status is the record.
- **The limit.** When the count reaches 3 or more the operation stops rescanning. The drive keeps its last block (queryable, unresumable); `WatchFailureMessage` and a faulted catch-up state carry the exception's message; that exception's `RecoveryStopped` is true; its message names the drive, the three losses and, when the report carries one, `SizeThatWouldHaveRetained` as the journal size to grow to (through `BrokerProcess.GrowUsnJournalAsync`). `RescanAsync(X)` throws that exception (the batched form reports it as `Failed`). `OpenAsync` does not throw for it, and X reads `Ready` with the report attached and its watch refused. `JournalCatchUpLostException` is a new public type in `MFTLib.Index`, so it reaches nothing in the flat `MFTLib` namespace.
- **Manual rescan.** A consumer's `RescanAsync(X)` is always honored whatever the count. With the count at or past the limit it makes exactly one attempt: success resets the count, clears the unresumable mark and restarts a requested watch; another loss raises the count and throws with no automatic retry. Below the limit it retries as above, continuing from the stored count.
- **Reports.** A commit made after a loss in the same operation (a retry or a recovery) keeps the report that the loss produced; a scan that is the first attempt of a consumer rescan and whose catch-up holds clears the report, as today (`FileIndex.Rescan.cs:207`); a `ScanCatchUp` report replaces an older one as the newer fact; a recovery keeps a `LiveWatch` report (B6).
- **Delivery.** `WatchFaulted(CatchUpLost, X)` is raised by the scan operation's thread with no write gate and no `_stateLock` held while the operation still holds X's lifecycle gate (spec 2.6.6 and 5). That is safe because B8 rejects, inside any handler of this index, every call that takes a lifecycle gate or waits for a pump. Every other fault is raised with no gate held.

**Instance-scoped batches.** B1's instance check inside `ApplyJournalEntriesCore` now runs under X's write gate; the commit of a new block for X also takes X's write gate, so a batch either lands before the commit on the old block or is dropped after it (state machine, R1).

- [ ] **Step 1: Failing tests:**
  - `ConcurrentRescans_BothProducersInsideAtOnce_BothCommit_SnapshotHoldsBoth` (spec 9, "Concurrent rescans").
  - `ConcurrentBlocklessAdoption_DistinctOrdinals_BothResolve` (spec 9): two cache-only-declined drives rescanned together; distinct `DriveOrdinal`s; `Snapshot.GetDriveBlock` resolves each to its own letter.
  - `ConcurrentBlocklessAdoption_OneFailsOneSucceeds_EachKeepsItsOwnStatus` (R8): `T`'s producer fails and `U`'s succeeds, both gated so they overlap; `T` reads `Failed` with `ProducerFailed` and its own message; `U` reads `Ready` with no failure message; neither message appears on the other drive, in either completion order (run both orders).
  - `ConcurrentBlocklessAdoption_BothFail_BothMessagesSurvive` (R8).
  - `BatchOnT_DoesNotWaitForUCommit`: `U`'s commit is held inside its write gate by a test seam; a `T` batch applies.
  - `PublishForU_MidBatchOnT_IsSafe`: a `T` batch holds `T`'s write gate on a gate inside the mutator seam; `U` commits and retires the snapshot; the `T` batch completes and its `FileChange` handles read correctly.
  - `ApplyJournalEntries_TakesOnlyItsDrivesWriteGate`.
  - `Dispose_DuringGatedRescans_CancelsThemAwaitsPumpsReleasesGates` (spec 9, "Disposal during rescans"): two rescans gated inside producers that observe their token; `DisposeAsync` completes, both rescans end with `OperationCanceledException`, no gate is left held (a later `WaitAsync(0)` on each succeeds through the test seam).
  - `Rescan_TokenLinkedToDisposal`.
  - The lost-catch-up cases (L1) live in `FileIndexCatchUpLossTests`. They use gated fake producers that set `CatchUpLoss`; the index reads no journal for it, so no journal window is needed here (C1 proves the loss against synthetic windows), and none reads or advances a clock, because no delay exists on this path. The standard loss: `CheckpointTrimmed`, `CheckpointUsn` 1000, `FirstUsn` 5000, `NextUsn` 9000, `AllocationDelta` 4096, `MaximumSize` 32768, `BytesBehind` 4000, `SizeThatWouldHaveRetained` 12288.
    - `Rescan_CatchUpLostOnce_RetriesAtOnceAndKeepsTheReport`: X watching; producer script lost, held; two producer calls; one `WatchFaulted(CatchUpLost, X)` with `ConsecutiveLostCatchUps` 1 and `RecoveryStopped` false; `Recovering` was visible in `Drives` to the handler; the count is 0 after the second publish; the watch starts from the second block's cursor; the `ScanCatchUp` report is kept.
    - `Rescan_CatchUpLostThreeTimes_StopsAfterThreeProducerCalls`: script lost, lost, lost, held; exactly three producer calls; the third fault has `RecoveryStopped`; X keeps its (third) block, queryable; `WatchCatchUp` is `Faulted`; `WatchFailureMessage` contains the drive letter, `3` and `12288`; `CheckpointLoss` is `ScanCatchUp` and `CheckpointTrimmed` with `BytesBehind` 4000 and `SizeThatWouldHaveRetained` 12288; `RescanAsync(X)` threw `JournalCatchUpLostException` with those values; `StartWatchingAsync(X)` is refused with a message naming `RescanAsync`; `DriveStatus.ConsecutiveLostCatchUps` is 3.
    - `Rescan_SuccessResetsTheCount`: two losses then a success: count 0; a later single loss retries rather than stopping.
    - `Rescan_ManualRescanAtTheLimit_MakesOneAttempt`: count 3; a rescan whose catch-up is lost calls the producer once, raises the count to 4 and throws; a rescan that succeeds resets the count, clears the unresumable mark and a start is accepted.
    - `CatchUpLostCount_SurvivesOperations`: a recovery loses two catch-ups and then its producer fails without a block (count stays 2); the next manual rescan loses once and stops at 3 with one producer call.
    - `CatchUpLost_JournalRecreated_ReportHasNoSuggestion`: the producer's loss has `Cause` `JournalRecreated` and no size; the message names no size.
    - `CatchUpFailureNotProven_ScanFailsWithNoBlock_CountUnchangedNoReport`: the producer throws `InvalidOperationException` (the client's outcome for an `Error` frame after `ScanReady`); the rescan throws it; the count is unchanged, there is no report and no `CatchUpLost` fault, and the old block is still published.
    - `CatchUpLost_ProducerFailure_IsNotCounted`: an `OperationCanceledException`, then an `InvalidOperationException`, from the producer leave the count unchanged and raise no `CatchUpLost` fault.
    - `CatchUpLost_CountsArePerDrive`: `T` loses three, `U` loses one then succeeds; `T` is stopped, `U` is `Ready` with count 0, and `U` applies batches and completes a rescan while `T` retries.
    - `CatchUpLost_ScanCatchUpReportReplacesLiveWatchReport`.
    - `CatchUpLost_HandlerRunsWithLifecycleGateHeld`: a handler that queues `RescanAsync(X)` with `Task.Run`, gated to start after the handler returns, observes it waiting for X's lifecycle gate until the operation ends (B8 covers the synchronous-wait case).
    - `CatchUpLost_LeavesNoBlockFileBehind`: each lost block that is superseded by a retry is released and its file is gone, so the cache directory holds only the final canonical block.
- [ ] **Step 2: See them fail;** implement; replace the `_swapGate` reflection in the three existing test files.
- [ ] **Step 3: Verify** targeted; the `FileIndex*` suites (`--filter FullyQualifiedName~Index`) because this touches every mutation path; whole suite; `aislop scan .`.
- [ ] **Step 4: Commit:** "Split FileIndex gates per drive, publish snapshots under the state lock and retry a drive three times after a lost catch-up".

**Gate:** green. **Depends on:** B2, B3, B4. **Parallel with:** C4, C5, C7.

### Task C4: Port client-side scan tests

**Files:** Create `MFTLib.Tests/BrokerMftBlockProducerTests.cs`, `BrokerMftBlockProducerProtocolTests.cs`, `BrokerBlockContractTests.cs`, `GrowUsnJournalClientTests.cs`, `VolumeQueryClientTests.cs` (singular query), `MftProducerEndToEndTests.cs`, `TestSupport/BrokerBlockTestBase.cs` from their base-commit versions onto `BrokerTestHarness`. `MftProducerEndToEndTests` also re-covers the session-dependent scenarios A3 listed, through `BrokerMftBlockProducer` and `FileIndex.RescanAsync`. `BrokerProcessTests.BlockSections.cs` ports `JournalBrokerClientTests.BlockSectionsAndProgress.cs` (section naming, capacity from volume information, lifetime aliasing), including `DisposeAsync_WithAScanStillInFlight_DisposesTheLeftoverBlockAndLifetime` (`:321`; now `BrokerProcess.DisposeAsync` with an open scan channel releases the unpublished block and its section lifetime), the section-disposal cases at `:236` and `:292`, and the progress cases at `:354` and `:396`; the drive-letter normalization cases at `JournalBrokerClientTests.BlockSectionsAndProgress.cs:472-499` become `BrokerDriveLetterTests.cs`.

- [ ] Port; commit "Port producer, block contract and control-request client tests to BrokerProcess".

**Gate:** green. **Depends on:** C2. **Parallel with:** B5, C5, C7.

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

### Task C7: Client liveness: stall limit and control reply timeouts

**Files:** Modify `MFTLib/Broker/Client/BrokerFrameReader.cs` (stall limit from `BrokerLiveness.StallLimit` on the injected `TimeProvider`: a pipe with no frame of any kind for that long is closed and the read throws `BrokerChannelLostException(drive, "No frame from the broker for 30 seconds")`), `BrokerProcess.Control.cs` (the control reader uses the same limit; a stall ends the process: `Ended` fires; each control request waits at most `ControlReplyTimeout` and then throws `TimeoutException`, leaving its id to be dropped when a late reply arrives), `BrokerProcess.Scan.cs` (a `Stalled` frame on a scan channel throws `BrokerChannelLostException(drive, hostMessage)`). Create `MFTLib.Tests/BrokerProcessLivenessTests.cs`.

- [ ] **Failing tests:** `ControlSilentPastStallLimit_EndsProcess`; `ControlHeartbeats_KeepProcessAlive`; `ScanChannelSilent_FailsScanWithChannelLost_OtherScanUnaffected`; `ScanProgressKeepsScanAlive`; `ScanChannelStalledFrame_FailsWithHostMessage`; `ControlReply_TimesOut_LateReplyDropped_NextRequestSucceeds`. See them fail; implement; verify; commit "BrokerProcess closes any pipe that goes silent past the stall limit".

**Gate:** green. **Depends on:** C2. **Parallel with:** B5, C4, C5.

---

## Wave 5

### Task B6: Automatic recovery (Opus)

Opus because a recovery rescan runs on the thread pool and races stop, a consumer rescan, disposal and a second fault on the same drive.

**Files:** Create `MFTLib/Index/FileIndex.Recovery.cs`, `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs`; modify `FileIndex.WatchPump.cs` (fault path), `FileIndex.Rescan.cs` (a recovery flavor that keeps a `LiveWatch` loss), `FileIndex.DriveRuntime.cs` (`RecoveryState`: `None`, `Recovering`, `RecoveredAwaitingCatchUp`), `FileIndex.Disposal.cs` (await recovery tasks after cancelling), `MFTLib.Tests/Index/FileIndexWatchRecoveryFaultTests.cs` and `FileIndexWatchFaultTests.cs` (cases whose expectation changes from "stays faulted" to "recovers"; each change named in the commit).

Behavior (spec 2.6 "Recovery", 2.7, with amendments R3 and L1): a `Drive` or `Apply` fault on the `Current` instance records the fault, runs `RecordCheckpointLossForFaultedDrive`, publishes `Recovering` under `_stateLock`, then raises `WatchFaulted(Drive or Apply)` (R3: the state is visible to a handler that reads `Drives`), then queues a recovery on the thread pool: a `RecoveryTicket(WatchInstance FailedInstance, DriveBlock FailedBlock)` stored on the runtime with its task and a cancellation source linked to disposal. The recovery takes X's lifecycle gate and then revalidates under `_stateLock`: `Current` is still `FailedInstance`, still `Faulted`, `FailedBlock` is still X's published block, `WatchRequested` is set, and the ticket is still the runtime's; otherwise it releases the gate and ends without scanning. A manual `RescanAsync(X)`, `StartWatchingAsync(X)`, `StopWatchingAsync(X)` or disposal clears the ticket (disposal also cancels and awaits it). A valid recovery runs the rescan body with the gate already held and its scan operation is B5's loop, which retries a lost catch-up itself (spec 2.6.6, limit 3 in a row on the drive) and re-checks `WatchRequested` before each further attempt; the recovery passes no thread count (S3); a successful commit keeps a `LiveWatch` checkpoint loss on the drive, then restarts X's watch. If the recovery scan fails, or X faults again before it next reaches `CaughtUp`, raise `WatchFaulted(Recovery, X)` and leave X `Faulted` with no further automatic recovery until the consumer calls `RescanAsync(X)` or `StartWatchingAsync(X)`. A lost catch-up inside a recovery is not a recovery failure until that limit; a recovery that stops on the third has raised `WatchFaulted(CatchUpLost)` with `RecoveryStopped` (B5) and raises no `Recovery` fault as well. `Channel` faults never recover. A `StopWatchingAsync(X)` during recovery clears `WatchRequested`, so the recovery commits and does not restart the watch. Pending `WaitForCatchUpAsync(X)` calls are cancelled by the recovery rescan as by any rescan. The recovery ticket's completion source is created with `RunContinuationsAsynchronously` (spec 2.6.8), and a test attaches a continuation to it that records the `[ThreadStatic]` flag (as in B1's `PumpFaultSettlesWaiter_ContinuationNotInline`) around the step that completes the ticket and asserts it is false.

- [ ] **Failing tests:** `DriveFault_RecoversByRescan_ReachesCaughtUp_LiveWatchLossSurvives` (spec 9); `ApplyFault_Recovers`; `SecondFaultBeforeCaughtUp_RaisesRecovery_NoSecondRescan` (spec 9); `RecoveryScanFails_RaisesRecovery_DriveFaulted`; `ChannelFault_NoRecovery`; `StopDuringRecovery_CommitsWithoutRestart`; `DisposeDuringRecovery_CancelsIt`; `ConsumerRescanAfterRecoveryFault_RestoresWatch`; `RecoveringState_ReportedInDriveStatus`; `RecoveringPublishedBeforeWatchFaultedRaised` (R3: a handler reading `Drives` sees `Recovering`); `QueuedRecovery_SupersededByManualRescan_DoesNotScanAgain` (R3: the recovery is held before its gate; a manual rescan completes; releasing the recovery invokes the producer zero more times); `QueuedRecovery_AfterStop_IsDropped` (R3); `ObsoleteRecoveryFailure_DoesNotFaultNewerWatch` (R3); `TwoDrivesFaultTogether_RecoverConcurrently` (ruling 13: both producers entered before either gate opens); and the lost-catch-up cases (L1; fake producers setting `CatchUpLoss` as in B5, no clock): `RecoveryRescan_LosesCatchUpTwiceThenSucceeds_ReachesCaughtUp_NoRecoveryFault` (script lost, lost, held inside one recovery; three producer calls; two `CatchUpLost` faults, neither with `RecoveryStopped`; no `Recovery` fault; the count resets), `RecoveryRescan_CatchUpLostThreeTimes_StopsWithRecoveryStopped_StaysFaulted_NoFurtherRecovery` (the faults are `Drive`, then `CatchUpLost` three times with `RecoveryStopped` on the third, and no `Recovery`; exactly three producer calls; polling `Drives` afterwards queues no fourth), `StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying` (`WatchRequested` re-checked before each further attempt), `CatchUpCounts_ArePerDrive_OtherDriveRecoversNormally`, `ManualRescanAfterCatchUpStop_RestoresWatchAndResetsCount`. See them fail; implement; verify with the `Index` filter, whole suite, `aislop scan .`; commit "A drive whose watch faults recovers by rescanning itself".

**Gate:** green. **Depends on:** B5. **Parallel with:** C6.

### Task B9: Concurrent open

Owner ruling 13. `FileIndex.OpenAsync` settles every configured drive concurrently; the broker's allocator bounds the threads across those scans and every other scan, and `FileIndex` passes no thread count.

**Files:** Modify `MFTLib/Index/FileIndex.cs` (`OpenAsync`), `MFTLib/Index/FileIndex.Scanning.cs` (`AddDriveAsync` returns B5's `PendingDriveResult` instead of adding to `_driveBlocks`; `AddDriveWithProgressAsync` goes), `MFTLib/Index/IndexDriveOpened.cs`, `MFTLib/Index/FileIndexOptions.cs` (`OpenProgress` doc); rewrite `MFTLib.Tests/Index/FileIndexOpenProgressTests.cs`; create `MFTLib/Index/EnumerationWalkLimit.cs`, `MFTLib.Tests/Index/FileIndexConcurrentOpenTests.cs`.

```csharp
public sealed record IndexDriveOpened
{
    public required char DriveLetter { get; init; }
    public required int SettledCount { get; init; }   // this drive was the SettledCount-th to settle, 1-based; replaces Ordinal
    public required int Total { get; init; }
    public required BlockSource BlockSource { get; init; }
    public required DriveState State { get; init; }
}
```

Behavior: `OpenAsync` starts one settle task per configured drive, each with the cancellation token; each MFT scan goes through B5's scan-operation loop and passes no thread count (S3), so a cold drive that loses its catch-up rescans itself up to three times in a row; after three it settles `Ready` (its last block, unresumable, count 3, the `ScanCatchUp` report attached) and `OpenAsync` does not throw (L1); it reports `OpenProgress` once, when it settles. Enumeration walks (`FileIndex.Scanning.cs:157-163`) are admitted through a process-wide limit of one walk per processor (`MFTLib/Index/EnumerationWalkLimit.cs`, with an internal size seam), so the limit on scans holds wherever drives are scanned (spec 2.6.7). Each task, when it settles, publishes its `PendingDriveResult` under `_stateLock` (ordinal assigned then, R8) and reports `OpenProgress` synchronously on its own thread with the next `SettledCount` (an `Interlocked.Increment`). `OpenAsync` awaits every task before it publishes the snapshot or throws: if any task threw (cancellation included), it waits for the rest to settle, releases every block already adopted (`ReleaseUnpublishedBlocks`) and every block a still-settling task produced, then throws the first failure (`OperationCanceledException` when cancelled). Block ordinals follow settle order; `Drives` and `DriveStatus` keep `FileIndexOptions.Drives` order because they are computed from the options (`FileIndex.cs:104-121`).

- [ ] **Failing tests:** `Open_TwoColdDrives_BothProducersInsideAtOnce`; `Open_ProgressReportsInSettleOrder_WithSettledCount` (`U` settles before `T` by gates; reports are `U` 1 of 2 then `T` 2 of 2); `Open_ProgressReportedFromSettlingThread` (the report arrives while the opening caller is still awaiting); `Open_CatchUpLostTwiceThenHolds_DriveReadyAndReportedOnce` (L1: script lost, lost, block; three producer calls; one `OpenProgress` for the drive; no block file left over); `Open_CatchUpLostThreeTimes_DriveReadyUnresumableWithReport_OpenDoesNotThrow` (L1: `Ready`, count 3, the report, the watch refused; the other drive `Ready`; synthetic journal windows, no clock); `Open_EnumerationWalks_NeverExceedTheWalkLimit` (size seam 1 with two enumeration drives: one walk at a time; `[DoNotParallelize]`, the limit is process-wide); `Open_OneDriveProducerFails_OtherSucceeds_EachStatusOwn` (R8); `Open_CancelledWhileGated_SettlesAllThenReleasesAndThrows` (no block left mapped; the cache files of warm-started drives remain); `Open_DrivesStatusOrderFollowsOptions`; `Open_WarmAndColdMix_WarmDoesNotWaitForCold`. Port `FileIndexOpenProgressTests` cases that asserted configured order onto settle order. See them fail; implement; verify with the `Index` filter, whole suite, `aislop scan .`; commit "FileIndex opens its drives concurrently and reports each as it settles".

**Gate:** green. **Depends on:** B5. **Parallel with:** B6, C6 (disjoint files: B6 does not touch `FileIndex.cs` or `FileIndex.Scanning.cs`).

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

## Wave 6

### Task B7: Batched entry points

**Files:** Create `MFTLib/Index/DriveOperationResult.cs`, `MFTLib/Index/FileIndex.Batched.cs`, `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs`; modify `FileIndex.Rescan.cs` (the single-drive rescan throws `InvalidOperationException` carrying `DriveStatus.MftProducerFailureMessage` when the producer returns no block, and throws the `JournalCatchUpLostException` after a lost-catch-up stop (B5)).

```csharp
public enum DriveOperationOutcome { Succeeded, Failed, NotApplicable }
public sealed record DriveOperationResult(char DriveLetter, DriveOperationOutcome Outcome, Exception? Failure);
public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(IReadOnlyList<char> driveLetters, CancellationToken cancellationToken);
public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(CancellationToken cancellationToken);
```

Contract: spec section 3, "Batched-call contract". No entry point passes a thread count: the broker's allocator divides the processors among whatever scans are running (S3), so a batched rescan of four drives and four single-drive rescans behave alike. `NotApplicable`: start on a drive with no MFT-backed block; stop and catch-up on a drive that is not watching. Stop's `Failed` carries the fault that had ended the drive's watch. The batched catch-up wait's completion source is created with `RunContinuationsAsynchronously`, and its cancellation comes from a token registration, not `Task.WaitAsync` (spec 2.6.8); B7 adds `BatchedWait_SettledByPumpFault_ContinuationNotInline` (B1's pump-fault row with a batched wait naming X pending, using B1's hook).

Also: audit every existing test that expects a failed rescan to complete normally, since the single-drive rescan now throws. Candidates found by Grep for `RescanAsync` at the base commit: `Index/FileIndexBlockReleaseTests.cs`, `FileIndexBlockSourceTests.cs`, `FileIndexCacheTagTests.cs`, `FileIndexCheckpointLossLifetimeTests.cs`, `FileIndexDisposalRaceTests.cs`, `FileIndexDriveStatusTests.cs`, `FileIndexLifetimeTests.cs`, `FileIndexOpenProgressTests.cs`, `FileIndexOwnerLockTests.cs`, `FileIndexProducerSelectionTests.cs`, `FileIndexQueryTests.cs`, `FileIndexRescanCleanupTests.cs`, `FileIndexResilienceTests.cs`, `.DeleteLogging.cs`, `SnapshotSwapTests.cs`, `CacheDirectoryDeletionLiveOwnerTests.cs`. Each case that relied on a silent failure asserts the `InvalidOperationException` and its message.

- [ ] **Failing tests:** `BatchedStart_PartialFailure_ReportsPerDrive_DoesNotThrow` (spec 9: source throws for `U`, an enumeration-backed `V`; `T Succeeded`, `U Failed` with that exception, `V NotApplicable`; `T` watches); `BatchedStart_CancelledWhileGated_ThrowsOnlyAfterEverySettles_NoHandlePublished` (spec 9); `Batched_DuplicateLetter_ThrowsArgumentBeforeStarting`; `Batched_UnknownLetter_Throws`; `Batched_Null_Throws`; `Batched_Disposed_ThrowsObjectDisposed`; `NoListForm_CoversDrivesInOptionsOrder`; `BatchedStop_ReturnsFaultAsFailed`; `BatchedWait_ReturnsPerDrive`; `BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OthersSucceed` (L1: `T`'s producer loses its catch-up three times and `U`'s holds; `T` is `Failed` with a `JournalCatchUpLostException` (`RecoveryStopped`, the journal size in its message), `U` is `Succeeded`; fake producers setting `CatchUpLoss`, no clock); `SingleRescan_CatchUpStop_ThrowsJournalCatchUpLostException` (L1); `SingleRescan_ProducerReturnsNoBlock_ThrowsWithFailureMessage`. See them fail; implement; verify with `NamespaceBoundaryTests`, whole suite, `aislop scan .`; commit "Batched FileIndex operations fan out per drive and return one result each".

**Gate:** green. **Depends on:** B6, B9, C6. **Parallel with:** C8.

### Task C8: Cross-drive liveness scenarios

**Files:** Create `MFTLib.Tests/BrokerCrossDriveLivenessTests.cs` (in-process `FileIndex` over `BrokerTestHarness` with `FakeTimeProvider` on both host and client).

- [ ] **Failing tests:** `OneDriveStallsWhileOthersFlow` (spec 9, row 1: `T`'s watch source never yields and its heartbeats stop because the test freezes `T`'s host pipe writer; `U` yields batches and catches up while `T` is stuck; advancing past the stall limit faults `T` with `Channel`; `U` untouched); `HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery` (spec 9); `IdleWatchOverBroker_StaysAliveUnderFakeClock`; `HostErrorOverBroker_RecoversByRescan` (extends C6's test now that B6 is merged); `CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops` (L1: in-process `FileIndex` over `BrokerTestHarness`, the fake clock on both sides never advanced, a synthetic journal window for `T` whose armed cursor is trimmed; the host's catch-up source throws on every call for `T` and holds for `U`; rescanning both, `T` opens exactly three scan channels, raises `WatchFaulted(CatchUpLost)` three times (`RecoveryStopped` on the third, and no `Recovery`), and keeps its third block, unresumable, with a `ScanCatchUp` report whose `SizeThatWouldHaveRetained` matches `JournalSizeArithmetic`, while `U` completes normally in the same run; then the source is fixed and `RescanAsync(T)` succeeds, resetting the count and accepting a start); `CatchUpLostOnceOverBroker_SecondScanSucceeds` (L1); `CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry` (L1: the window retains the armed cursor; the rescan throws, exactly one scan channel is opened, the count is unchanged and no report exists). Most of these pass on arrival if C5, C6, C7 and B6 are right; any that fails is a bug in those tasks and gets a regression test in the owning file. Commit "Cross-drive liveness scenarios over the broker".

**Gate:** green. **Depends on:** C5, C6, B6. **Parallel with:** B7.

## Wave 7

### Task B8: Callback reentrancy guard

Implements amendment R9 (spec 2.6.8). A `Changed` or `WatchFaulted` handler runs on a drive's pump, or on the scan operation that raised `WatchFaulted(CatchUpLost)` (B5). Delivery is concurrent across drives, so a deadlock needs no self-call: X's `Changed` handler blocks on `StopWatchingAsync(Y)` while Y's handler blocks on `StopWatchingAsync(X)`, each stop awaits the other pump's `Drained`, and neither handler can return (`FileIndex.Watch.cs:321-327`, `FileIndex.WatchPump.cs:316-320` at the base commit). The rule is therefore per index, not per drive.

**Files:** Create `MFTLib/Index/FileIndex.Reentrancy.cs`, `MFTLib.Tests/Index/FileIndexCallbackReentrancyTests.cs`; modify the raise sites (the pump's delivery and B5's `CatchUpLost` raise) and the entry points of `StopWatchingAsync`, `RescanAsync`, `StartWatchingAsync`, `DisposeAsync`, `WaitForCatchUpAsync` and every batched form of them.

- **Rejected from inside any callback of this index, whichever drive it names:** `StopWatchingAsync`, `RescanAsync`, `StartWatchingAsync` (a start takes a lifecycle gate and may await a previous instance's `Drained`), `DisposeAsync`, their batched forms, and an unsettled `WaitForCatchUpAsync` for any drive. Each checks synchronously at entry, before its first await, and fails with `InvalidOperationException("FileIndex.<Operation> was called from inside a Changed or WatchFaulted handler; it can wait for a watch pump that is blocked in a handler. Queue the call to run after the handler returns, for example with Task.Run.")`. The returned task is already faulted, so a handler that blocks on it gets the exception at once, and a fire-and-forget call also gets it on its task.
- **Allowed:** queries (`Find`, `FindByName`, `Search`, `Enumerate`, `Largest`, `DuplicateNames`, `Root`, `FileEntry` members), `Drives` and `DriveStatus`, `QueryUsnJournalSettings`, a `WaitForCatchUpAsync` that is already settled, and calls outside the index such as `BrokerProcess.GrowUsnJournalAsync`. None takes a lifecycle gate or waits for a pump.
- **Detection:** around every handler invocation the raiser sets an `AsyncLocal<DeliveryMarker?>` (`DeliveryMarker` names the `FileIndex` and holds a volatile `Active` flag) and clears `Active` in a `finally` when the invocation returns. The marker flows through awaits and into any continuation or `Task.Run` the handler starts, so a handler that blocks on an async helper which awaits and then calls `StopWatchingAsync` is still rejected. Work the handler queued that runs after it returned sees an inactive marker and is allowed, which is the remedy the message names. A check rejects only when the marker is non-null, names this index and is `Active`.
- **Asynchronous completion (spec 2.6.8):** the marker is only sound if no awaiter's continuation runs inline on the handler's stack (a continuation that does not flow its own context would see the active marker, and one that restores its own context would see none and could block on a lifecycle call on the pump's own thread, undetected). B8 audits every `TaskCompletionSource` in `MFTLib/Index` that a handler can settle directly or through a token it cancels (Grep `new TaskCompletionSource` and `new(TaskCreationOptions`): `Drained`, the catch-up slot's `Waiter` and `FaultWaiter`, the batched catch-up wait, the recovery ticket's completion (B1, B6, B7) and the snapshot borrow drain (`Snapshot.cs:341`, already created with the option) must each carry `RunContinuationsAsynchronously`, and every public wait must deliver token cancellation through a registration, never `Task.WaitAsync`. The promise is non-inline execution only.

- [ ] **Failing tests** (each caught inside the handler within a bounded `WaitAsync`, so a regression shows as a test timeout and not a hung suite):
  - `ChangedHandlerOnX_CallsStopOnY_ThrowsImmediately_YKeepsWatching`.
  - `TwoHandlerCycle_EachStopsTheOtherDrive_BothFailAtOnce`: X's and Y's `Changed` handlers are each released by a `TestGate` and then block on `StopWatchingAsync` of the other drive; both calls fail at once; both pumps continue and apply a later batch.
  - `WatchFaultedHandler_CallsRescanOfAnotherDrive_Throws`, `WatchFaultedHandler_CallsStartOfAnotherDrive_Throws`, `ChangedHandler_CallsDispose_Throws`, `ChangedHandler_CallsUnsettledWaitForCatchUp_Throws` and `ChangedHandler_CallsSettledWaitForCatchUp_ReturnsItsResult`.
  - `CatchUpLostHandler_CallsRescanOfAnotherDrive_Throws` (the handler runs on a scan operation, not a pump).
  - `MarkerSurvivesAwaitInsideHandler_StopStillRejected`: the handler blocks on an async helper that awaits a completed `TestGate` on the thread pool and then calls `StopWatchingAsync(Y)`.
  - `QueuedWorkAfterHandlerReturns_IsAllowed`: the handler queues `StopWatchingAsync(X)` with `Task.Run` gated to start after the handler returns; the stop succeeds and X drains.
  - `HandlerMayQueryAndGrow`: a handler runs `Search`, reads `Drives` and awaits `BrokerProcess.GrowUsnJournalAsync` through the harness; all succeed.
  - `HandlerCancelsWaitersToken_ContinuationNotInline` (spec 9): a `WaitForCatchUpAsync(Y)` is pending with a token from a test-owned source; X's `WatchFaulted` handler sets a test `[ThreadStatic]` flag, calls that source's synchronous `Cancel()`, and clears the flag; the awaiter's continuation records the flag on its own thread, then calls `DisposeAsync` on the index; assert the recorded value is false (an inline continuation on the handler's stack would read true) and that `DisposeAsync` completes. No thread ids, no ordering, no sleeps.
  - `StopCancellationSettlesOffTheHandlersStack`: a `StopWatchingAsync(Y)` is pending on Y's `Drained` with a token from a test-owned source; X's `Changed` handler sets the flag, cancels that source, and clears the flag; the stop's caller records the flag on its own thread in its `OperationCanceledException` handler, then calls `RescanAsync(Y)`; assert the recorded value is false and the rescan completes.
  - The pump's-fault row is B1's `PumpFaultSettlesWaiter_ContinuationNotInline` (it needs B1's hook); B8 re-runs it in the suite and adds nothing to it.
  - See them fail (the blocking ones hang without the guard, bounded by `WaitAsync`); implement; verify (standard); commit "FileIndex rejects a lifecycle call made from inside one of its own handlers".

**Gate:** green. **Depends on:** B7.

---

## Wave 8: documentation and verification

### Task D1: AGENTS.md and CHANGELOG

**Files:** `AGENTS.md`, `CHANGELOG.md`.

AGENTS.md paragraphs rewritten from the implementation (spec section 4 list, confirmed against the base commit):
- Architecture, MFTLib, "Checkpoint loss": keep the journal-read and `DetectedDuring` rules; replace everything from "The same check also runs mid-session, from `FileIndex.WatchPump`" to the end of the bullet (sessions, reclaim, restart, `_unreportedWatchFaults`, `ResumeDriveAfterRescanAsync`, PR 230 and 241 handling) with the per-drive fault, recovery and `Recovery` kind rules, the rule that a recovery rescan keeps a `LiveWatch` loss, and the lost-catch-up rule (proven by the journal on the host; `ScanCatchUp` detection, three consecutive lost catch-ups stop automatic recovery, a scan whose catch-up holds resets the count, the report carries the journal-size suggestion).
- "Watch start readiness": whole bullet, replaced by one sentence (`StartWatchingAsync(X)` returns once X's channel is connected and `StartWatch` is written).
- "Watch and catch-up lifetime": whole bullet, rewritten per drive with `Recovering`.
- "VolumeBroker": whole bullet, rewritten for `BrokerProcess`, control pipe, owned channel open, drive channels, the parse-thread allocator and its per-chunk rebalance, the `CatchUpLost` frame, heartbeats (control unconditional, independent per pipe), watchdog, stall limit, request ids.
- Add a "Per-drive state machine" bullet under FileIndex (watch instances, linearization points, recovery tickets, the reentrancy guard) from the plan's section of that name.
- Project list, "MFTLibTestExtensions": `BrokerTestHarness`.
- Test coverage, journal isolation: "may run on the watch-pump thread" becomes "may run on any drive's pump thread, concurrently for different drives".
- Add a "Lock order" bullet under FileIndex (spec section 5).

CHANGELOG "Unreleased": one entry per public change (every row of spec section 3; the deleted types; the frame renumbering; `RunBroker(string?)`; `MftRecordBatchSource` and `UsnJournalCatchUpSource` shapes; the thread allowance and cancellation on the native parse export; `ParseThreadAllowance`; the `CatchUpLost` frame; `JournalCatchUpLostException`; `MftBlockProduceResult.CatchUpLoss` and `BrokerDriveScanResult.CatchUpLoss`; `DriveStatus.ConsecutiveLostCatchUps`; `FileIndex.LostCatchUpRecoveryLimit`; `JournalCheckpointLossDetection.ScanCatchUp`; `WatchFaultKind.CatchUpLost` and `Recovery`; `WatchCatchUpState.Recovering`; `StreamRecords` and `ReadRecordBatches` shapes; `JournalBatchSource` and `IBrokerOperationReporter`; `BrokerTestHarnessOptions`; concurrent `OpenAsync` and `IndexDriveOpened.SettledCount`; `BlockFile.Flush` and `BlockWriter.Complete`; issue 252 closed). Describe what exists; no history notes.

- [ ] Write; grep AGENTS.md and CHANGELOG.md for every deleted identifier (`JournalBrokerClient`, `JournalBrokerScanSession`, `ScanSessionTestHarness`, `WatchStreamNotRunningException`, `DriveWatchFailure`, `ReadyOnFirstMoveWatchStream`, `WatchSession`, `_swapGate`, `_rescanGate`, `ArmEpoch`, `EndWatchAck`, `SendStartWatchAsync`, `StopLiveWatchAsync`, `QueryVolumesAsync`, `ArmScanAndCatchUpAsync`, `BrokerScanResult`, `BrokerDied`, `WriteWarning`), one search per name; only CHANGELOG deletion entries may name them. Commit.

### Task D2: Broker docs

**Files:** `docs/broker-integration.md` (entirely), `docs/broker-testing.md` (`BrokerTestHarness`, per-drive fake `IIndexWatchSource`), `docs/broker-scan-tuning.md` ("Customizing watch cursors: ReplaceWatchCursors and WatchCursors" is a session section and goes; sizing the block stays and gains the per-chunk thread rebalance and the lost-catch-up rule, which tells the consumer to grow the journal and rescan), `docs/handoff-release-0.3.0.md` (status paragraph and the package list for issue 264).

- [ ] Write; same identifier grep as D1; commit.

### Task D3: README and index format

**Files:** `README.md` sections "Keep the application non-elevated" (`:420`), "Build a live index with FileIndex" (`:468`), "Errors and recovery" (`:558`, which also describes the `CatchUpLost` and `Recovery` fault kinds and the `ScanCatchUp` report), and any other section the identifier grep hits; `docs/index-format.md` around `:296-306` (catch-up waits per drive and batched).

- [ ] Write; identifier grep; commit.

### Task V1: Final verification and pull request (orchestrator)

- [ ] Merge D1 to D3; `git -C ~/MFTLib rev-parse --abbrev-ref HEAD` is `main` (the primary checkout was never touched).
- [ ] Native Release and Debug builds; `dotnet build -c Release -p:Platform=x64`; `.\scripts\run-coverage.ps1 -NonInteractive` (background); `pwsh -NoProfile -File scripts/test-coverage-status.ps1`; `.\scripts\native-coverage.ps1`; Linux `scripts/coverage-linux.sh` on llamabox; `aislop scan .` and `aislop ci .`.
- [ ] Coverage: `.\scripts\run-coverage.ps1 -NonInteractive` on the merged head must show 100 percent line coverage for the `MFTLib` and `MFTLib.Index` namespaces (the project standard for managed code), not merely pass the CI publisher's drop-from-main check (a drop of more than 10 points, or zero covered lines in a tested namespace). Every residual uncovered line is either covered by a new or ported test or carries a written unreachability reason in the pull request body; compare against the base commit to find where a port lost cases.
- [ ] Grep the whole repository (excluding `docs/superpowers/`) for each deleted identifier separately.
- [ ] Delete the spec and this plan after folding durable content into docs (house plan lifecycle); open the pull request with the claude-code token against `main`, body carrying `Closes #252`, the wave list, and the measurement tasks still owed.

---

## Attended measurements (owner, real hardware, elevated; never run by a lane)

### M1: Concurrent versus sequential rescan

On a machine with at least three NTFS volumes, one of them spinning or USB storage if available: time `FileIndex.RescanAsync(drives, ct)` (batched; the broker sets each scan's thread allowance) against a loop of single-drive rescans over the same drives, three runs each, cold file cache between runs where possible. Record per-drive and total wall time, CPU, and disk queue length. Repeat the comparison for `FileIndex.OpenAsync` over the same drives with an empty cache directory: the concurrent open (B9) against opening the drives one at a time (the base commit's sequential open). Decides whether the one-thread-per-processor budget and the per-chunk rebalance stand (ruling 13). Record each scan's `ioTimeMs`, `fixupTimeMs` and `parseTimeMs` (`MFTLibNative/mft/mft.parse_core.cpp:454-456`, managed as `MftResult` timings at `MFTLib/Mft/MftResult.cs:45`), which show whether a scan is limited by the processor or by the disk. No broker path reads them today (a search of `MFTLib` for `IoTimeMs` finds only the field declaration and that one use), so take them from a single-drive `MftVolume` scan of the same volume.

### M2: Liveness limits under concurrent scans

During M1's batched rescan with diagnostics on (`MFTLIB_BROKER_DIAG=1`), extract from the broker log: the maximum gap between frames on each scan pipe, the maximum gap between heartbeats on idle watch pipes, and whether any `Stalled` frame or client stall fired, and how many `CatchUpLost` frames were written (a scan that outlived the journal). Confirms or adjusts 5 s, 30 s and 30 s (spec 11).

### M3: Chunk size throughput

Time a single-drive scan with the 64 MB host chunk (C1) against the 262144-record chunk at the base commit on the largest volume available. Confirms C1's chunk cap did not regress scan throughput; if it did by more than 5 percent, raise the chunk to the largest value that keeps M2's maximum frame gap under 10 seconds.

Record results as a comment on MFTLib issue 265 and fold final values into AGENTS.md.

---

## Consumer migrations

Each runs in its own repository, in a sibling worktree `~/<repo>-worktrees/265-per-drive`, branched from `gitea/main` (never `origin/main`, which is a stale GitHub mirror), with the `external/MFTLib` submodule moved to the MFTLib commit V1 lands as. These are the pin-bump pull requests; `scripts/sync_consumers.sh` may open the bump itself, in which case the lane works on that branch. Build per each repository's AGENTS.md: build `external/MFTLib/MFTLib/MFTLib.csproj` and `external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj` with `-p:Platform=x64` first.

### Task F1: file-wizard migration

Read at `gitea/main` `f4f9940`.

| File | Change |
|---|---|
| `FileWizard/FileWizardAPI.cs:10` | `new BrokerSessionHost(ct => BrokerProcess.LaunchAsync(BrokerLauncher.Launch, ct))` |
| `FileWizard/BrokerSessionHost.cs` | Holds `BrokerProcess`; subscribes `Ended` where it subscribed `BrokerDied` (`:57`, `:85`); `ConnectAsync` is `Func<CancellationToken, Task<BrokerProcess>>`; `Client` property type |
| `FileWizard/FileIndexHost.cs:107-108` | `new BrokerMftBlockProducer(brokerHost.ConnectAsync)` compiles unchanged against the new factory type |
| `FileWizard/FileIndexHost.cs` `RescanAsync` wrapper (`:110` onward) | Add `Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IReadOnlyList<char> driveLetters, CancellationToken)` applying the same cache-only refusal per drive (a refused drive is reported `Failed` with the refusal exception) and delegating the rest to `Index.RescanAsync(list, ct)` |
| `FileWizard/JournalWatcher.cs:178-187` | Drop the null-drive branch; `endsTheWatch` is true for `Channel`, `Recovery`, and `CatchUpLost` whose `JournalCatchUpLostException.RecoveryStopped` is true; `Drive`, `Apply`, and `CatchUpLost` without it mark the drive recovering (still active, `LastError` set); a later catch-up clears the recovering flag |
| `FileWizard/JournalWatcher.cs:194-202` | The loss that belongs to the fault being handled is one detected `LiveWatch` or `ScanCatchUp`; `DriveOpening` stays excluded; the comment says so |
| `FileWizard/JournalWatcher.cs:175-215`, `FileWizardMaui/MainPage.LiveUpdates.cs:335-341` | Audit that nothing reached from `OnWatchFaulted` or `OnJournalEvent` blocks on a `FileIndex` lifecycle call (spec 2.6.8); queue any that does. At `gitea/main` `JournalWatcher.OnWatchFaulted` runs under `_stateLock` and calls `_handler?.OnJournalEvent` (`:175-215`); the plan's author did not read `MainPage.LiveUpdates.cs:335-341`, so the audit there is real work |
| `FileWizardMaui/MainPage.Scanning.cs:170`, `FileWizard/IUpdateHandler.cs:28` (`ScanDriveProgressTracker`) | Scan progress arrives from several drives' scans at once; confirm `ScanDriveProgressTracker` accepts interleaved drives (spec 8) |
| `FileWizardMaui.Logic/JournalHintLogic.cs:17-25`, `:53-70` | `FormatHint` gets a branch for `ScanCatchUp` (a `FormatScanCatchUpHint` beside `FormatLiveWatchHint`): a scan of this drive outlasted the journal N times in a row (`DriveStatus.ConsecutiveLostCatchUps`) and automatic rescans stopped after three; with `CanGrow` (`:27-30`) true it names `SizeThatWouldHaveRetained` and the size clause (`:75-78`); for `JournalRecreated` it says a rescan is needed and nothing needs changing |
| `FileWizardMaui/MainPage.LiveUpdates.cs:79` | `var results = await host.Index.StartWatchingAsync(cancellationToken);` every `Failed` goes to per-drive health through `ReconcileDriveHealth`; `_watchStartFailure` only when every applicable drive failed |
| `FileWizardMaui/MainPage.LiveUpdates.cs:150` | Batched stop; results ignored (faults already reached `JournalWatcher`) |
| `FileWizardMaui/MainPage.Scanning.cs:56-57`, `:288-289` | Each `foreach ... await host.RescanAsync(...)` becomes one `await host.RescanAsync(drivesToRescan.Select(s => s.DriveLetter).ToArray(), cancellationToken)`; `Failed` results feed the scan error state, including a `JournalCatchUpLostException` whose message carries the journal size |
| `FileWizardMaui/SettingsPage.xaml.cs:127-129`, `file-wizard/JournalCommand.cs:143-146` | `client` is a `BrokerProcess`; the `GrowUsnJournalAsync` call is unchanged |
| `file-wizard/BrokerSmoke.cs:62`, `:100`, `:114`, `:126`, `:129-130`, `:145` | Launch through `BrokerProcess`; batched start and stop results printed per drive; the rescan loop becomes one batched rescan |
| `file-wizard/CliRunner.Database.cs:105`, `:124` | Batched start and stop; print `Failed` results |
| `Directory.Build.targets:75-76` | Comment names `BrokerTestHarness` |
| `FileWizardMaui.Logic/OpenProgressPresenter.cs:12` onward, `FileWizardMaui.Logic/ScannerState.cs:74` | Read `IndexDriveOpened.SettledCount` where they read `Ordinal`; the display says "k of N drives settled" rather than naming the k-th configured drive |
| `FileWizardMaui/MainPage.Scanning.cs:181`, `:196-198`; `FileWizardMaui/MainPage.xaml.cs:36`, `:232-241` | The handler already stores the latest report in a `volatile` field and renders on the UI tick, which is safe for reports from any thread; keep it, and render the highest `SettledCount` seen so an out-of-order store cannot step the count backwards |
| `FileWizard/FileIndexHostOptions.cs:18-24` | Doc: reports arrive in settle order, from the settling thread |
| `FileWizardTests/FileIndexHostTests.cs:294` (`OpenAsync_WarmStart_ReportsOpenProgressPerDriveInConfiguredOrder`), `:314-316`, `:370`, `:421-425`; `FileWizardTests/Maui/OpenProgressPresenterTests.cs:11`, `:28`, `:43`; `FileWizardTests/Maui/ScannerStateTests.cs:502` (`WarmStarted` helper) and its callers; `FileWizardTests/Maui/ScannerErrorBannerTests.cs:78` | `SettledCount`; the configured-order test becomes "one report per drive, `SettledCount` 1 to N" |
| `AGENTS.md` | Mentions of `JournalBrokerClient` and `BrokerDied` |
| `FileWizardTests/BrokerDeathTests.cs` | Build the process with `BrokerTestHarness.StartInProcess` over a host whose sources throw; end it by disposing; assert `Ended` forwarded once. `SendStartWatchAsync` use goes |
| `FileWizardTests/BrokerSessionHostTests.cs:21`, `:40`, `:53`, `:66` (through `FakeClient` at `:79`) | `BrokerTestHarness.StartInProcess` |
| `FileWizardTests/CliServicesTests.cs:124-126` | Same |
| `FileWizardTests/JournalCommandTests.cs:166-185` | `BrokerProcess` on the real path |
| `FileWizardTests/JournalWatcherTests.cs` | Delete `OnWatchFaulted_MergedSourceFailureDeactivatesEveryDrive` (`:296-303`); map `WatchFaultKind.Source` uses (`:105`, `:226-256`, `:321`, `:354`, `:387`, `:421-441`) to `Drive` (now recovering, still active) or `Channel` (inactive); add `OnWatchFaulted_DriveKind_MarksRecoveringAndKeepsActive`, `OnWatchFaulted_RecoveryKind_Deactivates`, `OnWatchFaulted_CatchUpLostKind_MarksRecoveringAndKeepsActive`, `OnWatchFaulted_CatchUpLostWithRecoveryStopped_Deactivates` and `OnWatchFaulted_ScanCatchUpLoss_IsCarriedToStatus` |
| `FileWizardTests/Maui/JournalHintLogicTests.cs` (14 uses of `JournalCheckpointLossDetection`) | Add `FormatHint_ScanCatchUpTrimmed_NamesSuggestedSize`, `FormatHint_ScanCatchUpJournalRecreated_SaysRescanNeeded`, `FormatHint_ScanCatchUpNoSuggestion_SaysSizeUnavailable` and `CanGrow_ScanCatchUpTrimmed_IsTrue` |

Unchanged (verified): `App.xaml.cs:43`, `file-wizard/Program.cs:8`, `MainPage.DriveRescan.cs:66` (single-drive `RescanAsync` keeps its signature; it now throws on a failed scan, which that call site already catches: confirm), `LargestSummaryCommand.cs:98`, `CliRunner.Database.cs:51`, `FileWizard/IUpdateHandler.cs:86` (`OnBrokerDied` is file-wizard's own name). The scan `Warning` frame's text reaches neither consumer (a search of file-wizard and git-wizard `gitea/main` for `BrokerScanResult`, `LatestScan`, `Catch-up after` and `.Warning` finds no production use; `FileWizardTests/Maui/DriveCardPresenterTests.cs:64` uses "Journal catch-up failed" only as a literal input). These `CheckpointLoss` readers render through `JournalHintLogic` and need no change beyond it: `file-wizard/UpdateHandler.cs:242-243`, `FileWizardMaui/MainPage.Drives.cs:40-42`, `file-wizard/JournalCommand.cs:69` and `:118-119` (`CanGrow` and `SizeThatWouldHaveRetained`, which read `Cause`, not `DetectedDuring`).

- [ ] Failing tests first for the `JournalWatcher` recovering and `ScanCatchUp` cases, the `JournalHintLogic` `ScanCatchUp` cases and the `FileIndexHost` batched wrapper (`RescanAsync_CacheOnlyDriveInBatch_ReportedFailedOthersRescanned`); migrate; `dotnet test FileWizardTests/FileWizardTests.csproj`; `scripts/run-coverage.ps1` as the repository documents; commit; open the pin-bump pull request.

**Gate:** green. **Depends on:** V1 merged.

### Task G1: git-wizard production migration (red by design)

Read at `gitea/main` `15be5f3`. git-wizard tracks `Releases/*.zip` with Git LFS (`.gitattributes:97-116`); create the worktree with `GIT_LFS_SKIP_SMUDGE=1` since nothing in the build reads those files.

| File | Change |
|---|---|
| `GitWizard/MftBrokerConnection.cs` | `JournalBrokerClient` becomes `BrokerProcess` (`:13`, `:16`, `:18`, `:22`, `:28`, `:39`, `:53`, `:85`, `:120`); `BrokerDied` becomes `Ended` (`:64`, `:108`) |
| `GitWizard/MftIndexSession.cs:41`, `:53`, `:185` | Factory type; `GetBrokerClient` becomes `GetBrokerProcess` |
| `GitWizard/MftIndexSession.cs:103-107` | Unchanged (`Profile` and `KeepFileNames` remain) |
| `GitWizard/MftIndexSession.cs:117` (`Progress = progress`) | Scan progress arrives from several drives' scans at once during the concurrent open; confirm the handler passed in accepts interleaved drives (spec 8) |
| `GitWizard/MftIndexSession.Windows.cs:43-44`, `:51`, `:60` | `BrokerProcess.LaunchAsync`; factory types |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:59-64` | Grow through `GetBrokerProcess()` |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:229-233` | `var results = await index.StartWatchingAsync(ct)`; each `Failed` goes through `RecordStartupDriveFailure` |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:283-318` | Drop the null-drive branch; `Drive`, `Apply` and `CatchUpLost` without `RecoveryStopped` keep the drive usable and publish a recovering volume state; `Channel`, `Recovery` and `CatchUpLost` with `RecoveryStopped` remove it from the usable set and publish `Excluded`; `terminal` is "the usable set is empty" |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:299-317` | Audit that the `DriveFailed` subscriber (`RepositoryWatchService.cs:80`) and the `Stopped` subscribers reached through `ReportDeath` never block on a `FileIndex` lifecycle call; queue any that does (spec 2.6.8) |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:316-317`, `RepositoryWatchService.cs:225`, `IndexVolumeChangeSource.Startup.cs:47`, `IndexVolumeChangeSource.cs:153-158` | Verified safe, no change (spec 8). A terminal fault's `ReportDeath` raises `SourceDied` inside the `WatchFaulted` handler; `SourceDeathSignal.OnSourceDied` starts `_cts.CancelAsync()` without waiting. That token reaches the pending `WaitForCatchUpAsync` (through `RepositoryWatchService.cs:128`, `:244` and `IndexVolumeChangeSource.cs:77`, `:127`). The wait's cancellation completes asynchronously (spec 2.6.8, B1), so the arming code's catch block runs `DisposeAsync` off the handler's stack, in its own context, with no active marker |
| `GitWizard/Watch/IndexVolumeChangeSource.Journals.cs:46-57` (`ProcessFaultedDrive`, the check at `:53`) | The loss that belongs to the fault being handled is one detected `LiveWatch` or `ScanCatchUp`; the comment at `:46-50` says so. A drive stopped after three lost catch-ups reaches this through its final `CatchUpLost` fault (`RecoveryStopped`) (`RaiseFaultJournalWarning`, called at `IndexVolumeChangeSource.cs:303`) and raises its `JournalWarning` |
| `GitWizard/Watch/JournalHintBuilder.cs:19-65` | `BuildRecreatedHint` (`:19-31`) and `BuildRescanClause` (`:49-65`) branch on `LiveWatch` (`:21`, `:51`); a `ScanCatchUp` loss gets its own wording in both (the journal wrapped while the drive was scanned and the scan was repeated three times), and the trimmed hint's size suggestion (`:33-45`) applies unchanged |
| `GitWizard/Watch/IndexVolumeChangeSource.cs:344-365` | Batched stop; the `WasReported` filter and `_reportedFaults` go, since stop returns faults as results |
| `GitWizard/Watch/IndexVolumeChangeSource.Readiness.cs:82` | Treat `WatchCatchUpState.Recovering` as not failed (published as recovering), `Faulted` as failed |
| `GitWizard/Watch/IndexVolumeChangeSource*.cs` | `_usableDrives` locking fix (below) |
| `GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs:170-187` | One batched `RescanAsync` over the drives `NeedsScan` selects; report `WatchStartupPhase.Scanning` for each selected drive before the call; `Succeeded` to `accumulator.RescannedDrives`, `Failed` to `accumulator.Errors` |
| `Directory.Build.targets:64` | Comment names `BrokerTestHarness` |

git-wizard sets no `OpenProgress` (its only match is the test name `MftIndexSessionTests.cs:240` `ScanProgressFrameReachesOpenProgress`, which concerns scan progress), so the concurrent open needs no handler migration there. Unchanged (verified): `GitWizardUI/Program.cs:19-23`, `git-wizard/Program.Watch.cs:35-36` (pass the runner through `ElevatedEntryPoint.TryHandle`, which keeps its signature), `IndexVolumeChangeSource.Startup.cs:47` (single-drive `WaitForCatchUpAsync`), `GitWizardUI/ViewModels/MainViewModel.Journals.cs:45`, `GitWizard/Watch/IUsnJournalGrower.cs` (git-wizard's own interface), `GitWizard/Discovery/RepositoryDiscoveryCoordinator.cs:232` (`NeedsScan` is true for a Watch purpose whenever `CheckpointLoss` is not null, so a drive stopped after three lost catch-ups is rescanned on the next watch preparation as a single manual attempt; that is the consumer's retry and needs no change). `GitWizardTests/TestSupport/BlockBrokerFixture.cs` and `BlockBrokerFixtureTests.cs` contain no `Warning` frame.

**`_usableDrives` locking fix.** Every access at `gitea/main`:

| Site | Access | Lock held today |
|---|---|---|
| `IndexVolumeChangeSource.Journals.cs:24` | `Add` | none |
| `IndexVolumeChangeSource.cs:122` | `Count` | none |
| `IndexVolumeChangeSource.cs:126` | `ToArray` | none |
| `IndexVolumeChangeSource.cs:199` | `Count` | `_lifecycleLock` |
| `IndexVolumeChangeSource.cs:296-297` | `Remove`, `Count` | `_faultLock` |
| `IndexVolumeChangeSource.Startup.cs:66` | `Remove` | `_lifecycleLock` |
| `IndexVolumeChangeSource.Readiness.cs:60` | `Contains` | `_lifecycleLock` |

Fix: a dedicated leaf `Lock _usableDrivesLock` taken only around each `_usableDrives` access and never while calling out or taking another lock. It nests inside `_lifecycleLock` (`:199`, `Startup.cs:64`, `Readiness.cs:59`) and inside `_faultLock` (`:286`) without creating an order between those two, so no deadlock is possible. Reusing either existing lock would create a `_lifecycleLock` to `_faultLock` nesting that does not exist today.

Regression test: `UsableDrives_FaultAndStartupFailureConcurrentWithArming_NoCorruption` in `GitWizardTests/Watch/IndexVolumeChangeSourceTests.cs`: one task repeatedly raises per-drive faults through the scripted source while another records startup failures and a third reads the usable set through the arming path, 100000 iterations, released together on a gate. Run it five times on the unfixed code; it is expected to throw ("Collection was modified" or a corrupted `HashSet`) at least once. If it cannot be made to fail, say so in the pull request and rely on the lock review; do not claim a regression test that never failed.

**Gate:** red by design. Production projects (`GitWizard`, `GitWizardUI`, `git-wizard`) build; `GitWizardTests` does not compile until G2, with errors naming `JournalBrokerScanSession`, `ScanSessionTestHarness`, `BrokerFrame`, `DriveWatchFailure`, `JournalBatch` constructors with a drive letter, `DriveCaughtUp(char)`, the old `IIndexWatchSource` members and `RunBroker(string?, bool)`. G2 turns it green. The alternative is a shim in MFTLib, which the no-compatibility rule forbids. **Depends on:** V1 merged. **Parallel with:** F1.

### Task G2: git-wizard test migration

| File | Change |
|---|---|
| `GitWizardTests/TestSupport/BlockBrokerFixture.cs` | Rewrite on `BrokerTestHarness.StartInProcess` with a real `JournalBrokerHost`, a fixture `IBlockSectionWriter` that writes its rows, and fake cursor, catch-up and watch sources; the hand-written frames (`:126-292`) and `JournalBrokerScanSession` (`:22`, `:78-80`, `:109-115`) go |
| `GitWizardTests/BlockBrokerFixtureTests.cs:20`, `:62` | `ArmScanAndCatchUpAsync` becomes `BrokerProcess.ScanDriveAsync` |
| `GitWizardTests/TestSupport/DuplexStream.cs` | Delete if nothing else uses it |
| `GitWizardTests/TestSupport/ScriptedIndexWatchSource.cs` | Rewrite per drive: `StartAsync` returns a scripted handle; `Publish(letter, item)`, `FailDrive(letter, exception)`, `LoseChannel(letter, exception)` |
| `DriveWatchFailure` callers: `Discovery/WindowsWatchStartupTests.cs:245`, `Watch/IndexVolumeChangeSourceReadinessTests.cs:47`, `:123`, `TestSupport/WatchFailureAssertions.cs:147`, `:166`, `Watch/IndexVolumeChangeSourceTests.cs:357`, `:370`, `UI/MainViewModelPreparedSessionTests.cs:160` | `FailDrive` or `LoseChannel` |
| `JournalBatch(` with a drive letter: `Discovery/WindowsWatchStartupTests.cs`, `MftIndexPersistenceTests.cs`, `TestSupport/WatchFailureAssertions.cs`, `Watch/IndexVolumeChangeSourceTests.cs` | Drop the letter; publish through the drive's handle |
| `DriveCaughtUp(`: `Discovery/RepositoryDiscoveryCoordinatorWatchProgressTests.cs`, `Discovery/WindowsWatchStartupTests.cs`, `Watch/IndexVolumeChangeSourceReadinessTests.cs` | `new DriveCaughtUp()` on the drive's handle |
| `ElevatedBrokerEntryDispatchTests.cs:14-20`, `UI/DesktopStartupTests.cs:10-15` | `RunBroker(string? controlPipeName)`; `--once` assertions go |
| `MftBrokerConnectionTests.cs:29`, `:79`, `:128`, `MftIndexPersistenceTests.cs:44`, `Discovery/RepositoryDiscoveryCoordinatorWatchProgressTests.cs:81` | `BrokerProcess` type |
| `MftIndexPersistenceTests.cs`, `MftIndexSessionTests.cs`, `Watch/IndexVolumeChangeSourceTests.cs` | Batched start and stop results where they call the no-list forms |
| `Watch/IndexVolumeChangeSourceTests.cs` | Add recovering-state cases: a `Drive` fault keeps the drive usable and publishes recovering; `Recovery` excludes it; a `CatchUpLost` fault without `RecoveryStopped` does what `Drive` does; one with `RecoveryStopped` on a drive carrying a `ScanCatchUp` loss publishes `Excluded` and raises the `JournalWarning` |
| `GitWizardTests/Watch/JournalHintBuilderTests.cs` (5 uses of `JournalCheckpointLossDetection`), `Watch/JournalWarningTests.cs` | Add `ScanCatchUp` trimmed, trimmed-without-suggestion and recreated hint cases and a `ScanCatchUp` warning case |
| `TestSupport/IndexOwnerFixture.cs:15`, `TestSupport/PackedIndexFixture.cs:41` | Compile unchanged (parameter type only) |

- [ ] Rewrite fixtures first, then callers; build per git-wizard AGENTS.md (`dotnet build git-wizard.slnx -c Release`, then `dotnet test GitWizardTests/GitWizardTests.csproj --no-build -c Release`); the analyzers are the build gate (`TreatWarningsAsErrors`); commit; open the pin-bump pull request with G1 and G2 together.

**Gate:** green. **Depends on:** G1.

---

## Release bookkeeping

- `CHANGELOG.md`: D1.
- AGENTS.md architecture: D1 (paragraph list there).
- `docs/`: D2 and D3.
- [MFTLib issue 252 (a late EndWatchAck ends the next watch)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/252): closed by the implementation pull request (`Closes #252`), pinned by C6's `TimedOutStop_ThenNewWatch_RunsUndisturbed`.
- [MFTLib issue 264 (0.3.0 ships no TestExtensions package)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/264): stays its own work item and is not part of this plan. `BrokerTestHarness` raises its priority: after this plan both consumers' broker tests depend on it (file-wizard `BrokerDeathTests`, `BrokerSessionHostTests`, `CliServicesTests`; git-wizard `BlockBrokerFixture`), so a consumer that moves from the submodule to the `MFTLib` package cannot run its broker tests until 264 ships `MFTLib.TestExtensions`. Constraints this plan keeps for 264: `MFTLibTestExtensions` stays pure managed with a single `ProjectReference` to `MFTLib.csproj`, keeps the assembly name `MFTLibTestExtensions` that `MFTLib.csproj:40` friend-lists, and exposes only `BrokerTestHarness.StartInProcess` publicly beside the existing isolation types.
- [MFTLib issue 72 (attended 0.3.0 checklist)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/72): its release dry run must run on the main that contains this implementation, after M1 to M3; `docs/handoff-release-0.3.0.md` (D2) and `TEST-REPORT.md` take the new numbers.


## Appendix A: Spec corrections

Each row records where the plan corrected the first version of the specification (`c1d4378`); the merged specification (commit `3597586`) adopts them. "Scope" marks corrections that add files or work.

| Spec claim | Finding at `c1d4378` | Plan action |
|---|---|---|
| Section 9 lists every test file to delete or rewrite | Missed: `VolumeQueryClientTests.cs` and `VolumeQueryHostTests.cs` (plural `QueryVolumesAsync`), `MftProducerEndToEndTests.cs` (uses `JournalBrokerScanSession`, `BrokerScanResult`, `BlockTargets`), `UsnJournalSyntheticTests.Cancellation.cs:37` (calls the old `ServeAsync`), `TestSupport/BrokerBlockTestBase.cs` (`BlockTargets`), `Index/FileIndexCheckpointLossDetectionTests.cs` (`IIndexWatchSource`, `DriveWatchFailure`), `Index/FileIndexRescanCleanupTests.cs:323` (reflects on `_swapGate`) | Scope: A3, C1, C3b, C4, B1, B4, B5 |
| Section 9 "Rewritten": `FileIndexWatch*Tests.cs` | `FileIndexWatchRescanTests.cs:302` and `FileIndexWatchFailedRescanTests.cs:139` reflect on `_swapGate` by name | B5 replaces the reflection |
| Section 9 "Rewritten": `FileIndexWatchCatchUpRetentionTests.cs` | Its subject is `CatchUpCoordinator` collection through `_catchUpCoordinatorCreatedForTest` (`FileIndex.WatchCatchUp.cs:282`), both deleted | Not ported (B2) |
| Section 4: `CatchUpCoordinator` at `FileIndex.WatchCatchUp.cs:278-351` | The class is in `WatchCatchUpState.cs:160-367` (with `WatchCatchUpSlot` at `:34-158`); `FileIndex.WatchCatchUp.cs:278-351` is the aggregate wait that uses it | B1 deletes the class from `WatchCatchUpState.cs` |
| Section 4: `JournalBrokerClient*.cs` deleted, 12 files | Count confirmed (12). Its static `NormalizeDriveLetter` and `TryNormalizeDriveLetter` are used by the host (`JournalBrokerHost.cs:172`, `JournalBrokerHost.Scan.cs:327`) and the producer (`BrokerMftBlockProducer.cs:53`) | Scope: C1 moves them to internal `BrokerDriveLetter` |
| Section 4 and 8 omit doc crefs | `NtfsVolumeInformation.cs:20` and `JournalCheckpointLoss.cs:114` name `JournalBrokerClient` members | C1 |
| Section 2.6: `_swapGate` covers journal batches and swaps | The public `FileIndex.ApplyJournalEntries` (`FileIndex.Watch.cs:245-251`) also takes it | B5 moves it to the drive's write gate |
| Section 2.3: the caller passes a fixed per-scan thread budget | Superseded by owner ruling 6 on issue 265: parse threads are rebalanced at every chunk and no caller computes a share (S3) | A1 (native per-chunk read), C1 (allocator); no field carries a count from `FileIndex` to the producer |
| Section 2.3: "Constructor and CreateDefault unchanged" and "the processor count is a constructor argument" | Contradictory; also superseded by S3 (one allocator) and R11 | C1 adds optional trailing `processorCount` and `timeProvider` constructor parameters; `CreateDefault` unchanged |
| Section 2.3: scan cap plus per-call thread budget | Does not bound threads when scans start independently (S3) | C1 replaces both with `ParseThreadAllocator` |
| Section 2.2: heartbeat sender | Nothing heartbeats the idle control pipe (S1); one synchronous writer lets a blocked pipe delay every other pipe's heartbeat (S2) | C5 |
| Section 2.2: scan frame table puts `Warning` before `ScanReady`, and a failed catch-up watches from the current position | The retained pipeline sends `ScanReady`, attempts catch-up, then a `Warning` and a batch from a fresh cursor (`JournalBrokerHost.Scan.cs:204-251`; reviewer cited `:208-235`), so changes made during the scan are never replayed (owner ruling 7) (R13, L1) | C1, C2 keep the code's order and end it with `CatchUpLost`; B5, B6, B9 apply the rule |
| Sections 2.6 and 5 lifecycle bullets | Confirmed gaps: no drain barrier for a retiring pump (`FileIndex.Watch.cs:279-295` picks the current snapshot at gate time) (R1); no cancellable pending start (today's session exists before readiness, `FileIndex.WatchStart.cs:63-79`) (R2); no revalidation of queued recovery (compare `FileIndex.WatchCheckpointLoss.cs:65-69`) (R3); "scans with no gate held" contradicts section 5 (R4) | Per-drive state machine section; B1, B5, B6 |
| Section 2.4: request ids and cancellation | Today an interrupted exchange aborts the connection (`JournalBrokerClient.ControlExchange.cs:67-76`) and host framing needs the full payload (`JournalBrokerHost.cs:246-267`), so cancelling mid-write corrupts the pipe (R6); id retirement undefined (R12) | C2 |
| Sections 2.1 and 2.3: closing a scan pipe stops the scan | Native parse takes no cancellation (`MftVolume.cs:65-72`, `:129-130`; `JournalBrokerHost.Sources.cs:9-17`) (R7) | A1, C1 |
| Section 2.6: build the `DriveBlock` at commit | Not enough: producer failure, discarded-block and checkpoint-loss state are keyed by tentative ordinal (`FileIndex.Scanning.cs:31-39`, `:173-185`, `:297-306`; `FileIndex.Rescan.cs:256-269`) (R8). The open-time writes are sequential today and collide once the open is concurrent (ruling 13) | B5, B9 |
| Section 6: handlers run on the pump | A handler that waits on its own drive's drain deadlocks (`FileIndex.Watch.cs:321-327`, `FileIndex.WatchPump.cs:316-320`) (R9) | B8 |
| Section 2.8: process-wide lock around the append | Keeps synchronous disk I/O in every channel's frame path (`JournalBrokerHost.cs:190-198`, `BrokerDiagnostics.cs:133-137`) (R10) | A2 |
| Sections 3 and 9: test seams | Host constructor has no clock or processor count (`JournalBrokerHost.cs:26-39`); sources cannot report waiting versus processing (R11) | C1, C2, C5 |
| Additional doc comments (from the forwarded verification report) | `BlockSource.cs`, `DriveFailureKind.cs` describe rescan and watch behavior; `oneShot` also at `JournalBrokerHost.Session.cs:41`, `:50`, `:72` and `DefaultElevatedEntryRunner.cs:18`, `:44` | B1 (docs), C1 (`oneShot`) |
| Section 2.3: open-time scans divide the budget | Superseded by owner ruling 6: nothing divides a budget at the caller. `OpenAsync` is sequential (`FileIndex.cs:148-162`), and `IndexDriveOpened.Ordinal` is the configured position, reported in `Drives` order on the opening thread (`IndexDriveOpened.cs:3-19`, `FileIndexOptions.cs:64-77`) | Owner ruling 13: B9 makes the open concurrent and reshapes the progress report |
| Section 3 lists no change to the host source delegates | `MftRecordBatchSource` must carry the scan's `ParseThreadAllowance` and an operation reporter, `JournalBatchSource` the reporter, and `UsnJournalCatchUpSource` the buffer-read bound | Scope: C1 |
| Section 2.2: "the plan audits the scan pipeline" | Two phases are at risk beyond the parse chunk: the block flush (one `MemoryMappedViewAccessor.Flush()` over the whole view, `BlockFile.cs:257-260`) and the catch-up read (native reads to the tip in one call) | Scope: A4 (native bound), C1 (chunk cap), C5 (ranged flush, bounded catch-up loop) |
| Section 9 names no script changes | `scripts/coverage-linux.sh:72`, `:82` name `DefaultElevatedEntryRunnerTests.RunBroker_ValidPipeName_ConnectsRealNamedPipe_ServesUntilShutdown_ExitsWithCode0`, which is renamed | C1, C3b |
| Section 4: `ScanSessionTestHarness` deleted | `MFTLibTestExtensions.csproj:5-9` comment names the session | A3 |
| Section 9: `GateFrameWriteStream` and `CancellableGateFrameWriteStream` deleted "if no rewritten test uses them" | Both reference `JournalBrokerClient`; nothing ported needs them | Deleted in C1 |
| Section 9: `Index/WatchFailureObservationTests.cs` rewritten with the index tests | It drives `ScriptedWatchBrokerHarness` (a broker harness) | Ported in C6, not B2 |
| Section 8, file-wizard `MainPage.Scanning.cs`: loops become `host.Index.RescanAsync(drives, ct)` | The loops call `host.RescanAsync`, file-wizard's `FileIndexHost` wrapper with a cache-only refusal | Scope: F1 adds a batched wrapper on `FileIndexHost` |
| Section 8, file-wizard `BrokerSessionHostTests.cs:79` | `FakeClient` at `:79` is used at `:21`, `:40`, `:53`, `:66` | F1 lists all |
| Section 8, file-wizard omits | `Directory.Build.targets:75-76` comment and `AGENTS.md` name the old types | F1 |
| Section 8, git-wizard omits | `GitWizardTests/BlockBrokerFixtureTests.cs:20`, `:62` (`ArmScanAndCatchUpAsync`); `IndexVolumeChangeSource.Readiness.cs:82` must handle `Recovering`; `JournalBatch` and `DriveCaughtUp` constructor sites in `WindowsWatchStartupTests.cs`, `MftIndexPersistenceTests.cs`, `WatchFailureAssertions.cs`, `IndexVolumeChangeSourceTests.cs`, `RepositoryDiscoveryCoordinatorWatchProgressTests.cs`, `IndexVolumeChangeSourceReadinessTests.cs`; `MftIndexSession.Windows.cs:51`, `:60` factory types; `Directory.Build.targets:64` comment | Scope: G1, G2 |
| Section 8, git-wizard `RepositoryDiscoveryCoordinator.cs:170-187` | The loop reports `WatchStartupPhase.Scanning` per drive before each rescan | G1 reports every selected drive before the batched call |
| Section 8, git-wizard `_usableDrives` sites | Confirmed all seven; `Journals.cs:24` and `cs:122`, `:126` hold no lock; `:199`, `Startup.cs:66`, `Readiness.cs:60` hold `_lifecycleLock`; `:296` holds `_faultLock` | G1: dedicated leaf lock (table in G1) |
| Section 8, git-wizard "Unchanged: `IndexVolumeChangeSource.Startup.cs:47`" | Confirmed: it calls the single-drive `WaitForCatchUpAsync(drive, token)` | none |
| Section 8, consumer pins | Both consumers pin `external/MFTLib` at `88e97a3`, one commit behind `692820f` | Consumer tasks move the pin to V1's commit |
| Section 7b: native mutable globals are test hooks only | Confirmed (`test_hooks.cpp:10-32`); A1 adds two more hooks, `GetChunkThreadCounts` and `GetResolveThreadCount` | Tests that read them are `[DoNotParallelize]` |
| Section 2.2 and section 8: a catch-up that fails after the scan is a warning and the watch starts from the current position; the consumer lists have no journal-loss change | The host substitutes an empty batch and a fresh cursor (`JournalBrokerHost.Scan.cs:228-238`), so changes made during the scan are never replayed (owner ruling 7); both consumers filter reports on `DetectedDuring == LiveWatch` (`FileWizard/JournalWatcher.cs:200`, `FileWizardMaui.Logic/JournalHintLogic.cs:19`, `GitWizard/Watch/IndexVolumeChangeSource.Journals.cs:53`, `GitWizard/Watch/JournalHintBuilder.cs:21`, `:51`) and would drop a new detection value | Scope: C1, C2, C5, B5, B6, B9, C8; F1, G1, G2 |
| Section 2.6.8: a public wait may deliver token cancellation through `Task.WaitAsync` | `WaitForCatchUpAsync` does (`FileIndex.WatchCatchUp.cs:374`, `:388`), and `Task.WaitAsync` does not promise asynchronous continuations | B1 (token registration), B7 (batched wait), B8 (audit) |
| Other line citations in sections 1, 2, 4, 5, 7 | Spot-checked at `c1d4378` (`BrokerFrame.cs:12`, `:27-31`, `:115-118`; `JournalBrokerHost.cs:47-132`, `:58`, `:93`, `:103`, `:128`, `:169-183`, `:216-229`; `JournalBrokerHost.Session.cs:46`, `:69-139`, `:87-92`, `:94-97`, `:116-136`; `JournalBrokerHost.Scan.cs:45-85`, `:297-301`; `BrokerIndexWatchSource.cs:17-36`; `JournalBrokerClient.LiveWatch.cs:358`; `WatchStreamItem.cs:23`, `:31`; `FileIndex.cs:48-49`, `:88`; `FileIndex.Rescan.cs:89`, `:145-158`, `:160-240`, `:207`, `:252`, `:312-316`; `FileIndex.Scanning.cs:233`; `FileIndex.ScanCleanup.cs:276-277`; `FileIndex.Disposal.cs:9`; `FileIndex.WatchTargets.cs:61-69`; `FileIndex.WatchCheckpointLoss.cs:34`; `FileIndex.Watch.cs:285-295`; `NamedBlockSection.cs:113-116`; `BrokerDiagnostics.cs:47-67`, `:139-144`; `DefaultElevatedEntryRunner.cs:26-28`; `ElevatedEntryPoint.cs:41`; `IElevatedEntryRunner.cs:17`; `JournalBrokerClient.Connection.cs:66-99`; `JournalBrokerClient.BlockScan.cs:31`; `test_hooks.cpp:36-42`) | Accurate |
