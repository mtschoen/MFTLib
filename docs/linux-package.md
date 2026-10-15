# Linux native library in the package

The `MFTLib` package carries `runtimes/linux-x64/native/libMFTLibNative.so` next to the Windows
`runtimes/win-x64/native/MFTLibNative.dll`. SDK-style .NET consumers resolve both libraries
through the package's runtime assets. Ordinary builds and RID-less publishes retain the
runtime directories; RID-specific publishes place the selected native library beside the
application.

## Which library ships

The Release library built by `scripts/build-linux.sh`, never the coverage build in
`build/linux-coverage`. It is built on Ubuntu 24.04 (glibc 2.39, gcc 13) as that image ships it:
no older build container, no static libstdc++. `scripts/check-linux-native.sh <library>` is the
acceptance check. It fails unless the library is a 64-bit x86-64 ELF shared object, shows no
`__gcov` symbol in `nm -D`, carries no RPATH or RUNPATH, and exports `OpenMftDumpInput`,
`ParseMftDumpInput`, `CloseMftDumpInput`, `ParseMFTRecordsWithProgress`, `FreeMftResult` and
`GetMftNativeAbiVersion`. It prints the highest `GLIBC_` and `GLIBCXX_` symbol
version the library imports (`objdump -T`); those printed values are the floor the package README
states. The current floors are glibc 2.33 and GLIBCXX_3.4.22, as this check prints them. Update the README
sentence when the check prints different values.

## How a release gets the library

This Gitea deployment's `GET /actions/runs/{id}/artifacts` returns `total_count: 0`
for `actions/upload-artifact@v3` uploads; the official v4 upload action refuses to
run against Gitea. A Windows release therefore builds the library in WSL rather
than downloading the CI artifact:

- `scripts/build-linux-native.ps1` builds the Release library in WSL (`wsl -d Ubuntu-24.04`, the
  same Ubuntu 24.04 floor) through `scripts/build-linux.sh`, runs `scripts/check-linux-native.sh`
  and returns the Windows path of the library. It deletes any previous library first.
- `scripts/release.ps1` and `scripts/test-release-packaging.ps1` call it and pass the path to the
  pack as `-p:MFTLibLinuxNativeLibrary=<path>`. `MFTLib.csproj` packs the property's file at the
  `runtimes/linux-x64/native` entry. A plain `dotnet pack` without the property makes a package
  that `scripts/Test-ReleasePackages.ps1` rejects, because it requires that entry.
- `release.ps1` requires a clean tree at the release commit, so the library is built from `HEAD`.

## Release validation and publication

Before either a dry run returns successfully or publication starts,
`scripts/Test-ReleasePackages.ps1` requires `lib/net10.0/MFTLib.dll` and
`lib/net10.0/MFTLib.xml` in MFTLib, and `lib/net10.0/MFTLibTestExtensions.xml`
alongside the test-extension assembly in MFTLib.TestExtensions. Validation also
checks package identities, matching versions, the exact MFTLib dependency,
native libraries, README, license, and symbol-package files.

For both dry-run and publish mode, set `MFTLIB_GITEA_REPOSITORY_URL` in the
release environment to the canonical repository's literal HTTPS or SSH URL.
Missing input and remote aliases are rejected before the pipeline starts.
Both the canonical ancestry check and its tag push use that same value;
local remote names and push URLs are not consulted. GitHub remains the public
SourceLink mirror.

In publish mode, the script publishes the NuGet packages, then pushes the tag
to Gitea and GitHub in that order. Either tag-push failure exits with status 1.
GitHub release creation runs only after both pushes succeed. A failure at this
stage does not undo already published NuGet packages or a successful Gitea push.

Run the offline regressions with:

    pwsh -NoProfile -File scripts/test-release-package-validation.ps1
    pwsh -NoProfile -File scripts/test-release-notes.ps1

The first builds synthetic ZIP packages. The second tests canonical-destination validation and release-note handling
and runs the production GitHub-push tail in isolated PowerShell processes with
fake Git and GitHub CLI commands.

## CI

The `linux-package` job of `.gitea/workflows/test.yml` runs on every pull request and main push;
when every changed path is docs-only or the scanner pin, its `skip_build` detection skips the
steps below and the upload, and the job finishes green ([CI and quality gates](quality-gates.md)).
Otherwise it builds the Release library with `scripts/build-linux.sh`, runs `scripts/check-linux-native.sh`,
and runs `scripts/smoke-package-linux.sh`. That script packs MFTLib on Linux with the library under
the version `0.0.0-package-smoke` (never published), restores it from a local feed with an empty
package cache into `scripts/package-smoke`, and runs that console project. The project links the
test dump builder `MFTLib.Tests/TestSupport/MftDumpFixture.cs`, loads a dump through
`MftIndexSources.FromMftDumpFile`, asserts `dump:/D/documents/Notes.txt` is indexed with 11 bytes,
and asserts the library came from `runtimes/linux-x64/native` of the output and was not copied flat.
Unless `skip_build` skipped it, the job also uploads the library and the check output as an artifact named
`linux-native-<commit SHA>` for a person to download from the run page.

## Windows package smoke

Run `pwsh -NoProfile -File scripts/build-windows.ps1`, then
`pwsh -NoProfile -File scripts/smoke-package-windows.ps1`. The smoke packs the existing
Release/x64 build into a private feed under version `0.0.0-package-smoke`, restores a
plain .NET 10 PackageReference consumer into an empty package cache, and runs both its
build output and its RID-less publish output. The consumer sources and linked dump
fixture are staged outside the checkout, with ancestor Directory.Build imports disabled,
so repository source-build copy targets cannot supply its native DLL.

Both runs require `runtimes/win-x64/native/MFTLibNative.dll`, reject a flat native DLL,
index the synthetic 11-byte Notes.txt entry, and check the loaded Windows module path.
The package must contain neither custom MFTLib build-target entry. RID-specific publish
is not subject to the no-flat assertion because the SDK flattens its selected native asset.

`scripts/test-release-packaging.ps1` also invokes the smoke with `-PackagePath` to test the
exact full release package it creates. The Windows CI job runs the standalone smoke after
coverage has built Release/x64; the local `windows` preflight scope runs it at the same point.
