# Native C++ code coverage for MFTLibNative using Microsoft.CodeCoverage.Console
# Builds Debug|x64 (linked with /PROFILE), instruments the DLL, runs tests, and reports.
#
# Usage:
#   .\scripts\native-coverage.ps1
#   .\scripts\native-coverage.ps1 -HtmlReport   # also generate HTML report

param(
    [switch]$HtmlReport
)

if (-not $HtmlReport -and ($args -contains '-HtmlReport')) {
    $HtmlReport = [switch]::new($true)
}

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

. "$PSScriptRoot/windows-tools.ps1"
$coverageTool = Get-WindowsCodeCoverage
$solutionRoot = Split-Path -Parent $PSScriptRoot
$nativeDll = Join-Path $solutionRoot "MFTLib.Tests\bin\x64\Debug\net10.0\MFTLibNative.dll"
$testProject = Join-Path $solutionRoot "MFTLib.Tests\MFTLib.Tests.csproj"
$coberturaOutput = Join-Path $solutionRoot "native-coverage.cobertura.xml"
$settings = Join-Path $solutionRoot "native-coverage.runsettings"
$htmlOutput = Join-Path $solutionRoot 'native-coverage-html'
$evidencePaths = @($coberturaOutput)
if ($HtmlReport) { $evidencePaths += $htmlOutput }
foreach ($path in $evidencePaths) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Write-Host "=== Building Debug|x64 ===" -ForegroundColor Cyan
& "$PSScriptRoot/build-windows.ps1" -Configuration Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host "`n=== Instrumenting MFTLibNative.dll ===" -ForegroundColor Cyan
& $coverageTool instrument $nativeDll
if ($LASTEXITCODE -ne 0) { Write-Error "Instrumentation failed"; exit 1 }

Write-Host "`n=== Running tests with coverage collection ===" -ForegroundColor Cyan
& $coverageTool collect `
    --settings $settings `
    --output $coberturaOutput `
    --output-format cobertura `
    --include-files MFTLibNative.dll `
    -- dotnet test $testProject -p:Platform=x64 --no-build -c Debug
$collectionExitCode = $LASTEXITCODE
if ($collectionExitCode -ne 0) {
    Write-Warning "Coverage collection failed with exit code $collectionExitCode"
    exit $collectionExitCode
}
if (-not (Test-Path -LiteralPath $coberturaOutput -PathType Leaf)) {
    throw "Coverage output not found at $coberturaOutput"
}
try {
    [xml]$coverage = Get-Content -LiteralPath $coberturaOutput -Raw
    if ($null -eq $coverage.DocumentElement) { throw 'XML has no root element' }
} catch {
    throw "Invalid native coverage XML at ${coberturaOutput}: $($_.Exception.Message)"
}
$nativePackages = @($coverage.SelectNodes("/coverage/packages/package[@name='MFTLibNative']"))
if ($nativePackages.Count -ne 1) {
    throw "Expected one MFTLibNative package in $coberturaOutput; found $($nativePackages.Count)"
}
$package = $nativePackages[0]
foreach ($attribute in @('line-rate', 'branch-rate')) {
    $rate = 0.0
    $validRate = [double]::TryParse(
        $package.GetAttribute($attribute),
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$rate)
    if (-not $validRate -or [double]::IsNaN($rate) -or [double]::IsInfinity($rate) -or $rate -lt 0 -or $rate -gt 1) {
        throw "Invalid native coverage $attribute in $coberturaOutput"
    }
}

Write-Host "`n=== Native Coverage Results ===" -ForegroundColor Cyan
$lineRate = [math]::Round([double]$package.'line-rate' * 100, 1)
$branchRate = [math]::Round([double]$package.'branch-rate' * 100, 1)
Write-Host "  MFTLibNative: ${lineRate}% line, ${branchRate}% branch" -ForegroundColor Green
foreach ($class in $package.classes.class) {
    $className = if ($class.name) { $class.name } else { '(global)' }
    $classLineRate = [math]::Round([double]$class.'line-rate' * 100, 1)
    if ($classLineRate -lt 100) {
        $uncoveredLines = @()
        foreach ($line in $class.methods.method.lines.line) {
            if ([int]$line.hits -eq 0) { $uncoveredLines += [int]$line.number }
        }
        foreach ($line in $class.lines.line) {
            if ([int]$line.hits -eq 0) { $uncoveredLines += [int]$line.number }
        }
        $uncoveredLines = $uncoveredLines | Sort-Object -Unique
        Write-Host "    $className`: ${classLineRate}% ($($uncoveredLines.Count) uncovered lines)"
    }
}
Write-Host "`nCobertura report: $coberturaOutput"

if ($HtmlReport) {
    if (-not (Get-Command reportgenerator -ErrorAction SilentlyContinue)) {
        Write-Warning 'reportgenerator was not found on PATH. Install the global tool: dotnet tool install --global dotnet-reportgenerator-globaltool; ensure its tools directory is on PATH.'
        exit 1
    }
    Write-Host "`n=== Generating HTML report ===" -ForegroundColor Cyan
    & reportgenerator "-reports:$coberturaOutput" "-targetdir:$htmlOutput" "-reporttypes:Html"
    $reportExitCode = $LASTEXITCODE
    if ($reportExitCode -ne 0) {
        Write-Warning "HTML report generation failed with exit code $reportExitCode"
        exit $reportExitCode
    }
    $htmlIndex = Join-Path $htmlOutput 'index.html'
    if (-not (Test-Path -LiteralPath $htmlIndex -PathType Leaf)) {
        throw "HTML report was not generated at $htmlIndex"
    }
    Write-Host "HTML report: $htmlIndex"
}
exit 0
