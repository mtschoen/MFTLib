# Release script for MFTLib.
# Runs coverage, packs the NuGet package, tags the release, and publishes.
#
# Usage:
#   .\scripts\release.ps1          # dry run (build + test + pack only)
#   .\scripts\release.ps1 -Publish # full release (publish + tag + push)

param(
    [switch]$Publish
)

$nuGetKeyFile = "C:\Users\mtsch\nugetkey"

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path "$PSScriptRoot\.."
Set-Location $repoRoot

# Read version from MFTLib.csproj
[xml]$csproj = Get-Content "$repoRoot\MFTLib\MFTLib.csproj"
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    Write-Host "Could not read version from MFTLib.csproj." -ForegroundColor Red
    exit 1
}

$tag = "v$version"
$nupkg = "$repoRoot\MFTLib\bin\x64\Release\MFTLib.$version.nupkg"
$snupkg = "$repoRoot\MFTLib\bin\x64\Release\MFTLib.$version.snupkg"

Write-Host "Releasing MFTLib $tag" -ForegroundColor Cyan
Write-Host ""

# --- Preflight checks ---
if (git status --porcelain) {
    Write-Host "Working tree is dirty. Commit or stash changes before releasing." -ForegroundColor Red
    exit 1
}

if (git tag -l $tag) {
    Write-Host "Tag $tag already exists." -ForegroundColor Red
    exit 1
}

# --- Run coverage (builds the solution internally) ---
Write-Host "Running coverage..." -ForegroundColor Cyan
& "$PSScriptRoot\run-coverage.ps1" -Configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Coverage failed. Aborting release." -ForegroundColor Red
    exit 1
}

Write-Host ""

# --- Pack NuGet ---
Write-Host "Packing NuGet package..." -ForegroundColor Cyan
& MSBuild.exe "$repoRoot\MFTLib\MFTLib.csproj" -t:Pack -p:Configuration=Release -p:Platform=x64 -v:q -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Pack failed." -ForegroundColor Red
    exit 1
}

if (!(Test-Path $nupkg)) {
    Write-Host "Expected package not found: $nupkg" -ForegroundColor Red
    exit 1
}

Write-Host "Package created: $nupkg" -ForegroundColor Green

# --- Dry run stops here ---
if (-not $Publish) {
    Write-Host ""
    Write-Host "Dry run complete. To publish, re-run with:" -ForegroundColor Yellow
    Write-Host "  .\scripts\release.ps1 -Publish" -ForegroundColor Yellow
    exit 0
}

# --- Read NuGet API key ---
if (!(Test-Path $nuGetKeyFile)) {
    Write-Host "NuGet API key file not found: $nuGetKeyFile" -ForegroundColor Red
    exit 1
}

$NuGetApiKey = (Get-Content $nuGetKeyFile -Raw).Trim()
if (-not $NuGetApiKey) {
    Write-Host "NuGet API key file is empty." -ForegroundColor Red
    exit 1
}

# --- Publish to NuGet ---
Write-Host ""
Write-Host "Publishing to NuGet..." -ForegroundColor Cyan
dotnet nuget push $nupkg --api-key $NuGetApiKey --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) {
    Write-Host "NuGet push failed." -ForegroundColor Red
    exit 1
}

Write-Host "Published MFTLib $version to NuGet." -ForegroundColor Green

# --- Tag and push ---
Write-Host ""
Write-Host "Tagging $tag..." -ForegroundColor Cyan
git tag $tag
git push origin $tag

Write-Host ""
Write-Host "Release $tag complete." -ForegroundColor Green
