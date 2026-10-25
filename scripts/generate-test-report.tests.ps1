$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/generate-test-report.ps1"
$directory = Join-Path $PSScriptRoot '../.lane/fixtures'
New-Item -ItemType Directory -Force $directory | Out-Null
$trx = Join-Path $directory 'tests.trx'
$coverage = Join-Path $directory 'coverage.xml'
$native = Join-Path $directory 'summary.txt'
$output = Join-Path $directory 'TEST-REPORT.md'
function New-TrxText([int]$Passed, [int]$Failed, [int]$NotExecuted, [int]$NotExecutedCounter) {
    $total = $Passed + $Failed + $NotExecuted
    $outcomes = @(@('Passed') * $Passed) + @(@('Failed') * $Failed) + @(@('NotExecuted') * $NotExecuted)
    $results = ($outcomes | ForEach-Object { "<UnitTestResult outcome=`"$_`"/>" }) -join ''
    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest storage="/build/MFTLib.Tests.dll"/></TestDefinitions>' +
        "<Results>$results</Results><ResultSummary><Counters total=`"$total`" executed=`"$($total - $NotExecuted)`" passed=`"$Passed`" failed=`"$Failed`" notExecuted=`"$NotExecutedCounter`"/></ResultSummary></TestRun>"
}
New-TrxText 9 1 2 2 | Set-Content $trx
'<coverage line-rate="0.925" branch-rate="0.875"/>' | Set-Content $coverage
'lines: 81.2% (812 out of 1000)' | Set-Content $native
$arguments = @{
    WindowsTrx = @($trx); WindowsCobertura = @($coverage)
    LinuxTrx = @($trx); LinuxCobertura = @($coverage); LinuxNativeSummary = $native
    Commit = '0123456789abcdef0123456789abcdef01234567'
    RunId = '42'
    GeneratedAt = '2026-10-24T12:34:56Z'; OutputPath = $output
}
function Assert-Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Missing report content: $Expected" }
}
function Assert-Rejected([scriptblock]$Action) {
    $caught = $false
    try { & $Action } catch { $caught = $true }
    if (-not $caught) { throw 'Invalid report input was accepted' }
}
# The same patterns as AgentInstructionsTests.PrivateContentPatterns, assembled so this file passes them too.
$privateDomain = ('fleet', 'sticktoitive', 'net') -join '.'
$privatePatterns = @(
    '(?i)[a-z]:[\\/]+Users(?:[\\/]|\b)',
    '(?i)/mnt/[a-z]/Users(?:/|\b)',
    ('(?i)' + [regex]::Escape($privateDomain)),
    ('(?i)\b' + 'chon' + 'kers\b'),
    ('(?i)\b' + 'llama' + 'box\b'),
    ([regex]::Escape('~/' + 'schoen-lab/'))
)
$culture = [Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'
    & "$PSScriptRoot/generate-test-report.ps1" @arguments
    $first = [IO.File]::ReadAllText($output)
    foreach ($expected in @('generated on main by CI', 'must not be edited in a pull request',
        '## Windows unelevated', '## Linux', '| MFTLib.Tests | 12 | 9 | 1 | 2 |',
        '92.50% | 87.50%', 'lines: 81.2% (812 out of 1000)',
        'Source commit: 0123456789abcdef0123456789abcdef01234567',
        '2026-10-24T12:34:56Z', 'Workflow run: 42')) {
        Assert-Contains $first $expected
    }
    & "$PSScriptRoot/generate-test-report.ps1" @arguments
    if ($first -cne [IO.File]::ReadAllText($output)) { throw 'Report is not deterministic' }
    if ([IO.File]::ReadAllBytes($output).Length -ge 4096) { throw 'Report exceeds size budget' }
    if ($first.Contains([char]0x2014)) { throw 'Report contains an em dash' }
    foreach ($pattern in $privatePatterns) {
        if ($first -match $pattern) { throw "Report matches a private-content pattern: $pattern" }
    }
    if ($first -match 'https?://') { throw 'Report embeds a link' }
    foreach ($negative in @(('C:' + '\Users\fixture'), "https://$privateDomain/team/repo", ('llama' + 'box'))) {
        if (-not ($privatePatterns | Where-Object { $negative -match $_ })) { throw 'Private-content negative control not detected' }
    }
    foreach ($invalid in @('<broken', '<coverage/>', '<coverage line-rate="1.01" branch-rate="0.5"/>',
        '<coverage line-rate="NaN" branch-rate="0.5"/>')) {
        $invalid | Set-Content $coverage
        Assert-Rejected { & "$PSScriptRoot/generate-test-report.ps1" @arguments }
    }
    '<coverage line-rate="0.925" branch-rate="0.875"/>' | Set-Content $coverage
    $validTrx = Get-Content $trx -Raw
    # Observed CI shapes: the notExecuted counter is zero while results carry NotExecuted outcomes.
    foreach ($shape in @(@(3419, 0, 19, 3438), @(3277, 0, 79, 3356))) {
        New-TrxText $shape[0] $shape[1] $shape[2] 0 | Set-Content $trx
        & "$PSScriptRoot/generate-test-report.ps1" @arguments
        Assert-Contains ([IO.File]::ReadAllText($output)) "| MFTLib.Tests | $($shape[3]) | $($shape[0]) | $($shape[1]) | $($shape[2]) |"
    }
    foreach ($invalid in @('<TestRun/>', $validTrx.Replace('total="12"', 'total="bad"'),
        $validTrx.Replace('total="12"', 'total="13"'), $validTrx.Replace('passed="9"', 'passed="8"'),
        $validTrx.Replace('failed="1"', 'failed="0"'), $validTrx.Replace('outcome="Failed"', 'outcome="Timeout"'))) {
        $invalid | Set-Content $trx
        Assert-Rejected { & "$PSScriptRoot/generate-test-report.ps1" @arguments }
    }
    $validTrx | Set-Content $trx
    $arguments.LinuxNativeSummary = ''
    & "$PSScriptRoot/generate-test-report.ps1" @arguments
    if ([IO.File]::ReadAllText($output).Contains('Native')) { throw 'Invented native coverage' }
    $arguments.WindowsTrx = @($trx, $trx)
    Assert-Rejected { & "$PSScriptRoot/generate-test-report.ps1" @arguments }
    $arguments.WindowsTrx = @($trx)
    $arguments.RunId = 'run-42'
    Assert-Rejected { & "$PSScriptRoot/generate-test-report.ps1" @arguments }
    $arguments.RunId = '42'
    $arguments.Commit = 'invalid'
    Assert-Rejected { & "$PSScriptRoot/generate-test-report.ps1" @arguments }
} finally {
    [Threading.Thread]::CurrentThread.CurrentCulture = $culture
}
Write-Host 'Test report generator self-tests passed.'
