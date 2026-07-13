# Handoff: MFTLib 0.3.0 Release

Rewritten 2026-07-06 (the previous version predated the VolumeBroker extraction and
listed long-since-landed commits; CHANGELOG.md is now the authoritative "what's in
0.3.0" - this doc is only the release runbook).

## Status

Code-complete and pack-verified on branch **`feat/volume-broker` (fd0b750)**, which sits
on `feat/aislop-whole-repo-100` (28 commits ahead of `main`). NOT published; `v0.3.0`
not tagged. `MFTLib.0.3.0.nupkg` + `.snupkg` build clean via
`MSBuild MFTLib\MFTLib.csproj -t:Pack -p:Configuration=Release -p:Platform=x64` and the
package contents were verified (broker types in `lib/net8.0/MFTLib.dll`,
`runtimes/win-x64/native/MFTLibNative.dll`, `build`/`buildTransitive` targets,
LICENSE + README).

0.3.0 now includes the **VolumeBroker subsystem** extracted from file-wizard (see
CHANGELOG.md). Both downstream consumers are wired and green against fd0b750:

- **file-wizard** `feat/journal-broker-smoothing` @ d55f5a1 - consumes the broker from
  the `external/MFTLib` submodule pinned to fd0b750 (521/522 tests green).
- **git-wizard** `feat/journal-watch` - re-vendored `lib/MFTLib/` DLLs built from
  fd0b750; new `--watch` journal-watch feature filters live USN batches to tracked
  repos.

Verification at fd0b750: 347 tests, 100% managed line/branch/method coverage
(run-coverage.ps1 -NonInteractive), zero new aislop findings (see TEST-REPORT.md for
the pre-existing native jb-inspectcode caveat).

## Release checklist (in order)

### 1. Push the branches (unblocks downstream CI)

`feat/volume-broker` exists only locally. file-wizard's submodule pin resolves against
**both** MFTLib remotes (GitHub for local clones, Gitea for the CI runner), so push the
branch (and its parent) to both:

```bash
git push origin feat/aislop-whole-repo-100 feat/volume-broker
git push gitea  feat/aislop-whole-repo-100 feat/volume-broker
```

Then merge to `main` (claude-code PRs per the Gitea identity convention; branch
protection wants the windows + linux CI checks green).

### 2. Interactive validations (human at keyboard - UAC prompts)

- **MFTLib elevated coverage**: `.\scripts\run-coverage.ps1` (approve UAC) - admin
  suites + native line coverage close to 100%. Note the known admin-run exit hang
  (.plan "Test Infrastructure") - if it wedges after tests pass, the results are
  still valid.
- **file-wizard on-hardware broker smoke** (plan Task 12 Step 3 / Task 16 Step 1):
  CLI scan with ONE UAC prompt; MAUI session with one UAC, live change visible in the
  feed; Shift+Rescan reuses the broker (no second UAC).
- **git-wizard watch smoke**: `git-wizard --watch` (one UAC), touch a file inside a
  tracked repo, see the `changed: <repo>` line.

### 3. Release dry run

```powershell
.\scripts\release.ps1        # clean tree + no v0.3.0 tag required; runs coverage WITH UAC, then packs
```

### 4. Publish (the deliberate stopping point - not automated)

```powershell
.\scripts\release.ps1 -Publish
```

Requires: `C:\Users\mtsch\nugetkey`, `gh` authenticated, CHANGELOG.md (exists).
This pushes the nupkg to nuget.org, tags `v0.3.0`, pushes the tag to origin (GitHub -
required for SourceLink), and creates the GitHub release with CHANGELOG notes.
The exact packed commit must be on the GitHub mirror before packing/tagging
(SourceLink resolves against github.com/mtschoen/MFTLib) - step 1 covers that as long
as the release is packed from a pushed commit.

### 5. Post-publish consumer flips

**file-wizard - retire the submodule** (AGENTS.md "Retiring the submodule" has the
authoritative steps): remove the `external/MFTLib` submodule and its two
`file-wizard.sln` entries, delete `Directory.Build.targets` (central MFTLib
`<Reference>` + native-DLL copy + `BlockLocalMFTLibOnPublish` guard), and add to
`FileWizard/FileWizard.csproj`:

```xml
<PackageReference Include="MFTLib" Version="0.3.0" />
```

(flows transitively; buildTransitive targets place the native DLL). CI then drops its
VS-MSBuild step and builds everything with `dotnet`.

**git-wizard - retire the vendored bridge**: follow the checklist in
`git-wizard/lib/MFTLib/README.md` verbatim (PackageReference in
`GitWizard/GitWizard.csproj`, delete `lib/MFTLib/` + repo-root
`Directory.Build.targets`, re-enable the `release.yml` `pull_request:` trigger,
re-confirm CI + required checks). The `.gitattributes` CRLF block stays.

## Known issues (pre-existing, decide before or after publish)

- `aislop ci .` is red at baseline (68/100 vs failBelow:100): 10 jb-inspectcode Cpp*
  findings in MFTLibNative from the unfinished ReSharper-C++ sweep, plus the pinned CI
  aislop's clang-tidy parser bug (silently passes). Both predate 0.3.0 work; the broker
  diff adds zero findings. Detail: TEST-REPORT.md.
- Admin test runs can hang on exit after passing (see .plan "Test Infrastructure" and
  `reference_native_coverage_hang.md`).
- **`BrokerFrame` decomposition — RESOLVED (2026-07-11).** The 9-parameter
  positional constructor was eliminated: `BrokerFrame` keeps its single wire
  storage shape but is now built only through per-kind static factories
  (`BrokerFrame.ScanReady`/`.ArmedCursor`/`.JournalBatch`/`.Error`/…) with
  `private init` properties, so the aislop `too-many-params` finding is gone
  (gate back to 100/100) and invalid per-kind field combinations are
  unconstructible. Pure refactor: on-wire bytes are unchanged (pinned by the
  golden wire-byte tests in `BrokerProtocolTests`).
