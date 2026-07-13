# AGENTS.md

This file is the cross-tool source of truth for coding agents working in this repository. Tool-specific instruction files (`CLAUDE.md`, etc.) import it via `@AGENTS.md`.

## Build Commands

**ALWAYS use MSBuild with `-p:Platform=x64`** - the native C++ DLL must be built with MSBuild, not `dotnet build`.

```bash
# Build entire solution
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64

# Build just the test program (includes native dependency)
# NOTE: Always build the solution, not individual projects. The native DLL post-build
# xcopy only resolves $(SolutionDir) correctly when building the .sln.
MSBuild.exe TestProgram\TestProgram.csproj -p:Configuration=Release -p:Platform=x64
```

Do NOT use `dotnet build` - it cannot build the native C++ dependency (MFTLibNative).

### NuGet packaging

```bash
# Build Release and pack the NuGet package
MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64
MSBuild.exe MFTLib\MFTLib.csproj -t:Pack -p:Configuration=Release -p:Platform=x64

# Publish to nuget.org
dotnet nuget push "MFTLib\bin\x64\Release\MFTLib.*.nupkg" --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

Use `MSBuild -t:Pack` (not `dotnet pack`) since `dotnet pack` can't handle the vcxproj reference.

### Running the test program

The test program requires admin elevation (raw volume access). It now includes **self-elevation logic** via `ElevationUtilities`.

For the most reliable experience (proper UAC prompt handling), **run the compiled .exe directly**:

```bash
# Launch directly (will trigger UAC prompt if not already elevated)
.\TestProgram\bin\x64\Release\net8.0\TestProgram.exe C:

# Results are written to output.log in the same directory
cat .\TestProgram\bin\x64\Release\net8.0\output.log
```

If running via `dotnet TestProgram.dll`, the helper will still attempt to relaunch the process with `runas`, but running the `.exe` is preferred.

### Test coverage

**Managed (C#):** Run `scripts/run-coverage.ps1` — builds, runs all tests (including admin with UAC prompt), and reports coverage:
```powershell
.\scripts\run-coverage.ps1                  # full run with admin tests (UAC prompt)
.\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)
```

**Native (C++):** Microsoft.CodeCoverage.Console via `.claude/scripts/native-coverage.ps1`:
```powershell
.\scripts\native-coverage.ps1           # cobertura XML output
.\scripts\native-coverage.ps1 -HtmlReport  # also generate HTML
```

The native DLL must be built Debug|x64 (linked with `/PROFILE`) for instrumentation. The script handles build, instrument, test, and report automatically. Settings in `native-coverage.runsettings`.

## CI

Gitea Actions workflow at `.gitea/workflows/test.yml` runs `windows` + `linux` jobs on every PR and on push to `main`. Both run their respective coverage scripts (`scripts/run-coverage.ps1 -NonInteractive` and `scripts/coverage-linux.sh`). Branch protection on `main` requires both `(pull_request)` checks to pass before merge.

For Gitea-specific gotchas (act_runner host-mode quirks, VS BuildTools quirks, .NET version mismatch, PS7 + dotnet test comma-splitting, etc.), read `~/local-ci/docs/project-ci-setup.md` before modifying the workflow.

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges.
    - **VolumeBroker**: `JournalBrokerHost`/`JournalBrokerClient` run elevated MFT scans and USN journal watches through one elevated child process over a named pipe (control/journal frames) plus a page-file-backed `MemoryMappedFile` (cold-scan payload) - one UAC prompt per consumer session. `ElevatedEntryPoint`/`BrokerLauncher` dispatch and launch the `--broker` child mode; `BrokerDiagnostics` provides opt-in frame tracing.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Automatically self-elevates.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# xUnit) - Unit tests for record mapping and path resolution.

### Native error messages

Native exports write failure reasons into fixed-size `wchar_t errorMessage[256]` buffers on their result structs (`MftParseResult`, `UsnJournalInfo`, `UsnJournalResult`). Use the `SetErrorMessage` helper in `MFTLibNative/internal.h` — a variadic template that deduces buffer size, silently truncates via `_vsnwprintf_s(_TRUNCATE)`, and asserts in debug builds if a message doesn't fit. Avoid calling `swprintf_s` / `snprintf_s` directly at error-write sites; the helper keeps `cert-err33-c` silent and centralizes the truncation semantic.

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
workflow installs `aislop` as a git dependency pinned to a specific commit of
the `github.com/mtschoen/aislop` fork (git+ssh URL in `package.json`, resolution
locked in `package-lock.json`) with `npm ci` - which builds it on install - and
runs it with `npx --no-install`. The former `@schoen/aislop` Gitea-registry
tarball route is retired. To bump aislop, change the pinned commit in
`package.json` and refresh the lockfile (`npm install --package-lock-only`). It
deliberately does NOT use `actions/setup-node` (its 7zr extraction dies with
exit code 2 on the host-mode act_runner). The build step also
mirrors `run-coverage.ps1`'s 64-bit-amd64-MSBuild recipe (the checkout path is
WOW64-virtualized away from 32-bit MSBuild). See the traps in
`~/local-ci/docs/project-ci-setup.md`. For the gate to block merges, add
`aislop / quality-gate (pull_request)` to the branch-protection required checks
on `main`.

To refresh the pinned global binary to a newer fork release:
`pnpm add -g --allow-build=aislop "github:mtschoen/aislop#v0.12.3"`
