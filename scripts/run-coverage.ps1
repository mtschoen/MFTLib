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

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testProject = "$repoRoot\MFTLib.Tests\MFTLib.Tests.csproj"
$coverageDir = "$repoRoot\MFTLib.Tests"
$jsonFile = "$coverageDir\coverage.json"
$coberturaFile = "$coverageDir\coverage.xml"
$reportDir = "$coverageDir\coverage-report"
$includeFilter = '\"[MFTLib]*,[TestProgram]*,[Benchmark]*\"'

# Build first (doesn't need admin)
Write-Host "Building solution ($Configuration|x64)..." -ForegroundColor Cyan
& MSBuild.exe "$repoRoot\MFTLib.sln" -p:Configuration=$Configuration -p:Platform=x64 -v:q -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# Clean stale coverage
Remove-Item $jsonFile -ErrorAction SilentlyContinue
Remove-Item $coberturaFile -ErrorAction SilentlyContinue

# Run non-admin tests — output JSON for MergeWith compatibility (or cobertura if non-interactive)
if ($NonInteractive) {
    Write-Host "`nRunning tests (non-interactive, skipping admin tests)..." -ForegroundColor Cyan
    dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
        --filter "TestCategory!=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=cobertura `
        "-p:CoverletOutput=$coberturaFile" `
        "-p:Include=$includeFilter" `
        --verbosity quiet

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
        "-p:Include=$includeFilter" `
        --verbosity quiet

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Non-admin tests failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "Non-admin coverage saved." -ForegroundColor Green

    # Run admin tests elevated — write a temp script so output can be captured
    Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow

    $adminLog = Join-Path $repoRoot "admin-test-output.log"
    $adminScript = Join-Path $repoRoot "admin-test-runner.ps1"
    Remove-Item $adminLog -ErrorAction SilentlyContinue

    # Write the admin script with literal paths (no nested quoting issues)
    $template = @'
$ErrorActionPreference = "Stop"
Set-Location "REPO_ROOT"
$inc = '\"[MFTLib]*,[TestProgram]*,[Benchmark]*\"'
try {
    dotnet test "TEST_PROJECT" --no-build -c CONFIGURATION -p:Platform=x64 `
        --filter "TestCategory=RequiresAdmin" `
        -p:CollectCoverage=true `
        -p:CoverletOutputFormat=cobertura `
        "-p:CoverletOutput=COBERTURA_FILE" `
        "-p:MergeWith=JSON_FILE" `
        "-p:Include=$inc" `
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

# Generate summary
reportgenerator -reports:"$coberturaFile" -targetdir:"$reportDir" -reporttypes:"TextSummary" 2>&1 | Out-Null

if (Test-Path "$reportDir\Summary.txt") {
    Write-Host "`n--- Coverage Report ---" -ForegroundColor Cyan
    Get-Content "$reportDir\Summary.txt"
}

# Cleanup
Remove-Item $coberturaFile -ErrorAction SilentlyContinue
Remove-Item $reportDir -Recurse -ErrorAction SilentlyContinue
