### Strengths

- The per-drive lifecycle is internally coherent across B1, B5, B6, B7, B8, and B9. Each drive has its own lifecycle and write gates, `_stateLock` remains innermost, production runs without the write gate or `_stateLock`, and publication revalidates the drive instance and armed block before committing. Disposal cancels first, then takes lifecycle gates and write gates in drive-letter order before releasing snapshots.
- Watch effects are scoped to the exact `WatchInstance`. Batches, catch-up completion, faults, checkpoint reports, recovery tickets, and teardown all recheck instance identity. A retiring pump cannot publish into its successor, and a stale recovery cannot replace a newer block.
- Waiter completion is well designed. The relevant completion sources use `RunContinuationsAsynchronously`, cancellation reaches waits through registrations, and batched catch-up has an owned aggregate completion rather than an inline continuation chain. The reentrancy marker consistently rejects lifecycle calls made from `Changed` or `WatchFaulted` handlers while leaving queries and already-settled waits available.
- The broker redesign has clear ownership boundaries: one channel per drive operation, one writer per pipe, request ids retained until a late reply arrives, per-channel client stall detection, dedicated host heartbeat delivery, processing-state heartbeats, and the C5-Q2 in-flight-write rule. The parse-thread allocator also rebalances live registrations without coupling independent drive channels.
- Fault translation is mostly crisp. A drive-pipe loss stays drive-scoped, a control-pipe loss ends the process, proven catch-up loss remains distinct from an ordinary scan failure, and `BrokerIndexWatchSource` carries drive, channel, and catch-up outcomes into the `FileIndex` state machine without a merged-session compatibility layer.
- The public per-drive and batched APIs are consistent, use full-word names, and document ordering and per-drive failure results. The consumer test surface is isolated in `MFTLibTestExtensions`, and process-wide seams used by tests are generally protected by class-level `[DoNotParallelize]`.
- Controller verification is strong: 1936 tests with 1930 passed and 6 admin-only skips, 98.6 percent line and 96.4 percent branch coverage, classified residual uncovered lines, coverage-publisher regression checks, Linux build and coverage, and aislop at the ruled baseline.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. `MFTLib/Broker/BrokerDiagnosticsWriter.cs:53-66` exposes a transient `_accepted` value to `FlushAsync`, and `MFTLib/Broker/BrokerDiagnosticsWriter.cs:70-86` can consequently wait for work that was dropped and will never increment `_processed`. The code itself describes the race: enqueue increments `_accepted`, a concurrent flush snapshots it, and the synchronous drop callback then decrements it. If no later record arrives, an unbounded flush never completes; at broker shutdown the bounded flush can time out and lose the failure diagnostics it exists to preserve. `BrokerDiagnostics.ReplaceWriterForTest` adds a related stale-writer window at `MFTLib/Broker/BrokerDiagnostics.cs:144-149`, where a logger can enqueue into a writer just completed by a swap. Serialize enqueue accounting with the flush snapshot, handle `TryWrite` returning false after completion, and either retry a stale writer against the current instance or synchronize replacement with writer acquisition. Add deterministic tests that pause between the increment and drop and between writer acquisition and replacement.

2. A wrong known control reply violates the public control-loss contract. `MFTLib/Broker/Client/BrokerProcess.Control.cs:120-123` throws `BrokerChannelLostException` with a null drive but does not call `RequestEnd`, while `MFTLib/Broker/Client/BrokerChannelLostException.cs:7-9` says a null-drive loss ends the whole `BrokerProcess`. The caller sees a process-ending exception although `HasEnded` can remain false and the same potentially desynchronized control session accepts later requests. Build the mismatch reason, call `RequestEnd(reason)`, then throw the loss exception, and add a regression test that asserts `Ended`, `HasEnded`, and failure of another pending request.

3. `MftParseControl` is declared under `#pragma pack(push, 1)` at `MFTLibNative/mft_api.h:34` and `MFTLibNative/mft_api.h:73-76`, but both fields are read as shared 32-bit values that require natural alignment at `MFTLibNative/internal.h:44-53`. The managed caller currently uses an aligned `NativeMemory` allocation, so the tested path works, but the exported native type has alignment 1 and permits an embedded or array element whose address makes the shared load undefined or non-atomic. Move the control structure outside the packed wire-result region, add size, offset, and alignment assertions, and remove `Pack = 1` from `MFTLib/Interop/MftParseControl.cs:7` after confirming the unchanged 8-byte layout.

4. The V1 no-compatibility and plan-lifecycle gate is incomplete. The deleted `JournalBrokerScanSession` still owns analyzer sections at `.editorconfig:168-174`; old operation names remain in `MFTLib.Tests/BrokerProcessTests.BlockSections.cs:11`, `:63`, `:101`, and `:135` and in `MFTLib.Tests/VolumeQueryClientTests.cs:10` and `:32`; and `MFTLib.Tests/BenchmarkRunnerTests.cs:1131` still names `JournalBrokerClientTests`. These are exactly the deleted-identifier leftovers the V1 grep was meant to reject, and the analyzer entries are now inert compatibility debris. In addition, the tracked plan and specification still exist at `docs/superpowers/plans/2026-09-28-per-drive-watch-channels.md:1` and `docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md:1`, despite the V1 deletion requirement at plan line 1110. Remove the stale analyzer sections, rename or reword every leftover, rerun each deleted-identifier grep separately, and delete the plan and specification after this review is resolved and all durable rules are folded into maintained docs.

5. Two tests retain avoidable hang or timing hazards. `MFTLib.Tests/BrokerProcessLaunchTests.cs:188-198` waits on a real 50 ms timer with no independent hang bound, contrary to the repository rule to drive clocks rather than depend on wall time. `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:330` awaits a locally cancelled rescan without the `HangGuard` used by the analogous test in `FileIndexWatchFailedRescanTests`. A timer regression can make the former wait until the runner's global timeout, and a teardown regression can make the latter do the same. Route the launch timeout through an injectable `TimeProvider` or a timer-signaling seam and pin the boundary deterministically; bound the rescan assertion with `WaitAsync(HangGuard)`.

#### Minor (Nice to Have)

1. The protocol rejection tests at `MFTLib.Tests/BrokerProtocolTests.cs:52-59` and `MFTLib.Tests/BrokerProtocolTests.Scan.cs:207-212` assert only `InvalidDataException`. An unrelated validation failure would satisfy them, so the two explicit final-review open items are mutation survivors. Capture the exceptions and assert that their messages name the rejected kind and cause values. The additional non-empty `JournalBatch` and cause-number golden cases can remain follow-up coverage.

2. The public `ParseThreadAllowance` contract omits its exclusive-use rule. `MFTLib/Mft/ParseThreadAllowance.cs:3-8` presents a generally reusable thread-count object, but attaching it to a second concurrent parse throws at `MFTLib/Mft/ParseThreadAllowance.cs:49-55`, and the public `StreamRecords` parameter documentation at `MFTLib/Mft/MftVolume.cs:112-115` does not disclose that behavior. State that one allowance may be attached to only one running parse and document the possible `InvalidOperationException` on the public parse APIs.

3. Three comments no longer describe the code they sit beside. `MFTLib.Tests/Index/FileIndexWatchRescanTests.cs:272-273` says the original scan failure names the drive state, but the assertion at line 277 requires the restart failure; `MFTLib.Tests/Index/FileIndexCatchUpLossTests.cs:176-178` speaks about recovery as future task B6; and `MFTLib.Tests/JournalBrokerHostLivenessTests.StalledPipe.cs:78-79` says the stalled write begins on the fourth 5-second visit even though the 30-second threshold takes six visits. Rewrite them as target-state explanations.

### Deferred-minor triage

- Line 12, Task A3 friend-listing comment - can wait: it is wordy but accurately distinguishes the consumer assembly from the friend-listed test-extension assembly.
- Line 19, Task A2 stale-writer and flush accounting race - must fix before merge: it can strand `FlushAsync` or lose the final diagnostic line, as described in Important issue 1.
- Line 20, Task A2 `LongRunning` on the async drain - can wait: it wastes a scheduler thread only until the first asynchronous wait and does not change correctness.
- Line 21, Task A2 queued lines at broker exit - can wait: `DefaultElevatedEntryRunner.FlushDiagnostics` now performs the ruled bounded shutdown flush.
- Line 22, Task A2 RED capture and sink-failure wording - can wait: the wording was brief-mandated and missing historical RED evidence is not a current product defect.
- Line 24, Task A5 comment reflow and redundant `Internal` wording - can wait: the deleted types are gone and the surviving `FileIndex` documentation was rewritten.
- Line 26, Task A5 port-accounting wording - can wait: subsequent tranche accounting and the current suite establish the disposition of those cases; this is ledger history.
- Line 33, Task A1 packed `MftParseControl` alignment - must fix before merge: the public native declaration does not guarantee the alignment required by its shared 32-bit loads.
- Line 34, Task A1 `ParseThreadAllowance` one-parse rule - must fix before merge: this is observable behavior of a newly public parameter type and belongs in its API documentation.
- Line 35, Task A1 process-global chunk recorder comment - can wait: `ParseControlBlock` already states the `[DoNotParallelize]` requirement and the isolation guard covers native seam users.
- Line 36, Task A1 processor-count comparison - can wait: it is a known environment-sensitive assertion, and both controller platforms passed; replace it when a direct native processor-count seam is available.
- Line 37, Task A1 cancellation after `StreamRecords` returns - can wait: the native parse and registration have ended at that point, and later token behavior is outside the returned result's ownership.
- Line 41, Task A1 `ShouldForceCancel` ordering - can wait: the extra hook check is negligible and has no production semantic effect.
- Line 50, Task A4 negative-bound guard coverage - can wait: the residual line is explicitly classified in the accepted coverage report and does not represent an untested normal path.
- Line 58, Task B1 seven minor items - can wait: later waves and owner rulings settled the fault disposition; the remaining items are observability, diagnostic, and test-name polish.
- Line 63, Task C1 eight minor items - can wait: later protocol and runner tasks supplied the round trips, cleanup, and exit protection; the remaining host-drain observations are process-exit edge cases.
- Line 69, Task B1 retiring-slot, prompt-drain, cleanup, and unbounded-rescan items - can wait: identity checks clear the slot, handler reentrancy is rejected, cleanup gates were hardened, and the unbounded drain is an explicit specification choice.
- Line 76, Task C1 OCE-to-Error failing-first gap - can wait: the path is covered behaviorally even though it lacks a dedicated fail-first mutation.
- Line 77, Task C1 huge frame length - can wait: C2 added the shared maximum-length validation and both client and host regression coverage.
- Line 81, Task C1 flush-test cleanup - can wait: the final runner test now releases the sink and calls `EnsureRunnerLeavesAsync` from `finally`.
- Line 91, Task B4 four minors - can wait: the unresumable start-during-rescan case now exists at `FileIndexCatchUpLossTests.Operations.cs:75`; the remaining cause and explanatory-comment improvements do not change the contract.
- Line 97, Task B3 review findings - can wait: the missing stop assertions were restored and the two fault-disposition questions received owner rulings and durable documentation.
- Line 98, Task B3 three minors - must fix before merge: the contradictory restart-failure comment remains, and the locally cancelled rescan still has an unbounded assertion.
- Line 103, Task C3a three minors - can wait: duplicate attributes were removed; the remaining zero-cursor and synthetic-path cases are optional strengthening.
- Line 106, Task B2 port and specification gaps - can wait: the owner rulings now define fresh-start fault disposition, and current lifecycle tests cover cancellation and stale caught-up delivery at the state-machine level.
- Line 108, owner specification gaps - can wait: both gaps are resolved in `orchestrator-rulings.md` and the durable architecture text.
- Line 111, Task B3 omitted-stop comment - can wait: the ruling and current restart-failure assertions now define the behavior; only explanatory test prose remains.
- Line 123, Task C3b protocol-test minors - must fix before merge: strengthen the two exception-only rejection tests now; the large cohesive partial and additional golden cases can wait.
- Line 128, Task C3b cleanup timeout masking an original exception - can wait: it occurs only when the body and mandatory orphan-prevention cleanup both fail, where the teardown timeout is independently actionable.
- Line 130, orchestrator duplicate-attribute merge fix - can wait: the current partial class has one effective `[TestClass]` and `[DoNotParallelize]` declaration and the verified suite compiled it.
- Line 138, Task C2 eight minors - must fix before merge: the wrong-known-reply process invariant and real-time unbounded launch-timeout test remain; the other six items were fixed or can wait.
- Line 154, Task C2 disposal documentation and harness precision - can wait: the documentation was narrowed, the in-memory reader now translates close to `ObjectDisposedException`, and the remaining precision gaps do not affect production behavior.
- Line 178, Task W40 omission of `MS_INVALIDATE` - can wait: `MS_SYNC` supplies the required durable flush semantics and the Linux ranged-flush path passed; cache invalidation is not required for this writer.
- Line 182, Task C2 rereview minors - can wait: linked token allocation, bounded host-fault cleanup, in-memory-pipe fidelity, clock occurrence coupling, and throwing event handlers are test or documented extension points rather than merge defects.
- Line 195, open unknown-kind and unknown-cause assertions - must fix before merge: this is the explicit final-review assignment covered by Minor issue 1.
- Line 209, Task W40 flush-test mutation strength - can wait: the OS persistence assertion is not an isolated proof of the native call, but retry and range seams plus the Linux run cover the operational path; the `MS_INVALIDATE` concern is nonessential.
- Line 247, Task C5 `HostPipeWriter` paths - can wait: task CB added deterministic coverage for the queued-behind-stall and late-write paths.
- Line 249, controller merge fix 23709f6 - can wait: the integrated signatures compile and the controller's Windows and Linux verification exercised them.
- Line 256, Task B5 recovery wait, unreachable line, and docs - can wait: B6 fixed recovering waits, residual unreachable coverage is classified, and D1 replaced the deleted-field documentation.
- Line 267, controller `ScriptedScanSteps` merge fix - can wait: the collision is resolved and the full suite compiled both harnesses.
- Line 276, Task B9 late-open cleanup and README join - can wait: the current test disposes a completed open from `finally`, and the README paragraphs are separated correctly.
- Line 295, Task B6 duplicate drain and stale catch-up comment - must fix before merge: the duplicate completed await is harmless, but the comment still describes B6 as future work.
- Line 359, pre-existing cancelled-open partial cache - can wait: it predates the branch, the next open rejects and removes the incomplete block, and it is not part of issue 265.
- Line 368, task CB stalled-write interval comment - must fix before merge: the comment says four visits while the constants and adjacent test establish six.

### Recommendations

1. Fix the diagnostics accounting and stale-writer handoff first, because deterministic tests will likely shape the implementation.
2. End the broker process on a wrong known control reply and correct the native control-block alignment, then run targeted broker protocol and native interop tests.
3. Make the two timeout/cancellation tests deterministic and bounded, strengthen the two protocol rejection assertions, and correct the stale comments.
4. Finish V1 cleanup last: remove deleted-name leftovers, rerun one grep per deleted identifier excluding `docs/superpowers`, fold any remaining durable text into maintained docs, then delete the tracked plan and specification.
5. Re-run the controller's existing Windows coverage, coverage-publisher regression, Linux coverage, and aislop gates after these fixes. No broader architectural rewrite is indicated.

### Assessment

Ready to merge? With fixes. Reasoning: The per-drive state machine, broker channel architecture, recovery, batching, reentrancy, and concurrent open integrate cleanly, with no critical design defect found. Merge should wait for the five important issues, the assigned protocol-test strengthening, and the V1 cleanup because they affect shutdown diagnostics, the control-loss contract, native alignment, deterministic verification, and the repository's explicit no-compatibility gate.
