[CmdletBinding()]
param([string] $PackagePath)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The Windows package smoke requires Windows.' }
. "$PSScriptRoot\Test-ReleasePackages.ps1"

function Invoke-PackageSmokeDotnet {
    param([string[]] $Arguments)

    $PSNativeCommandUseErrorActionPreference = $false
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') exited with code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib-windows-package-smoke-' + [guid]::NewGuid().ToString('N'))
$feedDirectory = Join-Path $workDirectory 'feed'
$consumerDirectory = Join-Path $workDirectory 'consumer/scripts/package-smoke'
$fixtureDirectory = Join-Path $workDirectory 'consumer/MFTLib.Tests/TestSupport'
$previousNuGetPackages = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Path $feedDirectory, $consumerDirectory, $fixtureDirectory | Out-Null
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $version = '0.0.0-package-smoke'
        Assert-True (Test-Path -LiteralPath (Join-Path $repositoryRoot 'x64/Release/MFTLibNative.dll') -PathType Leaf) `
            'Build Release/x64 with scripts/build-windows.ps1 before running the package smoke.'
        Invoke-PackageSmokeDotnet @(
            'pack', (Join-Path $repositoryRoot 'MFTLib/MFTLib.csproj'),
            '-c', 'Release', '-p:Platform=x64', "-p:Version=$version",
            '--no-build', '--no-restore', '--output', $feedDirectory, '--nologo'
        )
        $PackagePath = Join-Path $feedDirectory "MFTLib.$version.nupkg"
    }
    else {
        $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
        $specification = Get-PackageSpecification $PackagePath
        Assert-Equal $specification.package.metadata.id 'MFTLib' 'Smoke package identity'
        $version = [string] $specification.package.metadata.version
        $destination = Join-Path $feedDirectory "MFTLib.$version.nupkg"
        Copy-Item -LiteralPath $PackagePath -Destination $destination
        $PackagePath = $destination
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        Assert-True ($null -ne $archive.GetEntry('runtimes/win-x64/native/MFTLibNative.dll')) `
            'The smoke package lacks runtimes/win-x64/native/MFTLibNative.dll.'
    }
    finally { $archive.Dispose() }

    foreach ($fileName in @('PackageSmoke.csproj', 'Program.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "package-smoke/$fileName") -Destination $consumerDirectory
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'MFTLib.Tests/TestSupport/MftDumpFixture.cs') -Destination $fixtureDirectory

    $configurationPath = Join-Path $workDirectory 'NuGet.Config'
    $escapedFeed = [Security.SecurityElement]::Escape($feedDirectory)
    [IO.File]::WriteAllText($configurationPath, "<configuration><packageSources><clear /><add key=`"smoke`" value=`"$escapedFeed`" /></packageSources></configuration>")
    $env:NUGET_PACKAGES = Join-Path $workDirectory 'packages'
    $consumerProject = Join-Path $consumerDirectory 'PackageSmoke.csproj'
    $buildDirectory = Join-Path $workDirectory 'build'
    $publishDirectory = Join-Path $workDirectory 'publish'
    $consumerProperties = @(
        "-p:MFTLibPackageVersion=$version", '-p:Platform=x64',
        '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false'
    )

    Push-Location -LiteralPath $consumerDirectory
    try {
        Invoke-PackageSmokeDotnet (@('restore', $consumerProject, '--configfile', $configurationPath) + $consumerProperties)
        Invoke-PackageSmokeDotnet (@('build', $consumerProject, '-c', 'Release', '--no-restore', '--output', $buildDirectory) + $consumerProperties)
        Invoke-PackageSmokeDotnet @((Join-Path $buildDirectory 'PackageSmoke.dll'))
        Invoke-PackageSmokeDotnet (@('publish', $consumerProject, '-c', 'Release', '--no-restore', '--output', $publishDirectory) + $consumerProperties)
        Invoke-PackageSmokeDotnet @((Join-Path $publishDirectory 'PackageSmoke.dll'))
    }
    finally { Pop-Location }

    # Check after execution so the pre-fix package fails the actual flat-copy smoke first.
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entryName in @('build/MFTLib.targets', 'buildTransitive/MFTLib.targets')) {
            Assert-True ($null -eq $archive.GetEntry($entryName)) "The smoke package contains $entryName."
        }
    }
    finally { $archive.Dispose() }
    Write-Host "Windows package smoke passed for MFTLib $version (build and RID-less publish)."
}
finally {
    $env:NUGET_PACKAGES = $previousNuGetPackages
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
