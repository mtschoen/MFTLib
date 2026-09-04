# Handoff: packed index plan 2 (native size and modified time, producer seam)

Branch `feat/index-mft-producer`, worktree `C:\Users\mtsch\MFTLib-worktrees\mft-producer`.
Plan: `docs/superpowers/plans/2026-09-03-packed-index-mft-producer.md` (20 tasks, 5 phases).
Tracking issue: MFTLib#128 (owns #116). No PR yet. Last verified state: 2026-09-04 evening.

## Where the five-plan train stands

Plan 1 (`MFTLib.Index`) merged as MFTLib#115, main `607e553`. Plans 3 (file-wizard
port), 4 (git-wizard port), and 5 (docs, aislop rule, measurement) have not started.

## Plan 2: landed on the branch, all reviewed

| Task | Commit | Note |
| --- | --- | --- |
| 1 fixture | `fc07f46` | |
| 2 compact entry v4 | `bebd0a1` | `MftRecordFields` struct per the maxParams ruling |
| 3 modified time | `da3c5a4` + `bb6d918` | clang-format follow-up |
| 4 size from `$DATA` | `5102605` + `cc16d58` + `72ade77` | plan defect fixed, see below |
| 6 root row header field (#116) | `e65b6ce` | |
| 5 broker record mapping | `0f9dfe5` | |
| 7 named block sections | `4696fea` | one parked finding, see below |
| 8 MFT producer seam | `12f684b` | |
| 10 block capacity planner | `5e88e35` | |

Verified: Windows full filtered suite green at every integration point (1042/0/3 at
`4696fea`, 1051/0/3 at `72ade77`); Linux (llamabox) smoke 20/20 at `72ade77` and
`coverage-linux.sh` 753/0/51 at `4696fea`. Red states for tasks 3 and 4 and for the
Task 4 regression test were demonstrated on Linux by swapping the pre-fix parser in.

## Task 4: a plan defect, fixed

The brief's `TryExtractDataSize` snippet read the non-resident `FileSize` (bytes 48
to 56 of the attribute) under a 24-byte guard. `cc16d58` adds a 64-byte non-resident
header guard on the existing whole-record rejection path; `72ade77` adds the native
regression case `malformed_nonresident_data_length`, which locates record 7's unnamed
`$DATA` at runtime (the fixture uses 1024-byte records, `$DATA` at 0x100) and fails
on the pre-fix parser with "malformed record 7 was accepted (size=1234567)". Nothing
is open on Task 4.

## Parked for the owner

- Task 7: `NamedBlockSection.Create` returns `(Block, Lifetime)` that alias one
  `MemoryMappedFile`, exactly as the brief mandates. Task 14's usage (keep the block
  view, dispose only the lifetime after `ScanReady`) works because a view accessor
  outlives its `MemoryMappedFile`; disposing the block before the broker opens the
  name would close the section. Needs a doc comment stating the disposal order, and
  the owner's confirmation that the aliasing is intended.
- Deferred minors are listed in the ledger (`Task N: minor (deferred)` lines) for the
  final whole-branch review.

## Next

Task 9 (FileIndex selects the producer; context staged at `task-9-context.md`,
brief at `task-9-brief.md`), then tasks 11 to 20 in plan order. Task 16 is an
attended elevated checkpoint on chonkers. Tasks 17 and 18 break file-wizard and
git-wizard until plans 3 and 4.

## Lanes

Codex `gpt-5.3-codex-spark` weekly cap is exhausted until 2026-09-08 03:29;
`gpt-5.6-sol` works. agy `gemini-3.8-flash-high` works with the fixed driver
(`lanes/dispatch-lane.sh`). kimi is out for the week. Sonnet subagents did the
reviews. Lane worktrees are created from the current feature tip with the native
DLL copied into `x64\Release`; managed-only tasks never build native.

## Steamdeck cutover: not ready

`ssh deck@steamdeck` works (bare `ssh steamdeck` does not); no MFTLib checkout, no
`claude` CLI on the host, `cpp-dev` distrobox stopped for weeks, aggressive suspend
drops sessions. Provisioning it is its own task.
