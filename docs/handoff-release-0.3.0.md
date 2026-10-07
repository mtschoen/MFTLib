# Handoff: MFTLib 0.3.0 Release

Updated 2026-10-05. `CHANGELOG.md` is the authoritative description of 0.3.0;
this document tracks the remaining release sequence.

## Status

The pre-ship simplification pass and consumer API migrations are merged. The remote named `origin`
in some checkouts is a stale GitHub mirror; Gitea `main` is canonical. MFTLib main is `d827b99`
(the last code commit, merged in pull request 388). Merged to `main`:

- Per-drive watch channels ([MFTLib issue 265, per-drive channels](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/265), merged in pull request 301):
  `BrokerProcess` owns one control pipe plus one channel per scan or watch
  operation; `FileIndex` runs watch lifecycle, rescan and recovery per drive.
- `MFTLib.TestExtensions` packaging ([MFTLib issue 264, release.ps1 packs only MFTLib](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/264), closed): `MFTLibTestExtensions.csproj`
  is packable (`PackageId` `MFTLib.TestExtensions`) and `scripts/release.ps1`
  packs it after `MFTLib`, validates both packages with
  `scripts/Test-ReleasePackages.ps1`, and on `-Publish` pushes `MFTLib` first,
  then `MFTLib.TestExtensions`.
- `BrokerScanOptions.IncludeFreed` opt-in scan of freed MFT records and `SearchQuery.IncludeDeleted` ([MFTLib issue 292, include-freed scan](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/292)); the
  native ABI is version 6.
- Pre-ship simplification pass: 17 types made internal and public members with no
  caller deleted; `DriveStatus.AccessDeniedSubtreeCount` and `SkippedRecordCount`
  split by producer; the broker scan frame trimmed to the advanced cursor;
  `FileId` renamed to `IndexRecordKey`; managed test hook declarations moved into
  `MFTLib.Tests`; every public member documented, with `MFTLib.xml` in the package.
- Public API internalization pass: test-only API is internal and reached through the `MFTLib.TestExtensions` package.
- The public `BrokerSession` type owns a consumer session's elevated broker; both consumers use it.
- `SyntheticBlockEditor.SetCacheTag` replaces a cached block's cache tag in place.
- The samples show a heads-up dialog before a self-elevating relaunch, and an attended unelevated broker run (the Watch `scan-drive` verb) shows one before the broker's UAC prompt; `MFTLIB_SAMPLE_UNATTENDED=1` skips the dialog and every elevation request.
- `scripts/build-windows.ps1` is the one Windows native and managed build recipe.
- `BrokerMftBlockProducer`, both `BrokerProcess.LaunchAsync` overloads and `BrokerProcess.GrowUsnJournalAsync` are internal ([pull request 385](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/385)); consumers reach the broker through `BrokerSession`.
- The post-clean init verification in `.gitea/workflows/test.yml` runs on pushes to `main` only, not on pull requests, and the host-mode Windows CI jobs no longer use `actions/setup-dotnet` ([pull request 386](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/386), [MFTLib issue 380](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/380)).
- MFTLib.Tests runs test classes in parallel (`Parallelize`, class level); classes that touch process-global seams carry `[DoNotParallelize]`, a guard test enforces it, and tests hold any finalizable snapshot they assert on (pull requests [387](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/387) and [388](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/pulls/388), [MFTLib issue 382](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/382)).

The 0.3.0 NuGet artifact set is:

- `MFTLib` version 0.3.0, including `MFTLibNative.dll` for Windows x64; and
- `MFTLib.TestExtensions` version 0.3.0, a pure managed test package with an
  exact-version dependency on `MFTLib` 0.3.0. It contains
  `BrokerDiagnosticsIsolation`, `BrokerTestHarness`, `CacheDirectoryIsolation`,
  `FileIndexTestAccess`, `InProcessBrokerHandle`, `InProcessBrokerScan`, `JournalIsolation`,
  `ScriptedBrokerVolumes`, `ScriptedDriveWatch`, `ScriptedScan`, `ScriptedWatchSource`,
  `ScriptedWatchStart`, `SyntheticBlock`, `SyntheticBlockEditor`, `SyntheticBlockOptions`,
  `SyntheticCacheTag`, `SyntheticCheckpointLoss`, `SyntheticDriveHeader`,
  `SyntheticIndexInspection`, `SyntheticIndexSource`, `SyntheticJournalEntry`,
  `SyntheticJournalEntryOptions`, `SyntheticJournalWindow`, `SyntheticMftProducer`,
  `SyntheticMftRecord`, `SyntheticMftRecordOptions`, and `SyntheticRow`.

Consumers have migrated to per-drive watch channels, the post-simplification API, and `BrokerSession`.
Both consumers pin MFTLib `d827b99` through their `external/MFTLib` submodule (the C4 round):
file-wizard main `cf631ca` (file-wizard pull request 546) and git-wizard main `9c8710c` (git-wizard
pull request 296).
No attended smoke has run against the C4 pin; the attended consumer runtime smokes remain
part of gate 2.

Not yet done: 0.3.0 is not published to nuget.org and `v0.3.0` is not tagged.

### Remaining gates, in order

2. Rerun the attended checklists, including the consumer runtime smokes in
   step 2 below: [MFTLib issue 72, attended MFTLib checklist](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/72),
   [file-wizard issue 299, attended checklist](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/299),
   [git-wizard issue 143, attended checklist](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/143).
3. Release dry run (step 3).
4. Publish (step 4, owner only).
5. Retire the consumer bridges (step 5):
   [file-wizard issue 288, retire submodule bridge](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/288),
   [git-wizard issue 134, retire submodule bridge](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134).

### Validation measurements

Measured 2026-09-10 on Linux (`scripts/coverage-linux.sh`), before the per-drive
channel merge (pull request 301); these must be re-measured at the release commit:

- Linux managed test suite: 1030 total tests (955 passed, 0 failed, 75 skipped for Windows/elevated features);
- Linux managed coverage (coverlet): MFTLib line 94.16% (5845/6207), branch 91.69% (1392/1518), method 97.26%; MFTLibTestExtensions line 100%, branch 100%, method 100%;
- Linux native coverage (gcovr): MFTLibNative 75.9% line (996/1313), 50.2% branch (487/970), 75.5% function (83/110).

Pending at the release commit: Windows test counts (including elevated
administrator tests), Windows native instrumentation coverage, and
`aislop ci .`, all from the attended run on Windows host `chonkers`.

## Release checklist

### 1. Synchronize mirrors

Ensure the exact merged history on Gitea `main` is mirrored to GitHub so SourceLink
(`PublishRepositoryUrl=true` + `SourceLink.GitHub`) can resolve the commit that will be packed.
`schoen/MFTLib` on Gitea has a push mirror to `https://github.com/mtschoen/MFTLib.git` that syncs on
every commit and every 8 hours; it synced at the last `main` merge with no error. Before running
`scripts/release.ps1`, confirm the mirror's last sync covers the release commit (`scripts/release.ps1`
refuses to run unless the release commit is present on GitHub `main`). The manual push below is the
fallback when the mirror is behind or failing:
Note: remote names for Gitea and GitHub vary per checkout, and
`scripts/release.ps1` pushes the release tag directly to the Gitea and GitHub
URLs rather than through a local remote name, so use the URLs directly here
too, regardless of local remote naming:

```bash
git switch main
git pull --ff-only https://gitea.fleet.sticktoitive.net/schoen/MFTLib.git main
git push https://github.com/mtschoen/MFTLib.git main
```

### 2. Validate downstream consumers (pre-publish sanity check)

Run against the C4 pin, MFTLib `d827b99`, and the C4 consumer commits named in Status
(file-wizard `cf631ca`, git-wizard `9c8710c`). Both consumers build MFTLib
from the `external/MFTLib` submodule until 0.3.0 ships.

- **file-wizard broker and index smoke**: launch the elevated broker through
  `BrokerSession` and open a `FileIndex` with
  `BrokerSession.CreateIndexSource()`; run a cold scan into a file-backed block section and
  verify `MFTLib.Index` query evaluation. Start watches through
  `FileIndex.StartWatchingAsync` (one watch per drive from the index source): modify files on two drives and verify `FileChange` events arrive
  per drive, exercise `RescanAsync` and stop-and-restart of one drive while the other
  keeps running, and confirm one UAC prompt covers the whole session. Kill the broker
  mid-watch and verify every watched drive reports a terminal `WatchFaultKind.Channel`
  fault that does not recover on its own; a broker restart needs a fresh session.
- **git-wizard watch smoke**: open the index through `MftIndexSession` and
  `BrokerSession`; run `git-wizard --watch`, modify files
  within a tracked repository, and verify live notifications via `FileChange`
  (`IndexVolumeChangeSource`). Kill the broker mid-watch and verify the watch
  reports a terminal `WatchFaultKind.Channel` fault that does not recover on its
  own; broker restart needs a fresh session (git-wizard caches its connection task
  and excludes terminal faults).
- **Automatic recovery smoke (separate)**: recovery is queued only for
  `WatchFaultKind.Drive` and `Apply` faults. Provoke a recoverable drive fault
  (for example, a journal read failure on one drive) and verify the drive reads
  `WatchCatchUpState.Recovering`, rescans once, and resumes, while a failed recovery
  raises `WatchFaultKind.Recovery` and leaves the drive faulted.

The Windows attended coverage run updates `TEST-REPORT.md` before final publish.

### 3. Release dry run

On Windows (`chonkers`):

```powershell
.\scripts\release.ps1
```

This requires a clean tree, no existing `v0.3.0` tag, and the release commit already present
on both Gitea `main` and GitHub `main` (the script verifies each with `git ls-remote` and
`git merge-base --is-ancestor` before doing anything else, in both dry-run and `-Publish` modes,
and prints the Gitea and GitHub URLs that will receive the tag).
It resolves 64-bit MSBuild via `vswhere`,
executes `scripts/run-coverage.ps1 -Configuration Release` (verifying full managed and elevated coverage),
and packs `MFTLib.0.3.0.nupkg`, `MFTLib.TestExtensions.0.3.0.nupkg`, and their
`.snupkg` files with `ContinuousIntegrationBuild=true`. It validates package
identity, version, the exact-version dependency, the native DLL and build targets,
each package's license and readme, and the presence of both symbol files. It does not publish.

### 4. Publish (Owner only)

On Windows (`chonkers`):

```powershell
.\scripts\release.ps1 -Publish
```

Publishing requires the NuGet key at `C:\Users\mtsch\nugetkey` (`~/nugetkey`), authenticated GitHub tooling (`gh`),
and the exact release commit already pushed to GitHub (`git push https://github.com/mtschoen/MFTLib.git main`,
or `git push <your github remote> main` if one is configured).
The script:
1. Pushes `MFTLib`, then `MFTLib.TestExtensions`, to nuget.org via `dotnet nuget push`.
2. Creates the git tag `v0.3.0` and pushes it directly to the Gitea URL
   (`gitea@gitea.fleet.sticktoitive.net:schoen/MFTLib.git`) first, then the GitHub URL
   (`git@github.com:mtschoen/MFTLib.git`), never through a local remote name. The Gitea
   push is fatal on failure, since Gitea is the canonical forge and its push mirror to
   GitHub can prune refs GitHub-only pushes would otherwise leave behind.
3. Creates the GitHub release with `CHANGELOG.md` release notes via `gh release create`.

### 5. Replace temporary consumer bridges

Publishing 0.3.0 unblocks [file-wizard issue 288, retire submodule bridge](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/288) and [git-wizard issue 134, retire submodule bridge](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134).
Both consumers carry the same bridge: the `external/MFTLib` git submodule and a root
`Directory.Build.targets` whose header documents the retirement steps. The workflows
at `gitea/main` that build the submodule (native, managed or test extensions):

- file-wizard: `.gitea/workflows/ci.yml`, `aislop.yml`, `preview.yml`.
- git-wizard: `.gitea/workflows/ci.yml`, `preview.yml`, `release.yml` (its pull request
  trigger paths include `Directory.Build.targets` and `.gitmodules`), and
  `screenshot.yml`. git-wizard's `aislop.yml` does not build it; it only excludes
  `external/MFTLib` from the scan.

- **file-wizard:** remove the `external/MFTLib` submodule (`.gitmodules`) and its solution entries,
  delete the root `Directory.Build.targets`, add
  `<PackageReference Include="MFTLib" Version="0.3.0" />` to
  `FileWizard/FileWizard.csproj`, add
  `<PackageReference Include="MFTLib.TestExtensions" Version="0.3.0" />` to
  test projects that use `BrokerTestHarness`, and remove the MFTLib build steps from
  `ci.yml`, `aislop.yml` and `preview.yml`.

- **git-wizard:** remove the `external/MFTLib` submodule, delete the root
  `Directory.Build.targets`, add
  `<PackageReference Include="MFTLib" Version="0.3.0" />` to
  `GitWizard/GitWizard.csproj`, add
  `<PackageReference Include="MFTLib.TestExtensions" Version="0.3.0" />` to
  test projects that use `BrokerTestHarness`, remove the MFTLib build steps from
  `ci.yml`, `preview.yml`, `release.yml` and `screenshot.yml` (and the `release.yml`
  trigger paths for the deleted files), and verify required checks.

- **MFTLib:** delete `.gitea/workflows/sync-consumers.yml`, `scripts/sync_consumers.sh`,
  `scripts/sync_consumers.tests.sh` and the `Test the consumer sync script` step of the
  `linux` job in `.gitea/workflows/test.yml`. The workflow runs by hand only until then.
  `sync_consumers.sh` exits 1 when no consumer matches, which is every push once the
  submodules are gone, so the script must not outlive the bridges.

### 6. Release notes highlights (0.3.0)

The release notes in `CHANGELOG.md` and GitHub Release must match `CHANGELOG.md` and highlight:
- **On-disk block format**: fixed 64KB-aligned binary block format (`MFTLib.Index`) storing path strings, file sizes, timestamps, attributes, sequence numbers, and directory hierarchies without in-memory object allocation overhead (see `docs/index-format.md`).
- **`MFTLib.Index` namespace**: indexed query and file snapshot model (`FileIndex`, `Snapshot`, `FileEntry`, `FileChange`) with low-latency query evaluation and direct directory traversal.
- **Broker block write path**: the elevated broker writes cold scan blocks directly into a client-owned file-backed block section; cold scans return packed blocks only.
- **Per-drive watch channels**: `BrokerProcess` runs one control pipe and one channel per drive operation; `FileIndex` start, stop, rescan and catch-up are per drive with concurrent list and all-drive overloads, automatic per-drive recovery, and bounded catch-up-loss recovery.
- **Include-freed scan**: `BrokerScanOptions.IncludeFreed` imports freed MFT records as scan-scoped rows with `FileEntry.IsDeleted` true, and `SearchQuery.IncludeDeleted` searches them; native ABI version 7.
- **`MFTLib.TestExtensions` package**: `BrokerTestHarness` and the cache and journal isolation guards ship as a separate package.
- **Documented public API**: the package ships `MFTLib.xml`, so IntelliSense documents every public member.

## Known issues (resolved)

- **Elevated build-start hang**: Resolved in PR #56 / #57 by resolving MSBuild via `vswhere` in `scripts/release.ps1` and `scripts/native-coverage.ps1`.
- **Admin test exit hang**: Historic intermittent UAC exit hang was not reproduced across recent attended runs.
