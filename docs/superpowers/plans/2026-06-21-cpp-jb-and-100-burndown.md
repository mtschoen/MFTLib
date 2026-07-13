# C++ jb Inspection + MFTLib 100/100 Burn-Down Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring MFTLib's whole repository (managed C# *and* native C++) under the aislop quality gate at a genuine 100/100 with real inspections - no suppressions, no relaxed thresholds, no excluded trees - by (A) teaching aislop to run JetBrains InspectCode (ReSharper C++) over the native project and (B) burning down every resulting real finding.

**Architecture:** Two coordinated phases across two repositories. **Phase A** (in `~/aislop`, branch `feat/cpp-support` -> merge to `schoen/main`, then publish a new `@schoen/aislop` tarball) adds a C++ jb lint path: InspectCode already emits the same `<Issue TypeId File Line>` XML for C++ as for C#, so the work is scoping inspectcode to include the `.vcxproj`, auto-labelling C++ issues, deduping jb's clang-tidy-backed findings against aislop's own clang-tidy, and a config toggle. **Phase B** (in `~/MFTLib`, a feature branch off `main`) drops the `MFTLibNative/**` exclusion, wires `compile_commands.json` generation into CI, and burns the native-tree findings (cppcheck + clang-tidy + ai-slop + complexity + jb-cpp) down to zero, holding 100% managed + native coverage throughout.

**Tech Stack:** aislop is TypeScript (Node ESM), vitest, zod/v4 config. External tools, all PATH-resolved and presence-gated: `jb` (JetBrains.ReSharper.GlobalTools 2026.1, installed at `~/.dotnet/tools/jb`), `cppcheck` 2.19, `clang-tidy`/`clang-format` (LLVM 22). MFTLib is C# (.NET 8, x64) + native C++ (MSBuild + MSVC, `MFTLibNative.vcxproj`), CMake lint target generates `build/lint/compile_commands.json`.

## Global Constraints

- **No suppressions, no corner-cutting** (user directive). The escalation ladder's steps 4-5 (`NOLINT`, `[SuppressMessage]`, lowered `failBelow`, relaxed `maxFileLoc`/`maxFunctionLoc`, rule-disable in `.aislop/config.yml` or `.clang-tidy`) are OFF the table unless a finding is a genuine false positive, and even then only with explicit user approval per-case. Default action for every finding is **fix the code**.
- **Maintain 100% coverage** - managed (`scripts/run-coverage.ps1`) AND native (`scripts/native-coverage.ps1`). Every burn-down edit re-runs both gates. A refactor that drops a covered line means a test moves with it.
- **Build with MSBuild `-p:Platform=x64`**, build the `.sln` (never `dotnet build`, never an individual `.csproj`). See AGENTS.md.
- **No em-dashes** in any generated content (code, comments, commit messages, docs). ASCII only.
- **No machine-specific hard-coded paths** in committed code or CI.
- aislop changes are measured with the **locally-built dev binary** (`node ~/aislop/dist/cli.js`) during development, but the **CI gate uses the published `@schoen/aislop` tarball** - Phase A is not "done" until the tarball is republished and the pin in `MFTLib/package.json` + lockfile is bumped (Task B1).
- jb identity / Gitea writes: act as `claude-code` (see `~/.claude/CLAUDE.md`).

## Decisions (made up front; reversible - flag if you disagree)

- **D1 - Augment, do not replace.** jb-cpp runs *in addition to* cppcheck + clang-tidy + ai-slop. To stop double-counting jb's clang-tidy-backed inspections against aislop's standalone clang-tidy, the cpp dedup normalizes jb `CppClangTidy<CamelCheck>` rule ids to the clang-tidy kebab id (`bugprone-narrowing-conversions`, etc.) before keying. ReSharper-native inspections (CppCStyleCast, CppTooWideScope, ...) have no clang-tidy twin and stay distinct.
- **D2 - SUGGESTION severity floor for cpp jb in MFTLib (full-tier sweep, user directive).** MFTLib clears the *entire* jb SUGGESTION+ tier - all 99 issues from the spike, including the 24 C-style casts, 33 use-anonymous-namespace, 8 const-ptr-or-ref, 7 internal-linkage, 6 too-wide-scope. (Only the HINT tier, advisory-only, is below the gate.) The aislop *default* for `lint.cpp.jbSeverityFloor` stays `WARNING` (sensible for the broader user base); MFTLib's `.aislop/config.yml` overrides it to `SUGGESTION`. Precedent: the same full-tier burn-down was done on the C# repos liminal and git-wizard.
- **D3 - jb-cpp is opt-in** (`lint.cpp.jb`, default `false`): it needs `jb` installed, a loadable solution, and (for the vcxproj) Windows + MSVC. MFTLib opts in via its `.aislop/config.yml`. Default-off keeps Linux/non-.NET repos unaffected.
- **D4 - `--no-build`.** InspectCode inspects the C++ project without an MSBuild build (confirmed in the spike: 99 issues, no build). CI passes `--no-build` to avoid a redundant/fragile native build inside inspectcode.

## Measured baseline (from this session's spike, fixed dev binary)

Native tree (`MFTLibNative`), after the FP fixes already merged:
- **aislop engines:** Linting 0 errors / 10 warn (10 cppcheck `dangerousTypeCast`); AI Slop 20 warn (15 `cpp-c-style-cast`, 4 `cpp-manual-delete`, 1 `trivial-comment`); Code Quality 4 warn (`mft_parse.cpp` 865 LOC; `GenerateSyntheticMFTImpl` 132 LOC; `usn_journal.cpp:32` 300 LOC + nesting depth 6); Format 0; Security 0. Score 34/100.
- **jb (ReSharper C++) at SUGGESTION floor, 99 issues total.** At WARNING floor (what gates): 9 `CppUnusedIncludeDirective`, 7 `CppClangTidyBugproneNarrowingConversions`, 2 `CppClangTidyBugproneSuspiciousReallocUsage`, 2 `CppClangTidyBugproneImplicitWideningOfMultiplicationResult`, 1 `CppAssignedValueIsNeverUsed` = **21 WARNING-level**. SUGGESTION-tier (below floor but worth clearing): 24 `CppCStyleCast`, 33 `CppClangTidyMiscUseAnonymousNamespace`, 8 `CppParameterMayBeConstPtrOrRef`, 7 `CppClangTidyMiscUseInternalLinkage`, 6 `CppTooWideScope`.
- Re-measure authoritatively at the start of Phase B (Task B0) - counts will shift as fixes land and as the file split changes line numbers.

---

## Phase A: aislop C++ jb lint path

Work in `~/aislop` on `feat/cpp-support`. Each task: build (`pnpm build`), typecheck (`pnpm typecheck`), run the touched vitest file, then the cpp/lint subset, commit. Merge `feat/cpp-support` -> `schoen/main` at phase end.

### Task A1: Auto-label jb issues by language; per-language exclude sets

The single InspectCode XML mixes C# (`CS*`, ReSharper `*`) and C++ (`Cpp*`) issues. The parser must label each correctly and apply the right exclude set, so one inspectcode pass can feed both dedup groups.

**Files:**
- Modify: `src/engines/lint/jb.ts` (`parseJbXml` ~42-92; `JbParseOptions` ~11-14)
- Test: `tests/lint/jb.test.ts` (create if absent) or extend existing jb test

**Interfaces:**
- Produces: `parseJbXml(xml, rootDirectory, options)` where `options.excludeTypes` may now be a function or a `{ csharp: Set, cpp: Set }`; each emitted `Diagnostic` has `category: "C++ Lint"` when `TypeId` starts with `Cpp`, else `"C# Lint"`. Rule stays `jb/<TypeId>`.

- [ ] **Step 1: Write failing test** - feed an XML fixture containing one `CS*` and one `Cpp*` IssueType+Issue; assert the cpp one gets `category: "C++ Lint"` and the C# one `"C# Lint"`.
- [ ] **Step 2: Run, verify fail.** `pnpm vitest run tests/lint/jb.test.ts`
- [ ] **Step 3: Implement** - in `parseJbXml`, set `category: typeId.startsWith("Cpp") ? "C++ Lint" : "C# Lint"`. Keep `toAislopSeverity` mapping. Exclude lookup picks the cpp vs csharp set by the same prefix test.
- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Commit** - `feat(cpp): label jb C++ issues as C++ Lint`.

### Task A2: cpp jb config (schema + defaults + docs)

**Files:**
- Modify: `src/config/schema.ts` (`CppLintSchema` ~36-40; defaults ~55-59), `src/config/defaults.ts` (cpp block ~31-33)
- Modify: `docs/configuration.md` (cpp lint section), `docs/rules.md` if it enumerates engines
- Test: `tests/config*.test.ts` (a defaults/parse test)

**Interfaces:**
- Produces: `lint.cpp.jb: boolean = false`, `lint.cpp.jbProjects?: string` (semicolon-separated project names to scope inspectcode; when unset and jb is on, the native projects are inferred / whole-solution is used), `lint.cpp.jbSeverityFloor: "ERROR"|"WARNING"|"SUGGESTION"|"HINT" = "WARNING"`, `lint.cpp.jbExcludeTypes: string[] = []`.

- [ ] **Step 1:** Failing test asserting the new defaults parse and round-trip.
- [ ] **Step 2:** Run, verify fail.
- [ ] **Step 3:** Add the four keys to `CppLintSchema` + both default literals; mirror into `resolveCppLintConfig` (`cppcheck.ts` `CppLintConfig` interface + `CPP_LINT_DEFAULTS`).
- [ ] **Step 4:** Run, verify pass. Regenerate the JSON schema: `pnpm gen:schema`; commit `schema/aislop.config.schema.json` too.
- [ ] **Step 5:** Commit - `feat(cpp): add lint.cpp.jb config (inspectcode toggle, scope, floor)`.

### Task A3: Run InspectCode over the C++ project

Extend the jb runner so, when `lint.cpp.jb` is on, the inspectcode scope includes the native project(s). Prefer **one** inspectcode pass over the union of C# + C++ project scopes (inspectcode is solution-level and slow); the parser (A1) already splits the output by language.

**Files:**
- Modify: `src/engines/lint/jb.ts` (`runJbLint` ~170-181, `analyzeJbTarget` ~126-168 - generalize the `--project` scope and exclude/floor selection so it carries both languages)
- Test: `tests/lint/jb.test.ts` (unit-test the *argv/scope builder*, extracted as a pure function like `buildJbProjectScope(csharp, cpp)`; the subprocess itself is integration-tested manually)

**Interfaces:**
- Consumes: `resolveCsharpLintConfig`, `resolveCppLintConfig`.
- Produces: `runJbLint(context)` returns diagnostics for BOTH languages (each correctly categorized). New pure helper `buildJbProjectScope({csharpProjects?, includeCsharp}, {cppProjects?, includeCpp}): string | undefined` returns the merged `--project` value (or `undefined` for whole-solution).

- [ ] **Step 1:** Failing test for `buildJbProjectScope` - union of `"A;B"` (csharp) and `"N"` (cpp) is `"A;B;N"`; cpp-only yields `"N"`; both-empty yields `undefined`.
- [ ] **Step 2:** Run, verify fail.
- [ ] **Step 3:** Implement `buildJbProjectScope`; have `runJbLint` use it and pass the merged scope; thread per-language floor (use the lower of the two floors as the inspectcode `--severity` pre-filter; the parser re-applies each language's floor authoritatively).
- [ ] **Step 4:** Run, verify pass.
- [ ] **Step 5: Manual integration check** - `node dist/cli.js scan` is run in Phase B; here just confirm a scoped `jb inspectcode MFTLib.sln --project=MFTLibNative --no-build` produces `Cpp*` issues (already confirmed in spike). Add `--no-build` to the cpp branch of the argv.
- [ ] **Step 6:** Commit - `feat(cpp): run InspectCode over C++ projects when lint.cpp.jb is on`.

### Task A4: Dedup jb-cpp clang-tidy findings against aislop clang-tidy

jb surfaces clang-tidy-backed inspections (`CppClangTidyBugproneNarrowingConversions`) that can duplicate aislop's own clang-tidy (`clang-tidy/bugprone-narrowing-conversions`) at the same site. Normalize jb's id so the existing file:line:bareId dedup collapses them.

**Files:**
- Modify: `src/engines/lint/index.ts` (`bareRuleId` ~24-27 or a new `canonicalCppRuleId`; the cpp dedup group ~90-97 to also receive jb-cpp diagnostics)
- Test: `tests/lint/cpp-dedup.test.ts`

**Interfaces:**
- Produces: `canonicalCppRuleId(rule)` maps `jb/CppClangTidyBugproneNarrowingConversions` -> `bugprone-narrowing-conversions` (strip `jb/`, strip `CppClangTidy`, CamelCase->kebab, lowercase) and `clang-tidy/bugprone-narrowing-conversions` -> `bugprone-narrowing-conversions`; passes other ids through `bareRuleId`. The cpp dedup keys on this.
- Wiring: the cpp dedup group becomes `dedupeCppDiagnostics([...cppcheck, ...clangTidy, ...jbCppPartition])`. jb runs once (csharp||cpp), and index.ts partitions its output by `category` into the csharp dedup group and the cpp dedup group.

- [ ] **Step 1:** Failing test - a jb narrowing-conversion diagnostic and a clang-tidy narrowing-conversion at the same file:line collapse to one; two genuinely different cpp issues at one line do not.
- [ ] **Step 2:** Run, verify fail.
- [ ] **Step 3:** Implement `canonicalCppRuleId` + CamelCase->kebab helper; route jb output partitions into both dedup groups; have `dedupeCppDiagnostics` key on `canonicalCppRuleId`.
- [ ] **Step 4:** Run, verify pass; run the full lint subset `pnpm vitest run tests/lint`.
- [ ] **Step 5:** Commit - `feat(cpp): dedup jb clang-tidy inspections against aislop clang-tidy`.

### Task A5: Phase A docs + full suite + merge

- [ ] **Step 1: Docs** - run the docs-update check; document `lint.cpp.jb*` in `docs/configuration.md`, note C++ inspectcode support in the C/C++ section of `docs/rules.md`/README and the cpp-support plan/CHANGELOG. Commit.
- [ ] **Step 2: Full suite** - `pnpm build && pnpm typecheck && npx vitest run`. Confirm no NEW failures beyond the 18 pre-existing environmental ones recorded this session (ruff/security/agents/cli/config-extends - reproduce on the clean branch). If any cpp/lint/jb/config test fails, fix before merge.
- [ ] **Step 3: Merge** - `git checkout schoen/main && git merge feat/cpp-support --no-edit`.

---

## Phase B: MFTLib whole-repo burn-down to 100/100

Work in `~/MFTLib` on a feature branch off `main` (e.g. `feat/aislop-whole-repo-100`). The CI gate uses the published tarball, so Task B1 must land (and be verified green locally) before the gate can enforce. Burn-down order: safest/most-mechanical first, each category gated by build + managed coverage + native coverage + `aislop scan`.

### Task B0: Branch, drop exclusion, authoritative inventory

**Files:**
- Modify: `.aislop/config.yml` (remove `MFTLibNative/**` from `exclude`; add `lint.cpp.jb: true` under `lint.cpp`; keep build-output excludes)
- Generate: `build/lint/compile_commands.json` (CMake lint configure - confirm the existing target still produces it; it is git-ignored build output)

- [ ] Create the feature branch off `main`.
- [ ] Drop the `MFTLibNative/**` exclude; enable `lint.cpp.jb: true` (and `lint.cpp.jbProjects: "MFTLibNative"`).
- [ ] Regenerate `build/lint/compile_commands.json`.
- [ ] Run the **authoritative** whole-repo inventory with the dev binary: `node ~/aislop/dist/cli.js scan . --json > /tmp/inventory.json` from the repo root; record per-rule counts (this supersedes the spike baseline). Confirm C# side still scores clean (no regression from un-scoping jb).
- [ ] Commit - `chore: un-exclude MFTLibNative from aislop, enable jb C++ (gate will be red until burn-down)`.

### Task B1: Publish updated @schoen/aislop tarball + bump CI pin

The fixes from this session and Phase A only reach CI via the published tarball.

**Files:**
- `~/aislop` packaging (the `@schoen/aislop` tarball build/publish flow to the Gitea npm registry)
- Modify: `MFTLib/package.json` + `MFTLib/package-lock.json` (version + sha512 integrity), per AGENTS.md "To bump aislop"

- [ ] Build + publish the new `@schoen/aislop` tarball (mirror of `schoen/main`) to the Gitea npm registry.
- [ ] Bump the version in `MFTLib/package.json`; refresh the lockfile (`npm install --package-lock-only`).
- [ ] Verify CI's aislop workflow installs the new version and the C++ tools (`cppcheck`/`clang-tidy`/`clang-format`/`jb`) are present on the windows runner; add a `compile_commands.json` generation step to `.gitea/workflows/aislop.yml` before the aislop step (CMake lint configure). Read `~/local-ci/docs/project-ci-setup.md` first.
- [ ] Commit - `ci(aislop): bump @schoen/aislop, generate compile_commands for C++ gate`.

### Task B2: Trivial / mechanical findings (dead code, unused includes, dead store)

Lowest-risk, no behavior change. Clears: `ai-slop/trivial-comment` (1), `CppUnusedIncludeDirective` (9), `CppAssignedValueIsNeverUsed` (1), `CppTooWideScope` (6), `CppClangTidyMiscUseInternalLinkage` (7) + `CppClangTidyMiscUseAnonymousNamespace` (33) (mark file-local helpers `static` / move into an anonymous namespace), `CppParameterMayBeConstPtrOrRef` (8).

- [ ] Fix each, file by file. Removing an include or narrowing a scope can break the build - rebuild the `.sln` after each file.
- [ ] **Gate:** `MSBuild MFTLib.sln -p:Configuration=Release -p:Platform=x64`; `scripts/run-coverage.ps1 -NonInteractive`; `scripts/native-coverage.ps1`; `node ~/aislop/dist/cli.js scan .`. Coverage stays 100%, these rule counts hit 0.
- [ ] Commit per coherent group - `refactor(native): remove unused includes / dead stores / internal linkage`.

### Task B3: Narrowing / widening / realloc conversions

Real correctness-adjacent findings: `CppClangTidyBugproneNarrowingConversions` (7), `CppClangTidyBugproneImplicitWideningOfMultiplicationResult` (2), `CppClangTidyBugproneSuspiciousReallocUsage` (2). These can mask genuine bugs (a multiplication that overflows before a widening assignment) - investigate each, don't just cast it away.

- [ ] Fix each with the correct-width type or an explicit, justified conversion (a named cast is fine here; a blind `static_cast` that hides a real overflow is not - treat per the systematic-debugging lens).
- [ ] **Gate:** build + both coverage gates + scan. (A changed type may need a test asserting the boundary behavior - add it.)
- [ ] Commit - `fix(native): correct narrowing/widening/realloc conversions flagged by clang-tidy`.

### Task B4: C-style casts -> named C++ casts

The big one: `ai-slop/cpp-c-style-cast` (15) + `cppcheck/dangerousTypeCast` (10) + jb `CppCStyleCast` (24) are largely the SAME casts seen by three tools. Convert each `(T)x` to `static_cast`/`reinterpret_cast`/`const_cast` as the semantics require. `reinterpret_cast` for pointer<->byte-buffer reinterpretation (the MFT parse hot path), `static_cast` for numeric. Each cast is a decision about intent and safety - this is where a wrong mechanical change introduces a real bug, so review the surrounding pointer arithmetic.

- [ ] Convert casts file by file (`mft_parse.cpp` dominates). Rebuild after each file.
- [ ] **Gate:** build + both coverage gates + scan. cast-related rule counts -> 0.
- [ ] Commit per file/group - `refactor(native): replace C-style casts with named C++ casts in <file>`.

### Task B5: Manual delete -> RAII

`ai-slop/cpp-manual-delete` (4) in `platform_posix.cpp`, `platform_win32.cpp`, `usn_journal.cpp` (+1). Own each resource with `std::unique_ptr` / a custom deleter (Windows `HANDLE` -> a `unique_ptr` with a `CloseHandle` deleter or an existing RAII wrapper). Behavior-preserving but lifetime-sensitive - verify no double-free / early-free against the tests.

- [ ] Introduce/RAII-wrap each owned resource; delete the manual `delete`/`CloseHandle`.
- [ ] **Gate:** build + both coverage gates (native coverage especially - lifetime paths) + scan.
- [ ] Commit - `refactor(native): replace manual delete with RAII ownership`.

### Task B6: Oversized function + deep nesting (`usn_journal.cpp:32`)

`complexity/function-too-long` (300 LOC) + `complexity/deep-nesting` (depth 6) on the same function (the spike labelled it `GetOverlappedResult` - verify the real name/extent first; complexity detection can mis-attribute across a Windows API macro). Extract cohesive sub-steps into named helpers (early returns to cut nesting). Must stay under `maxFunctionLoc: 80` and `maxNesting: 5`.

- [ ] Extract helpers; verify each is independently covered (extracting can create a newly-uncovered helper - add tests).
- [ ] **Gate:** build + both coverage gates + scan. function-too-long / deep-nesting -> 0.
- [ ] Commit - `refactor(native): extract helpers from oversized USN journal function`.

### Task B7: Oversized function `GenerateSyntheticMFTImpl` (132 LOC)

`complexity/function-too-long` in `mft_synthetic.cpp:172`. Same treatment - extract synthetic-MFT construction steps into helpers under 80 LOC each.

- [ ] Extract; cover.
- [ ] **Gate:** build + both coverage gates + scan.
- [ ] Commit - `refactor(native): split GenerateSyntheticMFTImpl into helpers`.

### Task B8: Oversized file `mft_parse.cpp` (865 LOC, max 400)

`complexity/file-too-large`. Split along natural boundaries into a `mft/` sub-structure (e.g. attribute parsing vs data-run parsing vs record fixup), each file < 400 LOC, updating the `.vcxproj` (and `.vcxproj.filters`) and includes. This is the riskiest structural change - do it last, after the casts (B4) have already reduced the file, in case the split alone gets it under 400 once casts are multi-line.

- [ ] Reassess `mft_parse.cpp` LOC after B4. If still > 400, split by responsibility; add the new `.cpp`/`.h` to `MFTLibNative.vcxproj` + filters + `CMakeLists.txt`.
- [ ] **Gate:** build (the vcxproj change is the failure-prone part - build the `.sln`) + both coverage gates + scan. file-too-large -> 0.
- [ ] Commit - `refactor(native): split mft_parse.cpp into focused translation units`.

### Task B9: Final gate + report + docs

- [ ] **Full whole-repo gate from the repo root:** `node ~/aislop/dist/cli.js ci .` -> exits 0 at score 100 (then re-confirm with the *published* tarball via the CI path). `scripts/run-coverage.ps1` (full, with admin tests) = 100%; `scripts/native-coverage.ps1` = 100%.
- [ ] **Write/update `TEST-REPORT.md`** at repo root: Mode `maintain`, Status PASS, git hash, test count, managed + native coverage, per-tool lint counts (cppcheck/clang-tidy/clang-format/jb/ai-slop/complexity all 0), 0 suppressions, 0 documented exceptions. Commit.
- [ ] **Docs** - run docs-update: `.aislop/config.yml` comment block (the `MFTLibNative` "NOT scored here" paragraph is now false - rewrite it), AGENTS.md "Quality gate" + "CI gate" sections (C++ now gated, jb-cpp added, compile_commands generated in CI), `.plan` roadmap. Add `aislop / quality-gate (pull_request)` already covers it; confirm branch protection. Commit.
- [ ] **Branch finish** - open PR as `claude-code` to Gitea; the user approves/merges. Fold any durable insight (the RAII HANDLE wrapper, the mft_parse split rationale) into code/AGENTS.md; this plan is deleted at merge.

---

## Risks / watch-items

- **Coverage churn:** every extraction (B6-B8) risks a newly-uncovered helper or a moved branch. The native-coverage script can hang when elevated (see memory `reference_native_coverage_hang.md`) - run it non-elevated and verify branch stays 100% + per-line.
- **vcxproj edits (B8):** the native DLL only builds under MSBuild + MSVC x64; a malformed `.vcxproj`/filters edit fails the `$(SolutionDir)` xcopy post-build. Build the `.sln`, not the project.
- **jb in CI:** the windows act_runner must have `jb` (dotnet global tool) + LLVM + cppcheck on PATH and a loadable solution; inspectcode is slow (~2 min) - the aislop workflow timeout must accommodate it. `--no-build` avoids a native build inside inspectcode but the project model must still load.
- **Tarball/gate ordering:** if CI flips red before B1's tarball is published + B-burndown lands, `main` PRs are blocked. Keep the un-exclude (B0) and the gate-enabling on the feature branch; only merge once green.
- **Dedup correctness (A4):** if the CamelCase->kebab normalizer is wrong, either real findings get swallowed (bad) or duplicates survive (cosmetic). The A4 test must cover a non-clang-tidy `Cpp*` id passing through unchanged.
