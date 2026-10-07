# Builds the Release linux-x64 native library for the NuGet package inside WSL and checks it.
#
# Gitea's artifacts endpoint lists no uploaded artifact, so a Windows release cannot download the
# library the CI linux-package job builds. This script builds the same library the same way, on
# the same Ubuntu 24.04 floor, through scripts/build-linux.sh (Release, never the coverage build)
# and scripts/check-linux-native.sh. It returns the Windows path of the library on success.
#
# Usage: $library = .\scripts\build-linux-native.ps1 [-Distribution Ubuntu-24.04]

param(
    [string]$Distribution = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path "$PSScriptRoot\..").Path
$libraryPath = Join-Path $repositoryRoot 'build\linux\libMFTLibNative.so'

# A leftover library must never satisfy a failed build.
Remove-Item -LiteralPath $libraryPath -Force -ErrorAction SilentlyContinue

$repositoryRootInWsl = "$(wsl -d $Distribution -e wslpath -a $repositoryRoot)".Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRootInWsl)) {
    throw "WSL distribution $Distribution could not translate $repositoryRoot."
}

Write-Host "Building the Release linux-x64 library in WSL ($Distribution)..." -ForegroundColor Cyan
# The path is a positional argument of the script, never text inside it.
$buildScript = 'cd "$1" && bash scripts/build-linux.sh && bash scripts/check-linux-native.sh build/linux/libMFTLibNative.so'
wsl -d $Distribution -e bash -lc $buildScript _ $repositoryRootInWsl | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "The linux-x64 library build or its acceptance checks failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $libraryPath)) {
    throw "The build succeeded but produced no library at $libraryPath."
}

return $libraryPath
