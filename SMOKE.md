# Smoke

The Smoke tier is the agent fast check: a small, non-admin, fast subset of the managed suite, tagged
`[TestCategory("Smoke")]`. It runs the same tests on Windows and Linux, against the freshly built
native library. It is the floor before pushing a change. It is not the bar.

```bash
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter TestCategory=Smoke
```

On Linux, `scripts/build-linux.sh` builds the Release library and then runs this filter with `-p:MFTLibNativeLinuxFlavor=Release`, so a leftover `build/linux-coverage` library is never the one tested. Omit
`-p:Platform=x64` there. On Windows run `.\scripts\build-windows.ps1` first so the native DLL is current.

## What it covers

- a dump parse round-trip at both record sizes, the generated fixtures byte for byte, and the
  record-size, rejected-content and hostile-fixup checks;
- the native ABI version check;
- the native exception barriers: an injected exception returns an empty failure with the exact
  message, and the next parse succeeds;
- a failed chunk read failing as incomplete, and progress reporting every chunk;
- on Linux, the interrupted system call handling of the positioned read and write.

Tests that need an interrupted system call are skipped (inconclusive) on Windows: the hook exists only
in the POSIX native library.

## The bar

A change is done when the full suite passes, not the Smoke tier. Run the whole suite
(`dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64`), or the coverage scripts in
[coverage procedures](docs/development-coverage.md). Linux native line coverage comes only from the
managed suite: `scripts/coverage-linux.sh` measures it.
