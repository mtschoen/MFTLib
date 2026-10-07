# MFTLib 0.3.0 release runbook

The [CHANGELOG](../CHANGELOG.md) describes the release contract: one `FileIndex`
for broker-backed live volumes, in-process local scans, saved MFT dumps and
enumeration, with a separate `MFTLib.TestExtensions` package and two samples.
Run these steps at `<release commit>`; pins and measurements belong in the checklist issues.

## Ordered release steps

1. Confirm Gitea `main` and GitHub `main` both contain `<release commit>`.
   Gitea is canonical; GitHub supplies the public SourceLink history.
2. Complete attended library and consumer smokes at that commit. Record the pins,
   test counts and coverage in [MFTLib issue 72 (attended release checklist)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/72),
   [file-wizard issue 299 (attended consumer checklist)](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/299)
   and [git-wizard issue 143 (attended consumer checklist)](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/143).
   Verify the required checks in [quality gates](quality-gates.md).
3. Run the dry run on chonkers:

   ```powershell
   .\scripts\release.ps1
   ```

   The [release script](../scripts/release.ps1) requires a clean tree, no existing
   release tag, and the release commit on both mirrors. It cleans the solution,
   restores packages and runs `scripts/run-coverage.ps1 -Configuration Release`.
   It builds the Release linux-x64 library in WSL through `scripts/build-linux.sh`
   and validates it with `scripts/check-linux-native.sh`. The library path is supplied
   to packaging as `-p:MFTLibLinuxNativeLibrary=<path>`; both managed projects are
   packed and validated with `Assert-ReleasePackages` and `Assert-ReleaseSymbolPackages`.
4. Read the printed glibc and libstdc++ symbol versions. Edit README, CHANGELOG and
   [Linux package](linux-package.md) only if the measured floors differ from glibc
   2.33 and GLIBCXX_3.4.22. Repeat validation at the resulting release commit.
5. **Owner only:** add `test / linux-package (pull_request)` to branch protection.
6. **Owner only:** publish from the validated release commit:

   ```powershell
   .\scripts\release.ps1 -Publish
   ```

   The script pushes MFTLib to NuGet first, then MFTLib.TestExtensions. It creates
   the release tag and pushes it to Gitea first, then GitHub. `gh release create`
   attaches both packages and symbol packages, using only the extracted `## 0.3.0`
   CHANGELOG section as its notes. A missing version heading fails before publishing;
   the temporary notes file is deleted after release creation.
7. **Owner only:** retire consumer bridges under
   [file-wizard issue 288 (NuGet bridge retirement)](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/288)
   and [git-wizard issue 134 (NuGet bridge retirement)](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134).
   Their submodule recipe is in [consumer submodule](consumer-submodule.md).
   Retire MFTLib's `.gitea/workflows/sync-consumers.yml`, `scripts/sync_consumers.sh`,
   `scripts/sync_consumers.tests.sh` and the consumer-sync test step in the Linux job.

## Package contents and Linux prerequisite

MFTLib contains `lib/net10.0/MFTLib.dll`, `lib/net10.0/MFTLib.xml`,
`runtimes/win-x64/native/MFTLibNative.dll`,
`runtimes/linux-x64/native/libMFTLibNative.so`, `build/MFTLib.targets`,
`buildTransitive/MFTLib.targets`, `LICENSE.txt` and `README.md`.
MFTLib.TestExtensions contains its managed assembly, license and the same README,
and depends on exactly MFTLib 0.3.0. Both symbol packages must exist.

The default WSL prerequisite is `wsl -d Ubuntu-24.04`; direct invocation of
`scripts/build-linux-native.ps1 -Distribution <name>` selects another distribution.
`scripts/check-linux-native.sh` requires x86-64 ELF, no `__gcov` symbols, no RPATH
or RUNPATH, and the exports `OpenMftDumpInput`, `ParseMftDumpInput` and
`CloseMftDumpInput`; it prints the highest imported GLIBC and GLIBCXX versions.
See [Linux package](linux-package.md) for the build and package acceptance contract.

## Smoke checks

- Open an index through `BrokerSession.CreateIndexSource()`, start and catch up
  watches on two drives, and verify `Changed` events identify each drive's changes.
  Restart one drive with `RescanAsync('C')` while its sibling keeps watching;
  finish with `StopWatchingAsync(cancellationToken)` and inspect all drive results.
- Simulate broker death and confirm terminal `WatchFaultKind.Channel` faults;
  a fresh session and new indexes supply the next connection. Check recoverable
  `Drive` and `Apply` faults separately through their recovery state events.
- The `linux-package` job validates a Release library and package on every pull request and main push ([Linux package](linux-package.md)).
- `scripts/package-smoke` loads a saved dump from a locally packed NuGet package ([Linux package](linux-package.md)).
- The job artifact `linux-native-<sha>` contains the library and acceptance output for attended inspection ([Linux package](linux-package.md)).
- The glibc floor policy states the actual imported symbol versions printed by the acceptance check ([Linux package](linux-package.md)).

## Release measurement snapshot

The operator regenerates `TEST-REPORT.md` at `<release commit>` from the summaries
of `scripts/run-coverage.ps1 -Configuration Release` and `scripts/coverage-linux.sh`.
The coverage scripts supply evidence; the operator writes the snapshot. Record the
attended consumer pins and results in the three checklist issues above.
