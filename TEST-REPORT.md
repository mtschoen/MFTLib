MFTLib test report - 2026-08-24
===========================================

Status:   PASS
Mode:     0.3.0 release verification (attended full managed run + separate elevated native run)
Tests:    686 passed, 0 failed (650 non-admin + 36 elevated NTFS/USN, managed);
          686 passed, 0 failed under native Debug|x64 instrumentation (full suite rerun)
Git:      main at 0251ee9 (incorporating #66, #67, #69, #70)

Managed coverage (`scripts/run-coverage.ps1`, full attended run):
  Non-admin phase (650 tests):
    Module                  Line     Branch   Method
    Benchmark               98.74%   95.6%    100%
    MFTLib                  100%     99.01%   100%
    MFTLibTestExtensions    100%     100%     100%
    TestProgram             100%     100%     100%
    Total                   99.74%   98.26%   100%
  Elevated admin phase (36 tests, merged into the same coverage run): every MFTLib
  class reports 100% line coverage. Aggregate totals are unchanged from the
  non-admin phase - Benchmark's uncovered lines are not admin-gated, so they
  still account for the total falling short of 100%.
  Exclusion annotations added by this run: 0

Native coverage (`scripts/native-coverage.ps1`, 686 tests, 13m 29s):
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
  - 650 non-admin managed tests plus 36 elevated tests against real NTFS MFT and
    USN journal APIs
  - Native coverage collection re-ran the full 686-test suite under
    Debug|x64 instrumentation, reaching 98.8% line / 100% branch on MFTLibNative
  - `aislop ci .` is 100/100 with zero score-affecting findings across 126 files
  - `scripts/release.ps1` dry run successfully packed `MFTLib.0.3.0.nupkg`
  - Linux build and test suite passing (15 native smoke + 372 managed unit tests passing)
  - No coverage exclusions or suppressions added anywhere

Remaining outward checks:
  - Tag and publish 0.3.0 per docs/handoff-release-0.3.0.md - the only step not
    yet done in the NuGet publish checklist

Commands:
  `.\scripts\run-coverage.ps1`
  `.\scripts\native-coverage.ps1`
  `.\scripts\release.ps1`
