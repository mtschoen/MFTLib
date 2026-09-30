# Handoff: MFTLib 0.3.0 Release

Updated 2026-09-29. `CHANGELOG.md` is the authoritative description of 0.3.0;
this document tracks the remaining release sequence.

## Status

`main` and `gitea/main` are at `3597586`, the merged per-drive channel
specification. The implementation is complete on `impl/265-per-drive-channels`
at `2889deb`: `BrokerProcess`, one scan or watch channel per drive operation,
per-drive `FileIndex` lifecycle and recovery, concurrent scan admission with
per-chunk thread rebalancing, and the issue 252 fix. It still needs its final
documentation and CI lanes merged to `main`. Consumer ports, issue 264 package
work, and the attended Windows release dry run remain release gates.

Issue 264 makes the 0.3.0 NuGet artifact set:

- `MFTLib` version 0.3.0, including `MFTLibNative.dll` for Windows x64; and
- `MFTLib.TestExtensions` version 0.3.0, a pure managed test package with an
  exact-version dependency on `MFTLib` 0.3.0. It contains
  `BrokerTestHarness`, `BrokerTestHarnessOptions`, `CacheDirectoryIsolation`,
  `JournalIsolation`, and `SyntheticJournalWindow`.

At this handoff commit `MFTLibTestExtensions.csproj` remains non-packable and
`scripts/release.ps1` packs only `MFTLib`, so issue 264 must update both before
the release dry run can validate the complete artifact set.

Validation measured on Linux (`scripts/coverage-linux.sh`):

- Linux managed test suite: 1030 total tests (955 passed, 0 failed, 75 skipped for Windows/elevated features);
- Linux managed coverage (coverlet): MFTLib line 94.16% (5845/6207), branch 91.69% (1392/1518), method 97.26%; MFTLibTestExtensions line 100%, branch 100%, method 100% (overall solution 94.16% line, 91.69% branch, 97.27% method);
- Linux native coverage (gcovr): MFTLibNative is 75.9% line (996/1313), 50.2% branch (487/970), 75.5% function (83/110);
- Windows test counts (including elevated administrator tests) and Windows native instrumentation coverage: pending the attended run on Windows host `chonkers`;
- `aislop ci .`: pending the attended run on Windows host `chonkers`.

0.3.0 is built, validated on Linux, and packable, but not yet published to nuget.org and `v0.3.0` is not tagged.

## Release checklist

### 1. Synchronize mirrors

Ensure the exact merged history on Gitea `main` is mirrored to GitHub so SourceLink
(`PublishRepositoryUrl=true` + `SourceLink.GitHub`) can resolve the commit that will be packed.
A Gitea push mirror to GitHub now syncs `main` on every commit (with an 8 hour fallback
sync), and `scripts/release.ps1` refuses to run unless the release commit is present on
GitHub `main`, so this step is a manual fallback rather than the only line of defense.
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

Consumer ports are in flight and gate the release:
- **file-wizard broker and index smoke**: verify against merged MFTLib `main`; run a
  cold scan via the broker block write path into a file-backed block section; verify
  `MFTLib.Index` query evaluation, live change tracking via `BrokerIndexWatchSource`, and
  rescan reuse without a second UAC prompt.
- **git-wizard watch smoke**: build against merged MFTLib `main`; run
  `git-wizard --watch`, modify files within a tracked repository, and verify live
  notifications via `FileChange`.

The Windows attended coverage run will update `TEST-REPORT.md` before final publish.

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
and packs `MFTLib.0.3.0.nupkg` and `.snupkg` with `ContinuousIntegrationBuild=true` without publishing.

### 4. Publish (Owner only)

On Windows (`chonkers`):

```powershell
.\scripts\release.ps1 -Publish
```

Publishing requires the NuGet key at `C:\Users\mtsch\nugetkey` (`~/nugetkey`), authenticated GitHub tooling (`gh`),
and the exact release commit already pushed to GitHub (`git push https://github.com/mtschoen/MFTLib.git main`,
or `git push <your github remote> main` if one is configured).
The script:
1. Pushes the package to nuget.org via `dotnet nuget push`.
2. Creates the git tag `v0.3.0` and pushes it directly to the Gitea URL
   (`gitea@gitea.fleet.sticktoitive.net:schoen/MFTLib.git`) first, then the GitHub URL
   (`git@github.com:mtschoen/MFTLib.git`), never through a local remote name. The Gitea
   push is fatal on failure, since Gitea is the canonical forge and its push mirror to
   GitHub can prune refs GitHub-only pushes would otherwise leave behind.
3. Creates the GitHub release with `CHANGELOG.md` release notes via `gh release create`.

### 5. Replace temporary consumer bridges

Publishing 0.3.0 unblocks resolving downstream consumer port issues file-wizard#288 and git-wizard#134:

- **file-wizard (unblocks file-wizard#288):** remove the `external/MFTLib` submodule and its solution entries,
  delete the temporary root `Directory.Build.targets`, add
  `<PackageReference Include="MFTLib" Version="0.3.0" />` to
  `FileWizard/FileWizard.csproj`, add
  `<PackageReference Include="MFTLib.TestExtensions" Version="0.3.0" />` to
  broker test projects that use `BrokerTestHarness`, switch to `MFTLib.Index`
  block reading and `BrokerIndexWatchSource`, and remove the CI native-submodule
  build step.

- **git-wizard (unblocks git-wizard#134):** follow `lib/MFTLib/README.md`: add
  `<PackageReference Include="MFTLib" Version="0.3.0" />`, delete the
  vendored DLL bridge and root `Directory.Build.targets`, adopt `MFTLib.Index` and live change notifications, restore release workflow
  triggers, add
  `<PackageReference Include="MFTLib.TestExtensions" Version="0.3.0" />` to
  broker test projects that use `BrokerTestHarness`, and verify required checks.

### 6. Release notes highlights (0.3.0)

The release notes in `CHANGELOG.md` and GitHub Release must highlight:
- **On-disk block format**: Fixed 64KB-aligned binary block format (`MFTLib.Index`) storing path strings, file sizes, timestamps, attributes, sequence numbers, and directory hierarchies without in-memory object allocation overhead (see `docs/index-format.md`).
- **`MFTLib.Index` namespace**: Rich indexed query and file snapshot model (`FileIndex`, `Snapshot`, `FileEntry`, `FileChange`, `IndexQuery`) with low-latency query evaluation and direct directory traversal without object allocation overhead.
- **Broker block write path**: Elevated broker writes cold scan blocks directly into a client-owned file-backed block section, bypassing named pipe data serialization bottlenecks.
- **`ScanPayload` retirement**: Obsolete IPC `ScanPayload` and flattened record serialization are retired in favor of block-based streaming.
- **Downstream consumer unblocking**: Unblocks resolving downstream port issues file-wizard#288 and git-wizard#134 to eliminate temporary submodules and vendored DLL bridges.

## Known issues (resolved)

- **Elevated build-start hang**: Resolved in PR #56 / #57 by resolving MSBuild via `vswhere` in `scripts/release.ps1` and `scripts/native-coverage.ps1`.
- **Admin test exit hang**: Historic intermittent UAC exit hang was not reproduced across recent attended runs.
