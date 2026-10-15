<#
.SYNOPSIS
Restores this checkout to a working state after `git clean -ffxd`.

.DESCRIPTION
A deep clean removes everything not tracked, which is safe by design - but two
things do not come back on their own: the NuGet package restore, and the agent
instruction files that the aislop installer generates. This script does both,
and reports any missing prerequisite it cannot install for you.

Intended use:

    git clean -ffxd && .\init.ps1

Safe to run at any time; every step is idempotent.

.PARAMETER Build
Also build the solution after restoring. Off by default to keep the script fast.

.EXAMPLE
.\init.ps1

.EXAMPLE
git clean -ffxd && .\init.ps1 -Build
#>
[CmdletBinding()]
param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/scripts/windows-tools.ps1"

$repositoryRoot = $PSScriptRoot
$missingPrerequisites = [System.Collections.Generic.List[string]]::new()
$completedSteps = [System.Collections.Generic.List[string]]::new()

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

Write-Host 'Checking prerequisites...' -ForegroundColor Cyan

if (Test-CommandAvailable 'dotnet') {
    $sdkVersion = (& dotnet --version 2>$null)
    Write-Host "  dotnet SDK          $sdkVersion"
}
else {
    $missingPrerequisites.Add('dotnet SDK 10 - https://dotnet.microsoft.com/download')
    Write-Host '  dotnet SDK          MISSING' -ForegroundColor Red
}

try {
    $msbuildPath = Get-WindowsMSBuild
    Write-Host "  MSBuild (MSVC)      $msbuildPath"
}
catch {
    $missingPrerequisites.Add("Visual Studio with the MSVC C++ workload - required to build MFTLibNative.vcxproj. $($_.Exception.Message)")
    Write-Host '  MSBuild (MSVC)      MISSING' -ForegroundColor Red
}

$aislopAvailable = Test-CommandAvailable 'aislop'
if ($aislopAvailable) {
    $aislopVersion = (& aislop --version 2>$null)
    Write-Host "  aislop              $aislopVersion"
}
else {
    $missingPrerequisites.Add('aislop - see docs/quality-gates.md for the pinned install command')
    Write-Host '  aislop              MISSING' -ForegroundColor Yellow
}

if (Test-CommandAvailable 'reportgenerator') {
    Write-Host '  reportgenerator     present'
}
else {
    $missingPrerequisites.Add('reportgenerator (managed coverage summaries and HTML reports) - dotnet tool install --global dotnet-reportgenerator-globaltool')
    Write-Host '  reportgenerator     missing (managed coverage summaries and HTML reports)' -ForegroundColor Yellow
}

Write-Host ''

if (-not (Test-CommandAvailable 'dotnet')) {
    Write-Host 'Cannot continue without the dotnet SDK.' -ForegroundColor Red
    if ($missingPrerequisites.Count -gt 0) {
        Write-Host ''
        Write-Host 'Missing prerequisites (install manually):' -ForegroundColor Yellow
        foreach ($prerequisite in $missingPrerequisites) {
            Write-Host "  - $prerequisite"
        }
    }
    exit 1
}

Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
# MFTLib.sln contains both managed projects and MFTLibNative.vcxproj.
# MSBuild/dotnet emits warning NU1503 for MFTLibNative.vcxproj because C++ projects
# are not supported by dotnet restore; this is expected and harmless.
& dotnet restore (Join-Path $repositoryRoot 'MFTLib.sln')
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Restore failed.' -ForegroundColor Red
    exit 1
}
$completedSteps.Add('NuGet packages restored')

if ($aislopAvailable) {
    Write-Host ''
    Write-Host 'Restoring agent instruction files...' -ForegroundColor Cyan
    & aislop hook install claude --project
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'aislop hook install failed.' -ForegroundColor Red
        exit 1
    }
    $completedSteps.Add('.claude/AISLOP.md and .claude/CLAUDE.md restored')

    # The installer also rewrites the tracked .claude/settings.json with LF endings.
    # The content is unchanged, so `git diff` sees nothing while `git status` reports
    # the file as modified. Restore it only in that exact case - a real edit to
    # settings.json produces a content diff and is left alone.
    $settingsPath = '.claude/settings.json'
    & git -C $repositoryRoot diff --quiet -- $settingsPath
    $hasContentChange = $LASTEXITCODE -ne 0
    $status = & git -C $repositoryRoot status --porcelain -- $settingsPath
    if (-not $hasContentChange -and $status) {
        & git -C $repositoryRoot restore -- $settingsPath
        Write-Host 'Normalized line endings the installer left in .claude/settings.json.'
    }
}
else {
    Write-Host ''
    Write-Host 'Skipping agent instruction files: aislop is not installed.' -ForegroundColor Yellow
    Write-Host 'Once installed, run: aislop hook install claude --project'
}

# Optional provisioning policy and diagnostics belong to scripts/optional-provisioning.
# It uses the already-required SDK; settings writes remain owned by the external tool.
# Only a successful helper result contributes a completed-step message.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $provisioningMessage = @(& dotnet run --project `
        (Join-Path $repositoryRoot 'scripts/optional-provisioning/OptionalProvisioning.csproj') `
        -c Release -p:Platform=x64 --no-launch-profile --verbosity quiet)
    $provisioningStatus = $LASTEXITCODE
    if ($provisioningStatus -eq 0) {
        foreach ($line in $provisioningMessage) {
            if ($line) { $completedSteps.Add([string]$line) }
        }
    }
    elseif ($provisioningStatus -notin @(10, 20)) {
        foreach ($line in $provisioningMessage) { Write-Host $line }
        Write-Warning "Optional provisioning launcher exited with $provisioningStatus; continuing."
    }
}

if ($Build) {
    & "$repositoryRoot/scripts/build-windows.ps1" -Configuration Release -NoRestore
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $completedSteps.Add('Solution built (Release|x64)')
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
foreach ($step in $completedSteps) {
    Write-Host "  $step"
}

if ($missingPrerequisites.Count -gt 0) {
    Write-Host ''
    Write-Host 'Missing prerequisites (install manually):' -ForegroundColor Yellow
    foreach ($prerequisite in $missingPrerequisites) {
        Write-Host "  - $prerequisite"
    }
}

if (-not $Build) {
    Write-Host ''
    Write-Host 'Next: .\init.ps1 -Build (.\init.bat -Build from cmd.exe),'
    Write-Host '      or .\scripts\run-coverage.ps1 for the full test run.'
}

# Optional helper exit codes do not determine successful initialization's exit code.
exit 0
