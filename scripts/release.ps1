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

# --- Verify the release commit is present on GitHub ---
# SourceLink (PublishRepositoryUrl=true + SourceLink.GitHub) embeds the exact commit
# being packed into the package, and symbol resolution for package consumers needs
# that commit reachable on the public GitHub mirror. This check runs in both dry-run
# and -Publish modes so the problem surfaces before the coverage run and pack, not
# only right before dotnet nuget push.
$releaseCommit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    Write-Host "Could not resolve the release commit with git rev-parse HEAD." -ForegroundColor Red
    exit 1
}

$githubMirrorUrl = "https://github.com/mtschoen/MFTLib.git"
Write-Host "Verifying release commit $releaseCommit is present on the GitHub mirror ($githubMirrorUrl)..." -ForegroundColor Cyan

$githubMainReference = git ls-remote $githubMirrorUrl refs/heads/main
if ($LASTEXITCODE -ne 0) {
    Write-Host "Could not reach the GitHub mirror ($githubMirrorUrl) with git ls-remote. Check network connectivity and try again." -ForegroundColor Red
    exit 1
}

$githubMainCommit = ($githubMainReference -split "`t")[0]

git fetch $githubMirrorUrl main
if ($LASTEXITCODE -ne 0) {
    Write-Host "Could not fetch main from the GitHub mirror ($githubMirrorUrl)." -ForegroundColor Red
    exit 1
}

git merge-base --is-ancestor $releaseCommit FETCH_HEAD
if ($LASTEXITCODE -ne 0) {
    Write-Host "Release commit $releaseCommit is not present on GitHub main ($githubMirrorUrl)." -ForegroundColor Red
    Write-Host "GitHub main is currently at $githubMainCommit." -ForegroundColor Red
    Write-Host "Push the release commit to GitHub and re-run:" -ForegroundColor Yellow
    Write-Host "  git push github main" -ForegroundColor Yellow
    Write-Host "  (or, if the github remote is not configured locally: git push $githubMirrorUrl main)" -ForegroundColor Yellow
    exit 1
}

Write-Host "Release commit $releaseCommit is present on GitHub main." -ForegroundColor Green
Write-Host ""

# --- Clean and restore ---
# Resolve MSBuild via vswhere: a fresh shell has no MSBuild on PATH (same fix
# as run-coverage.ps1 / native-coverage.ps1).
$vsInstallPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest 2>$null
$msbuild = if ($vsInstallPath) {
    Join-Path $vsInstallPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
} else { "MSBuild.exe" }

Write-Host "Cleaning solution..." -ForegroundColor Cyan
& $msbuild "$repoRoot\MFTLib.sln" -t:Clean -p:Configuration=Release -p:Platform=x64 -v:q -nologo

Write-Host "Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore "$repoRoot\MFTLib.sln"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed." -ForegroundColor Red
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
& $msbuild "$repoRoot\MFTLib\MFTLib.csproj" -t:Pack -p:Configuration=Release -p:Platform=x64 -p:ContinuousIntegrationBuild=true -v:q -nologo
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
if (git remote | Where-Object { $_ -eq "github" }) {
    git push github $tag
}

# --- Create GitHub release ---
Write-Host ""
Write-Host "Creating GitHub release..." -ForegroundColor Cyan
gh release create $tag $nupkg $snupkg --title $tag --notes-file "$repoRoot\CHANGELOG.md"
if ($LASTEXITCODE -ne 0) {
    Write-Host "GitHub release creation failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Release $tag complete." -ForegroundColor Green
