# Run all tests with coverlet coverage. By default, self-elevates to admin so
# volume-access tests can run. Use -NonInteractive to skip admin tests.
#
# Usage:
#   .\scripts\run-coverage.ps1                  # full run (self-elevates for admin tests)
#   .\scripts\run-coverage.ps1 -NonInteractive  # skip admin tests (CI / headless)

param(
    [string]$Configuration = "Release",
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

. "$PSScriptRoot/coverage-child.ps1"

function Invoke-ManagedAdminCoverage {
    param([string]$RepositoryRoot, [string]$Configuration)
    $coverageDirectory = Join-Path $RepositoryRoot 'MFTLib.Tests'
    $coberturaPath = Join-Path $coverageDirectory 'coverage.xml'
    $request = @{
        Target = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
        Arguments = @(
            'test', (Join-Path $coverageDirectory 'MFTLib.Tests.csproj'),
            '--no-build', '-c', $Configuration, '-p:Platform=x64',
            '--filter', 'TestCategory=RequiresAdmin', '-p:CollectCoverage=true',
            '-p:CoverletOutputFormat=cobertura', "-p:CoverletOutput=$coberturaPath",
            "-p:MergeWith=$(Join-Path $coverageDirectory 'coverage.json')",
            '--verbosity', 'normal'
        )
        WorkingDirectory = $RepositoryRoot
        LogPath = (Join-Path $RepositoryRoot 'admin-test-output.log')
        DonePath = (Join-Path $RepositoryRoot 'admin-test-output.done')
        Environment = @{ VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = '120000' }
    }
    $childExit = Invoke-CoverageChild $request
    if ($childExit -ne 0) {
        Write-Host "Admin tests failed with exit code $childExit. See $($request.LogPath)" -ForegroundColor Red
        return $childExit
    }
    if (-not (Test-Path -LiteralPath $coberturaPath)) {
        Write-Host 'Admin tests produced no Cobertura coverage file.' -ForegroundColor Red
        return 1
    }
    Remove-Item -LiteralPath $request.LogPath -ErrorAction SilentlyContinue
    return 0
}

if ($MyInvocation.InvocationName -eq '.') { return }

# vstest kills a testhost that has not exited 100 ms after the run ends. Coverlet writes each
# module's hit file from a ProcessExit handler, so a kill mid-flush leaves later modules partly
# or wholly unrecorded (Benchmark 33.45 percent, SampleProgram.Watch 0 percent) although every test
# passed. Give the exit-time flush a generous grace period; a healthy host still exits at once.
$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = "120000"

# Under act_runner (Gitea CI) host mode, $PSScriptRoot for inline run: blocks
# resolves to the act\workflow temp directory, not the checkout root. Use
# $env:GITHUB_WORKSPACE when set (which is the checkout root), and fall back
# to $PSScriptRoot\.. for local interactive use.
$repoRoot = if ($env:GITHUB_WORKSPACE) {
    # Use raw string to avoid Resolve-Path PathInfo object issues
    [string]$env:GITHUB_WORKSPACE
} else {
    [string](Resolve-Path -LiteralPath "$PSScriptRoot\..")
}

# Navigate to repo root so relative paths work for tools that don't honour CWD
Push-Location -LiteralPath $repoRoot

$testProject = Join-Path $repoRoot "MFTLib.Tests\MFTLib.Tests.csproj"
$coverageDir  = Join-Path $repoRoot "MFTLib.Tests"
$jsonFile     = Join-Path $coverageDir "coverage.json"
$coberturaFile= Join-Path $coverageDir "coverage.xml"
$reportDir    = Join-Path $coverageDir "coverage-report"

$copiedTrx    = Join-Path $reportDir "non-admin.trx"
$commitFile   = Join-Path $reportDir "commit.txt"

foreach ($path in @($jsonFile, $coberturaFile, $reportDir)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

& "$PSScriptRoot/build-windows.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit 1 }

$testResultsDirectory = Join-Path $reportDir 'test-results'

# Run non-admin tests - output JSON for MergeWith compatibility (or cobertura if non-interactive)
if ($NonInteractive) {
    Write-Host "`nRunning tests (non-interactive, skipping admin tests)..." -ForegroundColor Cyan
    dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
        --filter "TestCategory!=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=cobertura `
        "-p:CoverletOutput=$coberturaFile" `
        --results-directory "$testResultsDirectory" `
        --logger 'trx;LogFileName=non-admin.trx' `
        --verbosity quiet `
        --logger "console;verbosity=normal"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "`nRunning non-admin tests with coverage..." -ForegroundColor Cyan
    dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
        --filter "TestCategory!=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=json `
        "-p:CoverletOutput=$jsonFile" `
        --results-directory "$testResultsDirectory" `
        --logger 'trx;LogFileName=non-admin.trx' `
        --verbosity quiet

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Non-admin tests failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "Non-admin coverage saved." -ForegroundColor Green

    # Run admin tests elevated
    Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow
    $adminExit = Invoke-ManagedAdminCoverage $repoRoot $Configuration
    if ($adminExit -ne 0) { exit $adminExit }

    # Clean intermediate JSON
    Remove-Item $jsonFile -ErrorAction SilentlyContinue
}

# Summary evidence for the CI artifact: the non-admin TRX file (its Counters element carries the totals) and the commit.
Copy-Item -LiteralPath (Join-Path $testResultsDirectory 'non-admin.trx') -Destination $copiedTrx -ErrorAction Stop
$commitSha = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) {
    Write-Host 'git rev-parse HEAD failed.' -ForegroundColor Red
    exit 1
}
Set-Content -LiteralPath $commitFile -Value $commitSha -Encoding utf8 -ErrorAction Stop

& reportgenerator "-reports:$coberturaFile" "-targetdir:$reportDir" '-reporttypes:TextSummary'
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Coverage report generation failed.' -ForegroundColor Red
    exit 1
}
$summaryPath = Join-Path $reportDir 'Summary.txt'
if (-not (Test-Path -LiteralPath $summaryPath)) {
    Write-Host 'Coverage report summary was not generated.' -ForegroundColor Red
    exit 1
}
Write-Host "`n--- Coverage Report ---" -ForegroundColor Cyan
Get-Content -LiteralPath $summaryPath

# Cleanup (skip in non-interactive mode so CI can upload artifacts); keep the copied TRX and commit.txt
if (-not $NonInteractive) {
    Remove-Item $coberturaFile -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $reportDir -Force |
        Where-Object { $_.FullName -notin @($copiedTrx, $commitFile) } |
        Remove-Item -Recurse -ErrorAction SilentlyContinue
}
