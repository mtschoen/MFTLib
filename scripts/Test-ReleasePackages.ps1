# Shared release-package validation, dot-sourced by release.ps1 and test-release-packaging.ps1.
# Assert-ReleasePackages checks package identity, version, the exact [version] MFTLib
# dependency of MFTLib.TestExtensions, required package assets, and assembly separation.
# Assert-ReleaseSymbolPackages checks the symbol files. Validation throws on the first failure.

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Equal {
    param(
        [Parameter(Mandatory)] $Actual,
        [Parameter(Mandatory)] $Expected,
        [Parameter(Mandatory)] [string] $Label
    )

    if ($Actual -cne $Expected) {
        throw "$Label`: expected '$Expected', got '$Actual'."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-PackageSpecification {
    param([Parameter(Mandatory)] [string] $PackagePath)

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $specificationEntries = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
        Assert-Equal $specificationEntries.Count 1 "nuspec count in $PackagePath"

        $reader = [IO.StreamReader]::new($specificationEntries[0].Open())
        try {
            [xml] $packageSpecification = $reader.ReadToEnd()
            return $packageSpecification
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-SinglePackage {
    param(
        [Parameter(Mandatory)] [string] $PackageOutputDirectory,
        [Parameter(Mandatory)] [string] $Filter,
        [Parameter(Mandatory)] [string] $Label
    )

    $packages = @(Get-ChildItem -LiteralPath $PackageOutputDirectory -Filter $Filter -File)
    Assert-Equal $packages.Count 1 $Label
    return $packages[0]
}

function Assert-ReleasePackages {
    param(
        [Parameter(Mandatory)] [string] $MftLibPackagePath,
        [Parameter(Mandatory)] [string] $TestExtensionsPackagePath,
        [Parameter(Mandatory)] [string] $ExpectedVersion
    )

    Assert-True (Test-Path -LiteralPath $MftLibPackagePath) "Expected package not found: $MftLibPackagePath"
    Assert-True (Test-Path -LiteralPath $TestExtensionsPackagePath) "Expected package not found: $TestExtensionsPackagePath"
    $mftLibSpecification = Get-PackageSpecification $MftLibPackagePath
    $testExtensionsSpecification = Get-PackageSpecification $TestExtensionsPackagePath
    $expectedVersion = $ExpectedVersion

    Assert-Equal $mftLibSpecification.package.metadata.id 'MFTLib' 'MFTLib package identity'
    Assert-Equal $testExtensionsSpecification.package.metadata.id 'MFTLib.TestExtensions' 'MFTLib.TestExtensions package identity'
    Assert-Equal $mftLibSpecification.package.metadata.version $expectedVersion 'MFTLib package version'
    Assert-Equal $testExtensionsSpecification.package.metadata.version $expectedVersion 'MFTLib.TestExtensions package version'

    $testExtensionsDependencies = @(
        $testExtensionsSpecification.package.metadata.dependencies.dependency
        $testExtensionsSpecification.package.metadata.dependencies.group.dependency
    )
    $mftLibDependency = @($testExtensionsDependencies | Where-Object { $_.id -eq 'MFTLib' })
    Assert-Equal $mftLibDependency.Count 1 'MFTLib.TestExtensions MFTLib dependency count'
    Assert-Equal $mftLibDependency[0].version "[$expectedVersion]" 'MFTLib.TestExtensions MFTLib dependency version'

    $mftLibArchive = [IO.Compression.ZipFile]::OpenRead($MftLibPackagePath)
    try {
        Assert-True (-not @($mftLibArchive.Entries | Where-Object { $_.FullName -like '*MFTLibTestExtensions.dll' }).Count) 'MFTLib package contains MFTLibTestExtensions.dll.'
        foreach ($requiredEntry in @(
            'lib/net10.0/MFTLib.xml',
            'runtimes/win-x64/native/MFTLibNative.dll',
            'runtimes/linux-x64/native/libMFTLibNative.so',
            'build/MFTLib.targets',
            'buildTransitive/MFTLib.targets',
            'LICENSE.txt',
            'README.md'
        )) {
            Assert-True ($null -ne $mftLibArchive.GetEntry($requiredEntry)) "MFTLib package is missing $requiredEntry."
        }
    }
    finally {
        $mftLibArchive.Dispose()
    }

    $testExtensionsArchive = [IO.Compression.ZipFile]::OpenRead($TestExtensionsPackagePath)
    try {
        Assert-True (@($testExtensionsArchive.Entries | Where-Object { $_.FullName -like 'lib/*/MFTLibTestExtensions.dll' }).Count -eq 1) 'MFTLib.TestExtensions package does not contain MFTLibTestExtensions.dll.'
        foreach ($requiredEntry in @('LICENSE.txt', 'README.md')) {
            Assert-True ($null -ne $testExtensionsArchive.GetEntry($requiredEntry)) "MFTLib.TestExtensions package is missing $requiredEntry."
        }
    }
    finally {
        $testExtensionsArchive.Dispose()
    }
}

function Assert-ReleaseSymbolPackages {
    param(
        [Parameter(Mandatory)] [string] $LibrarySymbolPackagePath,
        [Parameter(Mandatory)] [string] $TestExtensionsSymbolPackagePath
    )

    foreach ($symbolPackagePath in @($LibrarySymbolPackagePath, $TestExtensionsSymbolPackagePath)) {
        Assert-True (Test-Path -LiteralPath $symbolPackagePath -PathType Leaf) "Expected symbol package not found: $symbolPackagePath"
    }
}
