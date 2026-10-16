function Invoke-WindowsBuildCommand {
    param([string] $FilePath, [string[]] $Arguments)

    $PSNativeCommandUseErrorActionPreference = $false
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') exited with code $LASTEXITCODE"
    }
}

function Get-WindowsVisualStudioInstallation {
    param([string] $RequiresComponent = 'Microsoft.Component.MSBuild')

    if (-not ${env:ProgramFiles(x86)}) {
        throw 'ProgramFiles(x86) is not set; run this build on Windows.'
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere was not found at $vswhere; install Visual Studio with the MSVC C++ workload."
    }
    $installation = Invoke-WindowsBuildCommand $vswhere @(
        '-products', '*', '-requires', $RequiresComponent,
        '-property', 'installationPath', '-latest'
    )
    if ([string]::IsNullOrWhiteSpace($installation)) {
        $tools = if ($RequiresComponent -eq 'Microsoft.Component.MSBuild') { 'MSBuild' } else { 'MSVC C++ tools' }
        throw "Visual Studio with $tools was not found; install the MSVC C++ workload."
    }
    return $installation.Trim()
}

function Get-WindowsMSBuild {
    $installation = Get-WindowsVisualStudioInstallation
    $msbuild = Join-Path $installation 'MSBuild/Current/Bin/amd64/MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        throw "64-bit MSBuild was not found at $msbuild."
    }
    return $msbuild
}

function Get-WindowsDumpbin {
    $command = Get-Command dumpbin.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $installation = Get-WindowsVisualStudioInstallation -RequiresComponent 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    $toolsets = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    Get-ChildItem -LiteralPath (Join-Path $installation 'VC/Tools/MSVC') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $toolsets.Add($_) }
    $toolsets.Sort([Comparison[IO.DirectoryInfo]] {
        param($left, $right)
        $leftVersion = $null
        $rightVersion = $null
        $leftParsed = [version]::TryParse($left.Name, [ref]$leftVersion)
        $rightParsed = [version]::TryParse($right.Name, [ref]$rightVersion)
        if ($leftParsed -ne $rightParsed) { return $rightParsed.CompareTo($leftParsed) }
        if ($leftParsed) {
            $comparison = $rightVersion.CompareTo($leftVersion)
            if ($comparison -ne 0) { return $comparison }
        }
        return [StringComparer]::Ordinal.Compare($right.Name, $left.Name)
    })
    foreach ($toolset in $toolsets) {
        $dumpbin = Join-Path $toolset.FullName 'bin/Hostx64/x64/dumpbin.exe'
        if (Test-Path -LiteralPath $dumpbin -PathType Leaf) { return $dumpbin }
    }
    throw "dumpbin.exe was not found in $installation; install Visual Studio with the MSVC C++ tools."
}

function Get-WindowsCodeCoverage {
    $installation = Get-WindowsVisualStudioInstallation
    $coverageTool = Join-Path $installation 'Common7/IDE/Extensions/Microsoft/CodeCoverage.Console/Microsoft.CodeCoverage.Console.exe'
    if (-not (Test-Path -LiteralPath $coverageTool -PathType Leaf)) {
        throw "Microsoft.CodeCoverage.Console was not found at $coverageTool; install the Visual Studio code coverage tools in the selected installation."
    }
    return $coverageTool
}
