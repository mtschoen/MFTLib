# Problem statements on pull requests

Every pull request answers to a problem statement: whatever triggered it. The
reviewer's problem-fit axis, and its mechanical evidence check, judge the diff
against that statement. A pull request without one has nothing to be judged
against. Ruled on
[schoen-lab issue 2996](https://gitea.fleet.sticktoitive.net/schoen/schoen-lab/issues/2996).

## Where the statement comes from

| Pull request | Problem statement |
| --- | --- |
| Crew PR triggered by an issue | The linked issue: `Closes owner/repo#N` in the description. The issue body and comments are the evidence. |
| Crew PR triggered by a prompt (dashboard, `.plans` bullet, general-work button, CI auto-fix) | The prompt, stored verbatim in the job's `body` column and written into the description under `## Problem`. |
| PR opened by the owner or an interactive agent session | A `## Problem` section in the description, unless it links an issue with `Closes` / `Fixes` / `Resolves`. |
| PR opened by an automated producer (for example a `sync-consumers` pin bump) | A `## Problem` section the producer writes: which upstream moved, from what pin, to what. |

## The `## Problem` section

- The heading is exactly the literal line `## Problem` at column zero outside
  code fences. A quoted `> ## Problem` or a Setext underline (`Problem\n-------`)
  does not count. `### Problem`, `## Problems` and `## Problem fit` do not count.
- The section runs to the next heading whose source line starts with `## ` at
  column zero outside code fences, or to the end of the description. A quoted
  heading or Setext underline does not end it. Only the first Problem section
  is read.
- A `## Problem` line inside a fenced or indented code block is example text: it
  neither opens nor ends the section, so an example cannot stand in for the
  statement.
- The section is read verbatim, HTML comments and fenced content included, so
  a reviewer can quote any part of it. A section that holds nothing but HTML
  comments, such as a template placeholder `<!-- what was observed -->`, is
  not a statement.
- State what was observed or requested, not the fix. "The nightly sweep
  deletes worktrees that still hold uncommitted work" is a problem statement;
  "Add a dirty-tree guard" is a summary of the fix and belongs under
  `## Summary`.

Example:

```markdown
## Problem

`pr-crew show <id>` prints "not found" for every job id, including jobs that
`pr-crew list` shows as queued.

## Summary

Look jobs up by integer id instead of by the formatted label.
```

The parser and the renderer both live in
`llm_harness.problem_statement` (`parse_problem_section`,
`render_problem_section`), so the writing side and the reading side cannot
drift apart. When pr-crew renders a stored prompt, level-one and level-two
headings outside code fences become level three so they stay inside the
section; fenced lines and HTML comments are written unchanged.

## What the review does with it

- **An issue is linked:** unchanged. The reviewer's `problem_fit_quote` must
  be copied from the issue, its comments, or directly linked issues and PRs.
- **No issue, but a `## Problem` section:** that section is the statement,
  and only that section. The quote must come from it (or from linked
  evidence). A quote copied from elsewhere in the description fails the
  evidence check exactly as an invented quote does: problem-fit is forced to
  `fail` and a Grade A is clamped to B.
- **No issue, no stored prompt, no `## Problem` section:** the review reports
  `no problem statement: link an issue or add a ## Problem section`. This is
  its own outcome, not a problem-fit failure on the merits:
  - a Grade A is held at B (`problem_fit_clamped` is set; the merge train
    lands only Grade A, so the PR does not land);
  - problem-fit is left as the reviewer reported it, except that a reviewer
    `fail` becomes `weak`;
  - there is no WRONG PROBLEM header, no `needs-human` label, no rejection
    notice, and no automatic `/iterate` for a held A, because changing code
    cannot supply a missing statement.

  To clear it, edit the description to add a `## Problem` section (or link
  the issue), then post `/review-force`: `/review` is refused on an unchanged
  head already graded B.

A closing reference that points at an issue the reviewer cannot fetch is
still a linked statement: the review does not report it as missing, and the
evidence check has no text to verify against.

## What the engine refuses

A `create_pr` job must name a source issue (`source` is `gitea-issue` with a
`source_ref`) or carry its prompt as the job `body`. `insert_job` raises
`MissingProblemStatementError` otherwise, and the runner fails a stored job
with neither before spawning a harness. The refusal text names both missing
pieces. `pr-crew enqueue` and `pr-crew enqueue-all` report it as `refused`.
