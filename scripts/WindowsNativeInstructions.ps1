# Symbol-assisted release check; the real AVX2 negative control guards attribution.
# Exact runtime names below were reviewed against MSVC CPU-dispatched code.
function Assert-WindowsDisassembly {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines,
        [Parameter(Mandatory)] [string] $LibraryPath
    )

    $runtimeSymbols = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in @(
        'memcpy', 'memmove', 'memset', 'wcsnlen', 'log10',
        '`anonymous namespace''::__std_find_trivial_impl<`anonymous namespace''::_Find_traits_8,unsigned __int64>',
        '`anonymous namespace''::__std_minmax_disp<1,`anonymous namespace''::_Minmax_traits_8,0>',
        '`anonymous namespace''::__std_minmax_element_impl<2,`anonymous namespace''::_Minmax_traits_d_avx>',
        '`anonymous namespace''::__std_minmax_impl<1,`anonymous namespace''::_Minmax_traits_8_avx,0>'
    )) { [void] $runtimeSymbols.Add($name) }

    $symbol = '<unattributed>'
    $instructionCount = 0
    $hasPrivateSymbols = $false
    foreach ($line in $Lines) {
        if ($line -cmatch '^(?<Symbol>\S.*):$') {
            $symbol = $Matches.Symbol
            if ($symbol -ceq '__isa_available_init') { $hasPrivateSymbols = $true }
            continue
        }
        if ($line -notmatch '^\s+[0-9A-Fa-f]{16}:') { continue }
        # dumpbin also emits raw undecoded bytes for inline jump-table data.
        if ($line -match '^\s+[0-9A-Fa-f]{16}:\s+(?:[0-9A-Fa-f]{2}(?:\s+|$))+$') { continue }
        if ($line -notmatch '^\s+(?<Address>[0-9A-Fa-f]{16}):\s+(?<Bytes>(?:[0-9A-Fa-f]{2}\s+)+)(?<Assembly>[A-Za-z][^\r\n]*)$') {
            throw "Unrecognized instruction row in '$LibraryPath': $line"
        }
        $instructionCount++
        $address = $Matches.Address
        $assembly = $Matches.Assembly.Trim()
        $bytes = $Matches.Bytes.Trim()
        # Skip legacy address/segment prefixes before the VEX/EVEX prefix.
        $bytes = $bytes -replace '^(?:(?:26|2E|36|3E|64|65|66|67|F0|F2|F3)\s+)+', ''
        if ($bytes -match '^(?:C4|C5|62)(?:\s|$)' -and -not $runtimeSymbols.Contains($symbol)) {
            throw "'$LibraryPath' requires AVX outside reviewed runtime: $symbol at $address ($assembly). Build Release|x64 for plain x64."
        }
    }
    if ($instructionCount -eq 0) {
        throw "dumpbin reported no instructions for '$LibraryPath'."
    }
    if (-not $hasPrivateSymbols) {
        throw "Cannot attribute instructions in '$LibraryPath'; make the matching native PDB available to dumpbin."
    }
}

function Assert-WindowsNativeInstructions {
    param([Parameter(Mandatory)] [string] $LibraryPath)

    $dumpbin = Get-WindowsDumpbin
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& $dumpbin /nologo /disasm:bytes $LibraryPath)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dumpbin /disasm:bytes failed with exit code $exitCode for '$LibraryPath'."
    }
    Assert-WindowsDisassembly -Lines $output -LibraryPath $LibraryPath
}
