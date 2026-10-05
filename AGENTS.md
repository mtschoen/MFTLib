# AGENTS.md

This file is the cross-tool source of truth for coding agents working in this repository. Tool-specific instruction files (`CLAUDE.md`, etc.) import it via `@AGENTS.md`.

Each linked docs page holds the full contract for its area: open it before you change that area.

## No backward compatibility

MFTLib has never shipped to an external consumer. file-wizard and git-wizard are its only consumers; they are dogfooding projects and are migrated in their pin-bump pull requests. Breaking changes are the purpose of 0.3.0.

- A breaking change is never a cost, a risk, or a trade-off. Do not list it as one in a plan, a specification, a review, or a pull request, and do not weigh it when choosing between designs.
- Delete in place. Do not keep old overloads, `[Obsolete]` members, flags or enum members that preserve previous behavior, transitional wire fields, dual read paths, or a parameter "in case a caller needs it".
- A constant or format that changes does not need version-bump ceremony for its own sake. A cache block in an old format is discarded and the drive cold-scans.
- Describe what each consumer must change as plain migration work, listed by file.
- Do not ask the owner whether to preserve compatibility. The answer is no.

## Build Commands

**Use MSBuild with `-p:Platform=x64` for the native C++ DLL**, and `dotnet build` for managed projects:

```bash
# Build native and managed Release|x64 projects
.\init.ps1 -Build

# Build managed projects (or test program)
dotnet build -c Release -p:Platform=x64
dotnet build TestProgram\TestProgram.csproj -c Release -p:Platform=x64
```

`dotnet build` cannot build `.vcxproj`. `init.ps1 -Build` passes MSBuild an absolute `SolutionDir` with a trailing backslash so the native DLL lands in root `x64\Release`, where managed builds copy it.

### NuGet packaging

```bash
# Build Release and pack the NuGet packages
.\init.ps1 -Build
dotnet pack MFTLib\MFTLib.csproj -c Release -p:Platform=x64
dotnet pack MFTLibTestExtensions\MFTLibTestExtensions.csproj -c Release -p:Platform=x64

# Publish to nuget.org, with MFTLib before its exact-version test dependency
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push "MFTLibTestExtensions\bin\x64\Release\MFTLib.TestExtensions.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Running the test program

The test program requires admin elevation (raw volume access). It includes **self-elevation logic** via `ElevationUtilities`.

For the most reliable experience (proper UAC prompt handling), **run the compiled .exe directly**:

```bash
# Launch directly (will trigger UAC prompt if not already elevated)
.\TestProgram\bin\x64\Release\net10.0\TestProgram.exe C:

# Results are written to output.log in the same directory
cat .\TestProgram\bin\x64\Release\net10.0\output.log
```

If running via `dotnet TestProgram.dll`, the helper will still attempt to relaunch the process with `runas`, but running the `.exe` is preferred.

### Test coverage

[Coverage procedures](docs/development-coverage.md): run `.\scripts\run-coverage.ps1` (admin/UAC) or `.\scripts\run-coverage.ps1 -NonInteractive` (headless). Native coverage: `.\scripts\native-coverage.ps1` or `.\scripts\native-coverage.ps1 -HtmlReport`; instrument Debug|x64 with `/PROFILE`, using `native-coverage.runsettings`. USN tests need admin; `scripts/native-coverage-elevated.ps1` self-elevates and streams `native-coverage-elevated.log`.

[Coverage status gate](docs/development-coverage.md): a drop of more than 10 percentage points from the nearest successful main-line measurement, or zero covered executable lines in a tested namespace, fails with a nonnumeric error status. Failed collection, missing/malformed reports and unavailable baseline lookup fail closed. Check MFTLib, MFTLib.Index, TestProgram, Benchmark and MFTLibTestExtensions; extend the namespace list and regression fixtures for new tested executable namespaces. Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1`. On rejection, preserve and inspect `windows-coverage` (`MFTLib.Tests/coverage.xml`, `coverage-report/`, `coverage-run.stdout.log`, `coverage-run.stderr.log`) and rerun CI; an error still blocks the gate and is not a trusted low-coverage measurement.

### Test isolation

[Native delegate isolation](docs/test-isolation.md): every test class referencing process-global seams in `MFTLibNative` or `FileUtilities`, either `ResetToDefaults`, or any `NativeTestHooks` member must have class-level `[DoNotParallelize]`. Keep cleanup resets; they do not isolate. Run `NativeSeamIsolationTests` on Windows and Linux. Stress with 32 ClassLevel MSTest workers and the existing `scripts/coverage-linux.sh` platform exclusions; never add exclusions.

[Default-cache isolation](docs/test-isolation.md): MFTLib.Tests enables the guard from a module initializer. Consumer tests must call `MFTLibTestExtensions.CacheDirectoryIsolation.ForbidDefaultCacheDirectory()` from `[ModuleInitializer]` before opening indexes; an assembly reference alone does not activate it. Activation is one-way/idempotent, has no reset, is not inherited by children and is not a native seam. `CacheDirectory.ResolveDefaultPath()` then throws `InvalidOperationException`; tests must set `FileIndexOptions.CacheDirectory` to an owned temporary directory, including empty-drive and `NoCache` opens. Explicit arbitrary paths are not blocked; non-opted-in production retains default behavior.

[Journal isolation](docs/test-isolation.md): the initializer also calls `MFTLibTestExtensions.JournalIsolation.ForbidLiveJournalReads()` with the same one-way terms. It returns "cannot say", not an exception or a live fallback. `OverrideJournalWindow(Func<char, SyntheticJournalWindow?>)` supplies synthetic journal windows; null never falls back live. Synchronize mutable observations because callbacks can overlap across drives. Public scopes are process-global; nesting/overlap throws `InvalidOperationException`. Mark the whole fixture `[DoNotParallelize]`, keep the scope through awaited work, and stop/dispose watches and indexes before its disposal. Never mix internal overrides with an active public scope. Real-volume tests use `JournalCheckpointCheck.ReadLiveJournal` explicitly.

## Cleaning the working tree

[Clean-tree invariant](docs/checkout-maintenance.md): `git clean -ffxd` must be safe before new work. Nothing unrecoverable lives here: files are tracked and pushed or reproducible; machine-only valuable files belong outside the tree. Never add files that satisfy neither rule. No exclude list protects against `-x`; do not substitute wrappers for this invariant. Regenerate agent files after cleaning or an aislop upgrade with `aislop hook install claude --project`; generated `.claude/AISLOP.md` and `.claude/CLAUDE.md` do not restore themselves. Aislop history is disposable; the gate is absolute `failBelow: 100`.

[Restore after cleaning](docs/checkout-maintenance.md): Windows uses `git clean -ffxd && .\init.ps1` or `git clean -ffxd && .\init.ps1 -Build`; cmd uses `git clean -ffxd && .\init.bat` or `git clean -ffxd && .\init.bat -Build`. Keep the `.\` prefix (`NoDefaultCurrentDirectoryInExePath=1`). Linux uses `git clean -ffxd && ./init.sh` or `git clean -ffxd && ./init.sh --build`. Restore is idempotent; build adds native plus managed Release|x64 on Windows. Windows prerequisites: .NET SDK, VS/MSVC, aislop, reportgenerator for HTML. Linux: .NET SDK, cmake, ninja, g++, aislop, gcovr for native coverage. Init checks, never installs, prerequisites.

## CI and quality gates

[CI rules](docs/quality-gates.md): `.gitea/workflows/test.yml` runs Windows and Linux coverage on PRs and main pushes, via `scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`. Both pull-request checks and `aislop / quality-gate (pull_request)` must pass before merge. Read `~/schoen-lab/packages/local_ci/docs/project-ci-setup.md` before workflow edits. Fix runner PATH/pwsh and `DOTNET_INSTALL_DIR` at the service, never with workflow bootstrap steps.

[Aislop rules](docs/quality-gates.md): invoke installed `aislop`, never `npx aislop`; the fork supplies C# engines. Before completion run `aislop scan .` and address findings; before committing run `aislop scan --staged`. `aislop fix` handles mechanical fixes; `aislop fix --claude` handles the rest. `aislop ci .` enforces `.aislop/config.yml`'s `failBelow: 100`; failure is a failing gate. `.gitea/workflows/aislop.yml` runs on windows-latest for every PR/main push; use 64-bit amd64 MSBuild/MSVC for the mixed solution. Preserve `lint.csharp.jbProjects` scoping and native clang-tidy/cppcheck separation. Do not use `actions/setup-node` on the host-mode runner; the workflow builds the pinned fork with pnpm and runs it with node. Keep the local fork pin and `.aislop/fork-commit` roughly synchronized but update them separately. Never infer a commit from a version/tag: inspect `aislop --version` and `pnpm ls -g --depth 0`. The global update command is `pnpm add -g --allow-build=aislop "github:mtschoen/aislop#<commit-ish>"`.

[Instruction budgets](docs/quality-gates.md): keep this file strictly below 15000 characters and each blank-line-delimited block strictly below 3000. Preserve rules here and relocate narrative verbatim to linked docs. `AgentInstructionsTests` runs in both existing test jobs; locally run `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AgentInstructionsTests` and `wc -m AGENTS.md`. Build before a `--no-build` test run so its copied document is current.

## Architecture

[Components and ABI](docs/architecture.md): preserve native thread safety, runtime record geometry and validated compact ABI/stride before parsing. Materialize lazy native-backed strings before freeing buffers. Preserve streaming/batched APIs and elevation dispatch. TestProgram self-elevates; Benchmark uses synthetic data. `MFTLibTestExtensions` ships separately, never inside MFTLib; its harness exposes production process/channel outcomes. The linked contracts specify layouts and component responsibilities.

[Public surface](docs/architecture.md): `PublicSurfaceTests` pins every public type and member of MFTLib and MFTLibTestExtensions to `MFTLib.Tests/PublicSurface/*.approved.txt`; update the file in the same pull request. A member is public only when a consumer needs it in production code. Test-only access goes through `MFTLibTestExtensions`, which forwards to internal code and never re-implements it. `InternalsVisibleTo` never names a consumer assembly. A pull request that grows an approved file names, in its body, the consumer production caller of every added member.

[Index namespace boundary](docs/architecture.md): `MFTLib.Index` must not depend on flat `MFTLib` or `MFTLib.Interop` except `UsnJournalEntry`, `UsnJournalEntryOptions`, `UsnJournalSettings` and `UsnReason`. `MFTLib.Tests/Index/NamespaceBoundaryTests.cs` enforces the compiled-IL boundary with a mandatory negative control; growing the allowlist requires review, not an import-lint rule.

[Cache identity](docs/architecture.md) and [block format](docs/index-format.md): CacheTag compares exactly, including zero; initialize before completion and copy `request.CacheTag` in custom producers. Consumers bump versions for profile/keep-list changes. Preserve the documented mismatch, cache-only failure and diagnostic contracts.

[Checkpoint loss](docs/checkpoint-loss.md): classify journal observations, never exception wording. Preserve `JournalCheckpointLoss.DetectedDuring` labels and existing reports on unrelated faults; automatic recovery retains LiveWatch reports. An unresumable block's refused start retains the watch request; a successful rescan clears the refusal and starts the watch. A failed rescan must never arm a cursor the journal cannot resume. Preserve bounded catch-up recovery, refusal after three consecutive losses, progress checks and journal-size arithmetic in the linked contract.

[Query and writer lifetime](docs/query-lifetime.md): all eight row-scanning APIs borrow snapshots throughout scanning and observe cancellation before the first row and every 4096 rows; Children uses only caller cancellation. DisposeAsync cancels linked work and drains current/retired borrows before unmapping. Suspended enumerators retain borrows: dispose promptly. Preserve per-access IsReleased checks and BlockWriter scopes; BlockFile.Dispose refuses new scopes and drains existing ones before unmapping. Raw property reads lack that serialization.

[Watch lifetime](docs/watch-lifetime.md): start readiness is connection plus StartWatch written, not CaughtUp. Waits follow the per-drive instance. Healthy watches survive rescan production until replacement commit; failed/cancelled production preserves them. Respect identity-checked publication, rescan recovery, fault consumption and stop-wins rules. Gates order lifecycle/write/state; no gate acquisition under state. Follow linked disposal ordering, callback reentrancy and concurrent-open contracts; Channel faults never recover. Manual restart failure reports RescanRestart, which stop rethrows once.

[Block ownership](docs/block-ownership.md): hold the owner lock before canonical validation, sweep, rename or deletion. Contention means cache-only InUse or private scanning. Never unlink `.lock` files; cache pruning must leave `*.mlix.lock` alone. `CacheDirectory.DeleteCached` is the lock-safe clearing API. Preserve its outcomes, diagnostics and rejected-name callback contracts. Only replacement publication changes CacheSlot backing; reread Drives after rescan, and preserve backing on failed/cancelled production.

[Broker lifetime](docs/broker-lifetime.md): one process/control pipe per session, one channel per drive operation; preserve request-id reservation and connection handshake. Drive failure isolates its operation; control EOF ends all. Preserve the shared parse budget, block validation and ScanReady/catch-up classification. Heartbeat visits are five seconds; processing progress and client silence limits are 30 seconds. Heartbeats never reset processing progress; skip in-flight writes. Preserve --broker dispatch and bounded self-filtered diagnostics (`MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in).

### Native error messages

[Native error helper](docs/architecture.md): use `SetErrorMessage` in `MFTLibNative/internal.h` for native result `wchar_t errorMessage[256]` buffers. Windows truncation uses `_snwprintf_s(..., _TRUNCATE, ...)`; Debug asserts overflow. Avoid direct `swprintf_s` / `snprintf_s` calls at error-write sites.

## Roadmap

See `.plan` for details. Current release is **0.3.0** with USN journal support. Primary consumers are file-wizard and git-wizard.
