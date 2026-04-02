# Run all tests (non-admin + admin elevated) with coverlet coverage collection,
# then merge reports and display a combined summary.

param(
    [string]$Configuration = "Debug"
)

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testProject = "$repoRoot\MFTLib.Tests\MFTLib.Tests.csproj"
$coverageDir = "$repoRoot\MFTLib.Tests"

# Build first
Write-Host "Building solution..." -ForegroundColor Cyan
& MSBuild.exe "$repoRoot\MFTLib.sln" -p:Configuration=$Configuration -p:Platform=x64 -v:q -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# Run non-admin tests with coverage
Write-Host "`nRunning non-admin tests with coverage..." -ForegroundColor Cyan
dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
    --filter "TestCategory!=RequiresAdmin" `
    -p:CollectCoverage=true `
    -p:CoverletOutputFormat=cobertura `
    "-p:CoverletOutput=$coverageDir\coverage-nonadmin.xml" `
    "-p:Include=[MFTLib]*" `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "Non-admin tests failed." -ForegroundColor Red
    exit 1
}
Write-Host "Non-admin coverage saved." -ForegroundColor Green

# Run admin tests with coverage (elevated)
Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow

$adminTestCmd = @"
Set-Location '$repoRoot'
dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 ``
    --filter "TestCategory=RequiresAdmin" ``
    -p:CollectCoverage=true ``
    -p:CoverletOutputFormat=cobertura ``
    "-p:CoverletOutput=$coverageDir\coverage-admin.xml" ``
    "-p:Include=[MFTLib]*" ``
    --verbosity quiet
`$exitCode = `$LASTEXITCODE
if (`$exitCode -eq 0) { Write-Host "Admin tests passed." -ForegroundColor Green }
else { Write-Host "Admin tests failed." -ForegroundColor Red }
Read-Host "Press Enter to close"
exit `$exitCode
"@

$encodedCmd = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($adminTestCmd))
Start-Process powershell -Verb RunAs -ArgumentList "-EncodedCommand", $encodedCmd -Wait
Write-Host "Admin coverage saved." -ForegroundColor Green

# Merge reports
Write-Host "`nMerging coverage reports..." -ForegroundColor Cyan
$reports = @(
    "$coverageDir\coverage-nonadmin.xml"
    "$coverageDir\coverage-admin.xml"
) | Where-Object { Test-Path $_ }

if ($reports.Count -eq 0) {
    Write-Host "No coverage files found." -ForegroundColor Red
    exit 1
}

$reportList = $reports -join ";"
reportgenerator -reports:"$reportList" -targetdir:"$coverageDir\coverage-report" -reporttypes:"TextSummary" 2>&1 | Out-Null

if (Test-Path "$coverageDir\coverage-report\Summary.txt") {
    Write-Host "`n--- Combined Coverage Report ---" -ForegroundColor Cyan
    Get-Content "$coverageDir\coverage-report\Summary.txt"
}

# Cleanup
Remove-Item "$coverageDir\coverage-nonadmin.xml" -ErrorAction SilentlyContinue
Remove-Item "$coverageDir\coverage-admin.xml" -ErrorAction SilentlyContinue
Remove-Item "$coverageDir\coverage-report" -Recurse -ErrorAction SilentlyContinue
