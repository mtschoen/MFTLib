$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/benchmark-report-refresh.ps1"

$failures = 0
$checks = 0
function Assert-Equal($Expected, $Actual, [string] $Name) {
    $script:checks++
    if ("$Expected" -cne "$Actual") {
        $script:failures++
        [Console]::Error.WriteLine("FAIL $Name`: expected [$Expected], got [$Actual]")
    }
}
function Assert-Contains([string] $Text, [string] $Expected, [string] $Name) {
    $script:checks++
    if (-not $Text.Contains($Expected)) {
        $script:failures++
        [Console]::Error.WriteLine("FAIL $Name`: missing [$Expected] in [$Text]")
    }
}

# A clock that only moves when the injected Sleep seam is called, so no test waits or reads real time.
function New-FakeClock {
    $clock = @{ Now = [DateTime]::new(2026, 11, 27, 4, 0, 0, [DateTimeKind]::Utc) }
    [pscustomobject]@{
        State = $clock
        GetNow = { $clock.Now }.GetNewClosure()
        Sleep = { param($Seconds) $clock.Now = $clock.Now.AddSeconds($Seconds) }.GetNewClosure()
    }
}
# Replays the samples in order, then repeats the last one. Each sample is (cpu, disk busy).
function New-FakeReader([object[]] $Samples) {
    $state = @{ Index = 0 }
    {
        $position = [Math]::Min($state.Index, $Samples.Count - 1)
        $state.Index++
        [pscustomobject]@{ CpuPercent = [double] $Samples[$position][0]; DiskBusyPercent = [double] $Samples[$position][1] }
    }.GetNewClosure()
}
function Invoke-Gate([object[]] $Samples, [int] $TimeoutMinutes = 30) {
    $clock = New-FakeClock
    Wait-BenchmarkQuietBox -ReadSample (New-FakeReader $Samples) -GetNow $clock.GetNow -Sleep $clock.Sleep `
        -TimeoutMinutes $TimeoutMinutes -QuietSeconds 60 -SampleIntervalSeconds 5 -CpuPercentLimit 15 -DiskBusyPercentLimit 10
}

# Quiet gate.
$result = Invoke-Gate @(, @(2, 1))
Assert-Equal $true $result.Quiet 'gate passes on a quiet box'
Assert-Equal 13 $result.SampleCount 'quiet window spans 13 samples at 5 s'
Assert-Equal 60 $result.WaitedSeconds 'quiet box waits exactly the window'
$result = Invoke-Gate @(, @(90, 1)) -TimeoutMinutes 2
Assert-Equal $false $result.Quiet 'gate times out on a busy box'
Assert-Equal 25 $result.SampleCount 'timeout samples every interval up to the limit'
Assert-Equal 25 $result.BusySampleCount 'every sample busy'
Assert-Equal 90 $result.PeakCpuPercent 'busy peak is recorded'
$result = Invoke-Gate @(, @(2, 55)) -TimeoutMinutes 1
Assert-Equal $false $result.Quiet 'disk activity alone keeps the gate closed'
Assert-Equal 55 $result.LastDiskBusyPercent 'last disk reading is recorded'
$samples = @(1..10 | ForEach-Object { , @(3, 2) }) + @(, @(90, 2)) + @(, @(4, 2))
$result = Invoke-Gate $samples
Assert-Equal $true $result.Quiet 'gate passes after a busy sample'
Assert-Equal 115 $result.WaitedSeconds 'one busy sample restarts the window'
Assert-Equal 4 $result.WindowPeakCpuPercent 'the busy sample is outside the reported window'
Assert-Equal 1 $result.BusySampleCount 'one busy sample counted'
$result = Invoke-Gate @(, @(15, 10))
Assert-Equal $true $result.Quiet 'samples exactly at both limits are quiet'
Assert-Equal 13 $result.SampleCount 'a sample at the limit does not restart the window'
Assert-Contains (Format-BenchmarkGateSummary -Result (Invoke-Gate @(, @(90, 1)) -TimeoutMinutes 1) -Volume 'D:') 'busy for 60 s on volume D:' 'busy summary'
Assert-Contains (Format-BenchmarkGateSummary -Result (Invoke-Gate @(, @(2, 1))) -Volume 'D:') 'quiet for 60 s on volume D:' 'quiet summary'

# Capture.
function New-CaptureFixture([string] $ReferenceBody) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('benchmark-report-test-' + [guid]::NewGuid().ToString('N'))
    $baselines = Join-Path $root 'baselines'
    New-Item -ItemType Directory -Force -Path $baselines | Out-Null
    [IO.File]::WriteAllText((Join-Path $baselines '2026-11-18-98df2955.txt'), $ReferenceBody)
    [pscustomobject]@{ Root = $root; Baselines = $baselines; Scratch = (Join-Path $root 'scratch') }
}
$allScenarios = @('exact-search', 'substring-search', 'enumeration-walk-sequential', 'enumeration-walk-parallel',
    'mft-enumeration-scan', 'mft-enumeration-rows', 'mft-enumeration-exact-name', 'mft-enumeration-substring-name')
$fullReference = ($allScenarios | ForEach-Object { "--- $_ ---`n  Median: 1 ms" }) -join "`n"
function New-FakeBenchmark([string[]] $FailCompare = @(), [string] $FailCommand = '') {
    $calls = [System.Collections.Generic.List[string]]::new()
    $runner = {
        param($Arguments)
        $calls.Add($Arguments -join ' ')
        if ($FailCommand -and $Arguments[0] -eq $FailCommand) { return [pscustomobject]@{ ExitCode = 1; Output = '' } }
        $outIndex = [array]::IndexOf($Arguments, '--out')
        if ($outIndex -ge 0) {
            Set-Content -LiteralPath $Arguments[$outIndex + 1] -Value "section-$($Arguments[0])`n" -NoNewline
        }
        $code = if ($Arguments[0] -eq 'compare' -and $FailCompare -contains $Arguments[3]) { 1 } else { 0 }
        [pscustomobject]@{ ExitCode = $code; Output = "$($Arguments -join ' ')" }
    }.GetNewClosure()
    [pscustomobject]@{ Calls = $calls; Run = $runner }
}
$clock = New-FakeClock
$fixture = New-CaptureFixture $fullReference
$benchmark = New-FakeBenchmark -FailCompare @('substring-search')
$capture = Invoke-BenchmarkReportCapture -BenchmarkPath 'unused' -BaselineDirectory $fixture.Baselines -ScratchDirectory $fixture.Scratch `
    -ShortSha 'abcd1234' -RunBenchmark $benchmark.Run -GetNow $clock.GetNow
$files = @(Get-ChildItem -LiteralPath $fixture.Baselines -File | ForEach-Object { $_.Name })
Assert-Equal '2026-11-27-abcd1234.txt' ($files -join ',') 'exactly one report remains, named by clock date and sha'
Assert-Equal "section-index`nsection-enumeration-walk`nsection-mft-enumeration`n" ([IO.File]::ReadAllText($capture.ReportPath)) 'sections are combined in recipe order'
$commands = @($benchmark.Calls | Where-Object { -not $_.StartsWith('compare ') })
Assert-Equal 3 $commands.Count 'three capture commands'
Assert-Contains $commands[0] 'index --synthetic 1000000 --iterations 5 --out' 'index arguments'
Assert-Contains $commands[1] 'enumeration-walk --directories 32768 --files-per-directory 1 --iterations 5 --out' 'walk arguments'
Assert-Contains $commands[2] 'mft-enumeration --synthetic 1000000 --iterations 5 --out' 'mft arguments'
Assert-Equal 8 (@($benchmark.Calls | Where-Object { $_.StartsWith('compare ') }).Count) 'every recipe scenario is compared'
Assert-Equal 'failed' (@($capture.Comparison | Where-Object { $_.Scenario -eq 'substring-search' })[0].Status) 'a failed compare is reported'
Assert-Equal 'pass' (@($capture.Comparison | Where-Object { $_.Scenario -eq 'exact-search' })[0].Status) 'a passing compare is reported'
Assert-Equal '7 passed; exceeded the limit: substring-search' (Format-BenchmarkComparisonSummary -Capture $capture) 'comparison summary'
Remove-Item -LiteralPath $fixture.Root -Recurse -Force

$fixture = New-CaptureFixture "--- exact-search ---`n  Median: 1 ms"
$benchmark = New-FakeBenchmark
$capture = Invoke-BenchmarkReportCapture -BenchmarkPath 'unused' -BaselineDirectory $fixture.Baselines -ScratchDirectory $fixture.Scratch `
    -ShortSha 'abcd1234' -RunBenchmark $benchmark.Run -GetNow $clock.GetNow
Assert-Equal 1 (@($benchmark.Calls | Where-Object { $_.StartsWith('compare ') }).Count) 'a scenario the reference lacks is not compared'
Assert-Contains (Format-BenchmarkComparisonSummary -Capture $capture) 'no reference: substring-search' 'missing reference is named'
Remove-Item -LiteralPath $fixture.Root -Recurse -Force

$fixture = New-CaptureFixture $fullReference
[IO.File]::WriteAllText((Join-Path $fixture.Baselines '2026-11-27-abcd1234.txt'), 'x')
Remove-Item -LiteralPath (Join-Path $fixture.Baselines '2026-11-18-98df2955.txt')
$null = Invoke-BenchmarkReportCapture -BenchmarkPath 'unused' -BaselineDirectory $fixture.Baselines -ScratchDirectory $fixture.Scratch `
    -ShortSha 'abcd1234' -RunBenchmark (New-FakeBenchmark).Run -GetNow $clock.GetNow
Assert-Equal 1 (@(Get-ChildItem -LiteralPath $fixture.Baselines -File).Count) 'a report with the same name is replaced in place'
Remove-Item -LiteralPath $fixture.Root -Recurse -Force

$fixture = New-CaptureFixture $fullReference
$threw = $false
try {
    $null = Invoke-BenchmarkReportCapture -BenchmarkPath 'unused' -BaselineDirectory $fixture.Baselines -ScratchDirectory $fixture.Scratch `
        -ShortSha 'abcd1234' -RunBenchmark (New-FakeBenchmark -FailCommand 'enumeration-walk').Run -GetNow $clock.GetNow
} catch { $threw = $true }
Assert-Equal $true $threw 'a failed benchmark command throws'
Assert-Equal '2026-11-18-98df2955.txt' ((Get-ChildItem -LiteralPath $fixture.Baselines -File | ForEach-Object { $_.Name }) -join ',') 'a failed capture leaves the committed report untouched'
Remove-Item -LiteralPath $fixture.Root -Recurse -Force

# Skip decision.
$bot = 'test-report-bot'
function Get-Decision($Head, $Authors) { (Get-BenchmarkRefreshDecision -ReportShortSha '98df2955' -HeadSha $Head -AuthorNames $Authors -BotName $bot).Run }
Assert-Equal $false (Get-Decision '98df295546f05aa456c2c677d5607fa8e701b324' @()) 'skip when the report already measures head'
Assert-Equal $false (Get-Decision 'ffff000000000000000000000000000000000000' @($bot, $bot)) 'skip when only bot commits follow'
Assert-Equal $true (Get-Decision 'ffff000000000000000000000000000000000000' @($bot, 'Matt Schoen')) 'run when a person committed after the report'
Assert-Equal $true (Get-Decision 'ffff000000000000000000000000000000000000' $null) 'run when the measured revision is unknown'
Assert-Equal $false (Get-Decision 'ffff000000000000000000000000000000000000' @()) 'skip when no commit follows the measured revision'
Assert-Equal $true (Get-Decision 'ffff000000000000000000000000000000000000' @('Test-Report-Bot')) 'author comparison is exact'

# Issue helper.
function New-FakeApi($OpenIssues = @()) {
    $calls = [System.Collections.Generic.List[string]]::new()
    $bodies = [System.Collections.Generic.List[object]]::new()
    $api = {
        param($Method, $Path, $Body)
        $calls.Add("$Method $Path")
        $bodies.Add($Body)
        if ($Method -eq 'GET') {
            if ($Path -match 'page=(\d+)') {
                $start = ([int] $Matches[1] - 1) * 50
                if ($start -lt $OpenIssues.Count) { return @($OpenIssues[$start..([Math]::Min($start + 49, $OpenIssues.Count - 1))]) }
            }
            return @()
        }
        if ($Method -eq 'POST' -and $Path -eq '/issues') { return [pscustomobject]@{ number = 77 } }
        $null
    }.GetNewClosure()
    [pscustomobject]@{ Invoke = $api; Calls = $calls; Bodies = $bodies }
}
$title = 'Benchmark needed when the box is quiet'
$api = New-FakeApi
$outcome = Publish-BenchmarkNeededIssue -InvokeApi $api.Invoke -Observation 'peak CPU 80 percent' -Date '2026-11-27' -RunUrl 'https://forge.example/run/1'
Assert-Equal 'created' $outcome.Action 'no open issue creates one'
Assert-Equal 'POST /issues' $api.Calls[$api.Calls.Count - 1] 'create call'
$created = $api.Bodies[$api.Bodies.Count - 1]
Assert-Equal $title $created.title 'fixed title'
foreach ($heading in @('## Mechanism', '## Evidence', '## Detection', '## Done when', 'peak CPU 80 percent')) {
    Assert-Contains $created.body $heading "issue body $heading"
}
$existing = @([pscustomobject]@{ number = 5; title = 'Benchmark needed when the box is quiet soon' }, [pscustomobject]@{ number = 6; title = $title })
$api = New-FakeApi $existing
$outcome = Publish-BenchmarkNeededIssue -InvokeApi $api.Invoke -Observation 'peak CPU 80 percent' -Date '2026-11-28'
Assert-Equal 'commented' $outcome.Action 'an open issue gets a comment'
Assert-Equal 6 $outcome.Number 'the exact-title issue is the one commented'
Assert-Equal 0 (@($api.Calls | Where-Object { $_ -eq 'POST /issues' }).Count) 'no second issue is opened'
Assert-Contains $api.Bodies[$api.Bodies.Count - 1].body '2026-11-28' 'comment carries the date'
$near = @([pscustomobject]@{ number = 5; title = 'benchmark needed when the box is quiet' })
Assert-Equal 'created' (Publish-BenchmarkNeededIssue -InvokeApi (New-FakeApi $near).Invoke -Observation 'x' -Date '2026-11-27').Action 'a near-miss title is not the marker'
$crowded = @(1..50 | ForEach-Object { [pscustomobject]@{ number = $_; title = "other $_" } }) + @([pscustomobject]@{ number = 90; title = $title })
Assert-Equal 90 (Publish-BenchmarkNeededIssue -InvokeApi (New-FakeApi $crowded).Invoke -Observation 'x' -Date '2026-11-27').Number 'the issue is found on a later page'
$api = New-FakeApi @([pscustomobject]@{ number = 6; title = $title })
$outcome = Close-BenchmarkNeededIssue -InvokeApi $api.Invoke -ReportName '2026-11-27-abcd1234.txt'
Assert-Equal 'closed' $outcome.Action 'a capture closes the open issue'
Assert-Equal 'PATCH /issues/6' $api.Calls[$api.Calls.Count - 1] 'close call'
Assert-Equal 'closed' $api.Bodies[$api.Bodies.Count - 1].state 'close state'
Assert-Contains $api.Bodies[$api.Bodies.Count - 2].body '2026-11-27-abcd1234.txt' 'close comment names the report'
Assert-Equal 0 (@($api.Bodies | Where-Object { $_ -and $_.body -match '(?i)\b(close[sd]?|fix(e[sd])?|resolve[sd]?)\s+#' }).Count) 'no close keyword before an issue reference'
$api = New-FakeApi
Assert-Equal 'none' (Close-BenchmarkNeededIssue -InvokeApi $api.Invoke -ReportName 'x.txt').Action 'closing with no open issue does nothing'
Assert-Equal 0 (@($api.Calls | Where-Object { -not $_.StartsWith('GET ') }).Count) 'no write when no issue is open'

# Orchestration.
function Invoke-FakeRefresh([bool] $Quiet, [string] $PublishOutcome = 'published') {
    $events = [System.Collections.Generic.List[string]]::new()
    $gate = [pscustomobject]@{ Quiet = $Quiet; SampleCount = 13; BusySampleCount = 0; WaitedSeconds = 60; QuietSeconds = 60
        CpuPercentLimit = 15; DiskBusyPercentLimit = 10; PeakCpuPercent = 50; PeakDiskBusyPercent = 3
        WindowPeakCpuPercent = 4; WindowPeakDiskBusyPercent = 2; LastCpuPercent = 4; LastDiskBusyPercent = 2 }
    $capture = [pscustomobject]@{ ReportName = '2026-11-27-abcd1234.txt'; ReferenceName = '2026-11-18-98df2955.txt'
        Comparison = @([pscustomobject]@{ Scenario = 'exact-search'; Status = 'pass'; Output = '' }) }
    $waitForQuiet = { $gate }.GetNewClosure()
    $captureStep = { $events.Add('capture'); $capture }.GetNewClosure()
    $publishStep = { param($Message) $events.Add("publish:$Message"); $PublishOutcome }.GetNewClosure()
    $fileIssueStep = { param($Observation) $events.Add("issue:$Observation") }.GetNewClosure()
    $closeIssueStep = { param($ReportName) $events.Add("close:$ReportName") }.GetNewClosure()
    $result = Invoke-BenchmarkRefresh -Volume 'C:' -SourceShortSha 'abcd1234' -WaitForQuiet $waitForQuiet `
        -Capture $captureStep -Publish $publishStep -FileIssue $fileIssueStep -CloseIssue $closeIssueStep
    [pscustomobject]@{ Result = $result; Events = @($events) }
}
$run = Invoke-FakeRefresh $false
Assert-Equal 'busy' $run.Result.Outcome 'a busy box ends as busy without throwing'
Assert-Equal 1 $run.Events.Count 'a busy box only files the issue'
Assert-Contains $run.Events[0] 'issue:busy for' 'the issue records the busy observation'
$run = Invoke-FakeRefresh $true
Assert-Equal 'published' $run.Result.Outcome 'a quiet box publishes'
Assert-Equal 'capture' $run.Events[0] 'capture runs first'
Assert-Contains $run.Events[1] 'Quiet gate: quiet for 60 s on volume C:' 'the commit message records the gate observation'
Assert-Contains $run.Events[1] 'Compare against 2026-11-18-98df2955.txt: 1 passed' 'the commit message records the comparison'
Assert-Equal 'close:2026-11-27-abcd1234.txt' $run.Events[2] 'publication closes the issue'
$run = Invoke-FakeRefresh $true 'main-advanced'
Assert-Equal 2 $run.Events.Count 'a lost race publishes nothing and keeps the issue'

# Repository hygiene for the new scripts.
foreach ($name in @('benchmark-quiet-gate.ps1', 'benchmark-report-capture.ps1', 'benchmark-report-issue.ps1',
        'benchmark-report-refresh.ps1', 'benchmark-report.tests.ps1', 'benchmark-report-workflow.tests.sh', '../.gitea/workflows/benchmark-report.yml')) {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $name))
    Assert-Equal $false $text.Contains([char] 0x2014) "$name has no em dash"
}

if ($failures -gt 0) {
    [Console]::Error.WriteLine("$failures of $checks checks failed")
    exit 1
}
Write-Host "benchmark-report tests passed: $checks checks"
