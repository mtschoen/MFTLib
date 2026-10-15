# Run native coverage with elevation (for USN journal tests that need admin).
# Self-elevates if needed. The elevated process runs in a visible console window that
# closes when it exits, and streams its output
# to a log file as it goes; the visible parent prints new log lines and a heartbeat
# while waiting, so a healthy long run is distinguishable from a hung one.
#
# Usage:
#   .\scripts\native-coverage-elevated.ps1
#   .\scripts\native-coverage-elevated.ps1 -TimeoutSeconds 3600
#   .\scripts\native-coverage-elevated.ps1 -HeartbeatSeconds 15
#
# Note: -TimeoutSeconds is a warning threshold when the child process is alive;
# the parent logs a warning but continues waiting while the child process runs.
# Heartbeats are emitted at a 2-second polling granularity (due to the parent's
# 2-second polling interval), so values like -HeartbeatSeconds 1 evaluate on the
# 2-second polling loop rather than producing 1-second updates.

param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TimeoutSeconds = 1800,
    # Polling runs on a 2-second interval, so heartbeats are emitted at 2-second granularity.
    [ValidateRange(1, [int]::MaxValue)]
    [int]$HeartbeatSeconds = 30,
    [string]$TargetScript,
    [switch]$NoElevation,
    [switch]$ForceNonAdmin,
    [switch]$FunctionsOnly
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/coverage-child.ps1"

function Invoke-NativeCoverage {
    param([string]$RepositoryRoot, [string]$TargetScript,
        [switch]$NoElevation, [switch]$ForceNonAdmin,
        [int]$TimeoutSeconds = 1800, [int]$HeartbeatSeconds = 30)
    $target = if ($TargetScript) { $TargetScript } else {
        Join-Path $RepositoryRoot 'scripts/native-coverage.ps1'
    }
    $isAdmin = $false
    if (-not $ForceNonAdmin) {
        try {
            $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)
        } catch { $isAdmin = $false }
    }
    if ($isAdmin) {
        $global:LASTEXITCODE = 0
        & $target | ForEach-Object { Write-Host $_ }
        return [int]$LASTEXITCODE
    }
    $request = @{
        Target = [IO.Path]::GetFullPath($target); Arguments = @()
        WorkingDirectory = $RepositoryRoot
        LogPath = (Join-Path $RepositoryRoot 'native-coverage-elevated.log')
        DonePath = (Join-Path $RepositoryRoot 'native-coverage-elevated.done')
        Environment = @{}
    }
    return Invoke-CoverageChild $request -NoElevation:$NoElevation -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds $HeartbeatSeconds
}

if ($FunctionsOnly -or $MyInvocation.InvocationName -eq '.') { return }
$repositoryRoot = [string](Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..'))
exit (Invoke-NativeCoverage -RepositoryRoot $repositoryRoot -TargetScript $TargetScript -NoElevation:$NoElevation -ForceNonAdmin:$ForceNonAdmin -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds $HeartbeatSeconds)
