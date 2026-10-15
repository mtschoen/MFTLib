function Invoke-WindowsBuildCommand {
    param([string] $FilePath, [string[]] $Arguments)

    $PSNativeCommandUseErrorActionPreference = $false
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') exited with code $LASTEXITCODE"
    }
}

function Get-WindowsVisualStudioInstallation {
    if (-not ${env:ProgramFiles(x86)}) {
        throw 'ProgramFiles(x86) is not set; run this build on Windows.'
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere was not found at $vswhere; install Visual Studio with the MSVC C++ workload."
    }
    $installation = Invoke-WindowsBuildCommand $vswhere @(
        '-products', '*', '-requires', 'Microsoft.Component.MSBuild',
        '-property', 'installationPath', '-latest'
    )
    if ([string]::IsNullOrWhiteSpace($installation)) {
        throw 'Visual Studio with MSBuild was not found; install the MSVC C++ workload.'
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

function Get-WindowsCodeCoverage {
    $installation = Get-WindowsVisualStudioInstallation
    $coverageTool = Join-Path $installation 'Common7/IDE/Extensions/Microsoft/CodeCoverage.Console/Microsoft.CodeCoverage.Console.exe'
    if (-not (Test-Path -LiteralPath $coverageTool -PathType Leaf)) {
        throw "Microsoft.CodeCoverage.Console was not found at $coverageTool; install the Visual Studio code coverage tools in the selected installation."
    }
    return $coverageTool
}
