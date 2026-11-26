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

.PARAMETER WorktreeOnly
Restore (and with -Build, build) in place and do nothing else: no aislop hook
install, no optional provisioning, no git configuration. Used to prepare a
review worktree. A missing prerequisite exits non-zero at once with one line.

.EXAMPLE
.\init.ps1

.EXAMPLE
git clean -ffxd && .\init.ps1 -Build
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$WorktreeOnly
)

. "$PSScriptRoot/scripts/windows-tools.ps1"

# The body is functions so scripts/init.tests.ps1 can dot-source this file and exercise each branch in one
# process. The entry point exits once, using a status separate from command output.

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Write-InitError {
    param([Parameter(Mandatory)][string]$Message)
    [Console]::Error.WriteLine($Message)
}

function Get-InstalledPesterVersion {
    $newest = Get-Module -ListAvailable -Name Pester |
        Sort-Object -Property Version -Descending | Select-Object -First 1
    if ($newest) { return $newest.Version }
    return $null
}

function Test-WorktreeOnlyPrerequisites {
    param([switch]$Build)

    if (-not (Test-CommandAvailable 'dotnet')) {
        Write-InitError 'worktree-only init: dotnet SDK is missing (https://dotnet.microsoft.com/download)'
        return $false
    }
    if ($Build) {
        try { $null = Get-WindowsMSBuild }
        catch {
            Write-InitError "worktree-only init: amd64 MSBuild with the MSVC C++ workload is missing ($($_.Exception.Message -replace '\s+', ' '))"
            return $false
        }
    }
    return $true
}

# Reports each prerequisite and returns the manifest: the missing list and whether aislop is available.
function Write-PrerequisiteReport {
    $missing = [System.Collections.Generic.List[string]]::new()

    if (Test-CommandAvailable 'dotnet') {
        $sdkVersion = (& dotnet --version 2>$null)
        Write-Host "  dotnet SDK          $sdkVersion"
    }
    else {
        $missing.Add('dotnet SDK 10 - https://dotnet.microsoft.com/download')
        Write-Host '  dotnet SDK          MISSING' -ForegroundColor Red
    }

    try {
        $msbuildPath = Get-WindowsMSBuild
        Write-Host "  MSBuild (MSVC)      $msbuildPath"
    }
    catch {
        $missing.Add("Visual Studio with the MSVC C++ workload - required to build MFTLibNative.vcxproj. $($_.Exception.Message)")
        Write-Host '  MSBuild (MSVC)      MISSING' -ForegroundColor Red
    }

    $aislopAvailable = Test-CommandAvailable 'aislop'
    if ($aislopAvailable) {
        $aislopVersion = (& aislop --version 2>$null)
        Write-Host "  aislop              $aislopVersion"
    }
    else {
        $missing.Add('aislop - see docs/quality-gates.md for the pinned install command')
        Write-Host '  aislop              MISSING' -ForegroundColor Yellow
    }

    if (Test-CommandAvailable 'reportgenerator') {
        Write-Host '  reportgenerator     present'
    }
    else {
        $missing.Add('reportgenerator (managed coverage summaries and HTML reports) - dotnet tool install --global dotnet-reportgenerator-globaltool')
        Write-Host '  reportgenerator     missing (managed coverage summaries and HTML reports)' -ForegroundColor Yellow
    }

    $pesterVersion = Get-InstalledPesterVersion
    if ($pesterVersion -and $pesterVersion -ge [version]'6.0') {
        Write-Host "  Pester              $pesterVersion"
    }
    else {
        $missing.Add('Pester 6 (in-process init tests, scripts/init.tests.ps1) - Install-Module Pester -MinimumVersion 6.0 -Scope AllUsers')
        Write-Host '  Pester              missing (version 6 or newer runs the in-process init tests)' -ForegroundColor Yellow
    }

    return [pscustomobject]@{ Missing = $missing; AislopAvailable = $aislopAvailable }
}

function Write-MissingPrerequisites {
    param([Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$Missing)

    if ($Missing.Count -gt 0) {
        Write-Host ''
        Write-Host 'Missing prerequisites (install manually):' -ForegroundColor Yellow
        foreach ($prerequisite in $Missing) {
            Write-Host "  - $prerequisite"
        }
    }
}

function Install-AgentInstructionFiles {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$CompletedSteps,
        [Parameter(Mandatory)][ref]$Status
    )

    $Status.Value = 0
    Write-Host ''
    Write-Host 'Restoring agent instruction files...' -ForegroundColor Cyan
    & aislop hook install claude --project
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'aislop hook install failed.' -ForegroundColor Red
        $Status.Value = 1
        return
    }
    $CompletedSteps.Add('.claude/AISLOP.md and .claude/CLAUDE.md restored')

    # The installer also rewrites the tracked .claude/settings.json with LF endings.
    # The content is unchanged, so `git diff` sees nothing while `git status` reports
    # the file as modified. Restore it only in that exact case - a real edit to
    # settings.json produces a content diff and is left alone.
    $settingsPath = '.claude/settings.json'
    & git -C $RepositoryRoot diff --quiet -- $settingsPath
    $hasContentChange = $LASTEXITCODE -ne 0
    $statusText = & git -C $RepositoryRoot status --porcelain -- $settingsPath
    if (-not $hasContentChange -and $statusText) {
        & git -C $RepositoryRoot restore -- $settingsPath
        Write-Host 'Normalized line endings the installer left in .claude/settings.json.'
    }
}

# Optional provisioning policy and diagnostics belong to scripts/optional-provisioning.
# It uses the already-required SDK; settings writes remain owned by the external tool.
# Only a successful helper result contributes a completed-step message.
function Invoke-OptionalProvisioning {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$CompletedSteps
    )

    $PSNativeCommandUseErrorActionPreference = $false
    $provisioningMessage = @(& dotnet run --project `
        (Join-Path $RepositoryRoot 'scripts/optional-provisioning/OptionalProvisioning.csproj') `
        -c Release -p:Platform=x64 --no-launch-profile --verbosity quiet)
    $provisioningStatus = $LASTEXITCODE
    if ($provisioningStatus -eq 0) {
        foreach ($line in $provisioningMessage) {
            if ($line) { $CompletedSteps.Add([string]$line) }
        }
    }
    elseif ($provisioningStatus -notin @(10, 20)) {
        foreach ($line in $provisioningMessage) { Write-Host $line }
        Write-Warning "Optional provisioning launcher exited with $provisioningStatus; continuing."
    }
}

function Invoke-SolutionBuild {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][ref]$Succeeded
    )

    & "$RepositoryRoot/scripts/build-windows.ps1" -Configuration Release -NoRestore
    $Succeeded.Value = $LASTEXITCODE -eq 0
}

function Write-InitSummary {
    param(
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$CompletedSteps,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$Missing,
        [switch]$Build
    )

    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Green
    foreach ($step in $CompletedSteps) {
        Write-Host "  $step"
    }

    Write-MissingPrerequisites -Missing $Missing

    if (-not $Build) {
        Write-Host ''
        Write-Host 'Next: .\init.ps1 -Build (.\init.bat -Build from cmd.exe),'
        Write-Host '      or .\scripts\run-coverage.ps1 for the full test run.'
    }
}

function Invoke-Init {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][ref]$Status,
        [switch]$Build,
        [switch]$WorktreeOnly
    )

    $ErrorActionPreference = 'Stop'
    $Status.Value = 1
    $completedSteps = [System.Collections.Generic.List[string]]::new()

    if ($WorktreeOnly -and -not (Test-WorktreeOnlyPrerequisites -Build:$Build)) { return }

    Write-Host 'Checking prerequisites...' -ForegroundColor Cyan
    $report = Write-PrerequisiteReport
    Write-Host ''

    if (-not (Test-CommandAvailable 'dotnet')) {
        Write-Host 'Cannot continue without the dotnet SDK.' -ForegroundColor Red
        Write-MissingPrerequisites -Missing $report.Missing
        return
    }

    Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
    # MFTLib.sln contains both managed projects and MFTLibNative.vcxproj.
    # MSBuild/dotnet emits warning NU1503 for MFTLibNative.vcxproj because C++ projects
    # are not supported by dotnet restore; this is expected and harmless.
    & dotnet restore (Join-Path $RepositoryRoot 'MFTLib.sln')
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Restore failed.' -ForegroundColor Red
        return
    }
    $completedSteps.Add('NuGet packages restored')

    if ($WorktreeOnly) {
        # Review worktree: no agent instruction files, no git writes and no optional provisioning.
    }
    else {
        if ($report.AislopAvailable) {
            $installStatus = 0
            Install-AgentInstructionFiles -RepositoryRoot $RepositoryRoot -CompletedSteps $completedSteps -Status ([ref]$installStatus)
            if ($installStatus -ne 0) { return }
        }
        else {
            Write-Host ''
            Write-Host 'Skipping agent instruction files: aislop is not installed.' -ForegroundColor Yellow
            Write-Host 'Once installed, run: aislop hook install claude --project'
        }
        Invoke-OptionalProvisioning -RepositoryRoot $RepositoryRoot -CompletedSteps $completedSteps
    }

    if ($Build) {
        $buildSucceeded = $false
        Invoke-SolutionBuild -RepositoryRoot $RepositoryRoot -Succeeded ([ref]$buildSucceeded)
        if (-not $buildSucceeded) { return }
        $completedSteps.Add('Solution built (Release|x64)')
    }

    Write-InitSummary -CompletedSteps $completedSteps -Missing $report.Missing -Build:$Build

    # Optional helper exit codes do not determine successful initialization's exit code.
    $Status.Value = 0
}

# Runs only when the script is executed; dot-sourcing (the in-process tests) loads the functions and stops.
if ($MyInvocation.InvocationName -ne '.') {
    $initStatus = 1
    Invoke-Init -RepositoryRoot $PSScriptRoot -Status ([ref]$initStatus) -Build:$Build -WorktreeOnly:$WorktreeOnly
    exit $initStatus
}
