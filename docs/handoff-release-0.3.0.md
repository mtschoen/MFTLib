# Handoff: MFTLib 0.3.0 Release

Updated 2026-07-13. `CHANGELOG.md` is the authoritative description of 0.3.0;
this document tracks the remaining release sequence.

## Status

Gitea PR #19 (VolumeBroker and release validation) and PR #20 (source
organization and 0.3 documentation) are merged to `main` at `159d963`.
The public `MFTLib` namespace and API remain intact while source files are grouped
by MFT, journal, broker, elevation, interop, and internal responsibilities.

The follow-up `fix/native-coverage-100` branch closes the remaining native coverage
gap and fixes two synthetic-generator defects found while exercising those paths:

- native C++ `bool` results are marshaled as one-byte values, so early conversion
  failures reliably propagate to managed callers;
- a completed asynchronous writer is marked no longer pending before failure
  teardown, preventing a second join attempt.

Validation on the integrated tree:

- 383 non-admin tests passed;
- 34 attended administrator tests passed against real NTFS and USN APIs;
- managed coverage is 100% line, branch, and method;
- native coverage is 100% line and branch;
- `aislop ci .` is 100/100 with zero findings;
- `MFTLib.0.3.0.nupkg` and `.snupkg` pack successfully with the managed DLL,
  native runtime DLL, build targets, README, and license.

0.3.0 is not published and `v0.3.0` is not tagged. file-wizard and git-wizard
still need final validation against the merged MFTLib commit before publication.

## Release checklist

### 1. Merge the coverage follow-up and synchronize mirrors

Merge `fix/native-coverage-100` to Gitea `main` after its Windows, Linux, and
quality-gate jobs pass. Then mirror the exact merged history to GitHub so SourceLink
can resolve the commit that will be packed:

```bash
git switch main
git pull --ff-only gitea main
git push origin main
```

### 2. Validate downstream consumers

- **file-wizard broker smoke**: update its submodule to merged MFTLib `main`; run a
  CLI cold scan with one UAC prompt; verify MAUI scan, live changes, and Shift+Rescan
  reuse the same broker without a second prompt.
- **git-wizard watch smoke**: build against merged MFTLib `main`; run
  `git-wizard --watch`, change a file inside a tracked repository, and verify the
  corresponding `changed:` notification.

The MFTLib attended coverage run itself is complete and does not need repeating
unless MFTLib changes again.

### 3. Release dry run

```powershell
.\scripts\release.ps1
```

This requires a clean tree and no existing `v0.3.0` tag. It reruns attended coverage
and packs without publishing.

### 4. Publish

```powershell
.\scripts\release.ps1 -Publish
```

Publishing requires the NuGet key at `~/nugetkey`, authenticated GitHub tooling,
and the exact release commit already pushed to GitHub. The script pushes the package
to nuget.org, tags `v0.3.0`, pushes the tag, and creates the GitHub release.

### 5. Replace temporary consumer bridges

**file-wizard:** remove the `external/MFTLib` submodule and its solution entries,
delete the temporary root `Directory.Build.targets`, add
`<PackageReference Include="MFTLib" Version="0.3.0" />` to
`FileWizard/FileWizard.csproj`, and remove the CI native-submodule build step.

**git-wizard:** follow `lib/MFTLib/README.md`: add the package reference, delete the
vendored DLL bridge and root `Directory.Build.targets`, restore release workflow
triggers, and verify required checks.

## Known issue

Attended coverage historically could hang after tests passed while the parent shell
waited for the elevated process. The 2026-07-13 run completed normally. Keep the
existing diagnostic note in `.plan` until repeated release runs establish that the
hang is no longer reproducible.
