$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Test-ReleasePackages.ps1"

function New-TestPackage {
    param(
        [string] $PackagePath,
        [string] $Identity,
        [string[]] $Entries
    )

    $archive = [IO.Compression.ZipFile]::Open($PackagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $dependency = if ($Identity -eq 'MFTLib.TestExtensions') {
            '<dependencies><dependency id="MFTLib" version="[0.3.0]" /></dependencies>'
        } else { '' }
        $specification = "<package><metadata><id>$Identity</id><version>0.3.0</version>$dependency</metadata></package>"
        $writer = [IO.StreamWriter]::new($archive.CreateEntry("$Identity.nuspec").Open())
        try { $writer.Write($specification) } finally { $writer.Dispose() }
        foreach ($entry in $Entries) {
            $archive.CreateEntry($entry) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}

function Assert-Rejected {
    param([scriptblock] $Action, [string] $ExpectedMessage)

    $failure = $null
    try { & $Action } catch { $failure = $_.Exception.Message }
    Assert-True ($null -ne $failure) "Validation accepted an incomplete release: $ExpectedMessage"
    Assert-True ($failure.Contains($ExpectedMessage)) "Unexpected validation failure: $failure"
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("MFTLib-package-validation-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    $mftLibPackage = Join-Path $temporaryDirectory 'MFTLib.nupkg'
    $testExtensionsPackage = Join-Path $temporaryDirectory 'MFTLib.TestExtensions.nupkg'
    $mftLibEntries = @(
        'lib/net10.0/MFTLib.dll',
        'runtimes/win-x64/native/MFTLibNative.dll',
        'build/MFTLib.targets',
        'buildTransitive/MFTLib.targets',
        'LICENSE.txt',
        'README.md'
    )
    $testExtensionsEntries = @('lib/net10.0/MFTLibTestExtensions.dll', 'LICENSE.txt', 'README.md')
    New-TestPackage $mftLibPackage 'MFTLib' $mftLibEntries
    New-TestPackage $testExtensionsPackage 'MFTLib.TestExtensions' $testExtensionsEntries
    Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0'
    $passed = 1

    foreach ($package in @(
        @{ Identity = 'MFTLib'; Path = $mftLibPackage; Entries = $mftLibEntries },
        @{ Identity = 'MFTLib.TestExtensions'; Path = $testExtensionsPackage; Entries = $testExtensionsEntries }
    )) {
        foreach ($missingEntry in $package.Entries | Where-Object { $_ -notlike 'lib/*' }) {
            $incompletePackage = Join-Path $temporaryDirectory ("incomplete-$passed.nupkg")
            New-TestPackage $incompletePackage $package.Identity @($package.Entries | Where-Object { $_ -cne $missingEntry })
            $libraryPath = if ($package.Identity -eq 'MFTLib') { $incompletePackage } else { $mftLibPackage }
            $extensionsPath = if ($package.Identity -eq 'MFTLib.TestExtensions') { $incompletePackage } else { $testExtensionsPackage }
            Assert-Rejected { Assert-ReleasePackages $libraryPath $extensionsPath '0.3.0' } $missingEntry
            $passed++
        }
    }

    $mftLibSymbols = Join-Path $temporaryDirectory 'MFTLib.snupkg'
    $testExtensionsSymbols = Join-Path $temporaryDirectory 'MFTLib.TestExtensions.snupkg'
    [IO.File]::WriteAllBytes($mftLibSymbols, [byte[]]@())
    [IO.File]::WriteAllBytes($testExtensionsSymbols, [byte[]]@())
    Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols
    $passed++
    foreach ($missingSymbols in @($mftLibSymbols, $testExtensionsSymbols)) {
        Remove-Item -LiteralPath $missingSymbols
        Assert-Rejected { Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols } $missingSymbols
        New-Item -ItemType Directory -Path $missingSymbols | Out-Null
        Assert-Rejected { Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols } $missingSymbols
        Remove-Item -LiteralPath $missingSymbols
        [IO.File]::WriteAllBytes($missingSymbols, [byte[]]@())
        $passed += 2
    }
    Write-Host "Release package validation regressions passed: $passed checks."
}
finally {
    Assert-Equal (Resolve-Path -LiteralPath $temporaryDirectory).Path $temporaryDirectory 'Temporary package directory'
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}
