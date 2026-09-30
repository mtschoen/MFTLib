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
</classes></package><package name="MFTLibTestExtensions"><classes>
<class name="MFTLibTestExtensions.BrokerTestHarness" filename="MFTLibTestExtensions/BrokerTestHarness.cs">
<methods><method name="StartInProcess" signature="()" line-rate="1" branch-rate="1"><lines>
<line number="23" hits="1" branch="true" condition-coverage="100% (2/2)"/>
</lines></method></methods>
<lines><line number="23" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines>
</class></classes></package></packages></coverage>
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
    $extensionCases = @(
        @{ Name='missing package'; Change={ param($p) $p.ParentNode.RemoveChild($p) | Out-Null } },
        @{ Name='empty package'; Change={ param($p) $p.RemoveChild($p.classes) | Out-Null } },
        @{ Name='uncovered line'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('hits','0') } },
        @{ Name='partial branch'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('condition-coverage','50% (1/2)') } },
        @{ Name='rounded branch'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('condition-coverage','100% (99999/100000)') } },
        @{ Name='missing branch counts'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').RemoveAttribute('condition-coverage') } },
        @{ Name='bad hits'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('hits','bad') } },
        @{ Name='missing methods'; Change={ param($p) $p.SelectSingleNode('classes/class').RemoveChild($p.SelectSingleNode('classes/class/methods')) | Out-Null } },
        @{ Name='uncovered method'; Change={ param($p) $p.SelectSingleNode('classes/class/methods/method/lines/line').SetAttribute('hits','0') } },
        @{ Name='invalid branch marker'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('branch','invalid') } },
        @{ Name='orphan condition coverage'; Change={ param($p) $p.SelectSingleNode('classes/class/lines/line').SetAttribute('branch','false') } },
        @{ Name='method-level partial branch'; Change={ param($p) $p.SelectSingleNode('classes/class/methods/method/lines/line').SetAttribute('condition-coverage','50% (1/2)') } },
        @{ Name='missing class lines'; Change={ param($p) $class = $p.SelectSingleNode('classes/class'); $class.RemoveChild($class.SelectSingleNode('lines')) | Out-Null } },
        @{ Name='partly covered method'; Change={ param($p)
            $missed = $p.OwnerDocument.CreateElement('line')
            $missed.SetAttribute('number', '24')
            $missed.SetAttribute('hits', '0')
            $p.SelectSingleNode('classes/class/methods/method/lines').AppendChild($missed) | Out-Null } }
    )
    foreach ($case in $extensionCases) {
        Write-Report '97.2'
        [xml]$document = Get-Content $coverage -Raw
        $package = $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]')
        & $case.Change $package
        $document.Save($coverage)
        Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } ''
    }

    Write-Report '97.2'
    [xml]$document = Get-Content $coverage -Raw
    $extra = $document.CreateDocumentFragment()
    $extra.InnerXml = '<class name="MFTLibTestExtensions.Internal.Owner/Nested" filename="Owner.cs"><methods><method name="Run" signature="()"><lines><line number="8" hits="0"/></lines></method></methods><lines><line number="8" hits="0"/></lines></class>'
    $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]/classes').AppendChild($extra) | Out-Null
    $document.Save($coverage)
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'MFTLibTestExtensions'

    Write-Report '97.2'
    [xml]$document = Get-Content $coverage -Raw
    $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]/classes/class').SetAttribute('name', 'MFTLibTestExtensionsExtra.BrokerTestHarness')
    $document.Save($coverage)
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'MFTLibTestExtensions'

    Write-Report '97.2'
    [xml]$document = Get-Content $coverage -Raw
    $package = $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]')
    $package.ParentNode.AppendChild($package.CloneNode($true)) | Out-Null
    $document.Save($coverage)
    Assert-Throws { Test-CoverageMeasurement $summary $coverage 97.2 } 'duplicate'
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
    foreach ($case in $extensionCases) {
        Write-Report '97.2'
        [xml]$document = Get-Content $coverage -Raw
        $package = $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]')
        & $case.Change $package
        $document.Save($coverage)
        $posted.Clear()
        $code = Publish-CoverageMeasurement $summary $coverage 'success' 'https://example.test/run' $readApi $postStatus
        Assert-Equal $code 1 $case.Name
        Assert-Equal $posted.Count 1 'single rejected POST'
        Assert-Equal $posted[0].state 'error' 'extension error state'
        Assert-Equal $posted[0].context 'pr-crew/coverage' 'extension status context'
        Assert-Equal $posted[0].target_url 'https://example.test/run' 'extension target URL'
        if ($posted[0].description -match '[0-9%]') { throw 'Rejected extension measurement leaked numeric description' }
    }

    Write-Report '97.2'
    [xml]$document = Get-Content $coverage -Raw
    $package = $document.SelectSingleNode('/coverage/packages/package[@name="MFTLibTestExtensions"]')
    foreach ($line in $package.SelectNodes('.//line')) {
        $line.RemoveAttribute('branch')
        $line.RemoveAttribute('condition-coverage')
    }
    $declaration = $document.CreateDocumentFragment()
    $declaration.InnerXml = '<method name="Declaration" signature="()"><lines/></method>'
    $package.SelectSingleNode('classes/class/methods').AppendChild($declaration) | Out-Null
    $document.Save($coverage)
    Assert-Equal (Test-CoverageMeasurement $summary $coverage 97.2) ([decimal]97.2) 'branchless executable method'

    Write-Report '97.2'
    $failedPost = { param($Body) throw 'POST unavailable' }
    Assert-Equal (Publish-CoverageMeasurement $summary $coverage 'success' 'url' $readApi $failedPost) 1 'POST failure'
    # vstest kills a testhost that has not exited 100 ms after the run ends, which cuts coverlet's
    # exit-time hit-file flush short and drops modules from the report (Benchmark 33.45 percent,
    # TestProgram 0 percent). Every coverage entry point must extend that grace period before it
    # starts the test host.
    function Assert-ShutdownTimeout([string]$Text, [string]$Assignment, [int]$Assignments, [string]$Invocation, [string]$Label) {
        $found = [regex]::Matches($Text, $Assignment, 'Multiline')
        $valid = @($found | Where-Object { [long]$_.Groups[1].Value -ge 1000 })
        $invocations = [regex]::Matches($Text, $Invocation, 'Multiline')
        if ($valid.Count -lt $Assignments -or $invocations.Count -lt $Assignments) {
            throw "$Label does not extend the vstest testhost shutdown timeout"
        }
        if ($valid[0].Index -gt $invocations[0].Index -or $valid[-1].Index -gt $invocations[-1].Index) {
            throw "$Label sets the vstest testhost shutdown timeout after starting the test host"
        }
    }
    $dotnetTestInvocation = '^\s*dotnet test "'
    $powerShellAssignment = '^\s*\$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT\s*=\s*"(\d+)"'
    $shellAssignment = '^\s*export VSTEST_TESTHOST_SHUTDOWN_TIMEOUT=(\d+)\s*$'
    $runCoverage = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'run-coverage.ps1') -Raw
    $linuxCoverage = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'coverage-linux.sh') -Raw
    Assert-ShutdownTimeout $runCoverage $powerShellAssignment 2 $dotnetTestInvocation 'run-coverage.ps1'
    Assert-ShutdownTimeout $linuxCoverage $shellAssignment 1 $dotnetTestInvocation 'coverage-linux.sh'
    Assert-Throws { Assert-ShutdownTimeout ($runCoverage -replace '(?m)^(\s*)\$env:VSTEST', '$1# $env:VSTEST') $powerShellAssignment 2 $dotnetTestInvocation 'mutant' } 'does not extend'
    Assert-Throws { Assert-ShutdownTimeout ($runCoverage -replace '"120000"', '"100"') $powerShellAssignment 2 $dotnetTestInvocation 'mutant' } 'does not extend'
    Assert-Throws { Assert-ShutdownTimeout ($runCoverage -replace '(?m)^\$env:VSTEST.*$', '') $powerShellAssignment 2 $dotnetTestInvocation 'mutant' } 'does not extend'
    Assert-Throws { Assert-ShutdownTimeout ($linuxCoverage -replace '=120000', '=1') $shellAssignment 1 $dotnetTestInvocation 'mutant' } 'does not extend'
    Assert-Throws { Assert-ShutdownTimeout ('dotnet test "x"' + [Environment]::NewLine + '$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = "120000"') $powerShellAssignment 1 $dotnetTestInvocation 'mutant' } 'after starting'
    Write-Host 'Coverage status regression tests passed.'
} finally { Remove-Item $directory -Recurse -Force }
