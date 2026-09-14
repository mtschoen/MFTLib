# MFTLib - Test Report

2026-09-13

| Field | Value |
| --- | --- |
| Status | PASS: every changed executable production line is covered, and tests and the lint/security/quality engines are clean. aislop's format engine still reports 40 findings in files this branch changed, out of 293 repository-wide, but that engine is a host condition rather than a defect; see Lint below. |
| Mode | Full gate run: Linux native smoke tests, the full managed suite under coverlet, and the aislop scan. |
| Git | feat/index-ruled-issues at 6cd14c9, verified before commit (HEAD confirmed at the start of this run). |
| Tests | Native smoke tests: 20 passed, 0 failed. Managed suite via `scripts/coverage-linux.sh`'s standing Linux filter: `Passed!  - Failed:     0, Passed:   989, Skipped:    75, Total:  1064, Duration: 17 s`. |
| Timeout | Wrapped in a 1800-second process timeout; the whole script (native configure through the managed coverage summary) completed normally in about 114 seconds. |
| Coverage | Managed: 5921/6286 lines (94.19%), 1435/1560 branches (91.98%), from `coverage-report/managed/coverage.cobertura.xml`. Native (gcovr, MFTLibNative only): 997/1313 lines (75.9%), 484/970 branches (49.9%), 83/110 functions (75.5%). Every changed executable production line is covered: see Coverage limits. |
| Coverage limits | Overall managed and native coverage are each below 100%; the gate that matters is per-changed-line, not the global percentage. Every line added or modified by `git diff -U0 5462650..HEAD -- MFTLib/` was cross-referenced against the cobertura report: zero changed executable lines have a zero hit count. The final fix wave (`fix: close the final-review findings on the index train`) closed the two gaps this report previously listed here: `MFTLib/Index/LookupEngine.cs`'s root-less-block `continue;` (now at line 93) gained a dedicated test, `Find_SkipsABlockWithNoRootDirectory_AndResolvesThroughTheRootedBlock`, that steps over a root-less `DriveBlock` candidate and resolves through a rooted one; and `MFTLib/Index/CacheDirectory.cs`'s per-file `catch (IOException)` / `catch (UnauthorizedAccessException)` around the `CachedBlockFile` construction was deleted rather than covered, because `DirectoryInfo.EnumerateFiles` had already populated every field those catches guarded and neither body could ever run. `MFTLib/Index/BlockSource.cs` is a plain enum with no executable lines and does not appear in the cobertura report at all, which is expected. Native coverage was not compared line-by-line against the diff because this branch changed no files under `MFTLibNative/`. |
| Lint | aislop 0.16.0: score 84/100 ("Healthy"), 0 errors, 293 warnings, all in the format engine (`csharp-formatting`, whole-file, mechanical, fixable). Code Quality, Security, AI Slop and Linting (jb inspectcode plus roslynator) engines each reported 0 issues. Of the 293 format warnings, 40 are in files this branch changed and 253 are inherited (files this branch did not touch); unchanged from the prior gate run, since the fix wave reformatted nothing. The format engine flags 293 of the repository's roughly 300 `.cs` files, including many this branch never touched (for example `MFTLib/Broker/Launch/DefaultElevatedEntryRunner.cs`, `MFTLib/Broker/BrokerDiagnostics.cs`). The cause is line endings: `.editorconfig` mandates `end_of_line = crlf` for C# while `.gitattributes`' `* text=auto` checks those files out with LF on Linux, so `dotnet format --verify-no-changes` reports every `.cs` file on this host as unformatted, on every line. This is a repository-wide baseline, identical on `main`, and Windows CI (where the checkout is CRLF) is the authoritative run for this engine; it was not fixed or suppressed. Roslynator could not open `MFTLib.sln` (`System.MissingFieldException: Field not found: 'Microsoft.Build.Shared.MSBuildConstants.InvalidPathChars'`, an MSBuild/Roslyn version mismatch on this host) and fell back to per-project analysis for the 5 managed projects; every project it could load reported 0 diagnostics, matching jb inspectcode's 0 issues. |
| Native binary | Rebuilt Debug + coverage (`-DMFTLIB_ENABLE_COVERAGE=ON`) under `build/linux-coverage/` for this run only; this is instrumented for gcov and is not the Release binary. |
| Platform | Linux verified by `scripts/coverage-linux.sh` (native smoke tests plus the managed suite under its standing filter). Windows verification is owner-attended on the chonkers box and still pending; see the list below of exactly what it still needs to cover. |

### What Linux verified and what still needs Windows

Linux-verified by this run:

- Every test in Phases 1 through 5, under the script's standing filter.
- `#145`'s file-release assertion through the `/proc/self/fd` branch of `BlockFileHoldAssertions`.
- `#143`'s enumeration-block case rule, which on Linux takes the ordinal branch.
- `#143`'s real-path round trip over a temp subtree.
- `#118`'s boundary test and its negative control.

Needs Windows, owner-attended, via `.\scripts\run-coverage.ps1`:

- `#145`'s `FileShare.None` branch of `BlockFileHoldAssertions.AssertNotHeld`. On Linux a held file can still be deleted, so the `File.Delete` half of `DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile` only proves anything on Windows.
- `#143`'s claim that Windows MFT blocks are byte-identical before and after. The Windows-only test classes that exercise real MFT blocks (`MftResultTests`, `MftVolumeTests`, `NativeCoverageTests`, `NativeParserCoverageTests`, `UsnJournalSyntheticTests`) do not compile on Linux at all, per `MFTLib.Tests/MFTLib.Tests.csproj`.
- `#143`'s enumeration-block case rule on a case-insensitive host, the `Assert.IsNotNull` branch of `Find_AnEnumerationBlockFollowsTheHostCaseRule`.
- `#118`'s "passes on Windows CI" half of the ruling's acceptance.
- The four individual tests `scripts/coverage-linux.sh` filters out by name at its lines 79-82 (two `ElevationUtilitiesTests`, one `MockVolumeTests`, one `DefaultElevatedEntryRunnerTests`, all needing Windows-side platform behaviour or a real named pipe).

## Regression evidence

- `#145`: `EveryDocumentedRead_ThrowsOnceTheSnapshotIsReleased` failed before the change with `Assert.ThrowsException failed. No exception thrown. ObjectDisposedException exception was expected.`; `DisposeAsync_AfterAQueryAndWhileAHandleIsHeld_ReleasesTheBlockFile` failed with `Assert.IsFalse failed. ... is still open after everything that owns it was disposed`. Both passed after `FileEntry.IsDisposed` and `FileIndex.DisposeAsync`'s unconditional current-and-retired snapshot release.
- `#143`: `Path_OverARealSubtree_IsOpenableOnThisPlatform` failed before the change with `Assert.AreEqual failed. Expected:</tmp/.../readme.md>. Actual:<T:\Documents\readme.md>.` because `FileEntry.Path` still rendered a bare drive letter; `Find_AnEnumerationBlockFollowsTheHostCaseRule` (and nine sibling tests) failed with `Assert.IsNotNull failed.` because `FileIndex.Find` still required an `X:\`-style prefix. Both passed after `FileEntry.Path` rendered the block's real root directory and `FileIndex.Find` accepted a native path resolved against the longest matching indexed root.
- `#146`: `FileIndexBlockSourceTests` failed to compile before the change with `error CS0103: The name 'BlockSource' does not exist in the current context` and `error CS1061: 'DriveStatus' does not contain a definition for 'BlockSource'`. It passed (`Passed!  - Failed: 0, Passed: 446, ...`) after adding the `BlockSource` enum and `DriveStatus.BlockSource`.
- `#144`: `CacheDirectoryEnumerationTests` failed to compile before the change with `error CS0117: 'CacheDirectory' does not contain a definition for 'EnumerateCached'`. It passed (`Passed!  - Failed: 0, Passed: 15, Skipped: 2, ...`) after adding `CacheDirectory.EnumerateCached` and `CachedBlockFile`.
- `#118`'s regression evidence is its negative control rather than a before/after fix: with `NamespaceBoundaryViolationFixture` placed outside `namespace MFTLib.Index`, `TheBoundaryRule_ReportsADeliberateViolation` failed with `Assert.IsFalse failed. the rule must report the deliberate violation in NamespaceBoundaryViolationFixture` (i.e. the rule reported nothing). Once the fixture was moved into `MFTLib.Index`, the same rule reported the violation and the test passed, proving the boundary rule is not vacuously green.

## Remaining diagnostics

- aislop's format engine (`csharp-formatting`) flags 293 of the repository's roughly 300 C# files, 40 of them changed by this branch and 253 inherited. `.editorconfig` mandates `end_of_line = crlf` for C# while `.gitattributes`' `* text=auto` checks files out with LF on Linux, so every `.cs` file violates the setting on every line here; the count is a repository-wide baseline, identical on `main`, and Windows CI is the authoritative run for this engine. Not fixed or suppressed per this task's instructions.
- Roslynator cannot open `MFTLib.sln` on this host (`MissingFieldException` on `Microsoft.Build.Shared.MSBuildConstants.InvalidPathChars`) and falls back to per-project analysis; all 5 managed projects it could load reported 0 diagnostics.
- Native gcovr coverage (75.9% lines, 50.2% branches) is unchanged territory for this branch: no file under `MFTLibNative/` was touched. `tools/mft-cli.cpp` at 0% is the CLI entry point, not exercised by the unit/smoke tests.
- Every changed executable production line is covered after the fix wave: the root-less-block skip in `MFTLib/Index/LookupEngine.cs` is exercised by `Find_SkipsABlockWithNoRootDirectory_AndResolvesThroughTheRootedBlock`, and the unreachable per-file catch in `MFTLib/Index/CacheDirectory.cs` was deleted.

## Commands

```bash
./scripts/coverage-linux.sh
aislop scan .
```

```powershell
.\scripts\run-coverage.ps1
```

The first two ran on Linux for this report; the third is the pending owner-attended Windows half described above.
