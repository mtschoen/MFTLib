$ErrorActionPreference = 'Stop'

& "$PSScriptRoot\test-release-package-validation.ps1"
. "$PSScriptRoot\Test-ReleasePackages.ps1"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$linuxNativeLibrary = & "$PSScriptRoot\build-linux-native.ps1"
$packageOutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ("MFTLib-release-packaging-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageOutputDirectory | Out-Null

try {
    $sharedVersionProperties = [xml](Get-Content (Join-Path $repositoryRoot 'Directory.Build.props'))
    $expectedVersion = $sharedVersionProperties.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    Assert-True (-not [string]::IsNullOrWhiteSpace($expectedVersion)) 'Directory.Build.props does not define Version.'

    foreach ($projectPath in @(
        (Join-Path $repositoryRoot 'MFTLib\MFTLib.csproj'),
        (Join-Path $repositoryRoot 'MFTLibTestExtensions\MFTLibTestExtensions.csproj')
    )) {
        dotnet pack $projectPath -c Release -p:Platform=x64 "-p:MFTLibLinuxNativeLibrary=$linuxNativeLibrary" "-p:PackageOutputPath=$packageOutputDirectory" --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet pack failed for $projectPath with exit code $LASTEXITCODE."
        }
    }

    $mftLibPackages = @(Get-ChildItem -LiteralPath $packageOutputDirectory -Filter 'MFTLib.*.nupkg' -File | Where-Object { $_.Name -notlike 'MFTLib.TestExtensions.*.nupkg' })
    Assert-Equal $mftLibPackages.Count 1 'MFTLib package count'
    $testExtensionsPackage = Get-SinglePackage $packageOutputDirectory 'MFTLib.TestExtensions.*.nupkg' 'MFTLib.TestExtensions package count'
    Assert-ReleasePackages -MftLibPackagePath $mftLibPackages[0].FullName -TestExtensionsPackagePath $testExtensionsPackage.FullName -ExpectedVersion $expectedVersion
    Assert-ReleaseSymbolPackages (Join-Path $packageOutputDirectory "MFTLib.$expectedVersion.snupkg") (Join-Path $packageOutputDirectory "MFTLib.TestExtensions.$expectedVersion.snupkg")

    Write-Host "Release packaging check passed: MFTLib and MFTLib.TestExtensions $expectedVersion."
}
finally {
    Remove-Item -LiteralPath $packageOutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
