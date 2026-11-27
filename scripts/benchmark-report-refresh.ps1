<#
.SYNOPSIS
Runs the benchmark report refresh: decide whether main moved, wait for a quiet box, capture, publish.
.DESCRIPTION
Action Decide writes run=true or run=false to GITHUB_OUTPUT: false when the committed report already measures main's
head, or when every commit since the measured revision is the report bot's own (report commits never trigger
another capture). Action Refresh waits for a quiet box (scripts/benchmark-quiet-gate.ps1). A box that never quiets
files or updates the "benchmark needed" issue and ends successfully. A quiet box is captured
(scripts/benchmark-report-capture.ps1), committed as the report bot and pushed to main following the TEST-REPORT
publication rules (nothing is committed when main has advanced), and a published report closes the issue
(scripts/benchmark-report-issue.ps1). The Benchmark executable must already be built Release x64.
.PARAMETER Action
Decide or Refresh.
.PARAMETER RepositoryRoot
The checkout; defaults to the parent of this script's folder.
.PARAMETER BotName
The report bot's git author name; defaults to test-report-bot.
.PARAMETER Force
Decide returns run=true even when main has not moved (manual dispatch).
.PARAMETER TimeoutMinutes
.PARAMETER QuietSeconds
.PARAMETER SampleIntervalSeconds
.PARAMETER CpuPercentLimit
.PARAMETER DiskBusyPercentLimit
The quiet gate parameters, passed through unchanged; see scripts/benchmark-quiet-gate.ps1 for the defaults.
#>
[CmdletBinding()]
param(
    [ValidateSet('Decide', 'Refresh')][string] $Action = 'Decide',
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $BotName = 'test-report-bot',
    [switch] $Force,
    [int] $TimeoutMinutes = 30,
    [int] $QuietSeconds = 60,
    [int] $SampleIntervalSeconds = 5,
    [double] $CpuPercentLimit = 15,
    [double] $DiskBusyPercentLimit = 10
)

$ErrorActionPreference = 'Stop'
# The dot-sourced gate script declares parameters of the same names, which would overwrite these on load.
$gateSettings = @{
    TimeoutMinutes = $TimeoutMinutes; QuietSeconds = $QuietSeconds; SampleIntervalSeconds = $SampleIntervalSeconds
    CpuPercentLimit = $CpuPercentLimit; DiskBusyPercentLimit = $DiskBusyPercentLimit
}
. "$PSScriptRoot/benchmark-quiet-gate.ps1"
. "$PSScriptRoot/benchmark-report-capture.ps1"
. "$PSScriptRoot/benchmark-report-issue.ps1"

function Get-BenchmarkRefreshDecision {
    # AuthorNames lists the author of every commit after the measured revision, or $null when that revision is unknown.
    param(
        [Parameter(Mandatory)][string] $ReportShortSha,
        [Parameter(Mandatory)][string] $HeadSha,
        $AuthorNames,
        [Parameter(Mandatory)][string] $BotName
    )
    if ($HeadSha.StartsWith($ReportShortSha, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Run = $false; Reason = 'the committed report already measures the head of main' }
    }
    if ($null -eq $AuthorNames) {
        return [pscustomobject]@{ Run = $true; Reason = 'the measured revision is not in the history of main' }
    }
    $authors = @($AuthorNames)
    if ($authors.Count -eq 0) {
        return [pscustomobject]@{ Run = $false; Reason = 'main has no commit after the measured revision' }
    }
    if (@($authors | Where-Object { $_ -cne $BotName }).Count -eq 0) {
        return [pscustomobject]@{ Run = $false; Reason = 'only report bot commits follow the measured revision' }
    }
    [pscustomobject]@{ Run = $true; Reason = 'main has commits after the measured revision' }
}

function Get-CommittedBenchmarkReportSha {
    param([Parameter(Mandatory)][string] $BaselineDirectory)
    $reports = @(Get-ChildItem -LiteralPath $BaselineDirectory -Filter '*.txt' -File)
    if ($reports.Count -ne 1) { throw 'Expected exactly one committed release report.' }
    if ($reports[0].Name -notmatch '^\d{4}-\d{2}-\d{2}-(?<sha>[0-9a-f]{7,40})\.txt$') {
        throw "The committed report name does not carry a revision: $($reports[0].Name)"
    }
    $Matches['sha']
}

function New-BenchmarkCommitMessage {
    param(
        [Parameter(Mandatory)] $Capture,
        [Parameter(Mandatory)][string] $SourceShortSha,
        [Parameter(Mandatory)][string] $GateSummary
    )
    $lines = @(
        "chore: refresh the release benchmark report from $SourceShortSha",
        '',
        "Report: Benchmark/baselines/$($Capture.ReportName)",
        "Quiet gate: $GateSummary",
        "Compare against $($Capture.ReferenceName): $(Format-BenchmarkComparisonSummary -Capture $Capture)"
    )
    $lines -join "`n"
}

function Invoke-BenchmarkRefresh {
    param(
        [Parameter(Mandatory)][scriptblock] $WaitForQuiet,
        [Parameter(Mandatory)][scriptblock] $Capture,
        [Parameter(Mandatory)][scriptblock] $Publish,
        [Parameter(Mandatory)][scriptblock] $FileIssue,
        [Parameter(Mandatory)][scriptblock] $CloseIssue,
        [Parameter(Mandatory)][string] $Volume,
        [Parameter(Mandatory)][string] $SourceShortSha
    )
    $gate = & $WaitForQuiet
    $gateSummary = Format-BenchmarkGateSummary -Result $gate -Volume $Volume
    if (-not $gate.Quiet) {
        & $FileIssue $gateSummary
        return [pscustomobject]@{ Outcome = 'busy'; Summary = $gateSummary }
    }
    $captureResult = & $Capture
    $message = New-BenchmarkCommitMessage -Capture $captureResult -SourceShortSha $SourceShortSha -GateSummary $gateSummary
    $outcome = & $Publish $message
    if ($outcome -eq 'published') { & $CloseIssue $captureResult.ReportName }
    [pscustomobject]@{ Outcome = $outcome; Summary = $gateSummary; ReportName = $captureResult.ReportName }
}

function Invoke-GitChecked {
    param([Parameter(Mandatory)][string] $RepositoryRoot, [Parameter(Mandatory)][string[]] $Arguments)
    $output = & git -C $RepositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
    $output
}

function Publish-BenchmarkReportCommit {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $SourceCommit,
        [Parameter(Mandatory)][string] $Message,
        [Parameter(Mandatory)][string] $BotName,
        [Parameter(Mandatory)][string] $ServerUrl
    )
    Invoke-GitChecked $RepositoryRoot @('add', '-A', '--', 'Benchmark/baselines') | Out-Null
    & git -C $RepositoryRoot diff --cached --quiet -- Benchmark/baselines
    if ($LASTEXITCODE -eq 0) { return 'unchanged' }
    if ($LASTEXITCODE -ne 1) { throw 'Cannot compare the staged report.' }
    Invoke-GitChecked $RepositoryRoot @('fetch', 'origin', 'main') | Out-Null
    if ((Invoke-GitChecked $RepositoryRoot @('rev-parse', 'FETCH_HEAD')) -ne $SourceCommit) { return 'main-advanced' }
    # The bot's registered address is the server host without its service label.
    $domain = ([Uri] $ServerUrl).Host.Split('.', 2)[1]
    $messagePath = Join-Path ([IO.Path]::GetTempPath()) ("benchmark-commit-" + [guid]::NewGuid().ToString('N') + '.txt')
    [IO.File]::WriteAllText($messagePath, $Message + "`n", (New-Object Text.UTF8Encoding($false)))
    try {
        Invoke-GitChecked $RepositoryRoot @('-c', "user.name=$BotName", '-c', "user.email=$BotName@$domain", 'commit', '-F', $messagePath) | Out-Null
    } finally {
        Remove-Item -LiteralPath $messagePath -Force -ErrorAction SilentlyContinue
    }
    & git -C $RepositoryRoot push origin HEAD:main
    if ($LASTEXITCODE -ne 0) {
        # A concurrent update of main makes the ordinary push fail safely; the next run measures the newer head.
        Invoke-GitChecked $RepositoryRoot @('fetch', 'origin', 'main') | Out-Null
        if ((Invoke-GitChecked $RepositoryRoot @('rev-parse', 'FETCH_HEAD')) -ne $SourceCommit) { return 'main-advanced' }
        throw 'Cannot publish the benchmark report.'
    }
    'published'
}

function Write-BenchmarkWorkflowOutput {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Value)
    if ($env:GITHUB_OUTPUT) { "$Name=$Value" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
}

# Dot-sourcing (the tests) loads the functions only.
if ($MyInvocation.InvocationName -ne '.') {
    $baselineDirectory = Join-Path $RepositoryRoot 'Benchmark/baselines'
    $sourceCommit = (Invoke-GitChecked $RepositoryRoot @('rev-parse', 'HEAD')).Trim()
    if ($Action -eq 'Decide') {
        $reportSha = Get-CommittedBenchmarkReportSha -BaselineDirectory $baselineDirectory
        $logOutput = & git -C $RepositoryRoot log --format=%an "$reportSha..HEAD" 2>$null
        $authors = if ($LASTEXITCODE -eq 0) { @($logOutput | Where-Object { $_ }) } else { $null }
        $decision = Get-BenchmarkRefreshDecision -ReportShortSha $reportSha -HeadSha $sourceCommit -AuthorNames $authors -BotName $BotName
        $run = $decision.Run -or $Force.IsPresent
        Write-Host "Refresh $(if ($run) { 'needed' } else { 'skipped' }): $($decision.Reason)$(if ($Force) { ' (forced)' })."
        Write-BenchmarkWorkflowOutput -Name 'run' -Value $run.ToString().ToLowerInvariant()
        exit 0
    }

    $serverUrl = $env:SERVER_URL
    $temporaryDirectory = [IO.Path]::GetTempPath()
    $volume = Get-BenchmarkTemporaryVolume -Path $temporaryDirectory
    $shortSha = (Invoke-GitChecked $RepositoryRoot @('rev-parse', '--short=8', 'HEAD')).Trim()
    $issueApi = New-BenchmarkIssueApi -ServerUrl $serverUrl -Repository $env:REPOSITORY -Token $env:GITEA_TOKEN
    $date = [DateTime]::UtcNow.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $runUrl = if ($env:WORKFLOW_RUN) { "$($serverUrl.TrimEnd('/'))/$($env:REPOSITORY)/actions/runs/$($env:WORKFLOW_RUN)" } else { '' }
    $scratch = Join-Path $temporaryDirectory ('mftlib-release-benchmark-' + [guid]::NewGuid().ToString('N'))
    try {
        $result = Invoke-BenchmarkRefresh -Volume $volume -SourceShortSha $shortSha `
            -WaitForQuiet {
                Wait-BenchmarkQuietBox -ReadSample { Read-BenchmarkLoadSample -Volume $volume } @gateSettings
            } `
            -Capture {
                Invoke-BenchmarkReportCapture -BenchmarkPath (Join-Path $RepositoryRoot 'Benchmark/bin/x64/Release/net10.0/Benchmark.exe') `
                    -BaselineDirectory $baselineDirectory -ScratchDirectory $scratch -ShortSha $shortSha
            } `
            -Publish {
                param($Message)
                Publish-BenchmarkReportCommit -RepositoryRoot $RepositoryRoot -SourceCommit $sourceCommit -Message $Message `
                    -BotName $BotName -ServerUrl $serverUrl
            } `
            -FileIssue {
                param($Observation)
                Publish-BenchmarkNeededIssue -InvokeApi $issueApi -Observation $Observation -Date $date -RunUrl $runUrl | Out-Null
            } `
            -CloseIssue {
                param($ReportName)
                Close-BenchmarkNeededIssue -InvokeApi $issueApi -ReportName $ReportName | Out-Null
            }
    } finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Benchmark refresh outcome: $($result.Outcome). Quiet gate: $($result.Summary)"
    Write-BenchmarkWorkflowOutput -Name 'outcome' -Value $result.Outcome
    exit 0
}
