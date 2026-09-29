# Lane instructions common to every implementer on the per-drive watch channels plan

## Plan header

Goal: give every drive its own broker pipe, its own watch handle, its own pump, its own rescan
gate and its own write gate, so nothing index-wide exists except the snapshot and the short step
that publishes it, and nothing connection-wide exists except the elevated process and its control
pipe. Closes MFTLib issue 252; ships as the 0.3.0 watch architecture.

Governing specification (read only the sections your brief cites):
`docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md` in your worktree.
Where the plan and the spec disagree, the spec wins. Line references in briefs were read at commit
`c1d43784`; the code at your base is identical to that commit for every code file, but earlier
merged tasks may have moved lines, so locate by symbol when a line number does not match.

Never read the whole plan file. Your brief is your requirements. The binding constraints
(no backward compatibility, per-drive state machine, build and test commands, test rules, lane
rules) are in `global-constraints.md` beside this file: read it in full before you start.

## Worktree and lock

- Your worktree path and expected base SHA are in your dispatch. FIRST ACTION:
  `git -C <worktree> rev-parse HEAD` must equal that SHA. On mismatch STOP and report; never
  merge, rebase or reset to self-correct.
- Every command and every file path you touch is inside your worktree. Never write in
  `C:\Users\mtsch\MFTLib` (the primary checkout) or in any other worktree. Use absolute paths
  under your worktree in every Edit/Write call.
- SECOND ACTION: take the cooperative project lock on your worktree, or Edit/Write calls are denied:
  `python C:/Users/mtsch/.claude/skills/project-lock/scripts/project-lock.py acquire <worktree> --reason "<task id>: <task name>" --duration 3h --strategy worktree`
  Keep the printed lock id; release with `... release <worktree> --lock-id <id>` as your last
  action, also when you stop BLOCKED. Other worktrees are locked by other lanes: that is
  expected and is not a reason to stop.
- Any script you write to edit files (Python, sed, PowerShell) takes absolute paths under your
  worktree and never relies on the current directory: the shell's directory can reset to the
  primary checkout between calls. On 2026-09-29 a lane's scripted replace ran there and
  corrupted a file in `C:\Users\mtsch\MFTLib`. Before you report, run
  `git -C C:\Users\mtsch\MFTLib status --short` and state its output in your report; anything
  other than empty is reported, not fixed by you.
- Run `.\init.ps1` (PowerShell tool) once in your worktree before the first build.
- Commit only on your task branch. Do not push, do not merge, do not touch the integration branch.
  End every commit message with the line
  `Co-Authored-By: Claude <noreply@anthropic.com>`
  Multi-line commit messages from the Bash tool: `git commit -F - <<'EOF' ... EOF`.
- Other lanes are building on this machine at the same time. A build or test run that is slow is
  not hung; a file locked by another process in YOUR worktree is a real problem to report.

## Working method

1. Read your brief, then `global-constraints.md`. If anything is unclear or contradictory, stop
   and report NEEDS_CONTEXT with the specific question. Do not guess.
2. Implement exactly what the brief specifies, test-first where the brief lists failing tests:
   write the named tests, see each fail for the stated reason, implement, see them pass.
3. Standard verification as defined in `global-constraints.md`. Long commands run in the
   background teed to a file under your worktree's `.superpowers/` directory (git-ignored).
   At most three fix-and-rerun rounds, then report what is still red.
   aislop baseline: the locally installed aislop (0.16.0) scores the untouched base 99/100 with
   exactly four warnings that predate this plan: `MFTLib.Tests/NativeSeamIsolationFixtures.cs:73`
   and `:79` (AsyncFixer01) and `MFTLib/Index/CachedBlockDeletionOutcome.cs:8` and `:10`
   (redundant doc comment). Do not touch those. Your aislop gate is: no finding other than those
   four. List every remaining finding in your report. Keep CRLF line endings in files that have
   them, or aislop reports formatting warnings.
   `dotnet build` of the whole solution cannot load the native `.vcxproj`: build the native
   project with MSBuild, then the managed projects, as `global-constraints.md` shows.
   Never end your turn to wait on a background command: a lane that stops to wait is not
   reliably woken. While one runs, keep working (self-review, the report draft); if nothing is
   left, wait inside the same turn with a loop that polls for the log's final line.
   A native-only rebuild does not copy `MFTLibNative.dll` into the test output folder; targeted
   test runs after one need the DLL copied, or a managed build that copies it.
4. Commit with the message the brief names.
5. Self-review: completeness against the brief, nothing extra built, names accurate, tests verify
   behavior, test output free of warnings. Fix what you find before reporting.

Stop and report BLOCKED or NEEDS_CONTEXT rather than improvising when the task needs an
architectural decision the brief does not make, when the code does not match what the brief
describes, or when you are unsure your approach is correct. Bad work is worse than no work.

Do only your task. If you see an adjacent problem, name it in your report; do not fix it.

## Report

Write the full report to the report file named in your dispatch:
- what you implemented (or attempted, if blocked)
- TDD evidence: the RED command and failing output with why that failure was expected, the GREEN
  command and passing output
- whole-suite result, aislop result, any extra verification the brief names, each with the
  command and the relevant output lines
- files changed
- self-review findings, concerns, anything adjacent you noticed

Then reply with ONLY (under 15 lines):
- Status: DONE | DONE_WITH_CONCERNS | BLOCKED | NEEDS_CONTEXT
- Commits created (short SHA and subject)
- One-line test summary
- Concerns, if any
- The report file path

If the review finds issues you will be resumed with the findings: fix, re-run the covering tests,
append a fix report (what changed, tests run, command, output) to the same report file, and reply
with the same short contract.
