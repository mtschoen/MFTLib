# Handoff: MFTLib 0.3.0 Release

Updated 2026-08-23. `CHANGELOG.md` is the authoritative description of 0.3.0;
this document tracks the remaining release sequence.

## Status

`main` is at `a841998`. All 0.3.0 feature, broker, memory optimization, and test
hardening work is merged and validated on both Windows and Linux.
The public `MFTLib` namespace and API remain intact while source files are grouped
by MFT, journal, broker, elevation, interop, and internal responsibilities.

Validation on the integrated tree (`TEST-REPORT.md`):

- 679 total tests passing (643 non-admin + 36 elevated administrator tests against real NTFS and USN APIs);
- 679/679 tests passing under native Debug|x64 instrumentation;
- Managed coverage: MFTLib package is 100% line, 99.33% branch, 100% method (overall solution 99.73% line, 98.49% branch, 100% method);
- Native coverage: MFTLibNative is 98.8% line and 100% branch (uncovered lines are the documented-unreachable set from PR #55; zero exclusions);
- `aislop ci .` is 100/100 with zero score-affecting findings;
- `scripts/release.ps1` dry run resolves MSBuild via `vswhere` and packs `MFTLib.0.3.0.nupkg` and `.snupkg` successfully with the managed DLL, native runtime DLL, build targets, README, and license.

0.3.0 is built, validated, and packable, but not yet published to nuget.org and `v0.3.0` is not tagged.

## Release checklist

### 1. Synchronize mirrors

Ensure the exact merged history on Gitea `main` is mirrored to GitHub so SourceLink
(`PublishRepositoryUrl=true` + `SourceLink.GitHub`) can resolve the commit that will be packed:

```bash
git switch main
git pull --ff-only gitea main
git push origin main
```

### 2. Validate downstream consumers (pre-publish sanity check)

- **file-wizard broker smoke**: verify the submodule / bridge against merged MFTLib `main`; run a
  CLI cold scan with one UAC prompt; verify MAUI scan, live changes, and Shift+Rescan
  reuse the same broker without a second prompt.
- **git-wizard watch smoke**: build against merged MFTLib `main`; run
  `git-wizard --watch`, change a file inside a tracked repository, and verify the
  corresponding `changed:` notification.

The MFTLib attended coverage run itself is complete and recorded in `TEST-REPORT.md`.

### 3. Release dry run

On Windows (`chonkers`):

```powershell
.\scripts\release.ps1
```

This requires a clean tree and no existing `v0.3.0` tag. It verifies coverage
and packs without publishing.

### 4. Publish (Owner only)

On Windows (`chonkers`):

```powershell
.\scripts\release.ps1 -Publish
```

Publishing requires the NuGet key at `C:\Users\mtsch\nugetkey` (`~/nugetkey`), authenticated GitHub tooling (`gh`),
and the exact release commit already pushed to GitHub. The script pushes the package
to nuget.org, tags `v0.3.0`, pushes the tag to `origin`, and creates the GitHub release with `CHANGELOG.md` release notes.

### 5. Replace temporary consumer bridges

Once 0.3.0 is live on nuget.org:

**file-wizard:** remove the `external/MFTLib` submodule and its solution entries,
delete the temporary root `Directory.Build.targets`, add
`<PackageReference Include="MFTLib" Version="0.3.0" />` to
`FileWizard/FileWizard.csproj`, and remove the CI native-submodule build step.

**git-wizard:** follow `lib/MFTLib/README.md`: add the package reference `<PackageReference Include="MFTLib" Version="0.3.0" />`, delete the
vendored DLL bridge and root `Directory.Build.targets`, restore release workflow
triggers, and verify required checks.

## Known issues (resolved)

- **Elevated build-start hang**: Resolved in PR #56 / #57 by resolving MSBuild via `vswhere` in `scripts/release.ps1` and `scripts/native-coverage.ps1`.
- **Admin test exit hang**: Historic intermittent UAC exit hang was not reproduced across recent attended runs.
