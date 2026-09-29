You are an implementer lane on the MFTLib "per-drive watch channels" plan (C# and C++ on
Windows). Your task is C6: watch channels over the broker and the thin `BrokerIndexWatchSource`,
plus the ports of the old broker watch-source tests. It joins the two trunks: the index side
(per-drive `IIndexWatchSource`, B1 to B5) and the broker side (`BrokerProcess`, scan channels,
host liveness and client stall limit, C1 to C7) are merged on your base.

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-C6` (branch `task/265-C6`).
Expected HEAD: `9725d0b8a246cd56b9a4cefa3e17fd1e5c024d65`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-C6 rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file). If any file named below is missing, STOP and reply
BLOCKED naming it.

Read, in this order:
1. `WORKSPACE\lane-common.md`: lane rules (worktree, lock, build, verification, report contract).
2. `WORKSPACE\global-constraints.md`: binding constraints, state machine, test rules. In full.
3. `WORKSPACE\broker-common.md`: broker context and the port rules. In full. (Parts of its
   "What exists" list are older than your base: C4, C5 and C7 have since merged; read the code.)
4. `WORKSPACE\index-port-common.md`: what the index watch contract looks like. Its porting rules
   apply to the index-side cases you port.
5. `WORKSPACE\task-C6-brief.md`: read this first among requirements, with the exact names to
   use verbatim, EXCEPT where the rulings below amend it.
6. `WORKSPACE\orchestrator-rulings.md`: rows W5-1, W5-3, N-3, C2-Q1, C5-Q1, W40-R1 bind this task.

Rulings and context the brief cannot know:
- W5-1: task B6 (automatic recovery) runs in parallel with you. Any ported case that asserts
  post-fault drive state (`WatchCatchUpState.Faulted`, `WatchFailureMessage`) uses a `Channel`
  fault (pipe closed); `Error`-frame cases assert only the raised `WatchFault(Drive, X)`, because
  after B6 a `Drive` fault recovers.
- W5-3: the test is named `HostError_IsDriveWatchFault` (not `..._TriggersRecovery`); C8 owns the
  recovery assertion.
- N-3 (port accounting): the old broker watch-source test files deleted by task A5 are read with
  `git show c1d43784:<path>`. They are `BrokerIndexWatchSourceTests*.cs`,
  `BrokerIndexWatchSourceCaughtUpTests.cs`, `BrokerIndexWatchSourceFaultTests.cs`,
  `BrokerLiveWatchErrorTests.cs`, `BrokerFileIndexRescanTests.cs`, `BrokerDeathTests.cs`,
  `Index/WatchFailureObservationTests.cs`, and also the six the brief does not list:
  `BrokerIndexWatchSourceArmingTests.cs`, `BrokerIndexWatchSourceArmingTests.LastDrive.cs`,
  `BrokerPerDriveArmTests.cs`, `BrokerPerDriveArmTests.Recovery.cs`,
  `BrokerWatchSourceAbandonedStartTeardownTests.cs`, `BrokerWatchStartSendCancellationTests.cs`
  (verify the exact names with `git show c1d43784 --stat` of A5's parent or
  `git ls-tree -r --name-only c1d43784 MFTLib.Tests`). Every `[TestMethod]` of every one of those
  files is either ported or named as dropped, with its reason, in your commit message, built
  mechanically. Session, arm/disarm, readiness and merged-stream cases are dropped as such.
- A5 deleted two orphaned test-support members that a port may need: restore them from
  `git show c1d43784:<path>` only if a ported case reads them
  (`GateFrameWriteStream.ForwardedFrameKinds`, the `InProcessBlockBrokerHarness` `WrapClientTransport`
  setter). `JournalBrokerClient.CreateLiveWatchItemSource` no longer exists.
- C2-Q1 (owner): `BrokerTestHarness` has no fault surface of its own; host faults reach tests only
  through production surfaces (`BrokerProcess.Ended`/`HasEnded`, `BrokerChannelLostException`,
  `Error` frames). Do not add a harness member that observes host failures. Your rewritten
  `ScriptedWatchBrokerHarness` follows the same rule.
- C5-Q1: the host heartbeats a watch pipe that is `WaitingOnVolume` and a `Processing` pipe that
  made progress within the processing limit; `ReadAsync` skips `Heartbeat` frames.
- The client stall limit (C7) closes a pipe silent for `BrokerLiveness.StallLimit` on the injected
  `TimeProvider`; watch-channel tests that advance a fake clock must account for it.
- B1's single-ownership contract: the index pump cancels the read token and only then disposes the
  handle; `ReadAsync` must return promptly on cancellation without disposal.
- Handler reentrancy (blocking lifecycle calls inside a `Changed` or `WatchFaulted` handler) is
  task B8's; do not write a test that blocks inside a handler on such a call.
- W40-R1: every NEW test (the brief's named ones) needs RED evidence in the report: the exact
  command with its literal `--filter` and the failing output, before implementation or against an
  uncommitted scratch mutation. Ports need no RED.
- aislop gate: no finding other than the four baseline warnings in lane-common.md plus the ruled
  8-parameter `JournalBrokerHost` constructor warning. Keep CRLF line endings in the working tree.
- Nothing you run may need elevation; no real elevated process. Always pass `-NonInteractive` to
  `run-coverage.ps1`. Never end your turn to wait on a background command; poll inside the turn.

REPORT FILE: `WORKSPACE\task-C6-report.md` (the one file outside your worktree you may write).
Write the full report there per lane-common.md, including the base-to-new method table for every
base file above and a "Primary checkout" line with the output of
`git -C C:\Users\mtsch\MFTLib status --short`.

Commit on `task/265-C6` with the brief's commit message plus the dropped-method list, ending with
`Co-Authored-By: Claude <noreply@anthropic.com>`.

Reply with ONLY (under 15 lines): Status (DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT),
commits (short SHA and subject), one-line test summary, concerns, report path.
