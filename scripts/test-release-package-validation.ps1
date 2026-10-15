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
            $createdEntry = $archive.CreateEntry($entry)
            if ($entry -ceq 'runtimes/win-x64/native/MFTLibNative.dll') {
                $writer = [IO.StreamWriter]::new($createdEntry.Open())
                try { $writer.Write('packed-native-fixture-540') }
                finally { $writer.Dispose() }
            }
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

$script:dependencyOutput = @('    KERNEL32.dll')
$script:dependencyExitCode = 0
$script:dumpbinUnavailable = $false
$script:inspectedNativePaths = [Collections.Generic.List[string]]::new()
$previousExitCode = $global:LASTEXITCODE

function Get-WindowsDumpbin {
    if ($script:dumpbinUnavailable) { throw 'dumpbin.exe was not found.' }
    return 'Invoke-TestDumpbin'
}

function Invoke-TestDumpbin {
    param([string] $NoLogo, [string] $Dependents, [string] $LibraryPath)

    Assert-Equal $NoLogo '/nologo' 'dumpbin banner option'
    Assert-Equal $Dependents '/dependents' 'dumpbin dependency option'
    Assert-Equal ([IO.File]::ReadAllText($LibraryPath)) 'packed-native-fixture-540' 'Packed DLL bytes'
    $script:inspectedNativePaths.Add($LibraryPath)
    $global:LASTEXITCODE = $script:dependencyExitCode
    $script:dependencyOutput
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib package validation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
    $mftLibPackage = Join-Path $temporaryDirectory 'MFTLib.nupkg'
    $testExtensionsPackage = Join-Path $temporaryDirectory 'MFTLib.TestExtensions.nupkg'
    $mftLibEntries = @(
        'lib/net10.0/MFTLib.dll',
        'lib/net10.0/MFTLib.xml',
        'runtimes/win-x64/native/MFTLibNative.dll',
        'runtimes/linux-x64/native/libMFTLibNative.so',
        'build/MFTLib.targets',
        'buildTransitive/MFTLib.targets',
        'LICENSE.txt',
        'README.md'
    )
    $testExtensionsEntries = @(
        'lib/net10.0/MFTLibTestExtensions.dll',
        'lib/net10.0/MFTLibTestExtensions.xml',
        'LICENSE.txt',
        'README.md'
    )
    New-TestPackage $mftLibPackage 'MFTLib' $mftLibEntries
    New-TestPackage $testExtensionsPackage 'MFTLib.TestExtensions' $testExtensionsEntries
    Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0'
    $passed = 1

    foreach ($dependency in @(
        'MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll',
        'MSVCP140D.dll', 'VCRUNTIME140D.dll', 'msvcp140_atomic_wait.dll',
        'vcruntime140_1d.DLL'
    )) {
        $script:dependencyOutput = @(
            'Dump of file MFTLibNative.dll',
            '  Image has the following dependencies:',
            '    KERNEL32.dll',
            "    $dependency",
            '  Summary'
        )
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } $dependency
        $passed++
    }

    $script:dependencyOutput = @(
        'Dump of file MFTLibNative.dll',
        '  Image has the following dependencies:',
        '    KERNEL32.dll',
        '    api-ms-win-crt-runtime-l1-1-0.dll',
        '    ucrtbase.dll',
        '  Summary'
    )
    Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0'
    $passed++

    foreach ($inspectionFailure in @(
        @{ Output = @('    KERNEL32.dll'); ExitCode = 7; Message = 'dumpbin /dependents failed with exit code 7' },
        @{ Output = @(); ExitCode = 0; Message = 'dumpbin reported no dependency filenames' },
        @{ Output = @('unrecognized output'); ExitCode = 0; Message = 'dumpbin reported no dependency filenames' }
    )) {
        $script:dependencyOutput = $inspectionFailure.Output
        $script:dependencyExitCode = $inspectionFailure.ExitCode
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } $inspectionFailure.Message
        $passed++
    }

    $script:dependencyExitCode = 0
    $script:dependencyOutput = @('    KERNEL32.dll')
    $script:dumpbinUnavailable = $true
    try {
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } 'dumpbin.exe was not found'
        $passed++
    }
    finally { $script:dumpbinUnavailable = $false }

    $validationFailures = [Collections.Generic.List[string]]::new()
    foreach ($package in @(
        @{ Identity = 'MFTLib'; Path = $mftLibPackage; Entries = $mftLibEntries },
        @{ Identity = 'MFTLib.TestExtensions'; Path = $testExtensionsPackage; Entries = $testExtensionsEntries }
    )) {
        foreach ($missingEntry in $package.Entries) {
            $incompletePackage = Join-Path $temporaryDirectory ("incomplete-$passed.nupkg")
            New-TestPackage $incompletePackage $package.Identity @($package.Entries | Where-Object { $_ -cne $missingEntry })
            $libraryPath = if ($package.Identity -eq 'MFTLib') { $incompletePackage } else { $mftLibPackage }
            $extensionsPath = if ($package.Identity -eq 'MFTLib.TestExtensions') { $incompletePackage } else { $testExtensionsPackage }
            $expectedMessage = if ($missingEntry -ceq 'lib/net10.0/MFTLibTestExtensions.dll') {
                'MFTLibTestExtensions.dll'
            } else {
                $missingEntry
            }
            try {
                Assert-Rejected { Assert-ReleasePackages $libraryPath $extensionsPath '0.3.0' } $expectedMessage
            }
            catch {
                $validationFailures.Add("$($package.Identity): $($_.Exception.Message)")
            }
            $passed++
        }
    }
    Assert-Equal $validationFailures.Count 0 ("Missing-entry regressions: " + ($validationFailures -join "`n"))

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

    Assert-True ($script:inspectedNativePaths.Count -gt 0) 'The packaged DLL was never inspected.'
    foreach ($nativePath in $script:inspectedNativePaths) {
        Assert-True (-not (Test-Path -LiteralPath (Split-Path -Parent $nativePath))) "Inspection directory leaked: $nativePath"
    }

    Write-Host "Release package validation regressions passed: $passed checks."
}
finally {
    $global:LASTEXITCODE = $previousExitCode
    Assert-Equal (Resolve-Path -LiteralPath $temporaryDirectory).Path $temporaryDirectory 'Temporary package directory'
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
}
