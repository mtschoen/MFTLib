MFTLib test report - 2026-07-06
===========================================

Status:   PASS (managed gate 100/100; native C++ tree clang-tidy-clean)
Mode:     0.3.0 release prep (VolumeBroker subsystem ported in from file-wizard)
Tests:    347 passed, 0 failed (non-elevated run-coverage.ps1; admin suites skipped)
Git:      branch feat/volume-broker (commit fd0b750, on top of feat/aislop-whole-repo-100)

Managed coverage (run-coverage.ps1 -NonInteractive): 100% line, 100% branch, 100% method
  MFTLib      - 100%   TestProgram - 100%   Benchmark - 100%
  Totals: 1427/1427 lines, 378/378 branches, 219/219 methods, 0 exclusion annotations
  (Growth from the 2026-06-29 totals is the VolumeBroker port: JournalBrokerHost/Client,
   BrokerProtocol, ScanPayload, ElevatedEntryPoint/BrokerLauncher, BrokerDiagnostics,
   Mmf seams - all brought in at the same 100% bar with MSTest ports of the
   file-wizard suites. MFTLib.0.3.0.nupkg pack verified clean at this commit.)

Native coverage (scripts/native-coverage.ps1, non-elevated):
  MFTLibNative - 97.2% line, 100% branch.
  The ~2.8% uncovered line gap is admin-only code (USN journal + raw-volume
  path-resolution helpers) that runs only under the elevated test set. Re-run
  run-coverage.ps1 WITH UAC elevation before the final native gate to close it.

Native C++ lint (the burn-down result):
  Direct clang-tidy (clang-tidy <file> -p build/lint, WarningsAsErrors:"*") reports
  0 findings across the whole MFTLibNative tree - down from 34 at the start of this
  session. clang-format --dry-run --Werror clean. The amalgamation (component-as-TU)
  builds green (MSBuild Release|x64). What was cleared, by bucket:
    - 7 modernize-avoid-c-arrays -> std::array (+ extRecord once it left extern-C)
    - 4 bugprone-suspicious-include -> NOLINT on the owner's fragment #includes
    - 14 bugprone-easily-swappable-parameters -> VolumeOffset/FileOffset strong
      types, FilterSpec/SliceRange/ChunkSpan/ParseState/SyntheticRecordSpec/
      BatchParams/RecordCount param bundles, ReadMFTRecord reorder; 2 C-ABI export
      boundaries took a justified per-site NOLINT
    - 2 bugprone-suspicious-realloc-usage (real leak-on-OOM bugs) -> std::vector
      slices + a temp-pointer merge realloc; covered by a new failure-injection test
    - 5 readability-function-cognitive-complexity -> ParseMFTImpl (130->ok),
      ParseMFTRecords (78->ok), ProcessRecordSlice/Batch, GenerateBatch decomposed
    - readability trivials (uppercase-suffix, identifier-length)
  cppcheck --enable=all surfaces only its pre-existing style tier (constVariablePointer,
  knownConditionTrueFalse, etc.) which the aislop gate does not score.

GATE CAVEAT (important): the pinned @schoen/aislop 0.12.3 in package.json STILL HAS the
clang-tidy WarningsAsErrors parser bug, so `aislop ci .` silently drops every clang-tidy
finding and reports score 100 even when findings exist. The parser fix (commit e7f5303) +
the component tooling already live on the aislop fork's schoen/main branch. Plan is to PIN
THE GATE TO THAT BRANCH (a git dependency on schoen/main), not republish a version - see the
handoff for the approach and the known Windows-CI build obstacle. Until the pin moves to the
branch, CI cannot actually enforce the native clang-tidy surface - trust direct clang-tidy,
not the gate, for the C++ tree.

STILL PENDING (attended / outward - left for the maintainer):
  - Elevated managed coverage: run-coverage.ps1 (approve UAC) to verify the admin
    paths green under the new structure and confirm native line coverage closes to 100%.
  - Pin the gate to the aislop fork's schoen/main BRANCH (git dep), not a republished
    version, so CI enforces the native clang-tidy surface instead of silently passing.
    (Known obstacle: aislop's postinstall build under the Windows act_runner - see handoff.)
  - Finish the jb inspectcode (ReSharper C++) sweep - not re-run after this refactor.
    The GLOBAL aislop 0.12.3 binary now counts these (2026-07-06: score 68/100, 10
    findings, all Cpp* rules in MFTLibNative - 7x CppUnusedIncludeDirective, 1x
    CppUnnamedNamespaceInHeaderFile, 1x function-too-long + rounding), so `aislop ci .`
    is red at baseline against failBelow:100. Predates the VolumeBroker work, which
    adds zero findings (verified against the bare parent-branch tip).
  - Push branches feat/aislop-whole-repo-100 + feat/volume-broker + open the
    claude-code PRs. file-wizard's submodule pin (d55f5a1) and git-wizard's vendored
    DLLs both reference fd0b750, so the push must land on BOTH MFTLib remotes.
  See .superpowers/sdd/2026-06-29-burndown-complete-handoff.md and
  docs/handoff-release-0.3.0.md for the full next-steps detail.

Commands:
  .\scripts\run-coverage.ps1                  # full managed run incl. admin tests (UAC)
  .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
  .\scripts\native-coverage.ps1               # native line/branch coverage (non-elevated)
  cmake -S MFTLibNative -B build/lint -G Ninja -DCMAKE_CXX_COMPILER=clang-cl \
        -DCMAKE_EXPORT_COMPILE_COMMANDS=ON    # regenerate compile_commands for clang-tidy
