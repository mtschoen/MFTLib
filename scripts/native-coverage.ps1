# Native C++ code coverage for MFTLibNative using Microsoft.CodeCoverage.Console
# Builds Debug|x64 (linked with /PROFILE), instruments the DLL, runs tests, and reports.
#
# Usage:
#   .\.claude\scripts\native-coverage.ps1
#   .\.claude\scripts\native-coverage.ps1 -HtmlReport   # also generate HTML report

param(
    [switch]$HtmlReport
)

$ErrorActionPreference = "Stop"

$coverageTool = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Extensions\Microsoft\CodeCoverage.Console\Microsoft.CodeCoverage.Console.exe"
$solutionRoot = Split-Path -Parent $PSScriptRoot
$nativeDll = Join-Path $solutionRoot "MFTLib.Tests\bin\x64\Debug\net8.0\MFTLibNative.dll"
$testProject = Join-Path $solutionRoot "MFTLib.Tests\MFTLib.Tests.csproj"
$coberturaOutput = Join-Path $solutionRoot "native-coverage.cobertura.xml"
$settings = Join-Path $solutionRoot "native-coverage.runsettings"

if (-not (Test-Path $coverageTool)) {
    Write-Error "Microsoft.CodeCoverage.Console not found at: $coverageTool"
    exit 1
}

Write-Host "=== Building Debug|x64 ===" -ForegroundColor Cyan
Push-Location $solutionRoot
MSBuild.exe MFTLib.sln -p:Configuration=Debug -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }
Pop-Location

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
if ($LASTEXITCODE -ne 0) { Write-Warning "Test run returned non-zero exit code" }

Write-Host "`n=== Native Coverage Results ===" -ForegroundColor Cyan
if (Test-Path $coberturaOutput) {
    [xml]$coverage = Get-Content $coberturaOutput
    foreach ($package in $coverage.coverage.packages.package) {
        if ($package.name -eq "MFTLibNative") {
            $lineRate = [math]::Round([double]$package.'line-rate' * 100, 1)
            $branchRate = [math]::Round([double]$package.'branch-rate' * 100, 1)
            Write-Host "  MFTLibNative: ${lineRate}% line, ${branchRate}% branch" -ForegroundColor Green

            foreach ($class in $package.classes.class) {
                $className = if ($class.name) { $class.name } else { "(global)" }
                $classLineRate = [math]::Round([double]$class.'line-rate' * 100, 1)
                if ($classLineRate -lt 100) {
                    $uncoveredLines = @()
                    foreach ($line in $class.methods.method.lines.line) {
                        if ([int]$line.hits -eq 0) {
                            $uncoveredLines += [int]$line.number
                        }
                    }
                    # Also check class-level lines
                    foreach ($line in $class.lines.line) {
                        if ([int]$line.hits -eq 0) {
                            $uncoveredLines += [int]$line.number
                        }
                    }
                    $uncoveredLines = $uncoveredLines | Sort-Object -Unique
                    Write-Host "    $className`: ${classLineRate}% ($($uncoveredLines.Count) uncovered lines)"
                }
            }
        }
    }
    Write-Host "`nCobertura report: $coberturaOutput"
} else {
    Write-Warning "Coverage output not found at $coberturaOutput"
}

if ($HtmlReport) {
    $htmlOutput = Join-Path $solutionRoot "native-coverage-html"
    Write-Host "`n=== Generating HTML report ===" -ForegroundColor Cyan
    dotnet tool run reportgenerator -- "-reports:$coberturaOutput" "-targetdir:$htmlOutput" "-reporttypes:Html" "-filefilters:+*dllmain.cpp" 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "HTML report: $htmlOutput\index.html"
    } else {
        Write-Warning "Install reportgenerator for HTML: dotnet tool install --global dotnet-reportgenerator-globaltool"
    }
}
