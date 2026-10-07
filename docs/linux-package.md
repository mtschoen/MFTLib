# Linux native library in the package

The `MFTLib` package carries `runtimes/linux-x64/native/libMFTLibNative.so` next to the Windows
`runtimes/win-x64/native/MFTLibNative.dll`. The .NET host resolves the library from `runtimes/`, so
a Linux consumer needs no copy step. `build/MFTLib.targets` copies the Windows library only on a
Windows host.

## Which library ships

The Release library built by `scripts/build-linux.sh`, never the coverage build in
`build/linux-coverage`. It is built on Ubuntu 24.04 (glibc 2.39, gcc 13) as that image ships it:
no older build container, no static libstdc++. `scripts/check-linux-native.sh <library>` is the
acceptance check. It fails unless the library is a 64-bit x86-64 ELF shared object, shows no
`__gcov` symbol in `nm -D`, carries no RPATH or RUNPATH, and exports `OpenMftDumpInput`,
`ParseMftDumpInput` and `CloseMftDumpInput`. It prints the highest `GLIBC_` and `GLIBCXX_` symbol
version the library imports (`objdump -T`); those printed values are the floor the package README
states. Measured at the base of this slice: glibc 2.33 and GLIBCXX_3.4.22. Update the README
sentence when the check prints different values.

## How a release gets the library

Gitea lists no artifact over REST for an `actions/upload-artifact@v3` upload
(`GET /actions/runs/{id}/artifacts` returns `total_count: 0`, recorded in
`~/schoen-lab/packages/local_ci/docs/project-ci-setup.md`), and the official v4 action refuses to
run against Gitea. A Windows release therefore cannot download the CI library. Instead:

- `scripts/build-linux-native.ps1` builds the Release library in WSL (`wsl -d Ubuntu-24.04`, the
  same Ubuntu 24.04 floor) through `scripts/build-linux.sh`, runs `scripts/check-linux-native.sh`
  and returns the Windows path of the library. It deletes any previous library first.
- `scripts/release.ps1` and `scripts/test-release-packaging.ps1` call it and pass the path to the
  pack as `-p:MFTLibLinuxNativeLibrary=<path>`. `MFTLib.csproj` packs the property's file at the
  `runtimes/linux-x64/native` entry. A plain `dotnet pack` without the property makes a package
  that `scripts/Test-ReleasePackages.ps1` rejects, because it requires that entry.
- `release.ps1` requires a clean tree at the release commit, so the library is built from `HEAD`.

## CI

The `linux-package` job of `.gitea/workflows/test.yml` runs on every pull request and main push.
It builds the Release library with `scripts/build-linux.sh`, runs `scripts/check-linux-native.sh`,
and runs `scripts/smoke-package-linux.sh`. That script packs MFTLib on Linux with the library under
the version `0.0.0-package-smoke` (never published), restores it from a local feed with an empty
package cache into `scripts/package-smoke`, and runs that console project. The project links the
test dump builder `MFTLib.Tests/TestSupport/MftDumpFixture.cs`, loads a dump through
`MftIndexSources.FromMftDumpFile`, asserts `dump:/D/documents/Notes.txt` is indexed with 11 bytes,
and asserts the library came from `runtimes/linux-x64/native` of the output and was not copied flat.
The job also uploads the library and the check output as an artifact named
`linux-native-<commit SHA>` for a person to download from the run page.
