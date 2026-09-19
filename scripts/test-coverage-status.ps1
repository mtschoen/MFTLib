$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/coverage-status.ps1"
$directory = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$summary = Join-Path $directory 'Summary.txt'
$coverage = Join-Path $directory 'coverage.xml'
function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) { throw "$Label`: expected $Expected, got $Actual" }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        $caught = $true
        if ($_.Exception.Message -notlike "*$Message*") { throw }
    }
    if (-not $caught) { throw "Expected failure containing: $Message" }
}
function Write-Report([string]$Percentage, [int]$IndexHits = 1) {
    "Line coverage: $Percentage%" | Set-Content $summary
    @"
<coverage><packages><package name="MFTLib"><classes>
<class name="MFTLib.MftVolume"><lines><line number="1" hits="1"/></lines></class>
<class name="MFTLib.Index.FileRow"><lines><line number="1" hits="$IndexHits"/></lines></class>
<class name="MFTLib.Index.Uncovered"><lines><line number="2" hits="0"/></lines></class>
<class name="MFTLib.Index.FileIndex/Nested"><lines><line number="3" hits="0"/></lines></class>
<class name="MFTLib.IndexExtra.Other"><lines><line number="1" hits="1"/></lines></class>
</classes></package><package name="TestProgram"><classes>
<class name="TestProgram.DriveScanner"><lines><line number="1" hits="1"/></lines></class>
</classes></package><package name="Benchmark"><classes>
<class name="Benchmark.BenchmarkRunner"><lines><line number="1" hits="1"/></lines></class>
</classes></package></packages></coverage>
"@ | Set-Content $coverage
}
try {
    $culture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'
        foreach ($number in @('97.2', '87.2', '100')) {
            Write-Report $number
            $actual = Test-CoverageMeasurement $summary $coverage 97.2
            Assert-Equal $actual ([decimal]::Parse($number, [cultureinfo]::InvariantCulture)) $number
        }
    } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $culture }
    foreach ($number in @('77', '87.1')) {
        Write-Report $number
        Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'drop'
    }
    Write-Report '97.2' 0
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'MFTLib.Index'
    Write-Report '97.2'
    foreach ($bad in @('101', '-1', 'NaN', '1.2.3', '97,2')) {
        Assert-Throws { ConvertFrom-CoveragePercentage $bad } 'percentage'
    }
    Set-Content $summary 'No measurement'
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'summary'
    Write-Report '97.2'
    Set-Content $coverage '<broken'
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } ''
    Set-Content $coverage '<coverage><packages/></coverage>'
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'lines'
    Write-Report '97.2'
    Remove-Item $summary
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } ''

    $readApi = {
        param($Path)
        switch ($Path) {
            '/branches/main' { return @{ commit = @{ id = 'main-tip' } } }
            '/statuses/main-tip?sort=highestindex&page=1&limit=50' {
                return @(@{ context='pr-crew/coverage'; status='error'; description='rejected' })
            }
            '/statuses/main-tip?sort=highestindex&page=2&limit=50' { return @() }
            '/git/commits/main-tip' { return @{ parents = @(@{ sha='main-parent' }, @{ sha='feature' }) } }
            '/statuses/main-parent?sort=highestindex&page=1&limit=50' {
                return @(@{ context='other'; status='success'; description='100% line coverage' })
            }
            '/statuses/main-parent?sort=highestindex&page=2&limit=50' {
                return @(
                    @{ context='pr-crew/coverage'; status='success'; description='97.2% line coverage' },
                    @{ context='pr-crew/coverage'; status='success'; description='77% line coverage' }
                )
            }
            default { throw "Unexpected API path: $Path" }
        }
    }
    Assert-Equal (Get-MainCoverageBaseline $readApi) ([decimal]97.2) 'paginated main baseline'
    $emptyApi = {
        param($Path)
        if ($Path -eq '/branches/main') { return @{ commit=@{ id='root' } } }
        if ($Path -eq '/git/commits/root') { return @{ parents=@() } }
        return @()
    }
    Assert-Throws { Get-MainCoverageBaseline $emptyApi } 'baseline'
    $brokenApi = { param($Path) throw 'GET unavailable' }
    Assert-Throws { Get-MainCoverageBaseline $brokenApi } 'GET unavailable'
    $malformedApi = {
        param($Path)
        if ($Path -eq '/branches/main') { return @{ commit=@{ id='root' } } }
        return @(@{ context='pr-crew/coverage'; status='success'; description='broken' })
    }
    Assert-Throws { Get-MainCoverageBaseline $malformedApi } 'baseline'

    $posted = [Collections.Generic.List[object]]::new()
    $postStatus = { param($Body) $posted.Add($Body) }
    foreach ($case in @(
        @{ Percentage='97.2'; Hits=1; Outcome='success'; Code=0; State='success'; Api=$readApi },
        @{ Percentage='77'; Hits=1; Outcome='success'; Code=1; State='error'; Api=$readApi },
        @{ Percentage='97.2'; Hits=0; Outcome='success'; Code=1; State='error'; Api=$readApi },
        @{ Percentage='97.2'; Hits=1; Outcome='failure'; Code=1; State='error'; Api=$readApi },
        @{ Percentage='97.2'; Hits=1; Outcome='skipped'; Code=1; State='error'; Api=$readApi },
        @{ Percentage='97.2'; Hits=1; Outcome='success'; Code=1; State='error'; Api=$brokenApi },
        @{ Percentage='97.2'; Hits=1; Outcome='success'; Code=1; State='error'; Api=$emptyApi }
    )) {
        Write-Report $case.Percentage $case.Hits
        $posted.Clear()
        $code = Publish-CoverageMeasurement $summary $coverage $case.Outcome 'https://example.test/run' $case.Api $postStatus
        Assert-Equal $code $case.Code 'exit code'
        Assert-Equal $posted.Count 1 'single POST'
        Assert-Equal $posted[0].state $case.State 'state'
        Assert-Equal $posted[0].context 'pr-crew/coverage' 'context'
        Assert-Equal $posted[0].target_url 'https://example.test/run' 'target URL'
        if ($case.State -eq 'error' -and $posted[0].description -match '[0-9%]') {
            throw 'Rejected measurement leaked numeric description'
        }
        if ($case.State -eq 'success') {
            Assert-Equal $posted[0].description '97.2% line coverage' 'success format'
        }
    }
    Write-Report '97.2'
    $failedPost = { param($Body) throw 'POST unavailable' }
    Assert-Equal (Publish-CoverageMeasurement $summary $coverage 'success' 'url' $readApi $failedPost) 1 'POST failure'
    Write-Host 'Coverage status regression tests passed.'
} finally { Remove-Item $directory -Recurse -Force }
