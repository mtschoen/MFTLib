# AGENTS.md

This file is the cross-tool source of truth for coding agents working in this repository. Tool-specific instruction files (`CLAUDE.md`, etc.) import it via `@AGENTS.md`.

## Build Commands

**Use MSBuild with `-p:Platform=x64` for the native C++ DLL**, and `dotnet build` for managed projects:

```bash
# Build native C++ DLL
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64

# Build managed projects (or test program)
dotnet build -c Release -p:Platform=x64
dotnet build TestProgram\TestProgram.csproj -c Release -p:Platform=x64
```

`dotnet build` cannot build the native C++ project (`MFTLibNative.vcxproj`), which must be compiled with MSBuild.

### NuGet packaging

```bash
# Build Release and pack the NuGet package
MSBuild.exe MFTLibNative\MFTLibNative.vcxproj -p:Configuration=Release -p:Platform=x64
dotnet pack MFTLib\MFTLib.csproj -c Release -p:Platform=x64

# Publish to nuget.org
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Running the test program

The test program requires admin elevation (raw volume access). It now includes **self-elevation logic** via `ElevationUtilities`.

For the most reliable experience (proper UAC prompt handling), **run the compiled .exe directly**:

```bash
# Launch directly (will trigger UAC prompt if not already elevated)
.\TestProgram\bin\x64\Release\net10.0\TestProgram.exe C:

# Results are written to output.log in the same directory
cat .\TestProgram\bin\x64\Release\net10.0\output.log
```

If running via `dotnet TestProgram.dll`, the helper will still attempt to relaunch the process with `runas`, but running the `.exe` is preferred.

### Test coverage

**Managed (C#):** Run `scripts/run-coverage.ps1` - builds, runs all tests (including admin with UAC prompt), and reports coverage:
```powershell
.\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
.\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
```

**Native (C++):** Microsoft.CodeCoverage.Console via `scripts/native-coverage.ps1`:
```powershell
.\scripts\native-coverage.ps1           # cobertura XML output
.\scripts\native-coverage.ps1 -HtmlReport  # also generate HTML
```

The native DLL must be built Debug|x64 (linked with `/PROFILE`) for instrumentation. The script handles build, instrument, test, and report automatically. Settings in `native-coverage.runsettings`.

The USN journal tests need admin. `scripts/native-coverage-elevated.ps1` self-elevates, runs `native-coverage.ps1` hidden, and writes results to `native-coverage-elevated.log` at the repository root. Pass `-TimeoutSeconds <int>` (default 600) to adjust the poll-loop timeout when running on slower hardware.

## Cleaning the working tree

`git clean -ffxd` must always be safe to run. It is the check that this checkout still matches a fresh clone, so it is run before starting new work, and it must never be the thing that loses something.

That safety comes from an invariant, not from a wrapper: **nothing unrecoverable lives in the working tree.** Every file here is either tracked and pushed, or reproducible by re-running a tool. There is no exclude list, because an exclude list would leave the tree unequal to a fresh clone and defeat the reason for cleaning.

Anything that fails that test belongs somewhere else. If a file is worth keeping, track it and push it; if it is only worth keeping on one machine, keep it outside the working tree. Do not add a file to this repository that is neither.

Git offers no way to protect a file from `git clean`. Aliases cannot shadow built-in commands, there is no `pre-clean` hook, and `-x` overrides `.git/info/exclude`. Only tracked files and files outside the tree are safe, which is why the invariant above is the whole mechanism.

What a clean removes and how each comes back:

| Removed | Restored by |
| --- | --- |
| `bin/`, `obj/`, `x64/`, `build/`, `.vs/`, `node_modules/`, `TestResults/` | rebuild |
| Coverage reports and logs at the repository root | re-run the coverage scripts |
| `.claude/` and `.aislop/` scan output and caches | re-run aislop or inspectcode |
| `.claude/settings.local.json` | re-granted as needed; broad grants live in the user-scope settings, and any provisioned project-scope overrides are re-applied by the tool that wrote them |
| `.claude/AISLOP.md`, `.claude/CLAUDE.md` | `aislop hook install claude --project` |

`.claude/AISLOP.md` and `.claude/CLAUDE.md` are generated boilerplate the aislop installer writes into a sentinel-fenced block, which is why they are not tracked. They do not restore themselves - nothing rewrites them until that command is run, so run it after a clean. It is also how to refresh them after an aislop upgrade.

`.aislop/` run history (`history.jsonl`) and session logs are ephemeral runtime telemetry. The quality gate enforces an absolute `failBelow: 100` threshold on the current tree rather than relative historical deltas, so past run logs are not required to build or verify the repository, and fresh scan output and caches are regenerated on the next run.

### Getting back to work after a clean

`init.ps1` (Windows) and `init.sh` (Linux) at the repository root do the two things a clean does not undo by itself - the NuGet restore and the generated agent files - and report any prerequisite they cannot install for you:

```powershell
git clean -ffxd && .\init.ps1          # restore only, a few seconds
git clean -ffxd && .\init.ps1 -Build   # also build the solution Release|x64
```

From `cmd.exe`, use `init.bat`, which forwards to the same script:

```bat
git clean -ffxd && .\init.bat
git clean -ffxd && .\init.bat -Build
```

Keep the `.\` prefix. This machine sets `NoDefaultCurrentDirectoryInExePath=1`, so `cmd.exe` does not search the working directory for executables and a bare `init.bat` fails with "not recognized as an internal or external command".

```bash
git clean -ffxd && ./init.sh           # restore only
git clean -ffxd && ./init.sh --build   # also build native (cmake/ninja) + managed
```

Both are idempotent, so they are safe to run at any time, not only after a clean.

They also make one optional call: if a settings-provisioning tool is on PATH, they ask it to re-apply the project-scope settings it owns, since a clean removes `.claude/settings.local.json`. The call names a single feature rather than running the tool's whole pipeline, and the step is skipped entirely when the tool is absent, so nothing here depends on it.

They are separate scripts rather than one cross-platform script because the work genuinely differs: Windows resolves MSBuild through `vswhere` and builds `MFTLib.sln` (the same build Visual Studio runs, and the only one that can build `MFTLibNative.vcxproj`), while Linux drives cmake/Ninja through `scripts/build-linux.sh` and restores the managed projects individually, since the dotnet CLI cannot load the `.vcxproj` at all. This matches the existing split between `run-coverage.ps1` and `coverage-linux.sh`.

Prerequisites checked but not installed - Windows: .NET SDK, Visual Studio with the MSVC C++ workload, aislop, reportgenerator (HTML coverage only). Linux: .NET SDK, cmake, ninja, g++, aislop, gcovr (native coverage only).

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` runs `windows` + `linux` jobs on every PR and on push to `main`. Both run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires both `(pull_request)` checks to pass before merge.

For Gitea-specific gotchas (act_runner host-mode quirks, VS BuildTools quirks, .NET version mismatch, PS7 + dotnet test comma-splitting, etc.), read `~/schoen-lab/packages/local_ci/docs/project-ci-setup.md` before modifying the workflow. Runner-account environment needs (pwsh on PATH, `DOTNET_INSTALL_DIR`) are fixed at the runner service level - do not add per-workflow bootstrap steps for them.

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant. MFT record geometry (1024 or 4096-byte records) is detected at runtime rather than assumed - `FSCTL_GET_NTFS_VOLUME_DATA` for a live volume, record 0's header for an exported file. Results cross the P/Invoke boundary through a versioned compact ABI (`MFT_NATIVE_ABI_VERSION`): packed `MftCompactEntry` rows plus separate UTF-16 string pools, with an allocation-failure fallback that preserves raw entries and filenames if path resolution cannot allocate.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop.
    - **ABI versioning**: `MFTLibNative.EnsureCompatibleNativeAbi()` / `MftResult`'s constructor check the native ABI version and entry stride before parsing, and throw `InvalidOperationException` immediately on a managed/native mismatch instead of decoding mismatched memory.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`; `MaterializeBatches`/`ReadRecordBatches` provide bounded-memory batch materialization over the same result.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges.
    - **VolumeBroker**: `JournalBrokerHost`/`JournalBrokerClient` run elevated MFT scans and USN journal watches through one elevated child process over a named pipe (control/journal frames) plus a page-file-backed `MemoryMappedFile` (cold-scan payload) - one UAC prompt per consumer session. `ElevatedEntryPoint`/`BrokerLauncher` dispatch and launch the `--broker` child mode; `BrokerDiagnostics` provides opt-in frame tracing.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Automatically self-elevates.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# xUnit) - Unit tests for record mapping and path resolution.
- **MFTLibTestExtensions** (C# Library) - Public, consumer-facing test harness (`ScanSessionTestHarness`) over MFTLib's internal `JournalBrokerScanSession` construction seams, so consumer test assemblies can build a session over a fake client without MFTLib friend-listing them. Ships as the separate `MFTLib.TestExtensions` NuGet package at publish time; never folded into the `MFTLib` package.

### Native error messages

Native exports write failure reasons into fixed-size `wchar_t errorMessage[256]` buffers on their result structs (`MftParseResult`, `UsnJournalInfo`, `UsnJournalResult`). Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` - a variadic template that deduces buffer size, silently truncates via `_vsnwprintf_s(_TRUNCATE)`, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

## Roadmap

See `.plan` for details. Current release is **0.3.0** with USN journal support (`QueryUsnJournal`, `ReadUsnJournal`, `WatchUsnJournal`). Primary consumer is [file-wizard](C:\Users\mtsch\file-wizard).

## Quality gate: aislop

This project uses **aislop** as a deterministic quality gate for AI-written code
(narrative comments, swallowed exceptions, `as any`, dead stubs, oversized
functions, etc.) across TS/JS, Python, Go, Rust, Ruby, PHP, Java, and C#.

`aislop` is installed globally on this machine, pinned to the **v0.12.3** tag of
the fork `mtschoen/aislop` (which adds the C# engine: roslynator + jb
inspectcode; upstream npm `aislop` is Python-only). Call the installed binary
directly - do NOT use `npx aislop`, which pulls upstream from npm with no C#
support:

- **Before declaring work complete**, run `aislop scan .` and address findings.
- **Before committing**, run `aislop scan --staged` (staged files only).
- `aislop fix` auto-clears mechanical issues (formatting, unused imports, dead
  code); `aislop fix --claude` hands the rest back with full context.
- `aislop ci .` is the gate - exits non-zero if the score drops below the
  threshold (`failBelow: 100`) in `.aislop/config.yml`. Treat a failing gate
  like a failing test.

### CI gate (Windows)

`.gitea/workflows/aislop.yml` runs the gate on every PR and on push to `main`.
It runs on **windows-latest**, not Linux like the rest of the fleet: `MFTLib.sln`
includes the native `MFTLibNative.vcxproj`, which only loads/builds under
MSBuild + MSVC, and both jb inspectcode and roslynator load the full solution.
`lint.csharp.jbProjects` in `.aislop/config.yml` scopes jb inspection to the four
C# projects so the C++ tree stays on its own clang-tidy/cppcheck gate. The
workflow installs `aislop` from the `main` branch of the
`github.com/mtschoen/aislop` fork (git+ssh URL in `package.json`) with
`npm install` - which resolves the latest commit and builds it on install - and
runs it with `npx --no-install`. It deliberately does NOT use `actions/setup-node`
(its 7zr extraction dies with exit code 2 on the host-mode act_runner). The
build step also mirrors `run-coverage.ps1`'s 64-bit-amd64-MSBuild recipe (the
checkout path is WOW64-virtualized away from 32-bit MSBuild). See the traps in
`~/schoen-lab/packages/local_ci/docs/project-ci-setup.md`. For the gate to block merges, add
`aislop / quality-gate (pull_request)` to the branch-protection required checks
on `main`.

To refresh the pinned global binary to a newer fork release:
`pnpm add -g --allow-build=aislop "github:mtschoen/aislop#v0.12.3"`
