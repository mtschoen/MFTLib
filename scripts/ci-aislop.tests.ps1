$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$temporaryDirectory = Join-Path $sourceRoot ('.aislop/preflight/tests-' + [guid]::NewGuid().ToString('N'))
$repository = Join-Path $temporaryDirectory 'repository with spaces'
$checkout = Join-Path $temporaryDirectory 'scanner with spaces'
$gate = Join-Path $repository 'scripts/ci-aislop.ps1'
$pin = Join-Path $repository '.aislop/fork-commit'
$cli = Join-Path $checkout 'dist/cli.js'
$gitExecutable = @(Get-Command git -CommandType Application)[0].Source
$nodeExecutable = @(Get-Command node -CommandType Application)[0].Source
$savedPath = $env:PATH
$savedCheckout = $env:AISLOP_HOME
$savedExitCode = $global:LASTEXITCODE
$originalLocation = (Get-Location).Path
$failures = [Collections.Generic.List[string]]::new()
$checks = 0

function Assert-True([bool] $Condition, [string] $Label) {
    $script:checks++
    if (-not $Condition) { $script:failures.Add($Label) }
}

function Invoke-FixtureGit {
    & $script:gitExecutable -C $script:checkout -c user.name=Fixture -c user.email=fixture@example.invalid `
        -c commit.gpgsign=false -c core.hooksPath=disabled-hooks @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $args" }
}

function Invoke-Gate {
    param([string] $ReportPath = '', [string] $EvidenceLogPath = '', [switch] $EntryPoint)
    $messages = [Collections.Generic.List[string]]::new()
    $exitCode = 0
    $errorMessage = ''
    try {
        if ($EntryPoint) {
            $parameters = @('-NoProfile', '-File', $script:gate)
            if ($ReportPath) { $parameters += @('-ReportPath', $ReportPath) }
            & (Join-Path $PSHOME 'pwsh.exe') @parameters |
                ForEach-Object { $messages.Add([string] $_) }
            $exitCode = $LASTEXITCODE
        } else {
            & {
                Invoke-AislopGate -RepositoryRoot $script:repository -ReportPath $ReportPath -EvidenceLogPath $EvidenceLogPath
            } 6>&1 | ForEach-Object {
                if ($_ -is [Management.Automation.InformationRecord]) {
                    $messages.Add($_.ToString())
                } else {
                    $exitCode = [int] $_
                }
            }
        }
    } catch {
        $errorMessage = $_.Exception.Message
        $exitCode = 1
    }
    [pscustomobject]@{
        Output = $messages -join [Environment]::NewLine
        Error = $errorMessage
        ExitCode = $exitCode
    }
}

$savedFixture = $env:MFTLIB_AISLOP_FIXTURE
$savedNativePreference = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false
$savedScan = $null
$fixtureState = Join-Path $temporaryDirectory 'scanner-state.json'

function Set-FixtureState {
    param([string] $Report, [int] $ExitCode = 0, [int] $VersionExit = 0)
    @{
        report = $Report
        exitCode = $ExitCode
        versionExit = $VersionExit
        repository = $script:repository
    } | ConvertTo-Json | Set-Content -LiteralPath $script:fixtureState -Encoding utf8
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path $gate), (Split-Path $pin), (Split-Path $cli) | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot/ci-aislop.ps1" -Destination $gate
    Set-Content -LiteralPath $cli -Encoding utf8 -Value @'
const fs = require('node:fs');
const path = require('node:path');
const state = JSON.parse(fs.readFileSync(process.env.MFTLIB_AISLOP_FIXTURE, 'utf8'));
if (process.argv[2] === '--version') {
    console.log('fixture-scanner-1.0');
    process.exitCode = state.versionExit;
} else if (process.argv.slice(2).join(' ') === 'ci . --json') {
    if (path.resolve(process.cwd()) !== path.resolve(state.repository)) {
        throw new Error('Scanner must run in the target repository');
    }
    process.stdout.write(state.report);
    process.exitCode = state.exitCode;
} else {
    throw new Error('Unexpected scanner arguments');
}
'@
    Invoke-FixtureGit init --quiet
    Set-Content -LiteralPath (Join-Path $checkout 'source.txt') -Value 'fixture source'
    Set-Content -LiteralPath (Join-Path $checkout '.gitignore') -Value '/dist/'
    Invoke-FixtureGit add source.txt .gitignore
    Invoke-FixtureGit commit --quiet -m 'Fixture scanner'
    $expectedCommit = Invoke-FixtureGit rev-parse HEAD
    Set-Content -LiteralPath $pin -Value $expectedCommit

    $env:AISLOP_HOME = $checkout
    $env:MFTLIB_AISLOP_FIXTURE = $fixtureState
    $env:PATH = @((Split-Path $gitExecutable), (Split-Path $nodeExecutable)) -join [IO.Path]::PathSeparator
    Assert-True ($null -eq (Get-Command aislop -ErrorAction SilentlyContinue)) 'aislop must be unavailable'
    . $gate
    $savedScan = (Get-Item Function:Invoke-AislopScan).ScriptBlock
    Set-Item Function:Invoke-AislopScan {
        param([string] $Cli, [string] $RepositoryRoot, [string] $ReportPath)
        Assert-True ($Cli -eq $script:cli) 'Gate selects the validated built CLI'
        Assert-True ($RepositoryRoot -eq $script:repository) 'Gate selects its own repository'
        $state = Get-Content -LiteralPath $script:fixtureState -Raw | ConvertFrom-Json
        [IO.File]::WriteAllText($ReportPath, [string] $state.report)
        return [int] $state.exitCode
    }
    $goodReport = '{"score":100,"summary":{"files":12},"diagnostics":[]}'
    Set-FixtureState $goodReport
    $result = Invoke-Gate -EntryPoint
    Assert-True ($result.ExitCode -eq 0 -and -not $result.Error) 'Executable gate succeeds without a global aislop launcher'
    Assert-True ($result.Output.Contains('fixture-scanner-1.0')) 'Version comes from selected checkout'
    Assert-True ($result.Output.Contains($expectedCommit)) 'Provenance includes required commit'
    $report = Join-Path $repository '.aislop/preflight/report.json'
    Assert-True ([IO.File]::ReadAllText($report).TrimEnd([char[]] "`r`n") -ceq $goodReport) 'Default report preserves scanner JSON'

    $explicitReport = Join-Path $temporaryDirectory 'runner temp/aislop-report.json'
    $result = Invoke-Gate -ReportPath $explicitReport
    Assert-True ($result.ExitCode -eq 0 -and -not $result.Error) 'Explicit report path and selected checkout succeed'
    Assert-True ((Get-Content -LiteralPath $explicitReport -Raw) -ceq $goodReport) 'Explicit report is preserved'
    $result = Invoke-Gate -ReportPath 'relative reports/report.json'
    Assert-True ($result.ExitCode -eq 0 -and -not $result.Error) 'Relative report succeeds'
    Assert-True (Test-Path -LiteralPath (Join-Path $repository 'relative reports/report.json')) 'Relative report is repository-relative'
    $evidenceLog = Join-Path $temporaryDirectory 'evidence-log.txt'
    $result = Invoke-Gate -ReportPath $explicitReport -EvidenceLogPath $evidenceLog
    $evidenceLines = @(Get-Content -LiteralPath $evidenceLog)
    Assert-True ($result.ExitCode -eq 0 -and $evidenceLines.Count -eq 2) 'Evidence log receives exactly the provenance and score lines'
    Assert-True ($evidenceLines[0].Contains('fixture-scanner-1.0') -and $evidenceLines[0].Contains($expectedCommit) -and $evidenceLines[0].Contains('CI pin')) 'Evidence log carries checkout version, commit and CI pin'
    Assert-True ($evidenceLines[1] -ceq 'aislop score 100 over 12 files') 'Evidence log carries score and file count'

    foreach ($invalidPin in @(('a' * 39), ('A' * 40), ('g' * 40), "$expectedCommit`nextra")) {
        Set-Content -LiteralPath $pin -Value $invalidPin
        $result = Invoke-Gate
        Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains('lowercase 40-character')) 'Malformed pin fails before launch'
    }
    Set-Content -LiteralPath $pin -Value $expectedCommit

    $sourceMarker = Join-Path $checkout 'source.txt'
    Set-Content -LiteralPath $sourceMarker -Value 'dirty source'
    $result = Invoke-Gate
    Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains('modified tracked files')) 'Tracked dirt is rejected'
    Set-Content -LiteralPath $sourceMarker -Value 'fixture source'

    $savedCli = Join-Path $temporaryDirectory 'saved-cli.js'
    Move-Item -LiteralPath $cli -Destination $savedCli
    $result = Invoke-Gate
    Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains('not built')) 'Missing generated CLI is rejected'
    Move-Item -LiteralPath $savedCli -Destination $cli

    Set-FixtureState $goodReport -VersionExit 9
    $result = Invoke-Gate
    Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains('could not report its version')) 'Version command failure is rejected'

    $reportCases = @(
        @{ Name = 'empty'; Text = ''; Error = 'empty'; Status = 1 },
        @{ Name = 'malformed'; Text = '{bad'; Error = 'invalid JSON'; Status = 1 },
        @{ Name = 'null'; Text = 'null'; Error = 'null JSON'; Status = 1 },
        @{ Name = 'missing count'; Text = '{}'; Error = 'only scanned 0 files'; Status = 1 },
        @{ Name = 'nine'; Text = '{"score":100,"summary":{"files":9}}'; Error = 'only scanned 9 files'; Status = 1 },
        @{ Name = 'ten'; Text = '{"score":100,"summary":{"files":10}}'; Error = ''; Status = 0 }
    )
    foreach ($case in $reportCases) {
        Set-FixtureState $case.Text
        $result = Invoke-Gate -ReportPath $explicitReport
        Assert-True ($result.ExitCode -eq $case.Status -and $result.Error.Contains($case.Error)) $case.Name
        $raw = [IO.File]::ReadAllText($explicitReport)
        Assert-True ($raw -ceq $case.Text) "$($case.Name) raw stdout retained"
        Assert-True ((Get-Location).Path -eq $originalLocation) "$($case.Name) restores caller location"
    }

    $scoringReport = @{
        score = 90
        summary = @{ files = 12 }
        diagnostics = @(
            @{ engine = 'lint'; severity = 'warning'; rule = 'scored-rule'; filePath = 'File.cs'; line = 7; message = 'scored-message'; scoreImpact = @{ multiplier = 1 } },
            @{ engine = 'lint'; severity = 'warning'; rule = 'zero-rule'; filePath = 'Other.cs'; line = 8; message = 'hidden-zero'; scoreImpact = @{ multiplier = 0 } },
            @{ engine = 'lint'; severity = 'warning'; rule = 'absent-rule'; filePath = 'Other.cs'; line = 9; message = 'hidden-absent' }
        )
    } | ConvertTo-Json -Depth 6 -Compress
    Set-FixtureState $scoringReport -ExitCode 23
    $result = Invoke-Gate -EntryPoint -ReportPath $explicitReport
    Assert-True ($result.ExitCode -eq 23) 'Executable gate preserves scanner failure status'
    foreach ($text in @('::group::', '[lint/warning] scored-rule', 'File.cs:7', 'scored-message', '2 report-only findings not shown', '::endgroup::')) {
        Assert-True ($result.Output.Contains($text)) "Failure output contains $text"
    }
    foreach ($text in @('zero-rule', 'hidden-zero', 'absent-rule', 'hidden-absent')) {
        Assert-True (-not $result.Output.Contains($text)) "Failure output filters $text"
    }
    Assert-True ([IO.File]::ReadAllText($explicitReport).TrimEnd([char[]] "`r`n") -ceq $scoringReport) 'Failed scan preserves full report including filtered findings'

    foreach ($diagnostics in @('[]', '[{"message":"hidden-only","scoreImpact":{"multiplier":0}}]')) {
        Set-FixtureState "{`"score`":90,`"summary`":{`"files`":12},`"diagnostics`":$diagnostics}" -ExitCode 7
        $result = Invoke-Gate
        Assert-True ($result.ExitCode -eq 7) 'No scoring findings does not erase scanner failure'
        Assert-True ($result.Output.Contains('no score-affecting findings were reported')) 'No scoring findings has an explicit explanation'
        Assert-True (-not $result.Output.Contains('hidden-only')) 'Report-only message stays out of console output'
    }
    Set-FixtureState $goodReport

    Invoke-FixtureGit commit --quiet --allow-empty -m 'Different scanner commit'
    $actualCommit = Invoke-FixtureGit rev-parse HEAD
    $result = Invoke-Gate
    Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains($expectedCommit) -and $result.Error.Contains($actualCommit)) `
        "Different commit must be refused naming both commits: $($result.Error)"
    Assert-True ($result.Error.Contains('Refusing a different scanner')) 'Mismatch must fail at checkout identity validation'
}
finally {
    if ($null -ne $savedScan) { Set-Item Function:Invoke-AislopScan $savedScan }
    $env:MFTLIB_AISLOP_FIXTURE = $savedFixture
    $PSNativeCommandUseErrorActionPreference = $savedNativePreference
    $env:PATH = $savedPath
    $env:AISLOP_HOME = $savedCheckout
    $global:LASTEXITCODE = $savedExitCode
    Set-Location $originalLocation
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

$savedClock = (Get-Item Function:Get-AislopUtcNow).ScriptBlock
try {
    function Get-AislopUtcNow { $script:clockState.Now }
    function Start-Sleep {
        param([int] $Seconds)
        Assert-True ($Seconds -eq 5) 'Poll interval remains five seconds'
        $script:clockState.Now = $script:clockState.Now.AddSeconds($Seconds)
    }
    foreach ($case in @(
        @{ Finish = 0; Beats = 0; Status = 0 },
        @{ Finish = 55; Beats = 0; Status = 0 },
        @{ Finish = 65; Beats = 1; Status = 23 },
        @{ Finish = 125; Beats = 2; Status = 7 },
        @{ Finish = 0; Beats = 0; Status = $null }
    )) {
        $start = [datetime] '2026-01-01T00:00:00Z'
        $clockState = @{
            Now = $start
            Finish = $start.AddSeconds($case.Finish)
            HandleRead = $false
            Waited = $false
            Status = $case.Status
        }
        $process = [pscustomobject]@{ State = $clockState }
        $process | Add-Member ScriptProperty Handle {
            $this.State.HandleRead = $true
            return [IntPtr] 1
        }
        $process | Add-Member ScriptProperty HasExited {
            if (-not $this.State.HandleRead) { throw 'Handle was not retained' }
            return $this.State.Now -ge $this.State.Finish
        }
        $process | Add-Member ScriptMethod WaitForExit {
            if ($this.State.Now -lt $this.State.Finish) { throw 'Wait attempted before fake exit' }
            $this.State.Waited = $true
        }
        $process | Add-Member ScriptProperty ExitCode {
            if (-not $this.State.Waited) { throw 'Exit read before final wait' }
            return $this.State.Status
        }
        $observations = [Collections.Generic.List[object]]::new()
        $failure = ''
        try {
            Wait-AislopProcess $process 6>&1 | ForEach-Object { $observations.Add($_) }
        } catch { $failure = $_.Exception.Message }
        if ($null -eq $case.Status) {
            Assert-True ($failure.Contains('without an exit code')) 'Unknown process exit fails closed'
        } else {
            Assert-True (-not $failure) "Wait succeeds for fake finish $($case.Finish)"
            $statuses = @($observations | Where-Object { $_ -is [int] })
            Assert-True ($statuses.Count -eq 1 -and $statuses[0] -eq $case.Status) 'Process exit status preserved'
            $beats = @($observations | Where-Object { $_.ToString().Contains('aislop scan in progress') })
            Assert-True ($beats.Count -eq $case.Beats) 'Heartbeat cadence follows fake elapsed time'
        }
        Assert-True $clockState.Waited 'Final wait drains redirected output before returning'
    }
} finally {
    Set-Item Function:Get-AislopUtcNow $savedClock
    Remove-Item Function:Start-Sleep
}

$workflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.gitea/workflows/aislop.yml') -Raw
$gateStep = [regex]::Match($workflow, '(?ms)^      - name: aislop quality gate\r?\n.*?(?=^      - name:|\z)').Value
Assert-True ($gateStep.Contains('& "$env:GITHUB_WORKSPACE/scripts/ci-aislop.ps1" -ReportPath $report')) 'Workflow delegates to tested gate'
Assert-True ($gateStep.Contains('$env:AISLOP_HOME = (Resolve-Path -LiteralPath $env:AISLOP_FORK_DIRECTORY).Path')) 'Workflow selects prepared fork checkout'
$cacheStep = [regex]::Match($workflow, '(?ms)^      - name: Restore the built aislop fork\r?\n.*?(?=^      - name:|\z)').Value
Assert-True ($cacheStep -match '(?m)^\s*path: \.aislop-fork?$') 'Cache path is the job-private workspace-relative directory'
Assert-True ($cacheStep -notmatch '(?m)^\s*path:.*(\.\.|~|\$|\{\{|[A-Za-z]:|^/)') 'Cache path has no relative segments, home, variables or absolute roots'
Assert-True ($workflow.Contains('AISLOP_FORK_DIRECTORY=$env:GITHUB_WORKSPACE\.aislop-fork')) 'Fork directory is the workspace cache path'
Assert-True ($workflow -notmatch 'USERPROFILE|\$HOME|\.cache[/\\]') 'Workflow uses no home or shared cache directory'
Assert-True ((Get-Content -LiteralPath (Join-Path $sourceRoot '.gitignore')) -contains '.aislop-fork/') 'Fork directory is gitignored so aislop does not scan it'
Assert-True ($workflow.IndexOf('uses: actions/checkout') -lt $workflow.IndexOf('uses: actions/cache')) 'Cache restores after checkout'
Assert-True ($gateStep.Contains('$report = Join-Path $env:RUNNER_TEMP ''aislop-report.json''')) 'Workflow retains runner report destination'
Assert-True ($gateStep.Contains('exit $LASTEXITCODE')) 'Workflow propagates shared gate failure'
Assert-True ($gateStep -notmatch 'Start-Process|ConvertFrom-Json|summary\.files|scoreImpact|ci \. --json') 'Workflow owns no duplicate execution or report recipe'
$testWorkflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.gitea/workflows/test.yml') -Raw
Assert-True ($testWorkflow.Contains('run: pwsh -NoProfile -File scripts/ci-aislop.tests.ps1')) 'Windows CI retains gate regression tests'

$evidenceDirectory = Join-Path $temporaryDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
$evidenceReport = Join-Path $evidenceDirectory 'report.json'
$evidenceProvenance = Join-Path $evidenceDirectory 'provenance.txt'
$evidenceRepair = Join-Path $evidenceDirectory 'repair.txt'
'{"score":100,"summary":{"files":321}}' | Set-Content -LiteralPath $evidenceReport -Encoding utf8
'aislop checkout version 9.9.9, checkout commit abc; CI pin abc' | Set-Content -LiteralPath $evidenceProvenance -Encoding utf8
'Repaired 12 of 14 restored dependency links' | Set-Content -LiteralPath $evidenceRepair -Encoding utf8
$evidenceScript = Join-Path $sourceRoot 'scripts/ci-aislop-evidence.ps1'
$evidenceOutput = Join-Path $evidenceDirectory 'out'
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $evidenceScript -OutputDirectory $evidenceOutput `
    -ReportPath $evidenceReport -ProvenanceLogPath $evidenceProvenance -LinkRepairLogPath $evidenceRepair `
    -CacheHit true -LinkRepairOutcome success -ColdBuildOutcome skipped -GateOutcome success | Out-Null
Assert-True ($LASTEXITCODE -eq 0) 'Evidence script succeeds'
$summary = Get-Content -LiteralPath (Join-Path $evidenceOutput 'summary.txt') -Raw
foreach ($expected in @(
        'cache-hit: true', 'link-repair-step: success', 'link-repair: Repaired 12 of 14 restored dependency links',
        'cold-build-step: skipped', 'provenance: aislop checkout version 9.9.9, checkout commit abc; CI pin abc',
        'gate-step: success', 'score: 100', 'files: 321')) {
    Assert-True ($summary.Contains($expected)) "Evidence summary carries $expected"
}
Assert-True (Test-Path -LiteralPath (Join-Path $evidenceOutput 'aislop-report.json')) 'Evidence directory carries the JSON report'
$failedOutput = Join-Path $evidenceDirectory 'failed'
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $evidenceScript -OutputDirectory $failedOutput `
    -ReportPath (Join-Path $evidenceDirectory 'missing.json') -CacheHit false -ColdBuildOutcome failure | Out-Null
$failedSummary = Get-Content -LiteralPath (Join-Path $failedOutput 'summary.txt') -Raw
Assert-True ($LASTEXITCODE -eq 0 -and $failedSummary.Contains('cache-hit: false') -and $failedSummary.Contains('cold-build-step: failure') -and
    $failedSummary.Contains('provenance: not reached') -and $failedSummary.Contains('score: unavailable')) 'Evidence is written even when the gate never ran'
$fallbackOutput = Join-Path $evidenceDirectory 'fallback'
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $evidenceScript -OutputDirectory $fallbackOutput `
    -ReportPath $evidenceReport -ProvenanceLogPath $evidenceProvenance -LinkRepairLogPath $evidenceRepair `
    -CacheHit true -LinkRepairOutcome failure -ColdBuildOutcome success -GateOutcome success | Out-Null
$fallbackSummary = Get-Content -LiteralPath (Join-Path $fallbackOutput 'summary.txt') -Raw
Assert-True ($fallbackSummary.Contains('link-repair-step: failure') -and $fallbackSummary.Contains('cold-build-step: success') -and
    $fallbackSummary.Contains('fallback: link repair failed, so the fork was rebuilt cold (cold build outcome success)')) 'Evidence records the cold build fallback after a failed link repair'
Assert-True (-not $summary.Contains('fallback:')) 'Evidence records no fallback when the restored cache works'
$repairStepText = [regex]::Match($workflow, '(?ms)^      - name: Repair restored aislop dependency links\r?\n.*?(?=^      - name:|\z)').Value
$coldStepText = [regex]::Match($workflow, '(?ms)^      - name: Build C#-capable aislop.*?(?=^      - name:|\z)').Value
Assert-True ($repairStepText -match '(?m)^\s*continue-on-error: true\r?$') 'Failed link repair does not fail the job'
Assert-True ($coldStepText.Contains("steps.aislop-link-repair.outcome == 'failure'")) 'Cold build runs after a failed link repair'
foreach ($stepName in @('Write aislop gate evidence', 'Upload aislop gate evidence')) {
    $step = [regex]::Match($workflow, "(?ms)^      - name: $stepName\r?\n.*?(?=^      - name:|\z)").Value
    Assert-True ($step -match '(?m)^\s*if: always\(\)\r?$') "$stepName runs on every outcome"
}
$uploadStep = [regex]::Match($workflow, '(?ms)^      - name: Upload aislop gate evidence\r?\n.*?(?=^      - name:|\z)').Value
Assert-True ($uploadStep.Contains('uses: ChristopherHX/gitea-upload-artifact@81f940d004763f986ba3582c007fd842dd5cb0d7')) 'Workflow uses the pinned upload action visible to the Gitea artifacts API'
Assert-True ($uploadStep.Contains('name: aislop-gate-evidence') -and $uploadStep.Contains('path: ${{ runner.temp }}/aislop-gate-evidence')) 'Workflow uploads the named evidence directory'
$evidenceStep = [regex]::Match($workflow, '(?ms)^      - name: Write aislop gate evidence\r?\n.*?(?=^      - name:|\z)').Value
foreach ($expected in @('steps.aislop-cache.outputs.cache-hit', 'steps.aislop-link-repair.outcome', 'steps.aislop-cold-build.outcome', 'steps.aislop-gate.outcome', 'aislop-provenance.txt', 'aislop-link-repair.txt')) {
    Assert-True ($evidenceStep.Contains($expected)) "Evidence step reads $expected"
}
Assert-True ($gateStep.Contains("-EvidenceLogPath (Join-Path `$env:RUNNER_TEMP 'aislop-provenance.txt')")) 'Gate step records provenance for the evidence'

. "$PSScriptRoot/ci-aislop-link-repair.ps1"
$repairRoot = Join-Path $temporaryDirectory 'link repair'
$restored = Join-Path $repairRoot 'restored tree'
$external = Join-Path $repairRoot 'external'
$log = Join-Path $repairRoot 'repair.txt'
New-Item -ItemType Directory -Force -Path "$restored/node_modules/.pnpm/pkg/sub", "$repairRoot/old/node_modules/.pnpm/pkg/sub", $external | Out-Null
Set-Content -LiteralPath "$restored/node_modules/.pnpm/pkg/value.txt" -Value 'restored' -NoNewline
Set-Content -LiteralPath "$restored/node_modules/.pnpm/pkg/sub/value.txt" -Value 'restored-sub' -NoNewline
New-Item -ItemType Junction -Path "$restored/stale" -Target "$repairRoot/old/node_modules/.pnpm/pkg" | Out-Null
New-Item -ItemType SymbolicLink -Path "$restored/a-dependent" -Target 'stale\sub' | Out-Null
New-Item -ItemType SymbolicLink -Path "$restored/z-dependent" -Target 'stale\sub' | Out-Null
New-Item -ItemType Junction -Path "$restored/outside" -Target $external | Out-Null
New-Item -ItemType SymbolicLink -Path "$restored/relative-ok" -Target 'node_modules\.pnpm\pkg' | Out-Null
New-Item -ItemType SymbolicLink -Path "$restored/relative-forward" -Target 'node_modules\.pnpm\late' | Out-Null
New-Item -ItemType Directory -Force -Path "$restored/node_modules/.pnpm/late" | Out-Null
Set-Content -LiteralPath "$restored/node_modules/.pnpm/late/value.txt" -Value 'late' -NoNewline
Assert-True (-not (Test-Path -LiteralPath "$restored/relative-forward" -PathType Container)) 'Forward relative symlink starts as a file reparse point'
Remove-Item -LiteralPath "$repairRoot/old" -Recurse -Force
Repair-AislopDependencyLinks $restored $log
Assert-True ((Get-Content -LiteralPath "$restored/stale/value.txt" -Raw) -ceq 'restored') 'Stale link is retargeted into the restored tree'
Assert-True ((Get-Content -LiteralPath "$restored/a-dependent/value.txt" -Raw) -ceq 'restored-sub' -and
    (Get-Content -LiteralPath "$restored/z-dependent/value.txt" -Raw) -ceq 'restored-sub') 'Dependent relative links resolve in both visitation orders'
Assert-True ((Get-Content -LiteralPath "$restored/relative-forward/value.txt" -Raw) -ceq 'late') 'Forward relative directory symlink is repaired into a directory link'
Assert-True ((Get-Item -LiteralPath "$restored/outside").LinkTarget -ceq $external) 'Link to an existing external target is left alone'
Assert-True ((Get-Item -LiteralPath "$restored/relative-ok").LinkTarget -ceq 'node_modules\.pnpm\pkg') 'Healthy relative directory symlink is left alone'
Assert-True ((Get-Content -LiteralPath $log -Raw).Trim() -ceq 'Repaired 2 of 6 restored dependency links') 'Repair evidence carries actual outcomes'
foreach ($brokenCase in @(
    @{ Type = 'Junction'; Target = "$repairRoot/gone" },
    @{ Type = 'SymbolicLink'; Target = 'node_modules\.pnpm\missing' }
)) {
    if ($brokenCase.Type -eq 'Junction') {
        New-Item -ItemType Directory -Path $brokenCase.Target | Out-Null
        New-Item -ItemType Junction -Path "$restored/broken" -Target $brokenCase.Target | Out-Null
        Remove-Item -LiteralPath $brokenCase.Target -Force
    } else {
        New-Item -ItemType SymbolicLink -Path "$restored/broken" -Target $brokenCase.Target | Out-Null
    }
    $repairFailure = ''
    try { Repair-AislopDependencyLinks $restored $log } catch { $repairFailure = $_.Exception.Message }
    Assert-True ($repairFailure.Contains('do not map')) "Unmappable dangling $($brokenCase.Type) target fails"
    if (([IO.File]::GetAttributes("$restored/broken") -band [IO.FileAttributes]::Directory) -ne 0) {
        [IO.Directory]::Delete("$restored/broken")
    } else {
        [IO.File]::Delete("$restored/broken")
    }
}
Set-Content -LiteralPath "$restored/node_modules/.pnpm/kind" -Value 'file' -NoNewline
New-Item -ItemType Directory -Path "$repairRoot/old/node_modules/.pnpm/kind" -Force | Out-Null
New-Item -ItemType Junction -Path "$restored/wrongkind" -Target "$repairRoot/old/node_modules/.pnpm/kind" | Out-Null
Remove-Item -LiteralPath "$repairRoot/old" -Recurse -Force
$repairFailure = ''
try { Repair-AislopDependencyLinks $restored $log } catch { $repairFailure = $_.Exception.Message }
Assert-True ($repairFailure -ne '') 'Mapped target of the wrong kind fails repair'
Assert-True ((Get-Content -LiteralPath "$restored/node_modules/.pnpm/kind" -Raw) -ceq 'file') 'Failed repair preserves the candidate contents'
Assert-True ((Get-Item -LiteralPath "$restored/wrongkind" -Force).LinkTarget -ceq "$repairRoot/old/node_modules/.pnpm/kind".Replace('/', '\')) 'Failed repair leaves the original junction in place'
[IO.Directory]::Delete("$restored/stale"); [IO.Directory]::Delete("$restored/outside")
[IO.Directory]::Delete("$restored/a-dependent"); [IO.Directory]::Delete("$restored/z-dependent")
[IO.Directory]::Delete("$restored/relative-ok"); [IO.Directory]::Delete("$restored/relative-forward")
[IO.Directory]::Delete("$restored/wrongkind")

# tar restores a relative directory symlink as a file symlink when its target is extracted later.
$relativeRoot = Join-Path $repairRoot 'relative tree'
New-Item -ItemType Directory -Force -Path "$relativeRoot/.pnpm/pkg@1/node_modules/pkg", "$relativeRoot/.pnpm/consumer/node_modules" | Out-Null
Set-Content -LiteralPath "$relativeRoot/.pnpm/pkg@1/node_modules/pkg/value.txt" -Value 'relative' -NoNewline
$relativeLink = "$relativeRoot/.pnpm/consumer/node_modules/pkg"
$relativeTarget = '..\..\pkg@1\node_modules\pkg'
$relativeLinkWindows = $relativeLink.Replace('/', '\')
cmd /c "mklink `"$relativeLinkWindows`" $relativeTarget" | Out-Null
Assert-True (([IO.File]::GetAttributes($relativeLink) -band [IO.FileAttributes]::Directory) -eq 0) 'Fixture is a file symlink to a directory'
Repair-AislopDependencyLinks $relativeRoot $log
Assert-True ((Get-Content -LiteralPath "$relativeLink/value.txt" -Raw) -ceq 'relative') 'Relative file symlink to a directory is recreated as a directory symlink'
Assert-True ((Get-Item -LiteralPath $relativeLink -Force).LinkTarget -ceq $relativeTarget) 'Repaired relative symlink keeps its relative target'
Assert-True ((Get-Content -LiteralPath $log -Raw).Trim() -ceq 'Repaired 1 of 1 restored dependency links') 'Relative repair is counted in the evidence'

foreach ($failure in $failures) { Write-Host "FAIL: $failure" }
Write-Host "ci-aislop.tests.ps1: $checks checks, $($failures.Count) failures"
if ($failures.Count -gt 0) { exit 1 }
