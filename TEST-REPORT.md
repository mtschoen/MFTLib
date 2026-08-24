MFTLib test report - 2026-08-22
===========================================

Status:   PASS
Mode:     0.3.0 release verification (attended full managed run + separate elevated native run)
Tests:    679 passed, 0 failed (643 non-admin + 36 elevated NTFS/USN, managed);
          679 passed, 0 failed under native Debug|x64 instrumentation (full suite rerun elevated)
Git:      main at e26429f plus the MSBuild-resolution fix in
          scripts/native-coverage.ps1 landing in this commit and the
          elevated-wrapper marker fix in .claude/scripts/
          native-coverage-elevated.ps1 (gitignored, lives outside the repo history)

Managed coverage (`scripts/run-coverage.ps1`, full attended run):
  Non-admin phase (643 tests):
    Module                  Line     Branch   Method
    Benchmark               98.74%   95.6%    100%
    MFTLib                  100%     99.33%   100%
    MFTLibTestExtensions    100%     100%     100%
    TestProgram             100%     100%     100%
    Total                   99.73%   98.49%   100%
  Elevated admin phase (36 tests, merged into the same coverage run): every MFTLib
  class reports 100% line coverage. Aggregate totals are unchanged from the
  non-admin phase - Benchmark's uncovered lines are not admin-gated, so they
  still account for the total falling short of 100%.
  Exclusion annotations added by this run: 0

Native coverage (`scripts/native-coverage.ps1` via the elevated wrapper, 679 tests, 13m):
  MFTLibNative: 98.8% line, 100% branch
  Exclusion annotations added by this run: 0

Documented unreachable (carried forward from PR #55; no coverage exclusions or
suppressions added anywhere to reach these numbers):
  - BenchmarkRunner: 6 locations dead under the Process.Start contract
    (UseShellExecute=false throws instead of returning null) or requiring the
    compiled Benchmark.exe as the test host process.
  - JournalBrokerHost DirectProgress constructor null-check: private class,
    both call sites pass literal lambdas.
  - mft.records.cpp integer-overflow prechecks: a requested capacity near the
    size-type maximum is not constructible.
  - ResolvePath path-length guard (records.cpp:209): the maximum constructible
    total path length is exactly MAX_NTFS_PATH_UNITS (32767), given depth <= 128
    and per-level names <= 255 bytes (NTFS single-byte FileNameLength), so the
    > branch is mathematically dead. parse_core.cpp:169-170 and :181 are dead
    by the same construction and by caller gating.

Release validation performed:
  - Full Release|x64 solution build, then Debug|x64 build for native instrumentation
  - 643 non-admin managed tests plus 36 elevated tests against real NTFS MFT and
    USN journal APIs
  - Native coverage collection re-ran the full 679-test suite elevated under
    Debug|x64 instrumentation, reaching 98.8% line / 100% branch on MFTLibNative
  - No coverage exclusions or suppressions added anywhere

Remaining outward checks:
  - Tag and publish 0.3.0 per docs/handoff-release-0.3.0.md - the only step not
    yet done in the NuGet publish checklist

Commands:
  `.\scripts\run-coverage.ps1`
  `.\.claude\scripts\native-coverage-elevated.ps1`
