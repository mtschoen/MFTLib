# Run all tests with coverlet coverage. Non-admin tests run first to collect
# baseline coverage in JSON format, then admin tests run elevated and merge
# using coverlet's MergeWith parameter, outputting the final result as cobertura.

param(
    [string]$Configuration = "Debug"
)

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testProject = "$repoRoot\MFTLib.Tests\MFTLib.Tests.csproj"
$coverageDir = "$repoRoot\MFTLib.Tests"
$jsonFile = "$coverageDir\coverage.json"
$coberturaFile = "$coverageDir\coverage.xml"
$reportDir = "$coverageDir\coverage-report"

# Build first (doesn't need admin)
Write-Host "Building solution..." -ForegroundColor Cyan
& MSBuild.exe "$repoRoot\MFTLib.sln" -p:Configuration=$Configuration -p:Platform=x64 -v:q -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# Clean stale coverage
Remove-Item $jsonFile -ErrorAction SilentlyContinue
Remove-Item $coberturaFile -ErrorAction SilentlyContinue

# Run non-admin tests — output JSON for MergeWith compatibility
Write-Host "`nRunning non-admin tests with coverage..." -ForegroundColor Cyan
dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 `
    --filter "TestCategory!=RequiresAdmin" `
    -p:CollectCoverage=true `
    -p:CoverletOutputFormat=json `
    "-p:CoverletOutput=$jsonFile" `
    "-p:Include=[MFTLib]*" `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "Non-admin tests failed." -ForegroundColor Red
    exit 1
}
Write-Host "Non-admin coverage saved." -ForegroundColor Green

# Run admin tests elevated — merge with JSON, output as cobertura
Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow

$adminTestCmd = @"
Set-Location '$repoRoot'
dotnet test "$testProject" --no-build -c $Configuration -p:Platform=x64 ``
    --filter "TestCategory=RequiresAdmin" ``
    -p:CollectCoverage=true ``
    -p:CoverletOutputFormat=cobertura ``
    "-p:CoverletOutput=$coberturaFile" ``
    "-p:MergeWith=$jsonFile" ``
    "-p:Include=[MFTLib]*" ``
    --verbosity quiet
`$exitCode = `$LASTEXITCODE
if (`$exitCode -eq 0) { Write-Host "Admin tests passed." -ForegroundColor Green }
else { Write-Host "Admin tests failed." -ForegroundColor Red }
exit `$exitCode
"@

$encodedCmd = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($adminTestCmd))
Start-Process powershell -Verb RunAs -ArgumentList "-EncodedCommand", $encodedCmd -Wait

if (!(Test-Path $coberturaFile)) {
    Write-Host "No coverage file found. UAC prompt may have been declined." -ForegroundColor Red
    exit 1
}

# Generate summary
reportgenerator -reports:"$coberturaFile" -targetdir:"$reportDir" -reporttypes:"TextSummary" 2>&1 | Out-Null

if (Test-Path "$reportDir\Summary.txt") {
    Write-Host "`n--- Coverage Report ---" -ForegroundColor Cyan
    Get-Content "$reportDir\Summary.txt"
}

# Cleanup
Remove-Item $jsonFile -ErrorAction SilentlyContinue
Remove-Item $coberturaFile -ErrorAction SilentlyContinue
Remove-Item $reportDir -Recurse -ErrorAction SilentlyContinue
