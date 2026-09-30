# Task FX5 dispatch: two broker tests fail intermittently in whole Linux runs

Worktree: `C:\Users\mtsch\MFTLib-worktrees\265-FX5`, branch `task/265-FX5`, expected base SHA
`8dd64bf` (full: verify with `git -C C:\Users\mtsch\MFTLib-worktrees\265-FX5 rev-parse --short HEAD`).
Report file: `C:\Users\mtsch\MFTLib-worktrees\265-FX5\.superpowers\sdd\2026-09-28-per-drive-watch-channels\task-FX5-report.md`

Read first, in full, in this order (all under
`C:\Users\mtsch\MFTLib-worktrees\265-FX5\.superpowers\sdd\2026-09-28-per-drive-watch-channels\`):
`lane-common.md`, `global-constraints.md`, then `task-FX4-report.md` section 1 and "Concerns"
(the only evidence so far), and `task-FX3-report.md` (the method to copy). If any referenced file
is missing, STOP and report BLOCKED naming it.

## The problem

In whole Linux runs of the managed suite (the platform filter `scripts/coverage-linux.sh` uses),
two tests in the flat `MFTLib` broker area fail intermittently. Neither fails on Windows, and
neither has been seen failing alone or by class. Measured by the FX4 lane on llamabox, 20 whole
runs per commit:

| Test | Failures | Duration at failure |
|---|---|---|
| `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound` | 2 of 20 at 6d4396a, 2 of 20 at 8dd64bf, 0 of 20 at 03139d7 | 10 s |
| `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` | 1 of 20 at 8dd64bf | 20 s |

The round durations suggest a bound being hit under load. That is a HYPOTHESIS, not a finding.
Logs of those runs: llamabox `~/scratch/fx4/repro-<commit>.log`.

## What to do

1. Reproduce on llamabox (`ssh llamabox`, a Linux host; dotnet 10 installed). Work in a NEW scratch
   clone `~/scratch/fx5/clone` of `gitea@gitea.fleet.sticktoitive.net:schoen/MFTLib.git` checked out
   at `8dd64bf` (never reuse or modify another lane's directory under `~/scratch`; never `find` or
   scan rooted at `~` or `/` without `-maxdepth`). Build with `./init.sh --build`. Read
   `~/scratch/fx4-repro.sh` for a working loop and the exact whole-run filter.
   Capture, for each failing run, the full failure message and stack of the failing test (run with
   `--logger "trx"` or `--logger "console;verbosity=detailed"` scoped so the log stays readable).
   Runs are long: run loops in the background with `nohup`, teed to a file under `~/scratch/fx5/`,
   and poll; never re-run a loop only to read different lines of it.
2. Establish the cause with evidence, for each test separately. Decide, as FX3 and FX4 did, whether
   the TEST is wrong (races its own observation, depends on wall-clock time, a bound too tight for
   a loaded machine that the contract does not promise) or the PRODUCTION code is wrong (a real
   ordering defect the Linux scheduler exposes, as FX4 found). Read the test, the code under test,
   and what the docs and `docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md`
   promise about the behavior asserted. Whether the same cause explains both tests is part of the
   answer. If load is the trigger, show it: find which concurrently running test classes create it,
   and make the failure frequent with a targeted stressor instead of waiting for 1-in-20.
3. Fix at the cause.
   - A test defect: make the test deterministic per the test rules in `global-constraints.md`
     (order by signals, fake clock or seam, never widen a real-time bound to make a flake rarer and
     never add a retry). Do not weaken what the test asserts.
   - A production defect: failing-first test that reproduces the ordering by signals, then the fix.
     Rulings W40-R1 and W40-R1a: per-test scratch mutation evidence with the exact literal command
     and the real failing output satisfies the RED requirement.
   - Ruling COVERAGE (owner): 100 percent line coverage of `MFTLib` and `MFTLib.Index` for
     everything a non-elevated test can reach stays intact. New production lines need tests; any
     new seam is internal and instance-level, never static or process-wide. No coverage
     exclusions, no `ExcludeFromCodeCoverage`.
   - Sweep: once the cause is known, search the other broker test classes for the same pattern and
     fix every instance, naming each in the report.
4. Verify.
   - Linux: the fixed commit, whole run 20 times, 0 failures of these two tests; report any OTHER
     test that fails in those runs by name, count and message, without fixing it. Then unmodified
     `scripts/coverage-linux.sh` once, exit code and totals.
   - Windows, in your worktree: `.\init.ps1 -Build` once, the touched test classes three times,
     then ONE `.\scripts\run-coverage.ps1 -NonInteractive` (background, teed to a file under your
     worktree's `.superpowers\`), then ONE `aislop scan .`. The aislop gate is exactly five
     warnings: the four baseline warnings in `lane-common.md` plus the ruled 8-parameter
     `JournalBrokerHost` constructor warning (ruling W2-2).
   - NEVER run `run-coverage.ps1` without `-NonInteractive` and never run anything that elevates:
     the owner is not at the machine for a UAC prompt.
5. Commit on `task/265-FX5` in small commits whose subjects state the resulting behavior. End each
   message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. You MAY push
   `task/265-FX5` to the `gitea` remote (so llamabox can fetch it). Never push to `origin`, never
   push or touch `impl/265-per-drive-channels` or `main`.

## If you cannot reproduce

Say so plainly with the run counts. Do not commit a speculative fix as if it were a verified one:
state the hypothesis, what evidence supports it, and what you changed, labelled UNVERIFIED. An
earlier task on this plan (FX2) shipped a fix for a failure that never reproduced; do not repeat
that without the label.

## Report and reply

Report per `lane-common.md`, plus: the reproduction table (test alone / class / whole, per commit),
the cause for each test with file:line, the test-or-production decision with the contract text it
rests on, the sweep list, and the output of
`git -C C:\Users\mtsch\MFTLib status --short` (must be empty; if not, report it, do not fix it).
Release your project lock as your last action. Reply in under 15 lines per `lane-common.md`.
