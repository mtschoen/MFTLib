# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

**ALWAYS use MSBuild with `-p:Platform=x64`** - the native C++ DLL must be built with MSBuild, not `dotnet build`.

```bash
# Build entire solution
MSBuild.exe MFTLib.sln -p:Configuration=Debug -p:Platform=x64

# Build just the test program (includes native dependency)
# NOTE: Always build the solution, not individual projects. The native DLL post-build
# xcopy only resolves $(SolutionDir) correctly when building the .sln.
MSBuild.exe TestProgram\TestProgram.csproj -p:Configuration=Debug -p:Platform=x64
```

Do NOT use `dotnet build` - it cannot build the native C++ dependency (MFTLibNative).

### Running the test program

The test program requires admin elevation (raw volume access). Output is written to `output.log` next to the exe.

```bash
# Launch elevated (output goes to output.log)
powershell -command "Start-Process -FilePath 'TestProgram\bin\x64\Debug\net8.0\TestProgram.exe' -Verb RunAs -Wait -ArgumentList 'G'"

# Read the captured output
cat TestProgram\bin\x64\Debug\net8.0\output.log
```

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic. Reads boot sector, file records, and attributes via raw volume access. Exports `ParseMFT` and `PrintVolumeInfo`.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop, volume utilities, and NTFS structure definitions.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Requires admin elevation.
- **ConsoleApplication1** (C++ Console) - Legacy prototype, superseded by MFTLibNative.
