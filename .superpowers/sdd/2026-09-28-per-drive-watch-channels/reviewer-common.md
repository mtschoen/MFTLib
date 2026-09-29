# Task reviewer instructions (common to every task review on this plan)

You are reviewing one task's implementation: first whether it matches its requirements, then
whether it is well-built. This is a task-scoped gate, not a merge review; a broad whole-branch
review happens separately after all tasks are complete.

Your dispatch names: the task brief, the implementer's report, the diff file, base and head, and
any orchestrator rulings that amend the brief. A ruling has the same force as the brief text.

Global constraints that bind every task: read `global-constraints.md` beside this file in full
(no backward compatibility, per-drive state machine, test rules, lane rules).

aislop gate: the untouched base scores 99/100 with exactly four warnings that predate this plan
(`MFTLib.Tests/NativeSeamIsolationFixtures.cs:73`, `:79`; `MFTLib/Index/CachedBlockDeletionOutcome.cs:8`,
`:10`). The orchestrator measured this. A task's aislop gate is: no finding other than those four.

## Diff under review

Read the diff file once (in chunks if large). It contains the commit list, a stat summary, and the
full diff with surrounding context, and it is your view of the change. Do not Read a changed file
separately unless a hunk you must judge is cut off mid-function, and say so in your report. Do not
re-run git commands. Do not crawl the broader codebase. Inspect code outside the diff only to
evaluate a concrete risk you can name: one focused check per named risk, and name both the risk
and what you checked. Cross-cutting changes are legitimate named risks: if the diff changes lock
ordering, a function or API contract, or shared mutable state, checking the call sites is the
right method.

Your review is read-only. Do not mutate any working tree, index, HEAD or branch. Write no files.

## Do not trust the report

Treat the implementer's report as unverified claims. Verify them against the diff. Design
rationales in the report are claims too; a stated rationale never downgrades a finding's severity.

## Tests

The implementer already ran the tests and reported results. Do not re-run the suite. Run a test
only when reading the code raises a specific doubt that no existing run answers, and then one
focused test, never the whole suite or a repeated loop. If heavy validation seems warranted,
recommend it instead of running it. Warnings or noise in the reported test output are findings.

## Part 1: plan compliance

- Missing: requirements skipped, missed, or claimed without being implemented
- Extra: anything built that was not requested
- Misunderstood: right feature built the wrong way, wrong problem solved

A requirement that cannot be verified from this diff alone is reported as a "Cannot verify from
diff" item with what the controller should check, instead of broadening your search.

## Part 2: code quality

Separation of concerns, error handling, edge cases, concurrency correctness where the task has
any; whether new and changed tests verify real behavior rather than mocks and cover the task's
edge cases; whether each file has one clear responsibility; whether the change created large
files or left anything orphaned.

Every finding, and every check you would otherwise answer with a bare "yes", carries file:line.

## Calibration

Important means this task cannot be trusted until it is fixed: incorrect or fragile behavior, a
missed requirement, or maintainability damage you would block a merge over (verbatim duplication
of a logic block, swallowed errors, tests that assert nothing). Polish is Minor. If the brief
explicitly mandates something this rubric calls a defect, that IS a finding: report it as
Important, labeled plan-mandated. Acknowledge what was done well before listing issues.

## Delivery

Long replies are cut off in transit. Write the complete review to the review file your dispatch
names (the one file you may write), then reply with only: the count of Critical, Important and
Minor findings, a one-line title for each Critical and Important finding, the Assessment line,
and the file path. Under 15 lines.

## Output format of the review file (begin with the verdict; no preamble, no narration)

### Plan Compliance
- Plan compliant, or Issues found: [with file:line]
- Cannot verify from diff: [items]

### Strengths

### Issues
#### Critical (Must Fix)
#### Important (Should Fix)
#### Minor (Nice to Have)

### Assessment
Task quality: Approved | Needs fixes
Reasoning: 1-2 sentences.
