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

The test program requires admin elevation (raw volume access). Output is written to `output.log` next to the exe.

```bash
# Launch elevated (output goes to output.log)
powershell -command "Start-Process -FilePath 'TestProgram\bin\x64\Debug\net8.0\TestProgram.exe' -Verb RunAs -Wait -ArgumentList 'G'"

# Read the captured output
cat TestProgram\bin\x64\Debug\net8.0\output.log
```

## Architecture

- **MFTLibNative** (C++ DLL) - Core NTFS MFT parsing logic with multi-threaded parallel fixup+parse and double-buffered I/O. Exports `ParseMFTRecords`, `ParseMFTFromFile`, `GenerateSyntheticMFT`, and `PrintVolumeInfo`.
- **MFTLib** (C# Library) - Managed wrapper with P/Invoke interop, `MftVolume` public API, and NTFS structure definitions. Packaged as a NuGet package with the native DLL bundled.
- **TestProgram** (C# Console App) - CLI that reads MFT metadata for specified drives. Requires admin elevation.
- **Benchmark** (C# Console App) - Performance benchmark using synthetic MFT generation. Targets `net8.0-windows`.
- **MFTLib.Tests** (C# xUnit) - Unit tests.
- **ConsoleApplication1** (C++ Console) - Legacy prototype, superseded by MFTLibNative.
