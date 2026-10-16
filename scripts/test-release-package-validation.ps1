param([switch] $NativeIntegration)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Test-ReleasePackages.ps1"

function New-TestPackage {
    param(
        [string] $PackagePath,
        [string] $Identity,
        [string[]] $Entries,
        [string] $NativeLibraryPath
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
                if ($NativeLibraryPath) {
                    $source = [IO.File]::OpenRead($NativeLibraryPath)
                    try {
                        $destination = $createdEntry.Open()
                        try { $source.CopyTo($destination) }
                        finally { $destination.Dispose() }
                    }
                    finally { $source.Dispose() }
                }
                else {
                    $writer = [IO.StreamWriter]::new($createdEntry.Open())
                    try { $writer.Write('packed-native-fixture-540') }
                    finally { $writer.Dispose() }
                }
            }
        }
    }
    finally { $archive.Dispose() }
}

function New-TestSymbolPackage {
    param([string] $PackagePath, [string[]] $Entries)

    $archive = [IO.Compression.ZipFile]::Open($PackagePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($entryName).Open())
            try { $writer.Write('symbol-entry-fixture') }
            finally { $writer.Dispose() }
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

if ($NativeIntegration) {
    if (-not $IsWindows) { throw 'Native instruction regression requires Windows.' }
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $baselineLibrary = Join-Path $repositoryRoot 'x64/Release/MFTLibNative.dll'
    Assert-True (Test-Path -LiteralPath $baselineLibrary -PathType Leaf) 'Build Release/x64 before native instruction regression.'
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib instruction regression-' + [guid]::NewGuid().ToString('N'))
    $previousCompilerOptions = $env:_CL_
    New-Item -ItemType Directory -Path $directory | Out-Null
    try {
        $baselinePackage = Join-Path $directory 'baseline.nupkg'
        New-TestPackage $baselinePackage 'MFTLib' @('runtimes/win-x64/native/MFTLibNative.dll') $baselineLibrary
        $archive = [IO.Compression.ZipFile]::OpenRead($baselinePackage)
        try { Assert-PackagedWindowsRuntime $archive }
        finally { $archive.Dispose() }

        $outputDirectory = Join-Path $directory 'avx2-output'
        $intermediateDirectory = Join-Path $directory 'avx2-intermediate'
        $env:_CL_ = "$previousCompilerOptions /arch:AVX2"
        Invoke-WindowsBuildCommand (Get-WindowsMSBuild) @(
            (Join-Path $repositoryRoot 'MFTLibNative/MFTLibNative.vcxproj'),
            '-t:Rebuild', '-p:Configuration=Release', '-p:Platform=x64',
            '-p:PlatformToolset=v143', "-p:SolutionDir=$repositoryRoot\",
            "-p:OutDir=$outputDirectory\", "-p:IntDir=$intermediateDirectory\",
            '-v:minimal', '-nologo'
        )
        $negativeLibrary = Join-Path $outputDirectory 'MFTLibNative.dll'
        Assert-True (Test-Path -LiteralPath $negativeLibrary -PathType Leaf) 'AVX2 rebuild produced no DLL.'
        Assert-True (Test-Path -LiteralPath (Join-Path $outputDirectory 'MFTLibNative.pdb') -PathType Leaf) 'AVX2 rebuild produced no matching symbols.'
        $negativePackage = Join-Path $directory 'avx2.nupkg'
        New-TestPackage $negativePackage 'MFTLib' @('runtimes/win-x64/native/MFTLibNative.dll') $negativeLibrary
        $archive = [IO.Compression.ZipFile]::OpenRead($negativePackage)
        try {
            Assert-Rejected { Assert-PackagedWindowsRuntime $archive } 'requires AVX outside reviewed runtime'
        }
        finally { $archive.Dispose() }
        Write-Host 'Native instruction regression passed: packaged default x64 accepted; packaged AVX2 rejected.'
    }
    finally {
        $env:_CL_ = $previousCompilerOptions
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
    }
    return
}

$script:dependencyOutput = @('    KERNEL32.dll')
$script:dependencyExitCode = 0
$script:baselineDisassembly = @(
    '',
    '__isa_available_init:',
    '  0000000180001000: 33 C0              xor         eax,eax',
    'GetMftNativeAbiVersion:',
    '  0000000180001010: B8 08 00 00 00     mov         eax,8',
    '  0000000180001015: C3                 ret',
    '  0000000180001016: C5'
)
$script:disassemblyOutput = $script:baselineDisassembly
$script:disassemblyExitCode = 0
$script:dumpbinUnavailable = $false
$script:inspectedNativePaths = [Collections.Generic.List[string]]::new()
$previousExitCode = $global:LASTEXITCODE

function Get-WindowsDumpbin {
    if ($script:dumpbinUnavailable) { throw 'dumpbin.exe was not found.' }
    return 'Invoke-TestDumpbin'
}

function Invoke-TestDumpbin {
    param([string] $NoLogo, [string] $Operation, [string] $LibraryPath)

    Assert-Equal $NoLogo '/nologo' 'dumpbin banner option'
    Assert-Equal ([IO.File]::ReadAllText($LibraryPath)) 'packed-native-fixture-540' 'Packed DLL bytes'
    $script:inspectedNativePaths.Add($LibraryPath)
    switch -CaseSensitive ($Operation) {
        '/dependents' {
            $global:LASTEXITCODE = $script:dependencyExitCode
            $script:dependencyOutput
        }
        '/disasm:bytes' {
            $global:LASTEXITCODE = $script:disassemblyExitCode
            $script:disassemblyOutput
        }
        default { throw "Unexpected dumpbin operation: $Operation" }
    }
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

    foreach ($instruction in @(
        'C5 F9 EF C0        vpxor       xmm0,xmm0,xmm0',
        'C4 E2 7D 58 C0     vpbroadcastd ymm0,xmm0',
        '62 F1 7C 48 58 C0  vaddps      zmm0,zmm0,zmm0',
        '67 C5 F8 77        vzeroupper'
    )) {
        $script:disassemblyOutput = $script:baselineDisassembly + @(
            'ParseMFTRecordsWithProgress:',
            "  0000000180001020: $instruction"
        )
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } 'outside reviewed runtime'
        $passed++
    }

    foreach ($symbol in @('memcpy', 'memset', 'wcsnlen', 'log10')) {
        $script:disassemblyOutput = $script:baselineDisassembly + @(
            "${symbol}:",
            '  0000000180001030: C5 F8 77           vzeroupper'
        )
        Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0'
        $script:disassemblyOutput += @(
            'ProjectFunction:',
            '  0000000180001040: C5 F8 77           vzeroupper'
        )
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } 'ProjectFunction'
        $passed += 2
    }

    foreach ($case in @(
        @{ Lines = @(); ExitCode = 0; Message = 'no instructions' },
        @{ Lines = @('unrecognized output'); ExitCode = 0; Message = 'no instructions' },
        @{ Lines = @('  0000000180001000: 33 C0              xor eax,eax'); ExitCode = 0; Message = 'matching native PDB' },
        @{ Lines = @('__isa_available_init:', '  0000000180001000: ???'); ExitCode = 0; Message = 'Unrecognized instruction row' },
        @{ Lines = $script:baselineDisassembly; ExitCode = 7; Message = 'dumpbin /disasm:bytes failed with exit code 7' },
        @{ Lines = @('  0000000180001000: C5 F8 77           vzeroupper') + $script:baselineDisassembly; ExitCode = 0; Message = 'outside reviewed runtime' },
        @{ Lines = $script:baselineDisassembly + @('memcpy_project:', '  0000000180001040: C5 F8 77           vzeroupper'); ExitCode = 0; Message = 'outside reviewed runtime' }
    )) {
        $script:disassemblyOutput = $case.Lines
        $script:disassemblyExitCode = $case.ExitCode
        Assert-Rejected { Assert-ReleasePackages $mftLibPackage $testExtensionsPackage '0.3.0' } $case.Message
        $passed++
    }
    $script:disassemblyOutput = $script:baselineDisassembly
    $script:disassemblyExitCode = 0

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
    $symbolPackages = @(
        @{ Path = $mftLibSymbols; Entry = 'lib/net10.0/MFTLib.pdb'; OtherEntry = 'lib/net10.0/MFTLibTestExtensions.pdb' },
        @{ Path = $testExtensionsSymbols; Entry = 'lib/net10.0/MFTLibTestExtensions.pdb'; OtherEntry = 'lib/net10.0/MFTLib.pdb' }
    )
    foreach ($package in $symbolPackages) {
        New-TestSymbolPackage $package.Path @($package.Entry)
    }
    Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols
    $passed++

    $symbolFailures = [Collections.Generic.List[string]]::new()
    foreach ($package in $symbolPackages) {
        foreach ($case in @(
            @{ Name = 'missing-file'; Message = "Expected symbol package not found: $($package.Path)" },
            @{ Name = 'directory'; Message = "Expected symbol package not found: $($package.Path)" },
            @{ Name = 'empty-file'; Message = "Cannot read symbol package '$($package.Path)' as a ZIP archive" },
            @{ Name = 'non-zip'; Message = "Cannot read symbol package '$($package.Path)' as a ZIP archive" },
            @{ Name = 'pdb-less-zip'; Message = "Symbol package '$($package.Path)' is missing $($package.Entry)." },
            @{ Name = 'other-package-pdb'; Message = "Symbol package '$($package.Path)' is missing $($package.Entry)." },
            @{ Name = 'wrong-directory'; Message = "Symbol package '$($package.Path)' is missing $($package.Entry)." },
            @{ Name = 'wrong-case'; Message = "Symbol package '$($package.Path)' is missing $($package.Entry)." }
        )) {
            Remove-Item -LiteralPath $package.Path -Force
            try {
                switch ($case.Name) {
                    'missing-file' { }
                    'directory' { [IO.Directory]::CreateDirectory($package.Path) | Out-Null }
                    'empty-file' { [IO.File]::WriteAllBytes($package.Path, [byte[]]@()) }
                    'non-zip' { [IO.File]::WriteAllText($package.Path, 'not a ZIP archive') }
                    'pdb-less-zip' { New-TestSymbolPackage $package.Path @('README.md') }
                    'other-package-pdb' { New-TestSymbolPackage $package.Path @($package.OtherEntry) }
                    'wrong-directory' { New-TestSymbolPackage $package.Path @([IO.Path]::GetFileName($package.Entry)) }
                    'wrong-case' { New-TestSymbolPackage $package.Path @($package.Entry.ToUpperInvariant()) }
                }
                try {
                    Assert-Rejected {
                        Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols
                    } $case.Message
                    $passed++
                }
                catch {
                    $symbolFailures.Add("$($case.Name) / $($package.Path): $($_.Exception.Message)")
                }
            }
            finally {
                if (Test-Path -LiteralPath $package.Path) {
                    Remove-Item -LiteralPath $package.Path -Recurse -Force
                }
                New-TestSymbolPackage $package.Path @($package.Entry)
            }
        }
    }
    Assert-Equal $symbolFailures.Count 0 ("Symbol-package regressions: " + ($symbolFailures -join "`n"))
    Assert-ReleaseSymbolPackages $mftLibSymbols $testExtensionsSymbols
    $passed++

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
