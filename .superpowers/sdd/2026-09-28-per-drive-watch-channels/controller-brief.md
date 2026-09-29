# Controller brief: per-drive watch channels plan, from wave 4 onward

You are the subagent-driven-development CONTROLLER for the rest of this plan. You coordinate; you
do not write production or test code yourself. Invoke the skill
`superpowers:subagent-driven-development` first and follow it, with the amendments below. The
owner is asleep: never ask the owner anything. Where you would ask, make the ruling the
specification supports, record it in the ledger and in `orchestrator-rulings.md` as "owner may
overrule", and continue. The only reasons to stop are in "When to stop" below.

## Places

- Primary checkout (READ ONLY for everyone, must stay on `main`, clean): `C:\Users\mtsch\MFTLib`
- Integration worktree (yours): `C:\Users\mtsch\MFTLib-worktrees\impl-265`, branch
  `impl/265-per-drive-channels`. Only you commit here (cherry-picks and merge fixes).
- Workspace WS: `C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels`
  (git-ignored; NEVER run `git clean` in any worktree of this plan).
- Ledger: `WS\progress.md`. Read it from "## RESUME POINT" to the end before anything else, then
  `WS\orchestrator-rulings.md`, `WS\preflight-findings.md`, `WS\lane-common.md`,
  `WS\reviewer-common.md`, `WS\global-constraints.md`, `WS\broker-common.md`.
- Plan: `docs/superpowers/plans/2026-09-28-per-drive-watch-channels.md` in the integration
  worktree. Read only the wave table, the dependency table and the V1 task. Every task brief is
  already extracted as `WS\task-<ID>-brief.md`. The specification
  (`docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md`) governs over the plan.
- Task worktrees: `C:\Users\mtsch\MFTLib-worktrees\265-<ID>`, branch `task/265-<ID>`, created by
  you with `git -C C:\Users\mtsch\MFTLib worktree add <path> -b task/265-<ID> <integration head>`.
  Never use `isolation: "worktree"` (it branches from origin/main).
- Remote: `gitea`. Push the integration branch after every merged wave, and task branches when a
  task completes. Never push to `main`. Never force-push. Never delete a branch or a worktree
  that holds a lane still running; keep a finished lane's worktree until its whole wave merged.

## Order of work

Waves (from the plan): 4 = B5, C4, C5, C7 (plus W40, see the ledger); 5 = B6, B9, C6;
6 = B7, C8; 7 = B8; 8 = D1, D2, D3. Respect the dependency table. After each wave: cherry-pick in
dependency order, run on the merged head `pwsh -NoProfile -File scripts/run-coverage.ps1
-NonInteractive` (ALWAYS `-NonInteractive`; nothing may trigger elevation), per-namespace coverage
with `python .superpowers/scratch/nscov.py MFTLib.Tests/coverage.xml <namespace>`, and
`aislop scan .`; record the totals in the ledger; push. After wave 5 do the tranche B audit the
plan describes (coverage of namespace `MFTLib` back to at least main's 99.32 percent, and every
base-commit test method of the deleted broker test files named as ported or dropped). A Linux
run is owed on the merged head after wave 4 and again before V1: dispatch a sonnet lane that runs
`./init.sh --build` and `scripts/coverage-linux.sh` in a scratch clone on host `llamabox` over
ssh (the earlier one used `~/scratch/mftlib-265-w3`); W40's `msync` path has never run on Linux.

Every dispatch carries its rows from `orchestrator-rulings.md` and the "NOTE FOR ... DISPATCH"
lines in the ledger for that task. Every dispatch names an explicit model.

## Lanes and tiers

- Tier per task is in the plan's dependency table. "Opus" tasks (B5, C5, B6) get a Claude
  `opus` implementer subagent and an opus-tier review. "Sonnet" tasks get `sonnet`.
  Never use a model above opus for a lane.
- Reviews and scoped re-reviews go to the OpenAI pool where possible, to spare the Anthropic
  pool: `codex exec -m gpt-5.6-sol -c model_reasoning_effort=high -s workspace-write --cd
  <task worktree> - < <prompt file>`, run in the background with a `timeout 45m`, log to a file.
  The pattern that worked four times tonight is in the 265-C2 and 265-W40 worktrees under
  `.superpowers\sdd\2026-09-28-per-drive-watch-channels\lanes\` (read `rereview-C2-r4-prompt.md`
  and `review-W40-dispatch.md` as templates). Rules for a codex lane: stage every input file
  INSIDE the lane's own worktree workspace (its sandbox cannot read another worktree); name the
  single output file it may write; say "do not acquire or release any lock"; absolute paths;
  "if any referenced file is missing, STOP and write BLOCKED". Copy the review file back to WS.
  If a codex run produces no output file, or hangs with no CPU use, kill it and dispatch the
  review to a Claude subagent of the task's tier instead.
- A codex lane cannot commit in a linked worktree. If you use codex for an implementation lane
  (only for ports and docs: C6 is NOT one of them), tell it not to run git add or commit, and
  commit its work yourself after checking `git status` and `git diff --stat`.
- A subagent is NOT woken by a finished background command. Every lane brief says: do not end
  your turn to wait; poll inside the turn. The same holds for you: when you wait on a background
  command or a lane, poll with a bounded loop (for example `until [ -f <file> ]; do sleep 30;
  done` under a `timeout`), do not end your turn.
- Review packages: `C:\Users\mtsch\superpowers\skills\subagent-driven-development\scripts\review-package
  <plan file> <BASE> <HEAD>`, run from the task worktree.
- Fix loop per the skill: five rounds maximum per task; rounds 1 to 3 resume the implementer by
  SendMessage; every fix round ends with a scoped re-review; adjudicate only at the cap.

## Lanes already running when you start (dispatched by the previous controller)

Read the last lines of the ledger for their state. Their replies go to the previous controller,
who forwards them to you by message. Until then, leave worktrees 265-C4, 265-C7 and 265-W40
alone, except that you may read files in them.

- C4: codex `gpt-5.6-terra` headless lane in worktree 265-C4, no-commit rule. Log
  `...\265-C4\.superpowers\sdd\2026-09-28-per-drive-watch-channels\lanes\C4.log` (last line
  `EXIT <code>` when done); report `task-C4-report.md` and proposed commit message
  `task-C4-commit-message.txt` in that same workspace. When it is done: verify its claims by
  running the targeted tests yourself, commit its work on `task/265-C4` with the trailer
  `Co-Authored-By: GPT 5.6 Terra (codex) <noreply@openai.com>`, then run the task review.
- C7: Claude sonnet implementer, agent name `impl-C7`, worktree 265-C7, report `WS\task-C7-report.md`.
- W40: Claude sonnet implementer, agent name `impl-W40`, worktree 265-W40; its state is in the
  ledger.

## Rules that bit earlier sessions

- A lane's scripted edit once ran in the primary checkout. After every lane, run
  `git -C C:\Users\mtsch\MFTLib status --short` and `git -C C:\Users\mtsch\MFTLib rev-parse
  --abbrev-ref HEAD`; the first must be empty and the second `main`. If not: inspect the diff,
  record it in the ledger, restore with `git checkout -- <file>` only when the change is plainly
  a lane's stray edit.
- Lane and reviewer replies over about 60 lines are cut off: every lane writes its full report to
  a file and replies short.
- Lane claims (test counts, aislop, RED evidence) are unverified until the merged-head run.
- No em-dashes or en-dashes in anything you or a lane writes. Sweep lane reports before quoting.
- aislop gate: exactly the four baseline warnings plus the ruled 8-parameter `JournalBrokerHost`
  constructor warning. Do not edit `.aislop/config.yml`.
- No backward compatibility of any kind (see `global-constraints.md`).

## Scope limits

- Your scope ends when wave 8 (D1, D2, D3) is merged, verified and pushed, and the whole-branch
  final review (opus tier, per the skill) has run with its ONE fix dispatch and ONE re-review.
- Do NOT open the pull request, do NOT merge into `main`, do NOT start F1, G1, G2 (the consumer
  repositories), do NOT run M1 to M3, do NOT delete the workspace, branches or worktrees, and do
  NOT run anything elevated. V1's 100 percent coverage check needs the owner's elevated run;
  list every uncovered line of `MFTLib` and `MFTLib.Index` with file and line in your final
  report, and dispatch a test lane for each uncovered line that a non-elevated test can reach.
- Do not write memory notes and do not edit any file outside the plan's worktrees and WS.

## When to stop

Stop and write your final report when: (a) the scope above is complete; or (b) a task trips the
five-round breaker with a load-bearing finding; or (c) a merged-head suite stays red after one
fix dispatch; or (d) the primary checkout was damaged in a way you cannot plainly restore. In
cases (b) to (d), finish and merge whatever independent tasks can still complete first.

## Bookkeeping and final report

Append to the ledger as the skill prescribes, at every dispatch, review, fix round, completion,
merge and measurement. After every merged wave, snapshot the workspace to branch
`sdd/265-workspace` the way the ledger's WRAP section describes (without the `*.diff` packages)
and push it.

Write your final report to `WS\controller-final-report.md`: state of every task; integration
head and its suite, coverage and aislop numbers; every ruling you made; every deferred minor that
the final review said must be fixed before merge; open owner decisions; what was never verified.
Then reply with under 20 lines: what is done, what is not, the integration head, the report path.
