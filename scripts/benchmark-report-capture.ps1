<#
.SYNOPSIS
Function library that captures the release benchmark report and replaces the committed one.
.DESCRIPTION
Runs the three recipe commands of the release benchmark checklist (docs/handoff-release-0.3.0.md) in order, combines
their report sections into one candidate, compares the candidate with the committed reference for the eight recipe
scenarios, then writes Benchmark/baselines/<yyyy-MM-dd>-<short sha>.txt and removes the previous report so exactly one
remains. The benchmark process, the clock and the file system roots are parameters so the tests run offline. A
failed benchmark command throws before anything under the baseline directory changes. A failed comparison is
reported in the result and never blocks the replacement. Dot-source this file; it runs nothing by itself.
#>

$script:BenchmarkRecipeScenarios = @(
    'exact-search', 'substring-search', 'enumeration-walk-sequential', 'enumeration-walk-parallel',
    'mft-enumeration-scan', 'mft-enumeration-rows', 'mft-enumeration-exact-name', 'mft-enumeration-substring-name')

function Invoke-BenchmarkProcess {
    param([Parameter(Mandatory)][string] $BenchmarkPath, [Parameter(Mandatory)][string[]] $Arguments)
    $output = & $BenchmarkPath @Arguments 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Get-BenchmarkRecipeCommands {
    param([Parameter(Mandatory)][string] $ScratchDirectory)
    @(
        [pscustomobject]@{ Name = 'index'; Report = (Join-Path $ScratchDirectory 'index.txt')
            Arguments = @('index', '--synthetic', '1000000', '--iterations', '5', '--out') },
        [pscustomobject]@{ Name = 'enumeration-walk'; Report = (Join-Path $ScratchDirectory 'walk.txt')
            Arguments = @('enumeration-walk', '--directories', '32768', '--files-per-directory', '1', '--iterations', '5', '--out') },
        [pscustomobject]@{ Name = 'mft-enumeration'; Report = (Join-Path $ScratchDirectory 'mft.txt')
            Arguments = @('mft-enumeration', '--synthetic', '1000000', '--iterations', '5', '--out') }
    )
}

function Invoke-BenchmarkReportCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $BenchmarkPath,
        [Parameter(Mandatory)][string] $BaselineDirectory,
        [Parameter(Mandatory)][string] $ScratchDirectory,
        [Parameter(Mandatory)][string] $ShortSha,
        [scriptblock] $RunBenchmark,
        [scriptblock] $GetNow = { [DateTime]::UtcNow }
    )
    if (-not $RunBenchmark) {
        $RunBenchmark = { param($Arguments) Invoke-BenchmarkProcess -BenchmarkPath $BenchmarkPath -Arguments $Arguments }.GetNewClosure()
    }
    $references = @(Get-ChildItem -LiteralPath $BaselineDirectory -Filter '*.txt' -File)
    if ($references.Count -ne 1) { throw 'Expected exactly one committed release report to compare against.' }
    $reference = $references[0]
    New-Item -ItemType Directory -Force -Path $ScratchDirectory | Out-Null

    $sections = @()
    foreach ($command in (Get-BenchmarkRecipeCommands -ScratchDirectory $ScratchDirectory)) {
        $run = & $RunBenchmark (@($command.Arguments) + @($command.Report))
        if ($run.ExitCode -ne 0) { throw "The $($command.Name) benchmark failed with exit code $($run.ExitCode)." }
        if (-not (Test-Path -LiteralPath $command.Report -PathType Leaf)) { throw "The $($command.Name) benchmark wrote no report." }
        $sections += (Get-Content -LiteralPath $command.Report -Raw).TrimEnd()
    }
    $candidate = Join-Path $ScratchDirectory 'release.txt'
    [IO.File]::WriteAllText($candidate, ($sections -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))

    $referenceText = Get-Content -LiteralPath $reference.FullName -Raw
    $comparison = @()
    foreach ($scenario in $script:BenchmarkRecipeScenarios) {
        if (-not $referenceText.Contains("--- $scenario ---")) {
            $comparison += [pscustomobject]@{ Scenario = $scenario; Status = 'no-reference'; Output = '' }
            continue
        }
        $run = & $RunBenchmark @('compare', $reference.FullName, $candidate, $scenario)
        $status = if ($run.ExitCode -eq 0) { 'pass' } else { 'failed' }
        $comparison += [pscustomobject]@{ Scenario = $scenario; Status = $status; Output = $run.Output }
    }

    $date = (& $GetNow).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $reportName = "$date-$ShortSha.txt"
    $reportPath = Join-Path $BaselineDirectory $reportName
    Copy-Item -LiteralPath $candidate -Destination $reportPath -Force
    if ($reference.Name -cne $reportName) { Remove-Item -LiteralPath $reference.FullName -Force }
    [pscustomobject]@{ ReportName = $reportName; ReportPath = $reportPath; ReferenceName = $reference.Name; Comparison = $comparison }
}

function Format-BenchmarkComparisonSummary {
    param([Parameter(Mandatory)] $Capture)
    $failed = @($Capture.Comparison | Where-Object { $_.Status -eq 'failed' } | ForEach-Object { $_.Scenario })
    $passed = @($Capture.Comparison | Where-Object { $_.Status -eq 'pass' }).Count
    $missing = @($Capture.Comparison | Where-Object { $_.Status -eq 'no-reference' } | ForEach-Object { $_.Scenario })
    $text = "$passed passed"
    if ($failed.Count -gt 0) { $text += "; exceeded the limit: $($failed -join ', ')" }
    if ($missing.Count -gt 0) { $text += "; no reference: $($missing -join ', ')" }
    $text
}
