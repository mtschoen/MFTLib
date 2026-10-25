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

# Build only (restore + native + managed, without checkout initialization)
.\scripts\build-windows.ps1

# Build the managed library (or one sample)
dotnet build MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build SampleProgram.Watch\SampleProgram.Watch.csproj -c Release -p:Platform=x64
```

`dotnet build` cannot build `.vcxproj`. `scripts/windows-tools.ps1` owns checked Visual Studio discovery (including BuildTools). `scripts/build-windows.ps1` owns restore and the Windows build recipe: amd64 MSBuild, an absolute trailing-backslash `SolutionDir`, then managed builds. Native output lands in root `x64/<Configuration>`. Init passes `-NoRestore` after its successful restore; release checks Clean, then restores through coverage's shared build.

### NuGet packaging

```bash
# Pack through .\scripts\release.ps1 (dry run first): it builds the linux-x64 library in WSL, which a bare
# dotnet pack lacks and Test-ReleasePackages.ps1 rejects ([Linux package](docs/linux-package.md))

# Publish to nuget.org, with MFTLib before its exact-version test dependency
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
dotnet nuget push "MFTLibTestExtensions\bin\x64\Release\MFTLib.TestExtensions.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Running the sample programs

Direct local-volume runs need admin rights (raw volume access) and **self-elevate** via `IElevationProvider`; Direct dump runs need none. Watch is unelevated, its broker elevates, and it prints to the console.

**Run the compiled .exe directly** for UAC:

```bash
.\SampleProgram.Direct\bin\x64\Release\net10.0\SampleProgram.Direct.exe scan C:
cat .\SampleProgram.Direct\bin\x64\Release\net10.0\output.log
```

`dotnet <sample>.dll` cannot self-elevate. [Attended and unattended runs](docs/elevation.md): `MFTLIB_SAMPLE_UNATTENDED=1` skips every prompt.

### Test coverage

[Fast check](SMOKE.md): `--filter TestCategory=Smoke` is the agent floor; the full suite is the bar.

[Coverage procedures](docs/development-coverage.md): run `.\scripts\run-coverage.ps1` (admin/UAC) or `.\scripts\run-coverage.ps1 -NonInteractive` (headless). Native coverage: `./scripts/native-coverage.ps1` or `./scripts/native-coverage.ps1 -HtmlReport`. USN tests need admin; `scripts/native-coverage-elevated.ps1` self-elevates and streams `native-coverage-elevated.log`.

[Coverage status gate](docs/development-coverage.md): a drop of more than 10 percentage points from the nearest successful main-line measurement, or zero covered executable lines in a tested namespace, fails with a nonnumeric error status. Failed collection, missing/malformed reports and unavailable baseline lookup fail closed. Check MFTLib, MFTLib.Index, MFTLib.NativeInterop, both samples, Benchmark and MFTLibTestExtensions; extend the namespace list and regression fixtures for new tested executable namespaces. Run `pwsh -NoProfile -File scripts/test-coverage-status.ps1`.

### Test isolation

[Native delegate isolation](docs/test-isolation.md): every test class referencing process-global hooks in `MFTLibNative.Hooks`, `FileUtilities.Hooks`, `Kernel32.Hooks`, their `ResetToDefaults`, or any `NativeTestHooks` member must have class-level `[DoNotParallelize]`, cleanup resets, and one explicit control-code dispatcher when a scenario needs multiple `DeviceIoControl` operations. Resets do not isolate. Run `NativeSeamIsolationTests` on Windows and Linux. Stress with 32 ClassLevel MSTest workers and existing `scripts/coverage-linux.sh` exclusions; never add exclusions.

[Explicit cache ownership](docs/test-isolation.md): `CacheMode.Persistent` and `ExistingOnly` opens (null `FileIndexOptions.CacheMode` is `Persistent` for non-dump sources) require a nonblank application-owned `FileIndexOptions.CacheDirectory`, including empty-drive opens; missing configuration throws `ArgumentException`. Tests supply owned temporary paths. `Disabled` opens require no directory and ignore any supplied path without resolving or creating it. A dump source supplies its own drive and is never cached: validation runs first and requires non-null empty `Drives`, null `CacheMode` and `CacheDirectory`, and default `CacheTag`. Watch owns `<user profile>/.MFTLib.Sample.Watch/cache` with policy subdirectories; without a profile it requires `--cache-directory`.

[Journal isolation](docs/test-isolation.md): test assemblies call `MFTLibTestExtensions.JournalIsolation.ForbidLiveJournalReads()` from a module initializer. Activation is one-way/idempotent, has no reset and is not inherited by children; an assembly reference alone does not activate it. It returns "cannot say", not an exception or a live fallback. Synchronize mutable observations because callbacks can overlap across drives. Journal override scopes are process-global; nesting/overlap throws `InvalidOperationException`. MFTLib.Tests installs observations through `JournalIsolation.OverrideJournalWindow` directly; explicit live-reader tests pass a private `ReadLive`.

## Cleaning the working tree

[Clean-tree invariant](docs/checkout-maintenance.md): `git clean -ffxd` must be safe before new work. Nothing unrecoverable lives here: files are tracked and pushed or reproducible; machine-only valuable files belong outside the tree. Never add files that satisfy neither rule. Regenerate agent files after cleaning or an aislop upgrade with `aislop hook install claude --project`. Aislop history is disposable; the gate is absolute `failBelow: 100`.

[Restore after cleaning](docs/checkout-maintenance.md): Windows uses `git clean -ffxd && .\init.ps1` or `git clean -ffxd && .\init.ps1 -Build` (keep `.\` prefix; cmd uses `init.bat`). Linux uses `git clean -ffxd && ./init.sh` or `git clean -ffxd && ./init.sh --build`. Restore is idempotent; build adds native plus managed Release|x64 on Windows. Init checks, never installs, prerequisites (compilers, SDKs, aislop, coverage tools).

## CI and quality gates

[CI rules](docs/quality-gates.md): `.gitea/workflows/test.yml` runs Windows/Linux coverage and Linux package checks on PRs and main pushes via `scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`. The `test / windows`, `test / linux`, `test / linux-package` and `aislop / quality-gate` PR checks must pass before merge. Fix runner PATH/pwsh and `DOTNET_INSTALL_DIR` at the service, never with workflow bootstrap steps.

`TEST-REPORT.md` is generated on main by CI and never edited in a pull request.

[Maintained developer tools](docs/maintained-developer-tools.md): only redundant code is deleted. Lack of production or CI callers does not make a developer tool redundant; add CI exercise. Keep every script listed with its purpose, runner role and CI check. Syntax and inventory gates enforce the baseline.

`AgentInstructionsTests` guards tracked-content portability. Stage additions/removals first; `.maintenance.json` is ignored, `.plan` tracked.

[Aislop rules](docs/quality-gates.md): invoke installed `aislop`, never `npx aislop`. Before completion run `aislop scan .` and address findings; before committing run `aislop scan --staged`. `aislop ci .` enforces `.aislop/config.yml`'s `failBelow: 100`; failure is a failing gate.

[Instruction budgets](docs/quality-gates.md): keep this file strictly below 15000 characters and each blank-line-delimited block strictly below 3000. Preserve rules here and relocate narrative verbatim to linked docs. `AgentInstructionsTests` runs in both existing test jobs; locally run `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AgentInstructionsTests` and `wc -m AGENTS.md`. Build before a `--no-build` test run so its copied document is current.

## Architecture

[Components and ABI](docs/architecture.md): preserve native thread safety, runtime record geometry and validated compact ABI/stride before parsing. Materialize lazy native-backed strings before freeing buffers. Preserve streaming/batched APIs and elevation dispatch. Direct local scans self-elevate; Watch stays unelevated and its broker elevates; Benchmark uses synthetic data. `MFTLibTestExtensions` ships separately, never inside MFTLib; its harness exposes production outcomes.

[Public surface](docs/architecture.md): `PublicSurfaceTests` pins every public type and member of MFTLib and MFTLibTestExtensions to `MFTLib.Tests/PublicSurface/*.approved.txt`; update it in the same pull request. Public members need a sample or reasoned consumer caller (`PublicMemberCallerTests`). Test-only access goes through `MFTLibTestExtensions`, which forwards to internal code. `InternalsVisibleTo` never names a consumer assembly. A pull request that grows an approved file names, in its body, the consumer production caller of every added member.

[API decisions](docs/api-decisions.md): owner rulings on the public surface, with reasons. Read it before proposing to remove, move or reshape a public member; "MFTLib never calls it" or "not an MFT primitive" does not reopen a ruling. Record each new ruling there in the same change.

[Index namespace boundary](docs/architecture.md): `MFTLib.Index` may use `MFTLib.NativeInterop` for the shared native boundary; dependencies on flat `MFTLib` or `MFTLib.Interop` remain restricted to `UsnJournalEntry`, `UsnJournalSettings`, and `UsnReason`. `NamespaceBoundaryTests` enforces the compiled-IL boundary with a mandatory negative control and verifies the shared namespace permission. Growing the allowlist requires review.

[Cache identity](docs/architecture.md) and [block format](docs/index-format.md): CacheTag compares exactly, including zero; initialize before completion. The library compares row-shaping options as the scan shape; the tag carries consumer meaning only. Preserve the documented mismatch, cache-only failure and diagnostic contracts.

[Checkpoint loss](docs/checkpoint-loss.md): classify journal observations, never exception wording. Preserve `JournalCheckpointLoss.DetectedDuring` labels and existing reports on unrelated faults; automatic recovery retains LiveWatch reports. An unresumable block's refused start retains the watch request; a successful rescan clears refusal and starts the watch. A failed rescan must never arm an unresumable cursor. Bounded catch-up recovery, refusal after three consecutive losses, and progress checks follow the linked contract.

[Query and writer lifetime](docs/query-lifetime.md): all six row-scanning APIs borrow snapshots throughout scanning and observe cancellation before the first row and every 4096 rows; Children uses only caller cancellation. DisposeAsync cancels linked work and drains borrows before unmapping. Suspended enumerators retain borrows: dispose promptly. Preserve per-access IsReleased checks and BlockWriter scopes; BlockFile.Dispose drains existing scopes before unmapping.

[Watch lifetime](docs/watch-lifetime.md): start readiness is connection plus StartWatch written, not CaughtUp. Waits follow the per-drive instance. Healthy watches survive rescan production until replacement commit; failed/cancelled production preserves them. Respect identity-checked publication, rescan recovery, fault consumption and stop-wins rules. Gates order lifecycle/write/state; no gate acquisition under state. Channel faults never recover. Manual restart failure reports RescanRestart, which stop returns once as Failed. Each lifecycle verb takes enumerable DriveKey values and returns ordered per-drive results.

[Block ownership](docs/block-ownership.md): hold the owner lock before canonical validation, sweep, rename or deletion. Contention means cache-only InUse or private scanning. Never unlink `.lock` files; cache pruning leaves `*.mlix.lock` alone. `CacheDirectory.DeleteCached` is the lock-safe clearing API. Preserve its per-file outcomes and internal rejected-name callback contract. Only replacement publication changes cache location backing; reread Drives after rescan, and preserve backing on failed/cancelled production.

[Broker lifetime](docs/broker-lifetime.md): one process/control pipe per session, one channel per drive operation; preserve request-id reservation and connection handshake. Drive failure isolates its operation; control EOF ends all. Preserve the shared parse budget, block validation and ScanReady/catch-up classification. Heartbeat visits are five seconds; processing progress and client silence limits are 30 seconds. Heartbeats never reset processing progress; skip in-flight writes. Preserve --broker dispatch and bounded self-filtered diagnostics (`MFTLIB_BROKER_DIAG_INCLUDE_SELF=1` opts in).

### Native error messages

[Native error helper](docs/architecture.md): use `SetErrorMessage` in `MFTLibNative/internal.h` for native result `MftMessageChar errorMessage[256]` buffers (`wchar_t` on Windows, `char16_t` elsewhere). Windows truncation uses `_snwprintf_s(..., _TRUNCATE, ...)`; Debug asserts overflow. Avoid direct `swprintf_s` / `snprintf_s` calls at error-write sites.

## Naming

Full words in identifiers; `Mft` and `Usn` are NTFS names and stay.

## Roadmap

See `.plan` for details. Current release is **0.3.0** with USN journal support. Primary consumers are file-wizard and git-wizard.
