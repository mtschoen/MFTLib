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
    try {
        $output = & $script:gate 6>&1 | Out-String
        return [pscustomobject]@{ Output = $output; Error = ''; ExitCode = $LASTEXITCODE }
    }
    catch {
        return [pscustomobject]@{ Output = ''; Error = $_.Exception.Message; ExitCode = 1 }
    }
}

try {
    New-Item -ItemType Directory -Force -Path (Split-Path $gate), (Split-Path $pin), (Split-Path $cli) | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot/ci-aislop.ps1" -Destination $gate
    Set-Content -LiteralPath $cli -Encoding utf8 -Value @'
if (process.argv[2] === '--version') {
    console.log('fixture-scanner-1.0');
} else if (process.argv.slice(2).join(' ') === 'ci . --json') {
    console.log(JSON.stringify({score: 100, summary: {files: 12}, diagnostics: []}));
} else {
    throw new Error('Unexpected scanner arguments');
}
'@
    Invoke-FixtureGit init --quiet
    Invoke-FixtureGit add dist/cli.js
    Invoke-FixtureGit commit --quiet -m 'Fixture scanner'
    $expectedCommit = Invoke-FixtureGit rev-parse HEAD
    Set-Content -LiteralPath $pin -Value $expectedCommit
    $env:AISLOP_HOME = $checkout

    # Real git and node remain callable by absolute path with no launchers on PATH.
    function git {
        & $gitExecutable @args
        $global:LASTEXITCODE = $LASTEXITCODE
    }
    function node {
        & $nodeExecutable @args
        $global:LASTEXITCODE = $LASTEXITCODE
    }
    $env:PATH = ''
    Assert-True ($null -eq (Get-Command aislop -ErrorAction SilentlyContinue)) 'aislop must be unavailable'

    $result = Invoke-Gate
    Assert-True ($result.ExitCode -eq 0 -and -not $result.Error) "Valid pin without a launcher must pass: $($result.Error)"
    Assert-True ($result.Output.Contains('fixture-scanner-1.0')) 'Version must come from the selected checkout'
    $report = Join-Path $repository '.aislop/preflight/report.json'
    Assert-True (Test-Path -LiteralPath $report) 'The selected checkout must run the JSON gate'
    if (Test-Path -LiteralPath $report) {
        $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Assert-True ($json.score -eq 100 -and $json.summary.files -eq 12) 'The selected scanner report must be preserved'
    }

    Invoke-FixtureGit commit --quiet --allow-empty -m 'Different scanner commit'
    $actualCommit = Invoke-FixtureGit rev-parse HEAD
    $result = Invoke-Gate
    Assert-True ($result.ExitCode -ne 0 -and $result.Error.Contains($expectedCommit) -and $result.Error.Contains($actualCommit)) `
        "Different commit must be refused naming both commits: $($result.Error)"
    Assert-True ($result.Error.Contains('Refusing a different scanner')) 'Mismatch must fail at checkout identity validation'
}
finally {
    $env:PATH = $savedPath
    $env:AISLOP_HOME = $savedCheckout
    $global:LASTEXITCODE = $savedExitCode
    Set-Location $originalLocation
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

foreach ($failure in $failures) { Write-Host "FAIL: $failure" }
Write-Host "ci-aislop.tests.ps1: $checks checks, $($failures.Count) failures"
if ($failures.Count -gt 0) { exit 1 }
