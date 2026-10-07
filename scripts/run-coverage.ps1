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
    [string](Resolve-Path "$PSScriptRoot\..")
}

# Navigate to repo root so relative paths work for tools that don't honour CWD
Push-Location $repoRoot

$testProject = Join-Path $repoRoot "MFTLib.Tests\MFTLib.Tests.csproj"
$coverageDir  = Join-Path $repoRoot "MFTLib.Tests"
$jsonFile     = Join-Path $coverageDir "coverage.json"
$coberturaFile= Join-Path $coverageDir "coverage.xml"
$reportDir    = Join-Path $coverageDir "coverage-report"

foreach ($path in @($jsonFile, $coberturaFile, $reportDir)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

& "$PSScriptRoot/build-windows.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit 1 }


# Run non-admin tests - output JSON for MergeWith compatibility (or cobertura if non-interactive)
if ($NonInteractive) {
    Write-Host "`nRunning tests (non-interactive, skipping admin tests)..." -ForegroundColor Cyan
    dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
        --filter "TestCategory!=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=cobertura `
        "-p:CoverletOutput=$coberturaFile" `
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
        --verbosity quiet

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Non-admin tests failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "Non-admin coverage saved." -ForegroundColor Green

    # Run admin tests elevated - write a temp script so output can be captured
    Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow

    $adminLog = Join-Path $repoRoot "admin-test-output.log"
    $adminScript = Join-Path $repoRoot "admin-test-runner.ps1"
    Remove-Item $adminLog -ErrorAction SilentlyContinue

    # Write the admin script with literal paths (no nested quoting issues)
    $template = @'
$ErrorActionPreference = "Stop"
$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = "120000"
Set-Location "REPO_ROOT"
try {
    dotnet test "TEST_PROJECT" --no-build -c CONFIGURATION -p:Platform=x64 `
        --filter "TestCategory=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=cobertura `
        "-p:CoverletOutput=COBERTURA_FILE" `
        "-p:MergeWith=JSON_FILE" `
        --verbosity normal *>&1 | Tee-Object -FilePath "LOG_FILE"
    exit $LASTEXITCODE
} catch {
    $_ | Out-File "LOG_FILE" -Append
    exit 1
}
'@
    $template.Replace("REPO_ROOT", $repoRoot).
              Replace("TEST_PROJECT", $testProject).
              Replace("CONFIGURATION", $Configuration).
              Replace("COBERTURA_FILE", $coberturaFile).
              Replace("JSON_FILE", $jsonFile).
              Replace("LOG_FILE", $adminLog) |
        Set-Content $adminScript

    Start-Process powershell -Verb RunAs `
        -ArgumentList "-ExecutionPolicy", "Bypass", "-File", $adminScript `
        -Wait

    Remove-Item $adminScript -ErrorAction SilentlyContinue

    if (Test-Path $adminLog) {
        Write-Host (Get-Content $adminLog -Raw)
        Remove-Item $adminLog -ErrorAction SilentlyContinue
    }

    if (!(Test-Path $coberturaFile)) {
        Write-Host "No coverage file found. UAC prompt may have been declined." -ForegroundColor Red
        exit 1
    }

    # Clean intermediate JSON
    Remove-Item $jsonFile -ErrorAction SilentlyContinue
}

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

# Cleanup (skip in non-interactive mode so CI can upload artifacts)
if (-not $NonInteractive) {
    Remove-Item $coberturaFile -ErrorAction SilentlyContinue
    Remove-Item $reportDir -Recurse -ErrorAction SilentlyContinue
}
