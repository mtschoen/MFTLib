# Handoff: packed index plan 2 (native size and modified time)

Written by a `/wrap --fast` on 2026-09-04. Branch `feat/index-mft-producer`,
worktree `C:\Users\mtsch\MFTLib-worktrees\mft-producer`. Plan:
`docs/superpowers/plans/2026-09-03-packed-index-mft-producer.md` (20 tasks,
5 phases). Issue: MFTLib#128.

## Where the five-plan train stands

Plan 1 (`MFTLib.Index`: block format, FileIndex, snapshots, queries, mutation,
enumeration producer) merged as MFTLib#115, commit `607e553` on main. Plans 3
(file-wizard port), 4 (git-wizard port), and 5 (docs, aislop rule, measurement)
have not started. The prerequisite file-wizard#345 merged 2026-09-02.

## Where plan 2 stands

Committed on the branch, in order:

- `124cca4` plan document
- `26b7251` the three orchestrator decisions confirmed
- `fc07f46` Task 1, deterministic size and modified-time synthetic MFT fixture
  (`MFTLibNative/mft/mft_synthetic.fixture.cpp`)
- `bebd0a1` Task 2, `MftCompactEntry` widened with size and modified time,
  native interface bumped to version 4

Everything through Task 2 is pushed to `gitea/feat/index-mft-producer`. No PR is
open yet.

## Uncommitted work in the tree: Task 3, unverified

Task 3 (extract modified time from `$STANDARD_INFORMATION`) is **written but was
never built and never run**. Three dirty files:

- `MFTLibNative/mft/mft.records.cpp` - the real change. `TryExtractStandardInformation`
  and `FindNamedAttribute` were refactored from a pair of out-parameters
  (`siAttributes` / `sawStandardInformation`) to a `StandardInformationValues`
  struct carrying `fileAttributes`, `modifiedTime`, and `present`. Modified time
  is read at offset 8 of the `$STANDARD_INFORMATION` body ("last altered"), and
  `ScanRecordForEntry` now populates `outEntry->modifiedTime`, falling back to
  the `$FILE_NAME` attribute's `ModificationTime` when `$STANDARD_INFORMATION`
  is absent.
- `MFTLib.Tests/MftFixtureTests.cs` - managed assertions over the fixture.
- `MFTLibNative/test/linux_smoke_test.cpp` - native smoke assertions.

**First action for the next session is to build and run, not to write more code.**
The existing 36-byte guard in `TryExtractStandardInformation` is claimed in a
comment to cover both the offset-8 and offset-32 reads; that claim is reasoned,
not yet demonstrated by a passing test. Per the plan's own TDD steps, the tests
were supposed to be run failing first, and that did not happen here.

Task 4 (extract size from the unnamed `$DATA` attribute, plan lines 857-1043) has
not been started. `outEntry->size` is still left at zero. Tasks 5 through 20
(root row header field MFTLib#116, named block sections, the producer seam, the
broker block write path, attended real-drive verification, `ScanPayload` and
`ScanRecord` deletion, docs, coverage gate) are untouched.

## Steamdeck cutover: attempted, not achieved

The intent was to continue this work from the Steam Deck while travelling. It is
**not ready**, and the setup is more than a few minutes of work:

- `ssh deck@steamdeck` works from chonkers. A bare `ssh steamdeck` does not: it
  sends the Windows username and fails with `Permission denied (publickey,password)`.
- There is **no MFTLib checkout** on the Deck.
- There is **no `claude` CLI** on the Deck host. Whether one exists inside the
  `cpp-dev` distrobox was not checked.
- The `cpp-dev` distrobox (Ubuntu 24.04, the only C/C++ toolchain on the
  immutable SteamOS rootfs) has been **stopped for seven weeks** and would need
  starting and probably refreshing.
- The Deck suspends aggressively; an established SSH session dropped mid-command
  during this session.

Phase 1 of this plan is genuinely Linux-testable, so the Deck is a reasonable
target once provisioned. Provisioning it is its own task.

## Also open

Plan 1 landed with eleven follow-up issues against `MFTLib.Index`, MFTLib#117
through MFTLib#127. None block plan 2. The ones with teeth are MFTLib#126
(snapshot rollback can unmap blocks still held) and MFTLib#127 (spurious
`Deleted` change for a record that was never in use).

`file-wizard` has an uncommitted submodule pointer move on `external/MFTLib`
(`bd2536b` to `9b7046d`). It predates this session and is stale against MFTLib
main either way; plan 3 supersedes it.
