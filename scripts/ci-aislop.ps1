<#
.SYNOPSIS
Runs the prepared pinned aislop checkout for local preflight and CI.
.PARAMETER ReportPath
JSON destination; defaults to .aislop/preflight/report.json in the repository.
.PARAMETER EvidenceLogPath
Optional file that also receives the provenance and score lines, for the CI evidence artifact.
#>
[CmdletBinding()]
param([string] $ReportPath = '', [string] $EvidenceLogPath = '')

$ErrorActionPreference = 'Stop'

function Get-AislopUtcNow { [DateTime]::UtcNow }

function Wait-AislopProcess {
    param([object] $Process)
    $null = $Process.Handle
    $lastBeat = Get-AislopUtcNow
    while (-not $Process.HasExited) {
        Start-Sleep -Seconds 5
        $now = Get-AislopUtcNow
        if (($now - $lastBeat).TotalSeconds -ge 60) {
            Write-Host "aislop scan in progress ($($now.ToString('HH:mm:ss')))"
            $lastBeat = $now
        }
    }
    $Process.WaitForExit()
    $exitCode = $Process.ExitCode
    if ($null -eq $exitCode) { throw 'aislop process completed without an exit code.' }
    return [int] $exitCode
}

function Invoke-AislopScan {
    param([string] $Cli, [string] $RepositoryRoot, [string] $ReportPath)
    $nodeExecutable = @(Get-Command node -CommandType Application)[0].Source
    $process = Start-Process -FilePath $nodeExecutable `
        -ArgumentList "`"$Cli`" ci . --json" -WorkingDirectory $RepositoryRoot `
        -NoNewWindow -PassThru -RedirectStandardOutput $ReportPath
    try { return Wait-AislopProcess $process }
    finally { $process.Dispose() }
}

function Write-AislopDiagnostics {
    param($Result)
    $diagnostics = if ($Result.diagnostics) { @($Result.diagnostics) } else { @() }
    $scoring = @($diagnostics | Where-Object { $_.scoreImpact.multiplier -gt 0 })
    $reportOnly = $diagnostics.Count - $scoring.Count
    if ($scoring.Count -eq 0) {
        Write-Host "aislop: no score-affecting findings were reported ($reportOnly report-only findings not shown)"
        return
    }
    Write-Host "::group::aislop score-affecting findings ($($scoring.Count); $reportOnly report-only findings not shown)"
    foreach ($diagnostic in $scoring) {
        Write-Host ('[{0}/{1}] {2}' -f $diagnostic.engine, $diagnostic.severity, $diagnostic.rule)
        Write-Host ('    {0}:{1}  {2}' -f $diagnostic.filePath, $diagnostic.line, $diagnostic.message)
    }
    Write-Host '::endgroup::'
}

function Write-AislopEvidence {
    param([string] $Line, [string] $EvidenceLogPath)
    Write-Host $Line
    if ($EvidenceLogPath) { Add-Content -LiteralPath $EvidenceLogPath -Value $Line -Encoding utf8 }
}

function Get-AislopFailBelow {
    $configPath = '.aislop/config.yml'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        foreach ($line in Get-Content -LiteralPath $configPath) {
            if ($line -match '^\s*failBelow:\s*(\d+)') { return [int] $Matches[1] }
        }
    }
    return 100
}

function Get-AislopEngineProperty {
    param($Result, [string] $Property)
    if (-not $Result.engines) { return }
    foreach ($engine in $Result.engines.PSObject.Properties) {
        if ($engine.Value.$Property) { [pscustomobject]@{ Name = $engine.Name; Value = $engine.Value.$Property } }
    }
}

function Format-AislopEngineFailure {
    param([string] $EngineName, $Failure)
    $tool = if ($Failure.tool) { $Failure.tool } else { 'unknown' }
    $target = if ($Failure.target) { $Failure.target } else { 'unknown' }
    $reason = if ($Failure.reason) { $Failure.reason } else { 'no reason' }
    return "$EngineName $tool ${target}: $reason"
}

function Get-AislopExitReason {
    param($Result)
    $missingTools = @(@($Result.missingTools) + @(Get-AislopEngineProperty $Result 'missingTools' | ForEach-Object { $_.Value }) |
        Where-Object { $_ } | Select-Object -Unique)
    $failures = @(Get-AislopEngineProperty $Result 'failures' | ForEach-Object {
        $name = $_.Name
        foreach ($failure in @($_.Value)) { Format-AislopEngineFailure $name $failure }
    })
    $failedEngines = @(Get-AislopEngineProperty $Result 'failed' | ForEach-Object { $_.Name })
    $parts = @()
    if ($missingTools.Count -gt 0) { $parts += "missing tools: $($missingTools -join ', ')" }
    if ($failures.Count -gt 0) { $parts += "engine failures: $($failures -join '; ')" }
    if ($failedEngines.Count -gt 0) { $parts += "failed engines: $($failedEngines -join ', ')" }
    return $parts
}

function Write-AislopExitReason {
    param($Result, [string] $EvidenceLogPath)
    $parts = @(Get-AislopExitReason $Result)
    foreach ($part in $parts) { Write-Host "aislop $part" }
    $failBelow = Get-AislopFailBelow
    if ($Result.score -lt $failBelow) { return }
    $detail = if ($parts.Count -gt 0) { $parts -join '; ' } else { 'no missing tool or engine failure in the report, inspect it' }
    Write-AislopEvidence "aislop gate: exited nonzero at score $($Result.score) (failBelow $failBelow); $detail" $EvidenceLogPath
}

function Invoke-AislopGate {
    param([string] $RepositoryRoot, [string] $ReportPath = '', [string] $EvidenceLogPath = '')
    $PSNativeCommandUseErrorActionPreference = $false
    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    Push-Location -LiteralPath $RepositoryRoot
    try {
        $expectedCommit = (Get-Content .aislop/fork-commit -Raw).Trim()
        if ($expectedCommit -cnotmatch '^[0-9a-f]{40}$') {
            throw '.aislop/fork-commit must be one lowercase 40-character commit.'
        }
        $checkout = if ($env:AISLOP_HOME) { $env:AISLOP_HOME } else { Join-Path $HOME 'aislop' }
        $actualCommit = & git -C $checkout rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw "Cannot identify aislop checkout commit at $checkout; expected $expectedCommit." }
        if ($actualCommit -cne $expectedCommit) {
            throw "Refusing a different scanner: checkout $actualCommit, CI requires $expectedCommit. Build the pinned fork and set AISLOP_HOME to its checkout."
        }
        $changes = & git -C $checkout status --porcelain --untracked-files=no
        if ($LASTEXITCODE -ne 0 -or $changes) {
            throw 'Pinned aislop checkout has modified tracked files or cannot be inspected.'
        }
        $cli = Join-Path (Resolve-Path -LiteralPath $checkout).Path 'dist/cli.js'
        if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Pinned aislop is not built: $cli" }
        $nodeExecutable = @(Get-Command node -CommandType Application)[0].Source
        $checkoutVersion = & $nodeExecutable $cli --version
        if ($LASTEXITCODE -ne 0) { throw 'Pinned aislop checkout could not report its version.' }
        Write-AislopEvidence "aislop checkout version $checkoutVersion, checkout commit $actualCommit; CI pin $expectedCommit" $EvidenceLogPath
        if (-not $ReportPath) { $ReportPath = '.aislop/preflight/report.json' }
        if (-not [IO.Path]::IsPathRooted($ReportPath)) {
            $ReportPath = Join-Path $RepositoryRoot $ReportPath
        }
        $ReportPath = [IO.Path]::GetFullPath($ReportPath)
        New-Item -ItemType Directory -Force -Path (Split-Path $ReportPath -Parent) | Out-Null
        $gateExit = Invoke-AislopScan -Cli $cli -RepositoryRoot $RepositoryRoot -ReportPath $ReportPath
        $rawReport = Get-Content -LiteralPath $ReportPath -Raw
        if ([string]::IsNullOrWhiteSpace($rawReport)) {
            throw "aislop report is empty at $ReportPath (scanner exit $gateExit)."
        }
        try { $result = $rawReport | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "aislop report is invalid JSON at $ReportPath (scanner exit $gateExit): $($_.Exception.Message)" }
        if ($null -eq $result) { throw "aislop report is null JSON at $ReportPath (scanner exit $gateExit)." }
        $files = if ($result.summary.files) { $result.summary.files } else { 0 }
        Write-AislopEvidence "aislop score $($result.score) over $files files" $EvidenceLogPath
        if ($files -lt 10) { throw "aislop only scanned $files files; the C# engine did not run." }
        if ($gateExit -ne 0) {
            Write-AislopDiagnostics $result
            Write-AislopExitReason $result $EvidenceLogPath
            Write-Host "aislop scanner failed with exit code $gateExit"
        }
        return [int] $gateExit
    } finally { Pop-Location }
}

if ($MyInvocation.InvocationName -eq '.') { return }
exit (Invoke-AislopGate -RepositoryRoot (Split-Path -Parent $PSScriptRoot) -ReportPath $ReportPath -EvidenceLogPath $EvidenceLogPath)
