<#
.SYNOPSIS
Writes the aislop quality-gate evidence directory that CI uploads as an artifact.
.DESCRIPTION
pr-crew reviews only uploaded, head-bound artifacts, so the cache-hit acceptance (cache restored,
link repair, cold build skipped, checkout and pin provenance, report count and result) must be
readable from a small summary rather than from the job log.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $ReportPath = '',
    [string] $ProvenanceLogPath = '',
    [string] $LinkRepairLogPath = '',
    [string] $CacheHit = '',
    [string] $LinkRepairOutcome = '',
    [string] $ColdBuildOutcome = '',
    [string] $GateOutcome = ''
)

$ErrorActionPreference = 'Stop'

function Read-AislopEvidenceLines {
    param([string] $Path, [string] $Fallback)
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $lines = @(Get-Content -LiteralPath $Path | Where-Object { $_ })
        if ($lines.Count -gt 0) { return $lines }
    }
    return @($Fallback)
}

function Get-AislopEvidenceValue {
    param([string] $Value)
    if ($Value) { return $Value }
    return 'unknown'
}

function New-AislopEvidenceSummary {
    param(
        [string] $ReportPath, [string] $ProvenanceLogPath, [string] $LinkRepairLogPath,
        [string] $CacheHit, [string] $LinkRepairOutcome, [string] $ColdBuildOutcome, [string] $GateOutcome
    )
    $score = 'unavailable'
    $files = 'unavailable'
    if ($ReportPath -and (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        try {
            $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $report.score) { $score = [string] $report.score }
            if ($null -ne $report.summary.files) { $files = [string] $report.summary.files }
        } catch { $score = 'unreadable report' }
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("cache-hit: $(Get-AislopEvidenceValue $CacheHit)")
    $lines.Add("link-repair-step: $(Get-AislopEvidenceValue $LinkRepairOutcome)")
    foreach ($line in (Read-AislopEvidenceLines $LinkRepairLogPath 'not run')) { $lines.Add("link-repair: $line") }
    $lines.Add("cold-build-step: $(Get-AislopEvidenceValue $ColdBuildOutcome)")
    foreach ($line in (Read-AislopEvidenceLines $ProvenanceLogPath 'not reached')) { $lines.Add("provenance: $line") }
    $lines.Add("gate-step: $(Get-AislopEvidenceValue $GateOutcome)")
    $lines.Add("score: $score")
    $lines.Add("files: $files")
    return ($lines -join [Environment]::NewLine) + [Environment]::NewLine
}

function Write-AislopEvidenceDirectory {
    param(
        [string] $OutputDirectory, [string] $ReportPath, [string] $ProvenanceLogPath, [string] $LinkRepairLogPath,
        [string] $CacheHit, [string] $LinkRepairOutcome, [string] $ColdBuildOutcome, [string] $GateOutcome
    )
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $summary = New-AislopEvidenceSummary -ReportPath $ReportPath -ProvenanceLogPath $ProvenanceLogPath `
        -LinkRepairLogPath $LinkRepairLogPath -CacheHit $CacheHit -LinkRepairOutcome $LinkRepairOutcome `
        -ColdBuildOutcome $ColdBuildOutcome -GateOutcome $GateOutcome
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.txt') -Value $summary -Encoding utf8 -NoNewline
    if ($ReportPath -and (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        Copy-Item -LiteralPath $ReportPath -Destination (Join-Path $OutputDirectory 'aislop-report.json') -Force
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Write-AislopEvidenceDirectory -OutputDirectory $OutputDirectory -ReportPath $ReportPath -ProvenanceLogPath $ProvenanceLogPath `
    -LinkRepairLogPath $LinkRepairLogPath -CacheHit $CacheHit -LinkRepairOutcome $LinkRepairOutcome `
    -ColdBuildOutcome $ColdBuildOutcome -GateOutcome $GateOutcome
