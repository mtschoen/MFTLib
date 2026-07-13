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

# Build strategy for a mixed C#/C++ solution on VS BuildTools runner:
#
# Step 1 — dotnet restore: generates project.assets.json for C# projects.
#   (VS MSBuild doesn't auto-restore; dotnet.exe is 64-bit so no WOW64 issue.)
# Step 2 — 64-bit VS MSBuild builds the full solution.
#   Use the amd64 binary: the workspace is under C:\Windows\System32\config\
#   systemprofile\... which WOW64 redirects to SysWOW64 for 32-bit processes.
#   Set MSBuildSDKsPath so the .NET SDK resolver finds Microsoft.NET.Sdk.
#   Set MSBUILDENABLEWORKLOADRESOLVER=false to skip workload auto-import props
#   which fail when no workloads are installed (standard for BuildTools-only CI).
#   Override PlatformToolset=v143 since the vcxproj has v145 (not present here).

Write-Host "Restoring NuGet packages..." -ForegroundColor Cyan
$slnPath = Join-Path $repoRoot "MFTLib.sln"
& dotnet restore $slnPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed." -ForegroundColor Red
    exit 1
}
Write-Host "Restore succeeded." -ForegroundColor Green

$vsInstallPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest 2>$null
$msbuild = if ($vsInstallPath) {
    Join-Path $vsInstallPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
} else { "MSBuild.exe" }

$sdkEntry = & dotnet --list-sdks 2>$null | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
if ($sdkEntry -match '^(\S+)\s+\[(.+)\]') {
    $env:MSBuildSDKsPath = Join-Path (Join-Path $Matches[2] $Matches[1]) "Sdks"
}
$env:MSBUILDENABLEWORKLOADRESOLVER = "false"

Write-Host "Building solution ($Configuration|x64)..." -ForegroundColor Cyan
& $msbuild $slnPath -p:Configuration=$Configuration -p:Platform=x64 -p:PlatformToolset=v143 -v:q -nologo
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

    # Run admin tests elevated — write a temp script so output can be captured
    Write-Host "`nLaunching elevated test runner for admin tests (UAC prompt)..." -ForegroundColor Yellow

    $adminLog = Join-Path $repoRoot "admin-test-output.log"
    $adminScript = Join-Path $repoRoot "admin-test-runner.ps1"
    Remove-Item $adminLog -ErrorAction SilentlyContinue

    # Write the admin script with literal paths (no nested quoting issues)
    $template = @'
$ErrorActionPreference = "Stop"
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

# Generate summary
reportgenerator -reports:"$coberturaFile" -targetdir:"$reportDir" -reporttypes:"TextSummary" 2>&1 | Out-Null

if (Test-Path "$reportDir\Summary.txt") {
    Write-Host "`n--- Coverage Report ---" -ForegroundColor Cyan
    Get-Content "$reportDir\Summary.txt"
}

# Cleanup (skip in non-interactive mode so CI can upload artifacts)
if (-not $NonInteractive) {
    Remove-Item $coberturaFile -ErrorAction SilentlyContinue
    Remove-Item $reportDir -Recurse -ErrorAction SilentlyContinue
}
