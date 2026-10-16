# Shared release-package validation, dot-sourced by release.ps1 and test-release-packaging.ps1.
# Assert-ReleasePackages checks package identity, version, the exact [version] MFTLib
# dependency of MFTLib.TestExtensions, required assets, assembly separation, and the
# packed Windows DLL's imports and plain-x64 instruction baseline using dumpbin, with matching
# native build symbols required for attribution. MSVCP* and VCRUNTIME* imports are rejected.
# Assert-ReleaseSymbolPackages opens each symbol ZIP and requires its managed PDB entry.
# Validation throws on the first failure.

Add-Type -AssemblyName System.IO.Compression.FileSystem
. "$PSScriptRoot/windows-tools.ps1"
. "$PSScriptRoot/WindowsNativeInstructions.ps1"

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

function Get-WindowsNativeDependencies {
    param([Parameter(Mandatory)] [string] $LibraryPath)

    $dumpbin = Get-WindowsDumpbin
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& $dumpbin /nologo /dependents $LibraryPath)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dumpbin /dependents failed with exit code $exitCode for '$LibraryPath'."
    }
    $dependencies = @(
        foreach ($line in $output) {
            if ($line -match '(?i)^\s*([^\s\\/]+\.dll)\s*$') {
                $Matches[1]
            }
        }
    )
    if ($dependencies.Count -eq 0) {
        throw "dumpbin reported no dependency filenames for '$LibraryPath'."
    }
    return $dependencies
}

function Assert-PackagedWindowsRuntime {
    param([Parameter(Mandatory)] [IO.Compression.ZipArchive] $Archive)

    $entryName = 'runtimes/win-x64/native/MFTLibNative.dll'
    $entry = $Archive.GetEntry($entryName)
    Assert-True ($null -ne $entry) "MFTLib package is missing $entryName."
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib native inspection-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        $libraryPath = Join-Path $directory 'MFTLibNative.dll'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $libraryPath)
        $dependencies = @(Get-WindowsNativeDependencies $libraryPath)
        $forbidden = @($dependencies | Where-Object { $_ -match '^(MSVCP|VCRUNTIME)' })
        Assert-True ($forbidden.Count -eq 0) "$entryName imports the Visual C++ redistributable: $($forbidden -join ', '). Build Release|x64 with MultiThreaded."
        Assert-WindowsNativeInstructions -LibraryPath $libraryPath
    }
    finally {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
    }
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
            'lib/net10.0/MFTLib.dll',
            'lib/net10.0/MFTLib.xml',
            'runtimes/win-x64/native/MFTLibNative.dll',
            'runtimes/linux-x64/native/libMFTLibNative.so',
            'LICENSE.txt',
            'README.md'
        )) {
            Assert-True ($null -ne $mftLibArchive.GetEntry($requiredEntry)) "MFTLib package is missing $requiredEntry."
        }
        Assert-PackagedWindowsRuntime $mftLibArchive
    }
    finally {
        $mftLibArchive.Dispose()
    }

    $testExtensionsArchive = [IO.Compression.ZipFile]::OpenRead($TestExtensionsPackagePath)
    try {
        Assert-True (@($testExtensionsArchive.Entries | Where-Object { $_.FullName -like 'lib/*/MFTLibTestExtensions.dll' }).Count -eq 1) 'MFTLib.TestExtensions package does not contain MFTLibTestExtensions.dll.'
        foreach ($requiredEntry in @('lib/net10.0/MFTLibTestExtensions.xml', 'LICENSE.txt', 'README.md')) {
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

    $symbolPackages = @(
        @{ Path = $LibrarySymbolPackagePath; Entry = 'lib/net10.0/MFTLib.pdb' },
        @{ Path = $TestExtensionsSymbolPackagePath; Entry = 'lib/net10.0/MFTLibTestExtensions.pdb' }
    )
    foreach ($package in $symbolPackages) {
        $symbolPackagePath = $package.Path
        Assert-True (Test-Path -LiteralPath $symbolPackagePath -PathType Leaf) "Expected symbol package not found: $symbolPackagePath"
        try {
            $archive = [IO.Compression.ZipFile]::OpenRead($symbolPackagePath)
        }
        catch {
            throw "Cannot read symbol package '$symbolPackagePath' as a ZIP archive: $($_.Exception.Message)"
        }
        try {
            Assert-True ($null -ne $archive.GetEntry($package.Entry)) "Symbol package '$symbolPackagePath' is missing $($package.Entry)."
        }
        finally { $archive.Dispose() }
    }
}
