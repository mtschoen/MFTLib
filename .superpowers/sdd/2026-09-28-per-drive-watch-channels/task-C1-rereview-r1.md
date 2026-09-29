# Task C1 re-review, fix round 1 (fix base 50bda63, head 2f67e21)

### Findings

**F1: ADDRESSED**
- Deletions in the diff:
  - `BlockScanOutcome.cs` and `BlockScanTarget.cs` are deleted whole (diff:180-210).
  - `BrokerScanOptions.BlockTargets` is deleted (diff:223-224).
  - The `BlockFile.DeleteOnClose` property and the private constructor parameter that only set it are deleted (`BlockFile.cs`, diff:623-645, with the three call sites at diff:668, :691 and :714).
  - `BlockFileCreateOptions.DeleteOnClose` still selects `FileOptions.DeleteOnClose` (`MFTLib/Index/BlockFile.cs:158`), as the ruling requires.
- Grep of every `*.cs` file in the 265-C1 worktree (HEAD confirmed as 2f67e21, covering `MFTLib`, `MFTLibTestExtensions`, `MFTLib.Tests`, `TestProgram` and `Benchmark`):
  - `BlockScanTarget`, `BlockScanOutcome` and `BlockTargets` have no matches.
  - Every remaining `.DeleteOnClose` hit is on `BlockFileCreateOptions`, `MftBlockProduceRequest` or the index's own scan target (`FileIndex.Rescan.cs:316`, `:371`; `FileIndex.Scanning.cs:69`; `MftBlockProducerContractTests.cs:37`, where `seen` is an `MftBlockProduceRequest`). None is on `BlockFile`.
- What the spec keeps:
  - Spec:748 and :756 keep `BlockScanTarget` and `BlockScanOutcome` as part of C2's `ScanDriveAsync` surface. Deleting them now is the ruled "delete what nothing reads, C2 restores" case.
  - Spec:981 deletes `BrokerScanOptions.BlockTargets` outright.
  - The spec never mentions `BlockFile.DeleteOnClose`, so its public surface does not keep the property.
- The report (`aislop5.log`) says aislop now shows only the four baseline warnings plus the ruled 8-parameter constructor at `JournalBrokerHost.cs:43`.
- Not verifiable from the diff package:
  - Whether the commit body contains the "C2 restores what it reads:" list. The package shows only the subject line, and the report asserts the list is there. The orchestrator should confirm it at merge.
  - The aislop result itself (I did not re-run the scan).

**F2: ADDRESSED** (for the mechanisms the finding named; one residual in untouched code is listed under Out-of-Scope)
- Payload decode:
  - Every `PayloadReader` read now goes through `Take`, `Length`, `Count` or the pre-checked `Entry` (`BrokerProtocol.Payload.cs`, diff:442-564). A short field, a negative length, or a count that the remaining bytes cannot hold throws `InvalidDataException`.
  - `EntryFixedBytes = 46` matches `ReadEntry`'s layout (`BrokerProtocol.cs:47-67`: 8+8+2+8+8+4+4+4), and the name bound is checked in bytes, as `ReadEntry` slices it.
  - `ReadArmAndScanFrame` and `ReadJournalBatchFrame` use the checked counts (`BrokerProtocol.cs:133`, `:146`), so a garbled count can no longer size an allocation.
  - `ReadFirstRequestAsync`'s existing `InvalidDataException` catch (`JournalBrokerHost.Channel.cs:171-178`) then writes `Error` on that pipe and returns null. The channel ends and nothing reaches the session drain.
- Cancelled connector:
  - `ConnectChannelAsync` now sends everything except session cancellation to the Error reply (`Channel.cs:65-77`).
  - `HandleControlRequestAsync` / `AnswerControlRequestAsync` (`Session.cs`, diff:267-325) answer an OCE raised while the session is alive with an `Error` frame that carries the request id.
- Diagnostics flush: `RunBroker` wraps `ServeAsync` in `try { } finally { FlushDiagnostics(); }` (`DefaultElevatedEntryRunner.cs`, diff:383-393). A failing session still gets the bounded (2 s) flush before its exception leaves.
- Tests:
  - `DriveChannel_MalformedRequestPayload_WritesErrorAndOtherRequestsContinue` (3 rows) asserts one `Error` frame followed by EOF, then a working `QueryVolume` on control. The harness's dispose awaits `ServeAsync`, so a session fault would fail the test.
  - `OpenChannel_ConnectorThrowsOperationCanceled_RepliesErrorWithRequestId` covers the cancelled connector.
  - `RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving` covers the flush.
  - The report's red log gives each red reason: `ArgumentOutOfRangeException` twice, `OutOfMemoryException`, a 10 s `TaskCanceledException`, and `AreNotSame`. Each matches the pre-fix code.
- Can the new Error-frame paths throw while writing the frame and end the session that way? No.
  - Drive pipe: `WriteFrameAsync` turns `IOException` into `ClientDisconnectedException` (`JournalBrokerHost.Frames.cs:54-57`). `ServeChannelAsync` catches both it and OCE (`Channel.cs:123-132`), so a malformed request on an already-closed pipe ends that channel quietly.
  - Control pipe, `AnswerControlRequestAsync` Error write: a broken pipe surfaces as `ClientDisconnectedException` and reaches the outer handler, which ends the session deliberately (nobody is left to serve, the same as before). A session cancelled between the check and the write throws OCE, and the outer `when (session.Token.IsCancellationRequested)` filter absorbs it.
  - Control pipe, `ConnectChannelAsync`: the catch-block writes (`Channel.cs:61`, `:74`) propagate the same two exception types to the same outer handlers. The `try` at `Channel.cs:51-55` contains no writes, so the broad catch cannot swallow a write failure and then retry it.

**F3: ADDRESSED** (a statement, no code change)
- The report ("Where the volume is released before the section (R7)") gives the order:
  1. `JournalBrokerHost.Scan.cs:98` calls `blockSectionWriter.Write`, which enumerates synchronously.
  2. `RealBlockSectionWriter.cs:16` runs `MftBlockRowWriter.WriteBatches`, whose `foreach` (`MftBlockRowWriter.cs:29`) disposes the iterator.
  3. The iterator's `using var volume` (`JournalBrokerHost.Sources.cs:52`) releases the volume.
  4. `WriteBatches` returns, and `RealBlockSectionWriter.cs:14`'s `using var block` releases the section.
  5. `using (registration)` (`Scan.cs:33`) releases the registration last.
- That is volume, section, registration, with file:line for each step. I did not re-open those files: they lie outside the fix diff and the finding asked only for the statement.

### New Breakage in the Fix Diff

**Critical:** none.

**Important:**
1. **`RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving` breaks the test rules** (`MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs`, diff:90-91).
   - `Task.WhenAny(runTask, Task.Delay(TimeSpan.FromMilliseconds(500)))` followed by `Assert.AreNotSame(runTask, finished)` is a fixed real-time sleep used as the assertion's evidence.
   - `global-constraints.md` "Test rules every task inherits" forbids both: "Never assert on wall-clock time" and "Async waits poll a condition with a timeout ..., never a fixed sleep".
   - It cannot fail falsely in the green state, because with an infinite flush timeout the runner cannot return before `releaseAppend` is set. But its red-state detection depends on the runner returning within 500 ms, and every run costs half a second.
   - The static `_diagnosticsFlushTimeout` knob it needs (`DefaultElevatedEntryRunner.cs`, diff:351-355, 418, 424) exists only for this test.
   - A form that follows the rules:
     - Replace the knob with an injected `TimeProvider` for the flush bound, e.g. `BrokerDiagnostics.FlushAsync(...).WaitAsync(timeout, timeProvider)`, set through the same kind of internal static seam as `_exitProcess`.
     - Or add a signal seam fired when `FlushDiagnostics` begins.
     - The test then awaits "flush started" through `TestGate`/TCS with a timeout, asserts `runTask.IsCompleted == false`, and releases the parked sink. It then awaits `runTask` and asserts the sink received the line that was queued when the session failed.
     - Red state: without the `finally`, "flush started" never fires, and the signal's `WaitAsync` timeout fails the test. That is a condition wait, not a sleep.
     - With a `FakeTimeProvider` that is never advanced, the green run cannot time out the flush either.

**Minor:**
1. The new `AnswerControlRequestAsync` catch, which turns an OCE with the session alive into an `Error` (`JournalBrokerHost.Session.cs`, diff:316-324), has no test that fails without it.
   - The only new cancellation test (`OpenChannel_ConnectorThrowsOperationCanceled_...`) is satisfied by the `Channel.cs` change alone, because `ConnectChannelAsync` now handles that OCE before it can reach `Session.cs`.
   - A `QueryVolume` or `GrowUsnJournal` source that throws its own `OperationCanceledException` would cover it. That test must see the `Error` reply with the request id, and see the next request still answered.
2. `RunBroker` still lets the session's exception escape once the flush is done (`DefaultElevatedEntryRunner.cs`, diff:383-396). In the elevated child this is an unhandled-exception process exit, not `_exitProcess(1)`. The behavior predates the fix, the flush now precedes it (which is what F2 asked for), and the test pins it (`Assert.IsNull(exitCode)`). Whether the elevated child should exit with a defined nonzero code is left to the orchestrator.

### Out-of-Scope Observations
- **A malformed frame-length prefix still escapes as a non-`InvalidDataException`** (`JournalBrokerHost.Frames.cs:73-79`, untouched by the fix).
  - `ReadFrameAsync` rejects `totalLength < 1` but not a huge value. `new byte[4 + totalLength]` with `totalLength = int.MaxValue` overflows to a negative size and throws `OverflowException`. A value near 2 GB throws `OutOfMemoryException`, and any value below that allocates up to that size before waiting.
  - On a drive pipe, the exception escapes `ReadFirstRequestAsync`'s filter, faults the tracked channel task, and ends the session: the same class of fault as F2.
  - Recommendation: bound `totalLength` (for example, by a maximum request frame size) and throw `InvalidDataException`, with one more malformed-request row. Fold it into C1 or give it to C3b. It does not block this round.
- `docs/broker-scan-tuning.md`, `docs/broker-testing.md`, `docs/broker-integration.md`, `README.md` and `CHANGELOG.md` still name `BlockTargets`/`BlockScanTarget`/`BlockScanOutcome` or `BlockFile.DeleteOnClose`. Assuming the docs task rewrites these, it should list them.
- Earlier review Minor 1 (`DrainAsync` hides a fault when another task outlives the grace period) and Minor 2 (a check-then-act on the disposed control lock) are untouched. They are still Minor.

### Tests (evidence check)
- The report names the covering tests for each finding, the command (the targeted filter set, plus `run-coverage.ps1 -NonInteractive`), and the red (`fix1-red.log`: 5 failed) and green (`fix1-green.log`: 5 passed) outputs, and the claims are consistent with the diff.
- Post-deletion evidence:
  - The whole-suite run (1429 passed, 0 failed, 96.2% line coverage) predates the final `BlockScanTarget`/`BlockTargets` deletion.
  - After that deletion the report cites a clean build and a 144-test targeted run that includes `BlockFile`, `NamespaceBoundary`, `NativeSeamIsolation` and the broker host classes.
  - My grep confirms no remaining `*.cs` reference to the deleted names, so a type-level deletion can only break compilation, which the reported clean build rules out. Deleting members cannot change runtime behavior elsewhere.
  - I treat that as sufficient evidence that build and tests are green. The coverage gate itself was measured before the deletion; removing uncovered or unconstructed types can only raise line coverage.

### Verdict
All findings addressed. One Important item is new in the fix diff: the flush test breaks the wall-clock and fixed-sleep test rules and should be rewritten to the signal-based form above before approval.
