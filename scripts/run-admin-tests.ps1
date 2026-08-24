# Run MFTLib tests that require admin elevation.
# Builds the solution, then launches dotnet test elevated via UAC prompt.
# Results are written to a log file and printed to the console.

param(
    [string]$Filter = "TestCategory=RequiresAdmin",
    [string]$Configuration = "Debug",
    [switch]$AllTests
)

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$testProject = "$repoRoot\MFTLib.Tests\MFTLib.Tests.csproj"
$logFile = "$repoRoot\scripts\admin-test-results.log"

if ($AllTests) { $Filter = "" }

# Clear stale log
if (Test-Path $logFile) { Remove-Item $logFile }

# Build first (doesn't need admin)
Write-Host "Building native project..." -ForegroundColor Cyan
& MSBuild.exe "$repoRoot\MFTLibNative\MFTLibNative.vcxproj" -t:Build -p:Configuration=$Configuration -p:Platform=x64 -p:SolutionDir="$repoRoot\" -v:q -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Native build failed." -ForegroundColor Red
    exit 1
}

Write-Host "Building managed projects..." -ForegroundColor Cyan
foreach ($proj in @(
    "MFTLib\MFTLib.csproj",
    "MFTLibTestExtensions\MFTLibTestExtensions.csproj",
    "TestProgram\TestProgram.csproj",
    "Benchmark\Benchmark.csproj",
    "MFTLib.Tests\MFTLib.Tests.csproj"
)) {
    & dotnet build "$repoRoot\$proj" -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed for $proj." -ForegroundColor Red
        exit 1
    }
}

# Construct the test command
$testArgs = "test `"$testProject`" --no-build -c $Configuration -p:Platform=x64 --verbosity normal"
if ($Filter) {
    $testArgs += " --filter `"$Filter`""
}

# Launch elevated
$encodedCmd = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(
    "Set-Location '$repoRoot'; dotnet $testArgs 2>&1 | Tee-Object -FilePath '$logFile'; Read-Host 'Press Enter to close'"
))

Write-Host "Launching elevated test runner (UAC prompt)..." -ForegroundColor Yellow
try {
    Start-Process powershell -Verb RunAs -ArgumentList "-EncodedCommand", $encodedCmd -Wait
} catch {
    Write-Host "UAC prompt was declined or elevation failed." -ForegroundColor Red
    exit 1
}

# Show results
if (Test-Path $logFile) {
    Write-Host "`n--- Test Results ---" -ForegroundColor Cyan
    Get-Content $logFile
} else {
    Write-Host "No test results found. UAC prompt may have been declined." -ForegroundColor Red
    exit 1
}
