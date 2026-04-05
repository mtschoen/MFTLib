# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Fully thread-safe and re-entrant.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop.
    - **Lazy Materialization**: `MftRecord` stores native pointers; strings are only created on access.
    - **Memory Safety**: `ToArray()` and `Materialize()` ensure strings are stable in managed memory after native buffers are freed.
    - **Streaming API**: `StreamRecords` provides memory-efficient `IEnumerable<MftRecord>`.
    - **ElevationUtilities**: Shared logic for detecting and ensuring Administrative privileges.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Automatically self-elevates.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation.
- **MFTLib.Tests** (C# xUnit) - Unit tests for record mapping and path resolution.
- **ConsoleApplication1** (C++ Console) - Legacy prototype, superseded by MFTLibNative.

## Roadmap

See `.plan` for details. Next milestone is **0.3.0 — USN journal support** for incremental index updates (complement to full MFT scanning). Primary consumer is [file-wizard](C:\Users\mtsch\file-wizard).
