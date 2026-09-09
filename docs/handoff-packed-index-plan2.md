# Handoff: packed index plan 2 (native size and modified time, producer seam)

Branch `feat/index-mft-producer`, worktree `C:\Users\mtsch\MFTLib-worktrees\mft-producer`.
Plan 2 (20 tasks, 5 phases) is complete; its plan file is removed from the branch. The design
spec remains at `docs/superpowers/specs/2026-09-02-packed-index-design.md` for plans 3 to 5.
Tracking issue: MFTLib#128 (owns #116). PR opened as claude-code; number in the tracking issue.
Tip `1ae12b5`. The attended verification remains at `32f40b9` below.

## Where the five-plan train stands

Plan 1 (`MFTLib.Index`) merged as MFTLib#115. Plan 2's tasks 1 to 20 landed on this branch, the
whole-branch review ran, three fix waves applied their findings, and the PR is open. Plans 3
(file-wizard port), 4 (git-wizard port), and 5 (docs, aislop rule, measurement) have not
started.

**Owner directive 2026-09-08 for every remaining plan and brief:** this is an incubating project
with no external consumers. No back-compat surfaces, transitional wire fields, legacy overloads,
or compatibility parameters: delete in place, break the consumers, and let them pin a commit. A
package being published to NuGet does not make its surface a compatibility obligation. Plan 2
paid for this lesson (decision 6's transitional output-format field, and Task 17's fix round).

## Branch state

Based on main `1c58b2d` (crew follow-ups #129 and #130). Tip `1ae12b5`, pushed to both Gitea
and GitHub.

Verified at `1ae12b5`: Windows full suite via `scripts/run-coverage.ps1` 1066 passed / 0 failed /
3 skipped, MFTLib coverage 96.93 percent line, 92.93 percent branch, 99.3 percent method; Linux
(llamabox) smoke 20/20, `coverage-linux.sh` managed 772/0/64 (exit 64 because gcovr is not
installed there; the managed counts are valid); `aislop` 99/100 with the two remaining warnings
in files this branch never touched (`MFTLib.Tests/Index/BlockValidationMatrixTests.cs:158`
AsyncFixer02, `MFTLib.Tests/MftVolumeAdminTests.cs:401` IDISP001); the whole-repo score at
merge-base `1c58b2d` was 88.

## Review and fix waves

Task 20 (codex gpt-6-astra) fixed a scheduling race in a native-progress test (a bounded
DropOldest channel could drop every Parsing frame) and cleared 32 aislop warnings in
branch-touched files.

The whole-branch review then ran over `1c58b2d..HEAD` as seven slices: broker on opus; index,
native, and tests-other on sonnet; tests-session and tests-hostclient on codex gpt-6-astra; docs
on agy gemini-3.8-flash-high.

Fix wave 1 (sonnet): `DriveStatus.MftProducerFailureMessage` cleared after a recovered scan; a
cursor-mismatched MFT block is deleted before the mismatch throws (it used to stay on disk and
warm-start a later `ProducerPolicy.MftOnly` open).

Fix wave 2 (codex): a negative non-resident `$DATA` FileSize now reads as size-unknown instead
of a bogus size with `SizeKnown` true; fixture record 11 `negative-size.bin` covers it on both
platforms.

Fix wave 3 (opus): `JournalBrokerScanSession` disposes the blocks of a superseded or rejected
scan result, and of `LatestScan` on `DisposeAsync` (the session owns `LatestScan`'s blocks while
it holds them); `keepFileNames` dropped from `StartFromCursorsAsync`; `BrokerScanOptions`
required on `ArmScanAndCatchUpAsync` and `RescanAsync`; `BrokerScanResult` constructor defaults
removed; `scanCompleted` runs only after the block validates (an errored drive no longer reaches
the callback; the error still throws). Review minors: duplicate unknown-profile guard removed,
`ParentRecordNumber` got the same uint guard as `RecordNumber`, `EstimateRowCount` clamps below
the checked-add overflow, `BrokerScanPhase.Transferring` renumbered to 1, doc sentences added on
`ServeAsync`'s null writer and `BrokerScanProgress`'s byte columns. Four leaking or hollow tests
fixed: two end-to-end tests that leaked their returned blocks, one progress test that compared
real elapsed time under a false suppression, and one duplicate-watch test that asserted nothing
after the duplicate.

## Follow-ups to file (not filed)

- Mmf-to-section identifier rename (`JournalBrokerClient._mmfLifetimes`, `TakeMmfLifetime`,
  `BrokerFrame.RequireMmfName`, `ScanDriveRequest.MmfName`).
- `ParseScanSpecForTest` test-only accessor in `JournalBrokerHost.Scan.cs`.
- Elapsed-time propagation from host to client is untested; needs an injected clock on
  `ScanProgressState`.
- `BrokerScanOptions.BlockTargets` still nullable while documented as required.
- The redundant root-row tests in `LookupEngineTests`.
- `BlockLayout.ComputeSlotCapacity` checked-add headroom (plan 1 leftover).
- Resident-then-non-resident multi-`$DATA` fixture gap.
- `nameAttr` local in `ScanRecordForEntry`.
- `linux_smoke_test.cpp` over the size cap.

## Plan 2: landed on the branch, all reviewed

| Task | Commit | Note |
| --- | --- | --- |
| 1 fixture | `fc07f46` | |
| 2 compact entry v4 | `bebd0a1` | `MftRecordFields` struct per the maxParams ruling |
| 3 modified time | `da3c5a4` + `bb6d918` | clang-format follow-up |
| 4 size from `$DATA` | `5102605` + `cc16d58` + `72ade77` | plan defect fixed (non-resident header guard) |
| 6 root row header field (#116) | `e65b6ce` | merged with crew #129 on rebase |
| 5 broker record mapping | `0f9dfe5` | |
| 7 named block sections | `4696fea` | lifetime aliasing now documented on `NamedBlockSection.Create` (Task 14 fix round) |
| 8 MFT producer seam | `12f684b` | |
| 10 block capacity planner | `5e88e35` | |
| 9 FileIndex selects the producer | `a288e8a` + `fc01294` | one fix round, one ruling (below) |
| 11 output-format spec field | `c43aa8c` | codex gpt-6-astra lane |
| 12 broker writes rows into the section | `5be432d` | codex gpt-6-astra lane; `IBlockSectionWriter.Write` takes the armed cursor |
| 13 DirectoryIndex filter in block mode | `c10776e` | agy gemini-3.8-flash lane; `MftBlockRowFilter` threaded through the writer interface |
| 14 client creates and adopts the block | `2807982` + `ed3b3c0` + `80c5d49` | codex gpt-6-astra lane; one doc-only fix round; `80c5d49` is the controller's one-line cross-lane merge fix |
| 15 end-to-end tests | `cefdbe3` + `bf2bed4` | codex gpt-6-astra lane; `cefdbe3` is a real defect fix (below) |
| 16 attended verification | `32f40b9` | real-drive sign-off below |
| 17 block-only output | `1295017` + `b6d030f` | removed payload writers and output-format selection; `ScanReady` carries skipped records |
| 18 payload retirement | `3b501f2` | removed legacy session overloads and the payload carrier |
| 19 documentation | `d5bfb40` | codex gpt-6-astra lane; CHANGELOG Unreleased section, docs, dropped the dead `keepFileNames` parameter on `StartFromCursorsAsync` |
| 20 gate sweep | `32d2478` + `168b611` | fix(tests): make native progress assertion deterministic; chore(broker): coverage and aislop gate pass |
| review + fix wave 1 (sonnet) | `815f8e0` + `283af72` | fix(index): clear MftProducerFailureMessage after a recovered scan; fix(index): delete a cursor-mismatched MFT block before throwing |
| fix wave 2 (codex) | `a51974f` | fix(native): treat a negative non-resident $DATA size as unknown |
| fix wave 3 (opus) | `8184536` + `439f99c` + `0a15596` + `576dabc` + `2d04ba8` + `6e2caaf` + `1ae12b5` | fix(broker): dispose the scan blocks a session gives up; fix(broker): document who owns a scan result's blocks; fix(broker): drop the dead keepFileNames parameter from StartFromCursorsAsync; fix(broker): require BrokerScanOptions on the scan entry points; fix(broker): invoke scanCompleted only after the block validates; refactor(broker): review minors; test(broker): close the leaks and empty assertions the review found |

(Commit ids before the 2026-09-08 rebase are the pre-rebase ids; `git log` shows the rewritten ones.)

## Interfaces at 1ae12b5

- `IBlockSectionWriter.Write(string sectionName, UsnJournalCursor cursor, IEnumerable<IReadOnlyList<MftRecord>> batches, MftBlockRowFilter filter, IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)`; `MftBlockRowFilter(BrokerScanProfile Profile, IReadOnlyCollection<string>? KeepFileNames)` with a static `Full`.
- `JournalBrokerHost` second constructor parameter `MftRecordBatchSource scanDrive`; `ServeAsync(Stream stream, IBlockSectionWriter? blockSectionWriter, bool oneShot, CancellationToken cancellationToken)`; block arm in `JournalBrokerHost.BlockScan.cs`.
- `JournalBrokerClient` second constructor parameter `Func<string, BlockFileCreateOptions, (string SectionName, BlockFile Block, IDisposable Lifetime)> createDriveBlockSection` (production: `CreateRealDriveBlockSection`, wired by `SpawnAndConnectAsync`). The returned lifetime IS the block's own mapping handle; the doc comments state the invariant.
- `JournalBrokerClient.ArmScanAndCatchUpAsync(IReadOnlyList<string> drives, BrokerScanOptions options, CancellationToken cancellationToken = default)`; `options` is required and non-nullable, matching what `ValidateBlockTargets` already enforced at runtime.
- `JournalBrokerScanSession.StartFromCursorsAsync` has no `keepFileNames` parameter on either public overload; a warm session's later `RescanAsync` supplies keep-file names through its own options.
- `JournalBrokerScanSession.RescanAsync(BrokerScanOptions options, CancellationToken cancellationToken = default)` and the drives-taking overload both require `options`.
- `BrokerScanOptions.BlockTargets` is an `IReadOnlyDictionary<string, BlockScanTarget>`; each target supplies `Path`, `VolumeSerial`, and `DeleteOnClose`. Every scan requires a target per drive, runs the volume query, and plans with `MftBlockCapacity.Plan`.
- `BrokerScanResult`'s `warnings` and `blockOutcomes` constructor parameters have no defaults; every cold scan produces both. `BlockOutcomes` is `BlockScanOutcome(SectionName, Block, RowCount, NamePoolUsedBytes, SkippedRecordCount)`; the caller owns `Block`. `ScanReady` carries the three counts as int64 fields.
- `JournalBrokerScanSession` disposes the blocks of a superseded or rejected scan result and of `LatestScan` on `DisposeAsync`; the session owns `LatestScan`'s blocks while it holds them, so a caller that wants a block past the next rescan or past disposal takes it out of the result first.
- `BrokerMftBlockProducer(Func<CancellationToken, Task<JournalBrokerClient>> connectAsync, BrokerScanOptions? scanOptions = null, Action<BrokerScanResult>? scanCompleted = null)`, `CreateProducer()` returns the `MftBlockProducer` delegate. It does not own the client. It validates complete flag, volume serial, `ProducerKind.Mft`, row count above zero, and header cursor equal to the armed cursor, then invokes `scanCompleted` only after that validation succeeds; a drive that errored or a block that was rejected never reaches the callback.
- Test support: `InProcessBlockBrokerHarness` (duplex pipe, fake sources, `RecordingBlockSectionWriter` with a section resolver and an injected completion timestamp) runs everything on Linux.

## Task 16: attended checkpoint, signed off

Sign-off commit `32f40b9` (empty; the numbers live in its message body). Run on chonkers on
2026-09-08 against drive C: 10288535 rows for 10293248 MFT records, name pool 55 percent used,
compaction not needed, block file equals `BlockLayout.TotalBlockBytes`, block scan 8 to 10 s
against 12 s for the payload path (which then hit its pre-existing 2 GiB map cap after 6.28M
records). Ten of ten System32 spot checks match on size and modified time to the second; four
of them (kernel32, ntdll, notepad, shell32) are indexed under their WinSxS hard link only, per
plan decision 12 (one name per record). No production defect found. **`32f40b9` is the pin
for file-wizard and git-wizard until plans 3 and 4 land their ports.**

The script and run log are archived, git-ignored, in the SDD workspace as
`task-16-verify-mft-producer.ps1` and `task-16-verify.log`. Worth keeping in mind for plan 5's
measurement: a pwsh host cannot use `BrokerLauncher.Launch` (it relaunches the current
process expecting an `ElevatedEntryPoint.TryHandle` dispatch), so the script launched an
elevated child pwsh that calls `DefaultElevatedEntryRunner.RunBroker(pipe, oneShot: false)`.

## Rulings and defects for the owner

- Task 9: `DriveStatus.MftProducerFailureMessage` is new public surface; the brief contradicted
  itself ("the failure is recorded" versus "no new public surface"). The field stands; confirm.
- Task 9: a producer must `SetJournalCursor` before `Complete`; `FileIndex` and now
  `BrokerMftBlockProducer` treat a header-versus-armed mismatch as a producer failure.
- Task 12 brief: the writer interface carried a length nobody has and no cursor; resolved by the
  cursor parameter plus `NamedBlockSection.OpenExisting(string)` self-probing the header.
- Task 14: the seam tuple and `BlockScanOutcome` both carry the `BlockFile`; Task 17 kept it
  and resolved the skipped-count gap by adding `SkippedRecordCount` to `ScanReady`.
- Task 15 found a real defect: `FileIndex.RescanAsync` called the enumeration scan directly and
  bypassed `ProducerPolicy` and `MftProducer`; fixed in `cefdbe3` via `ProduceDriveBlockAsync`
  with a regression test in `FileIndexProducerSelectionTests`.
- Fix wave 3: `JournalBrokerScanSession` now owns and disposes `LatestScan`'s blocks
  (superseded, rejected, and at `DisposeAsync`); confirm this ownership model, since a caller
  that previously assumed it kept blocks past a rescan now loses them unless it takes the block
  out of the result first.
- Fix wave 3: `BrokerMftBlockProducer.scanCompleted` now fires only after the block passes
  validation, never for an errored drive or a rejected block; confirm consumers do not depend on
  the callback seeing an unvalidated result.
- Deferred minors are listed in the ledger (`Task N: minor (deferred)` lines) for the final
  whole-branch review: three overlapping root-row tests after the rebase, over-length broker
  files, the tripled unknown-profile guard, the field-by-field `BrokerScanOptions` copy in the
  producer, the magic completion-timestamp literal in the recording writer, and the duplicated
  `ArmedCursor` constant in `BrokerMftBlockProducerTests`.

## Lanes

Codex `gpt-6-astra` (owner-confirmed 2026-09-08) did tasks 11, 12, 14, 15, 17, 18, 19 (25 to 40
minutes each); agy `gemini-3.8-flash-high` did Task 13. Reviews ran on sonnet, the Task 14 and
Task 17 reviews on opus; fix rounds on sonnet. Lane
worktrees are created from the feature tip with the native DLL copied into `x64\Release`;
managed-only tasks never build native. Launch long codex lanes detached with a no-argument wrapper
(`lanes/run-task-N.sh`, paths baked in) passed as the only argument to `Start-Process bash.exe`;
a quoted `bash -c` string in `-ArgumentList` is split by the Windows argument parser and runs
nothing. `lanes/verify-linux.sh <sha> [branch]` verifies a lane branch on llamabox before
cherry-pick. Lane gotcha seen 2026-09-08: a
STOP rule phrased "if the DLL is missing after the build" was applied literally after a RED compile
that copied nothing; phrase such rules "after a build that succeeded".

Ledger (recovery map, git-ignored): the feature worktree's
`.superpowers/sdd/2026-09-03-packed-index-mft-producer/progress.md`; briefs, contexts, dispatches,
reports, reviews, `review-template.md`, and `lanes/` (`dispatch-lane.sh`, `make-dispatch.sh`,
`make-review.py`, `verify-linux.sh`) live beside it. Whole-branch review and fix-wave artifacts
live there too: fix reports as `fix-N-report.md` and review slices as `final-review-<slice>.md`
(plus their `-dispatch.md` companions). A `git clean -ffxd` there deletes all of it.

## Outside this plan

git-wizard#165's llamabox install was refreshed on 2026-09-08 (`~/bin/git-wizard` 0.4.1 with
the double-dash flags); the issue can get a comment that its llamabox half is done. The 0.3.0
checklists and the NuGet publish remain owner-held. The crew fence (`queue:wait`) is still on
MFTLib#72, file-wizard#299 #288, git-wizard#167 #165 #143 #134 #64.
