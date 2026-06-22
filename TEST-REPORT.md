MFTLib test report - 2026-06-22
===========================================

Status:   PASS (managed gate held; native aislop gate in progress)
Mode:     close-the-gap (native C++ tree brought under the whole-repo aislop gate)
Tests:    all passing (34 admin + non-admin via run-coverage.ps1, UAC run)
Git:      e301bd9 + working tree (branch feat/aislop-whole-repo-100)

Managed coverage: 100% line, 100% branch, 100% method, 100% full-method
  MFTLib      - 100% line, 100% branch, 100% method
  TestProgram - 100% line, 100% branch, 100% method
  Benchmark   - 100% line, 100% branch, 100% method
  Totals: 633/633 lines, 196/196 branches, 116/116 methods
  0 exclusion annotations
  (Measured this run: all native refactors are exercised by the managed suite,
   incl. admin USN-journal + path-resolution tests; behavior preserved.)

Native coverage: not re-measured this session (native changes are pure
  behavior-preserving refactors). Burndown baseline ~97.25% line / 100% branch
  (uncovered = error/null-guard paths). Re-run scripts/native-coverage.ps1
  (non-elevated) before the final native gate close.

Lint:
  C# (aislop v0.12.3, roslynator + jb inspectcode): 0 findings, score 100/100.
    1 per-case suppression (CA1711 on public [Flags] MatchFlags); CA1707 disabled
    for MFTLib.Tests via .editorconfig. Held this session (C# untouched).
  C++ (aislop whole-repo gate: clang-tidy + cppcheck + jb + ai-slop): 99 findings
    remaining, score 9/100 (down from 152 / score 7 at session start).
    Per-case suppressions: NOLINT(modernize-avoid-c-arrays) on 8 C-ABI/on-disk
      header sites (mft_api.h interop structs, ntfs.h USA typedef, internal.h
      SetErrorMessage array-ref param) - justified domain FPs.
    Remaining 99 = coverage-sensitive (casts ~46, conversions ~10, swappable-
      params ~11, manual-delete 2, .cpp c-array conversions 7, complexity/file
      splits 6) + jb-only FPs (3 cross-TU internal-linkage, ~6 unused-include
      where the symbol is used directly). See .superpowers/sdd/progress.md.

Changes this run (branch feat/aislop-whole-repo-100, native C++ only):
  - Cleared 53 coverage-neutral findings across 3 green-build commits
    (7a8de15 mft_parse 30, d9d171d mft_synthetic+usn_journal 15, e301bd9
    header c-array NOLINTs 8): anonymous-namespace consolidation, const-ref/
    const-ptr params, short-identifier renames, too-wide-scope fixes, and the
    pre-approved C-ABI c-array suppressions.

Managed coverage command:
  .\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
  .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)

Native coverage command:
  .\scripts\native-coverage.ps1

aislop gate:
  aislop ci .                                 # local (uses the pinned global binary)
  .gitea/workflows/aislop.yml                 # CI (Windows runner, fork v0.12.3)
