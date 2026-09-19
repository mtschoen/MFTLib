function ConvertFrom-CoveragePercentage {
    param([string]$Text)
    if ($Text -notmatch '^\d+(?:\.\d+)?$') { throw 'Invalid coverage percentage' }
    $number = [decimal]::Parse($Text, [cultureinfo]::InvariantCulture)
    if ($number -lt 0 -or $number -gt 100) { throw 'Invalid coverage percentage range' }
    return $number
}

function Get-MainCoverageBaseline {
    param([scriptblock]$ReadApi)
    $branch = & $ReadApi '/branches/main'
    $commit = [string]$branch.commit.id
    for ($depth = 0; $depth -lt 100 -and $commit; $depth++) {
        $exhausted = $false
        for ($page = 1; $page -le 20; $page++) {
            $statuses = @(& $ReadApi "/statuses/${commit}?sort=highestindex&page=$page&limit=50")
            if ($statuses.Count -eq 0) { $exhausted = $true; break }
            foreach ($status in $statuses) {
                if ($status.context -ne 'pr-crew/coverage' -or $status.status -ne 'success') { continue }
                if ([string]$status.description -notmatch '^(\d+(?:\.\d+)?)% line coverage$') {
                    throw 'Malformed successful main coverage baseline'
                }
                return ConvertFrom-CoveragePercentage $Matches[1]
            }
        }
        if (-not $exhausted) { throw 'Coverage baseline status search limit reached' }
        $details = & $ReadApi "/git/commits/$commit"
        $parents = @($details.parents)
        $commit = if ($parents.Count -gt 0) { [string]$parents[0].sha } else { '' }
    }
    throw 'No successful main coverage baseline found within search limit'
}

function Test-CoverageMeasurement {
    param([string]$SummaryPath, [string]$CoveragePath, [decimal]$Baseline)
    $summaryText = Get-Content -LiteralPath $SummaryPath -Raw -ErrorAction Stop
    $matches = [regex]::Matches($summaryText, '(?m)^\s*Line coverage:\s*(\d+(?:\.\d+)?)%\s*$')
    if ($matches.Count -ne 1) { throw 'Invalid coverage summary' }
    $percentage = ConvertFrom-CoveragePercentage $matches[0].Groups[1].Value
    [xml]$report = Get-Content -LiteralPath $CoveragePath -Raw -ErrorAction Stop
    $classes = @($report.SelectNodes('/coverage/packages/package/classes/class'))
    if ($classes.Count -eq 0) { throw 'Coverage XML has no executable lines' }
    $totals = @{}
    foreach ($class in $classes) {
        $typeName = ([string]$class.name -split '[/+]')[0]
        $separator = $typeName.LastIndexOf('.')
        if ($separator -lt 0) { continue }
        $namespace = $typeName.Substring(0, $separator)
        if (-not $totals.ContainsKey($namespace)) { $totals[$namespace] = @{ Lines=0; Covered=0 } }
        foreach ($line in $class.SelectNodes('lines/line')) {
            $hitText = $line.GetAttribute('hits')
            if ($hitText -notmatch '^\d+$') { throw 'Invalid coverage XML hit count' }
            $hits = [long]::Parse($hitText, [cultureinfo]::InvariantCulture)
            $totals[$namespace].Lines++
            if ($hits -gt 0) { $totals[$namespace].Covered++ }
        }
    }
    # These namespaces have non-admin Windows tests; interop-only declarations do not.
    foreach ($namespace in @('MFTLib', 'MFTLib.Index', 'TestProgram', 'Benchmark')) {
        if (-not $totals.ContainsKey($namespace) -or $totals[$namespace].Lines -eq 0) {
            throw "No executable coverage lines for tested namespace $namespace"
        }
        if ($totals[$namespace].Covered -eq 0) {
            throw "No covered lines for tested namespace $namespace"
        }
    }
    if ($Baseline - $percentage -gt 10) {
        throw "Implausible coverage drop: baseline=$Baseline current=$percentage"
    }
    return $percentage
}

function Publish-CoverageMeasurement {
    param(
        [string]$SummaryPath, [string]$CoveragePath, [string]$CoverageOutcome,
        [string]$TargetUrl, [scriptblock]$ReadApi, [scriptblock]$PostStatus
    )
    $state = 'error'
    $description = 'coverage measurement rejected; inspect run and rerun'
    try {
        if ($CoverageOutcome -ne 'success') { throw "Coverage step outcome: $CoverageOutcome" }
        $baseline = Get-MainCoverageBaseline $ReadApi
        $percentage = Test-CoverageMeasurement $SummaryPath $CoveragePath $baseline
        $state = 'success'
        $description = $percentage.ToString([cultureinfo]::InvariantCulture) + '% line coverage'
        Write-Host "Validated coverage against main baseline $baseline"
    } catch { Write-Host "::error::Coverage measurement rejected: $($_.Exception.Message)" }
    $body = @{ context='pr-crew/coverage'; state=$state; description=$description; target_url=$TargetUrl }
    try { & $PostStatus $body | Out-Null }
    catch {
        Write-Host "::error::Coverage status could not be posted: $($_.Exception.Message)"
        return 1
    }
    if ($state -eq 'success') { return 0 }
    return 1
}
