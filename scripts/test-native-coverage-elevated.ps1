# Regression tests for shared coverage child runner and callers
# (coverage-child.ps1, coverage-log.ps1, native-coverage-elevated.ps1, run-coverage.ps1)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/coverage-child.ps1"

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -cne $Expected) { throw "$Label`: expected $Expected, got $Actual" }
}
function Assert-Contains([string]$Actual, [string]$Expected) {
    if (-not $Actual.Contains($Expected)) { throw "Missing output: $Expected" }
}

$processWatchdogSeconds = if ($env:MFTLIB_SCRIPT_TEST_TIMEOUT_SECONDS) {
    [int]$env:MFTLIB_SCRIPT_TEST_TIMEOUT_SECONDS
} else { 180 }

function Wait-TestProcess([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForExit([TimeSpan]::FromSeconds($processWatchdogSeconds))) {
        $Process.Kill($true)
        $Process.WaitForExit()
        throw 'Integration fixture did not complete before the shared hang watchdog'
    }
}

function Get-PwshPath {
    @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
}

function Invoke-TestScript([string]$Path, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Get-PwshPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in (@('-NoProfile', '-File', $Path) + $Arguments)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        Wait-TestProcess $process
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $output.GetAwaiter().GetResult()
            ErrorOutput = $errorOutput.GetAwaiter().GetResult()
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

$testDir = Join-Path ([IO.Path]::GetTempPath()) ("MFTLib coverage ' `$ [fixture] " + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testDir) | Out-Null

function New-TestRequest([string]$Target, [string[]]$Arguments = @(), [string]$NameSuffix = '') {
    $suffix = if ($NameSuffix) { "_$NameSuffix" } else { '' }
    @{
        Target = $Target
        Arguments = $Arguments
        WorkingDirectory = $testDir
        LogPath = (Join-Path $testDir "child$suffix.log")
        DonePath = (Join-Path $testDir "child$suffix.done")
        Environment = @{ VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = '120000' }
    }
}

try {
    # Task 1: Step 1a - Target execution and argument transport
    $echoTarget = Join-Path $testDir 'echo target.ps1'
    @'
@{
    Arguments = @($args)
    Directory = (Get-Location).Path
    ShutdownTimeout = $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT
} | ConvertTo-Json -Depth 5 -Compress
'@ | Set-Content -LiteralPath $echoTarget -Encoding utf8
    $values = @('space value', "O'Brien", '$env:USERPROFILE', 'a"quoted"value', '"C:\a b\"', '', '$(throw "executed")')
    $request = New-TestRequest $echoTarget $values
    Assert-Equal (Invoke-CoverageTarget $request) 0 'script success'
    $observed = Get-Content -LiteralPath $request.LogPath -Raw | ConvertFrom-Json
    Assert-Equal ($observed.Arguments | ConvertTo-Json -Compress) ($values | ConvertTo-Json -Compress) 'script argv'
    Assert-Equal $observed.Directory $testDir 'working directory'
    Assert-Equal $observed.ShutdownTimeout '120000' 'explicit child environment'
    Assert-Equal ([IO.File]::ReadAllText($request.DonePath).Trim()) '0' 'success marker'
    Assert-Equal (Test-Path -LiteralPath "$($request.DonePath).tmp") $false 'marker publication finished'

    # Native executable transport
    $nativeRequest = New-TestRequest (Get-PwshPath) (@('-NoProfile', '-File', $echoTarget) + $values) 'native'
    Assert-Equal (Invoke-CoverageTarget $nativeRequest) 0 'executable success'
    $nativeObserved = Get-Content -LiteralPath $nativeRequest.LogPath -Raw | ConvertFrom-Json
    Assert-Equal ($nativeObserved.Arguments | ConvertTo-Json -Compress) ($values | ConvertTo-Json -Compress) 'executable argv'
    Assert-Equal $nativeObserved.Directory $testDir 'executable working directory'
    Assert-Equal $nativeObserved.ShutdownTimeout '120000' 'executable child environment'
    Assert-Equal ([IO.File]::ReadAllText($nativeRequest.DonePath).Trim()) '0' 'executable success marker'

    # Native stdout and stderr
    $stderrTarget = Join-Path $testDir 'stderr target.ps1'
    @'
[Console]::Out.WriteLine('native stdout fixture')
[Console]::Error.WriteLine('native stderr fixture')
exit 0
'@ | Set-Content -LiteralPath $stderrTarget -Encoding utf8
    $stderrRequest = New-TestRequest (Get-PwshPath) @('-NoProfile', '-File', $stderrTarget) 'stderr'
    Assert-Equal (Invoke-CoverageTarget $stderrRequest) 0 'stderr executable success'
    $stderrLog = Get-Content -LiteralPath $stderrRequest.LogPath -Raw
    Assert-Contains $stderrLog 'native stdout fixture'
    Assert-Contains $stderrLog 'native stderr fixture'

    # Exit 3 and throw
    $exit3Target = Join-Path $testDir 'exit3 target.ps1'
    'exit 3' | Set-Content -LiteralPath $exit3Target -Encoding utf8
    $exit3Request = New-TestRequest $exit3Target @() 'exit3'
    Assert-Equal (Invoke-CoverageTarget $exit3Request) 3 'exit 3 target status'
    Assert-Equal ([IO.File]::ReadAllText($exit3Request.DonePath).Trim()) '3' 'exit 3 marker'

    $throwTarget = Join-Path $testDir 'throw target.ps1'
    'throw "fixture failure"' | Set-Content -LiteralPath $throwTarget -Encoding utf8
    $throwRequest = New-TestRequest $throwTarget @() 'throw'
    Assert-Equal (Invoke-CoverageTarget $throwRequest) 1 'throw target status'
    Assert-Equal ([IO.File]::ReadAllText($throwRequest.DonePath).Trim()) '1' 'throw marker'
    Assert-Contains (Get-Content -LiteralPath $throwRequest.LogPath -Raw) 'fixture failure'

    # Nonexistent working directory
    $badDirRequest = New-TestRequest $echoTarget @() 'baddir'
    $badDirRequest.WorkingDirectory = Join-Path $testDir 'nonexistent-directory'
    Assert-Equal (Invoke-CoverageTarget $badDirRequest) 1 'nonexistent working directory status'
    Assert-Equal ([IO.File]::ReadAllText($badDirRequest.DonePath).Trim()) '1' 'nonexistent directory marker'

    # Task 1: Step 1b - Controlled monitor tests
    foreach ($expectedExit in @(0, 3)) {
        & {
            $request = New-TestRequest $echoTarget @() "monitor_$expectedExit"
            Remove-Item -LiteralPath $request.DonePath -ErrorAction SilentlyContinue
            [IO.File]::WriteAllText($request.LogPath, "begin`npartial")
            $process = [pscustomobject]@{ HasExited = $false; ExitCode = $expectedExit }
            $clock = [pscustomobject]@{ Elapsed = [TimeSpan]::Zero }
            $state = [pscustomobject]@{ Phase = 'waiting'; WarningSeen = $false; HeartbeatSeen = $false }
            function Start-CoverageClock { $clock }
            function Start-Sleep {
                param([int]$Seconds)
                Assert-Equal $Seconds 2 'production poll interval'
                if ($state.Phase -eq 'waiting') {
                    $clock.Elapsed = [TimeSpan]::FromSeconds(4)
                    $state.Phase = 'threshold'
                } elseif ($state.Phase -eq 'threshold') {
                    if (-not $state.WarningSeen -or -not $state.HeartbeatSeen) {
                        throw 'Alive child must produce warning and heartbeat before completion'
                    }
                    [IO.File]::WriteAllText($request.DonePath, [string]$expectedExit)
                    $process.HasExited = $true
                    $state.Phase = 'completed'
                } else { throw 'Monitor continued after completion' }
            }
            $exitCode = $null
            $messages = & {
                $exitCode = Wait-CoverageChild $process $request.LogPath $request.DonePath 2 2
                Set-Variable -Name result -Value $exitCode -Scope 1
            } 6>&1 | ForEach-Object {
                $text = $_.ToString()
                if ($text.Contains('child still alive, continuing to wait')) { $state.WarningSeen = $true }
                if ($text.Contains('elapsed, child still running')) { $state.HeartbeatSeen = $true }
                $text
            }
            Assert-Equal $result $expectedExit 'monitor status'
            Assert-Contains ($messages -join "`n") 'partial'
            Reset-LogTailer
        }
    }

    # Marker before exit phase
    & {
        $request = New-TestRequest $echoTarget @() 'marker_before_exit'
        Remove-Item -LiteralPath $request.DonePath -ErrorAction SilentlyContinue
        [IO.File]::WriteAllText($request.LogPath, "initial line`n")
        $process = [pscustomobject]@{ HasExited = $false; ExitCode = 0 }
        $clock = [pscustomobject]@{ Elapsed = [TimeSpan]::Zero }
        $state = [pscustomobject]@{ Phase = 'waiting' }
        function Start-CoverageClock { $clock }
        function Start-Sleep {
            param([int]$Seconds)
            if ($state.Phase -eq 'waiting') {
                [IO.File]::WriteAllText($request.DonePath, '0')
                $state.Phase = 'marker_written'
            } elseif ($state.Phase -eq 'marker_written') {
                [IO.File]::AppendAllText($request.LogPath, "final drained line")
                $process.HasExited = $true
                $state.Phase = 'completed'
            } else { throw 'Monitor continued after completion' }
        }
        $exitCode = $null
        $messages = & {
            $exitCode = Wait-CoverageChild $process $request.LogPath $request.DonePath 100 100
            Set-Variable -Name result -Value $exitCode -Scope 1
        } 6>&1
        Assert-Equal $result 0 'marker before exit status'
        Assert-Contains ($messages -join "`n") 'final drained line'
        Reset-LogTailer
    }

    # Dead child without marker
    & {
        $request = New-TestRequest $echoTarget @() 'dead_no_marker'
        Remove-Item -LiteralPath $request.DonePath -ErrorAction SilentlyContinue
        [IO.File]::WriteAllText($request.LogPath, 'unterminated')
        $process = [pscustomobject]@{ HasExited = $true; ExitCode = 42 }
        function Start-CoverageClock { [pscustomobject]@{ Elapsed = [TimeSpan]::Zero } }
        function Start-Sleep { param([int]$Seconds) }
        $messages = & {
            $code = Wait-CoverageChild $process $request.LogPath $request.DonePath
            Set-Variable -Name result -Value $code -Scope 1
        } 6>&1
        Assert-Equal $result 1 'dead child without marker'
        Assert-Contains ($messages -join "`n") 'without writing a completion marker'
        Assert-Contains ($messages -join "`n") 'unterminated'
        Reset-LogTailer
    }

    # Bad marker values on dead process
    foreach ($badMarker in @('', 'not-an-integer', '2147483648')) {
        & {
            $request = New-TestRequest $echoTarget @() "bad_marker_$badMarker"
            [IO.File]::WriteAllText($request.LogPath, 'log output')
            [IO.File]::WriteAllText($request.DonePath, $badMarker)
            $process = [pscustomobject]@{ HasExited = $true; ExitCode = 0 }
            function Start-CoverageClock { [pscustomobject]@{ Elapsed = [TimeSpan]::Zero } }
            function Start-Sleep { param([int]$Seconds) }
            $code = Wait-CoverageChild $process $request.LogPath $request.DonePath
            Assert-Equal $code 1 "bad marker '$badMarker' returns failure 1"
            Reset-LogTailer
        }
    }

    # Marker 0 but process exit 7
    & {
        $request = New-TestRequest $echoTarget @() 'marker0_exit7'
        [IO.File]::WriteAllText($request.LogPath, 'log output')
        [IO.File]::WriteAllText($request.DonePath, '0')
        $process = [pscustomobject]@{ HasExited = $true; ExitCode = 7 }
        function Start-CoverageClock { [pscustomobject]@{ Elapsed = [TimeSpan]::Zero } }
        function Start-Sleep { param([int]$Seconds) }
        $code = Wait-CoverageChild $process $request.LogPath $request.DonePath
        Assert-Equal $code 7 'process exit 7 overrides marker 0'
        Reset-LogTailer
    }

    # Step 3d: Real process protocol checks
    foreach ($case in @(
        @{ Body = 'Write-Host "success"; exit 0'; Code = 0; Marker = '0' },
        @{ Body = 'Write-Host "failure"; exit 3'; Code = 3; Marker = '3' },
        @{ Body = 'throw "fixture failure"'; Code = 1; Marker = '1' },
        @{ Body = '[Environment]::Exit(42)'; Code = 42; Marker = $null }
    )) {
        $target = Join-Path $testDir "exit_target_$($case.Code).ps1"
        Set-Content -LiteralPath $target -Value $case.Body -Encoding utf8
        $request = New-TestRequest $target @() "exit_$($case.Code)"
        Remove-Item -LiteralPath $request.DonePath -ErrorAction SilentlyContinue
        $process = Start-CoverageChild $request -NoElevation
        try {
            Wait-TestProcess $process
            Assert-Equal $process.ExitCode $case.Code 'real child exit'
            if ($null -eq $case.Marker) {
                Assert-Equal (Test-Path -LiteralPath $request.DonePath) $false 'abrupt exit has no marker'
                Assert-Equal (Wait-CoverageChild $process $request.LogPath $request.DonePath) 1 'abrupt exit reaches parent'
            } else {
                Assert-Equal ([IO.File]::ReadAllText($request.DonePath).Trim()) $case.Marker 'real child marker'
                Assert-Equal (Test-Path -LiteralPath "$($request.DonePath).tmp") $false 'atomic rename complete'
            }
        } finally { $process.Dispose(); Reset-LogTailer }
    }

    # Live-stream acknowledgment test
    $streamTarget = Join-Path $testDir 'stream target.ps1'
    $acknowledgment = Join-Path $testDir 'stream acknowledged'
    @'
param([string]$Acknowledgment)
Write-Host 'stream-ready'
while (-not (Test-Path -LiteralPath $Acknowledgment)) { Start-Sleep -Milliseconds 50 }
Write-Host 'stream-finished'
'@ | Set-Content -LiteralPath $streamTarget -Encoding utf8
    $request = New-TestRequest $streamTarget @($acknowledgment) 'stream'
    Remove-Item -LiteralPath $request.DonePath, $request.LogPath, $acknowledgment -ErrorAction SilentlyContinue
    $process = Start-CoverageChild $request -NoElevation
    $watchdog = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($processWatchdogSeconds))
    $observedLines = [Collections.Generic.List[string]]::new()
    try {
        while (-not $process.HasExited) {
            if ($watchdog.IsCancellationRequested) { throw 'Streaming fixture exceeded shared hang watchdog' }
            foreach ($line in @(Process-NewLogOutput -Path $request.LogPath -PassThru)) {
                $observedLines.Add($line)
                if ($line -eq 'stream-ready') {
                    Assert-Equal $process.HasExited $false 'stream delivered before child completion'
                    [IO.File]::WriteAllText($acknowledgment, 'observed')
                }
            }
            Start-Sleep -Milliseconds 50
        }
        foreach ($line in @(Process-NewLogOutput -Path $request.LogPath -Drain -PassThru)) {
            $observedLines.Add($line)
        }
        Assert-Equal $process.ExitCode 0 'streaming child status'
        Assert-Contains ($observedLines -join "`n") 'stream-finished'
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
        $watchdog.Dispose()
        Reset-LogTailer
    }

    # Recovered Test 5: Incomplete-line retention and final draining (UTF-8 and Unicode)
    Write-Host "`nIncomplete-line retention and final draining via Process-NewLogOutput (UTF-8 and Unicode)..."
    foreach ($testEncoding in @("UTF8", "Unicode")) {
        $probeFile = Join-Path $testDir "tailerProbe_$testEncoding.log"
        $encodingObj = if ($testEncoding -eq "Unicode") { [System.Text.Encoding]::Unicode } else { [System.Text.Encoding]::UTF8 }

        Reset-LogTailer

        $bytesPrefix = $encodingObj.GetBytes("Prefix without newline")
        $initialBytes = if ($testEncoding -eq "Unicode") { [System.Text.Encoding]::Unicode.GetPreamble() + $bytesPrefix } else { $bytesPrefix }
        [System.IO.File]::WriteAllBytes($probeFile, $initialBytes)

        # First call: incomplete prefix should remain in pendingText without emitting complete lines
        $emitted1 = @(Process-NewLogOutput -Path $probeFile -PassThru)
        if ($emitted1.Count -ne 0) {
            throw "Test 5 ($testEncoding) failed: incomplete line was emitted prematurely: $($emitted1 -join "; ")"
        }
        if ($script:pendingText -ne "Prefix without newline") {
            throw "Test 5 ($testEncoding) failed: pending text mismatch: '$($script:pendingText)'"
        }

        # Append completion and second line and trailing unterminated text
        $moreBytes = $encodingObj.GetBytes(" completed.`nSecond complete line.`nTrailing unterminated")
        $appendFs = [System.IO.FileStream]::new($probeFile, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        $appendFs.Write($moreBytes, 0, $moreBytes.Length)
        $appendFs.Dispose()

        # Second call: complete lines emitted, trailing unterminated remains in pendingText
        $emitted2 = @(Process-NewLogOutput -Path $probeFile -PassThru)
        if ($emitted2.Count -ne 2 -or $emitted2[0] -ne "Prefix without newline completed." -or $emitted2[1] -ne "Second complete line.") {
            throw "Test 5 ($testEncoding) failed: complete lines mismatch: $($emitted2 -join "; ")"
        }
        if ($script:pendingText -ne "Trailing unterminated") {
            throw "Test 5 ($testEncoding) failed: pending text mismatch: '$($script:pendingText)'"
        }

        # Final drain: trailing unterminated line is drained
        $emitted3 = @(Process-NewLogOutput -Path $probeFile -Drain -PassThru)
        if ($emitted3.Count -ne 1 -or $emitted3[0] -ne "Trailing unterminated") {
            throw "Test 5 ($testEncoding) failed: drained line mismatch: $($emitted3 -join "; ")"
        }
        if ($script:pendingText -ne "") {
            throw "Test 5 ($testEncoding) failed: pendingText should be empty after Drain"
        }

        Reset-LogTailer
        Remove-Item $probeFile -Force
    }

    # Task 2: Caller migration and status propagation
    . "$PSScriptRoot/run-coverage.ps1"
    . "$PSScriptRoot/native-coverage-elevated.ps1" -FunctionsOnly
    & {
        $coverageDirectory = Join-Path $testDir 'MFTLib.Tests'
        [IO.Directory]::CreateDirectory($coverageDirectory) | Out-Null
        $coveragePath = Join-Path $coverageDirectory 'coverage.xml'
        Set-Content -LiteralPath $coveragePath -Value '<coverage />'
        $observation = [pscustomobject]@{ Request = $null; ChildExit = 7; NoElevation = $null }
        function Invoke-CoverageChild {
            param([hashtable]$Request, [switch]$NoElevation, [int]$TimeoutSeconds, [int]$HeartbeatSeconds)
            $observation.Request = $Request
            $observation.NoElevation = [bool]$NoElevation
            return $observation.ChildExit
        }
        Assert-Equal (Invoke-ManagedAdminCoverage $testDir 'Release') 7 'failed child despite Cobertura'
        $request = $observation.Request
        Assert-Equal $request.Environment.VSTEST_TESTHOST_SHUTDOWN_TIMEOUT '120000' 'managed child timeout'
        Assert-Equal $observation.NoElevation $false 'managed default requests elevation'
        Assert-Equal $request.WorkingDirectory $testDir 'managed working directory'
        Assert-Equal ($request.Arguments -contains 'TestCategory=RequiresAdmin') $true 'admin filter'
        Assert-Equal ($request.Arguments -contains '-p:CoverletOutputFormat=cobertura') $true 'Cobertura output'
        Assert-Equal ($request.Arguments -contains "-p:MergeWith=$(Join-Path $coverageDirectory 'coverage.json')") $true 'JSON merge'
        $observation.ChildExit = 0
        Assert-Equal (Invoke-ManagedAdminCoverage $testDir 'Release') 0 'managed success'
        Remove-Item -LiteralPath $coveragePath
        Assert-Equal (Invoke-ManagedAdminCoverage $testDir 'Release') 1 'missing Cobertura after successful child'
        $observation.ChildExit = 3
        Assert-Equal (Invoke-NativeCoverage -RepositoryRoot $testDir -TargetScript $echoTarget -NoElevation -ForceNonAdmin) 3 'native status propagation'
        Assert-Equal $observation.Request.Target $echoTarget 'native target selection'
        Assert-Equal $observation.Request.Arguments.Count 0 'native argument vector'
        Assert-Equal $observation.NoElevation $true 'fixture launch stays non-elevated'
    }

    # Task 2: Subprocess regression of real run-coverage.ps1 body
    $managedFixture = Join-Path $testDir 'managed repository'
    $fixtureScripts = Join-Path $managedFixture 'scripts'
    [IO.Directory]::CreateDirectory($fixtureScripts) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $managedFixture 'MFTLib.Tests')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-coverage.ps1') -Destination $fixtureScripts
    Set-Content -LiteralPath (Join-Path $fixtureScripts 'build-windows.ps1') -Value 'exit 0'
    $sharedFixturePath = Join-Path $fixtureScripts 'coverage-child.ps1'
    @'
function Invoke-CoverageChild {
    param([hashtable]$Request)
    $Request | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Request.WorkingDirectory 'delegated.json')
    [IO.File]::WriteAllText((Join-Path $Request.WorkingDirectory 'MFTLib.Tests/coverage.xml'), '<coverage />')
    return 7
}
'@ | Set-Content -LiteralPath $sharedFixturePath -Encoding utf8
    $bootstrap = Join-Path $testDir 'managed bootstrap.ps1'
    @'
param([string]$FixtureRoot, [switch]$NonInteractive)
$ErrorActionPreference = 'Stop'
$env:GITHUB_WORKSPACE = $FixtureRoot
function dotnet {
    $raw = @($args)
    if ($raw[0] -ne 'test') { throw 'Unexpected dotnet operation' }
    $reconstructed = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $raw.Count; $i++) {
        if ($raw[$i] -in @('-p:', '-p') -and ($i + 1) -lt $raw.Count) {
            $i++
            $reconstructed.Add("-p:$($raw[$i])")
        } else {
            $reconstructed.Add($raw[$i])
        }
    }
    $arguments = $reconstructed.ToArray()
    $arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $FixtureRoot 'dotnet-arguments.json')
    $outputArgument = @($arguments | Where-Object { $_.StartsWith('-p:CoverletOutput=') })
    if ($outputArgument.Count -ne 1) { throw 'Expected one coverlet output argument' }
    [IO.File]::WriteAllText($outputArgument[0].Substring('-p:CoverletOutput='.Length), '<coverage />')
    $resultsDirectory = $arguments[[array]::IndexOf($arguments, '--results-directory') + 1]
    [IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $resultsDirectory 'non-admin.trx'), '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="2" passed="1" failed="0" notExecuted="1" /></ResultSummary></TestRun>')
    $global:LASTEXITCODE = 0
}
function git { 'fixture-sha' }
function reportgenerator {
    $arguments = @($args)
    $targetArgument = @($arguments | Where-Object { $_.StartsWith('-targetdir:') })
    if ($targetArgument.Count -ne 1) { throw 'Expected one report target' }
    $directory = $targetArgument[0].Substring('-targetdir:'.Length)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'Summary.txt'), 'fixture summary')
    [IO.File]::WriteAllText((Join-Path $FixtureRoot 'report-called'), 'called')
    $global:LASTEXITCODE = 0
}
& (Join-Path $FixtureRoot 'scripts/run-coverage.ps1') -NonInteractive:$NonInteractive
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $bootstrap -Encoding utf8
    $result = Invoke-TestScript $bootstrap @($managedFixture)
    Assert-Equal $result.ExitCode 7 'real managed parent rejects failed child with report'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $managedFixture 'report-called')) $false 'failed child cannot reach reportgenerator'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $managedFixture 'delegated.json')) $true 'shared protocol was exercised'
    @'
function Invoke-CoverageChild { throw 'Unexpected elevated child' }
'@ | Set-Content -LiteralPath $sharedFixturePath -Encoding utf8
    $result = Invoke-TestScript $bootstrap @($managedFixture, '-NonInteractive')
    Assert-Equal $result.ExitCode 0 'noninteractive parent success'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $managedFixture 'report-called')) $true 'noninteractive reports'
    $arguments = Get-Content -LiteralPath (Join-Path $managedFixture 'dotnet-arguments.json') -Raw | ConvertFrom-Json
    Assert-Equal ($arguments -contains 'TestCategory!=RequiresAdmin') $true 'noninteractive skips admin tests'
    Assert-Equal ($arguments -contains '-p:CoverletOutputFormat=cobertura') $true 'noninteractive Cobertura'

    # Task 2: Non-elevated operation smokes
    # 1. Native non-elevated CLI smoke
    $nativeSmokeFixture = Join-Path $testDir 'native smoke'
    $nativeSmokeScripts = Join-Path $nativeSmokeFixture 'scripts'
    [IO.Directory]::CreateDirectory($nativeSmokeScripts) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native-coverage-elevated.ps1') -Destination $nativeSmokeScripts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'coverage-child.ps1') -Destination $nativeSmokeScripts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'coverage-log.ps1') -Destination $nativeSmokeScripts
    $nativeEchoTarget = Join-Path $nativeSmokeFixture 'native echo.ps1'
    @'
Write-Host "native smoke starting"
Write-Host "native smoke finished"
exit 0
'@ | Set-Content -LiteralPath $nativeEchoTarget -Encoding utf8
    $nativeSmokeResult = Invoke-TestScript (Join-Path $nativeSmokeScripts 'native-coverage-elevated.ps1') @(
        '-TargetScript', $nativeEchoTarget, '-NoElevation', '-ForceNonAdmin'
    )
    Assert-Equal $nativeSmokeResult.ExitCode 0 'native smoke exit code'
    Assert-Contains $nativeSmokeResult.Output 'native smoke starting'
    Assert-Contains $nativeSmokeResult.Output 'native smoke finished'
    Assert-Contains $nativeSmokeResult.Output 'Elevated coverage run finished'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $nativeSmokeFixture 'native-coverage-elevated.done')) $false 'native smoke done marker cleaned'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $nativeSmokeFixture 'native-coverage-elevated.done.tmp')) $false 'native smoke done tmp cleaned'

    # 2. Managed non-elevated real child smoke
    $capturedRequest = [pscustomobject]@{ Value = $null }
    & {
        function Invoke-CoverageChild {
            param([hashtable]$Request)
            $capturedRequest.Value = $Request
            return 0
        }
        $null = Invoke-ManagedAdminCoverage $testDir 'Release'
    }
    Assert-Equal ($null -ne $capturedRequest.Value) $true 'captured managed request'
    $captured = $capturedRequest.Value
    $managedEchoTarget = Join-Path $testDir 'managed echo target.ps1'
    @'
$raw = @($args)
$reconstructed = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $raw.Count; $i++) {
    if ($raw[$i] -in @('-p:', '-p') -and ($i + 1) -lt $raw.Count) {
        $i++
        $reconstructed.Add("-p:$($raw[$i])")
    } else {
        $reconstructed.Add($raw[$i])
    }
}
@{
    Arguments = $reconstructed.ToArray()
    Directory = (Get-Location).Path
    ShutdownTimeout = $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT
} | ConvertTo-Json -Depth 5 -Compress
'@ | Set-Content -LiteralPath $managedEchoTarget -Encoding utf8
    $smokeRequest = @{
        Target = (Get-PwshPath)
        Arguments = @('-NoProfile', '-File', $managedEchoTarget) + $captured.Arguments
        WorkingDirectory = $captured.WorkingDirectory
        LogPath = (Join-Path $testDir 'managed_smoke.log')
        DonePath = (Join-Path $testDir 'managed_smoke.done')
        Environment = $captured.Environment
    }
    $smokeChild = Start-CoverageChild $smokeRequest -NoElevation
    try {
        Wait-TestProcess $smokeChild
        Assert-Equal $smokeChild.ExitCode 0 'managed smoke child exit code'
        $smokeObserved = Get-Content -LiteralPath $smokeRequest.LogPath -Raw | ConvertFrom-Json
        Assert-Equal $smokeObserved.ShutdownTimeout '120000' 'managed smoke child shutdown timeout'
        Assert-Equal ($smokeObserved.Arguments -contains 'TestCategory=RequiresAdmin') $true 'managed smoke args contain admin filter'
        Assert-Equal ($smokeObserved.Arguments -contains '-p:CoverletOutputFormat=cobertura') $true 'managed smoke args contain cobertura'
    } finally {
        $smokeChild.Dispose()
        Reset-LogTailer
    }

    # Second fake executable case: writes requested Cobertura path and exits 7
    $failingTarget = Join-Path $testDir 'failing target.ps1'
    @'
param([string]$CoberturaPath)
[IO.File]::WriteAllText($CoberturaPath, '<coverage />')
exit 7
'@ | Set-Content -LiteralPath $failingTarget -Encoding utf8
    $coberturaOut = Join-Path $testDir 'smoke_cobertura.xml'
    $failRequest = @{
        Target = (Get-PwshPath)
        Arguments = @('-NoProfile', '-File', $failingTarget, $coberturaOut)
        WorkingDirectory = $testDir
        LogPath = (Join-Path $testDir 'managed_fail_smoke.log')
        DonePath = (Join-Path $testDir 'managed_fail_smoke.done')
        Environment = @{}
    }
    $failExit = Invoke-CoverageChild $failRequest -NoElevation
    Assert-Equal $failExit 7 'failing target exit 7'
    Assert-Equal (Test-Path -LiteralPath $coberturaOut) $true 'cobertura written by failing child'

    # Task 3: CI integration and delegation guards
    $sourceRoot = Split-Path -Parent $PSScriptRoot
    $workflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.gitea/workflows/test.yml') -Raw
    Assert-Contains $workflow 'run: pwsh -NoProfile -File scripts/test-native-coverage-elevated.ps1'
    foreach ($relativePath in @('scripts/run-coverage.ps1', 'scripts/native-coverage-elevated.ps1')) {
        $text = Get-Content -LiteralPath (Join-Path $sourceRoot $relativePath) -Raw
        Assert-Contains $text 'Invoke-CoverageChild'
        if ($text -match 'admin-test-runner\.ps1|\$childCommand|\$template\.Replace|repoRootLiteral') {
            throw "$relativePath retains a generated child-wrapper implementation"
        }
    }

    Write-Host "`nAll shared coverage runner and caller regression tests passed successfully!"
}
finally {
    Reset-LogTailer
    if (Test-Path -LiteralPath $testDir) {
        Remove-Item -LiteralPath $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
