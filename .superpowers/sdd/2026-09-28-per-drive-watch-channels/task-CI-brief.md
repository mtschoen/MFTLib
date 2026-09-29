You are a test lane on the MFTLib "per-drive watch channels" plan (C# on Windows). Your task (CI)
raises line coverage of the `MFTLib.Index` namespace (`MFTLib/Index/`) toward 100 percent with
non-elevated tests. The project standard is 100 percent line coverage for managed code, required
at the plan's final verification. Test
code only, unless a line is dead code (then say so; see below).

YOUR WORKTREE: `C:\Users\mtsch\MFTLib-worktrees\265-CI` (branch `task/265-CI`).
Expected HEAD: `2889deb21e4e4e1315f0d8dc78aacbfb5e532938`. FIRST ACTION: `git -C C:\Users\mtsch\MFTLib-worktrees\265-CI rev-parse HEAD`
must equal that SHA; on mismatch STOP and report; never merge, rebase or reset to self-correct.

WORKSPACE = `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
(read-only for you, except your report file).

Read first: `WORKSPACE\lane-common.md`, `WORKSPACE\global-constraints.md` (in full),
`WORKSPACE\index-port-common.md` (older than your base; read the code), `WORKSPACE\orchestrator-rulings.md`
(rows B5-Q1, B9-Q1, B9-Q2, CB-Q1, W40-R1a).

Method:
1. Run `pwsh -NoProfile -File C:\Users\mtsch\MFTLib-worktrees\265-CI\scripts\run-coverage.ps1 -NonInteractive`
   once (background, poll inside your turn), then
   `python C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\scratch\nscov.py C:\Users\mtsch\MFTLib-worktrees\265-CI\MFTLib.Tests\coverage.xml "MFTLib.Index"`
   (it prints per-namespace totals and the uncovered lines per file for the named namespace; it
   truncates each file's list at 14 lines, so read `coverage.xml` directly for any file with more).
2. For every uncovered line, classify it: (a) reachable by a non-elevated test through the public
   or internal surface, with in-process fakes (WatchHarness, FakeIndexWatchSource, fake producers,
   TestGate, JournalIsolation.OverrideJournalWindow, the native seams); (b) reachable only with elevation or a
   real NTFS volume (the admin-only `RequiresAdmin` tests cover these in the owner's elevated run);
   (c) unreachable (dead code, or a defensive branch no input can reach). Read the code; do not
   guess from the file name.
3. Write tests for every (a) line, in the existing test file of that area (or a new focused file
   named after the class). Each test must assert behavior, not merely execute the line.
4. Class (c) means unreachable only; a branch reachable through a race the production code
   deliberately handles is (a) if a deterministic TestGate or seam ordering can force it, else
   (b)-race with the reason no seam exists. For (c), do NOT delete code in this lane. List each with the reason; the controller decides.
5. Re-run the coverage and report the new totals and the remaining uncovered lines with their
   classification.

Test rules: FakeTimeProvider where time matters; no real-time delay or polling sleep; no
elapsed-time assertion; every await in a test and its helpers bounded; owned cache directory;
`[DoNotParallelize]` where process-wide seams are touched (the `NativeSeamIsolationTests` guard
enforces it); no real elevated process, nothing that triggers UAC: never run `run-coverage.ps1`
without `-NonInteractive`, never call the real launcher. These are coverage tests for existing behavior, so they need no RED
evidence (ruling CB-Q1); if one fails against the code, that is a suspected defect: leave it out of the commit and
report it with test name, assertion, observed value, and the spec line.

Verification: the targeted classes; ONE final `run-coverage.ps1 -NonInteractive` after your last
edit (0 failed; paste Total/Passed/Failed and the nscov.py totals); ONE `aislop scan .` (gate: the
four baseline warnings plus the ruled 8-parameter `JournalBrokerHost` constructor warning). Keep
CRLF line endings. Never end your turn to wait on a background command; poll inside the turn.

Commit on `task/265-CI`: "Index code reachable without elevation is covered by tests",
ending with `Co-Authored-By: Claude <noreply@anthropic.com>`.

REPORT FILE: `WORKSPACE\task-CI-report.md`: before and after totals, a table of every uncovered
line at the start (file:line, class a/b/c, the covering test or the reason), suspected defects,
commands and outputs, and the output of `git -C C:\Users\mtsch\MFTLib status --short`.

Reply with ONLY (under 12 lines): Status, commit, MFTLib.Index coverage before and after, counts of
a/b/c lines, suspected defects, report path.
