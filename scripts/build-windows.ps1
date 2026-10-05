<#
.SYNOPSIS
Restores and builds the Windows native and managed projects.
.PARAMETER Configuration
Build configuration; defaults to Release. The platform is always x64.
.PARAMETER NoRestore
Reuse a restore already completed by the caller, as init.ps1 does.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

function Invoke-WindowsBuildCommand {
    param([string] $FilePath, [string[]] $Arguments)

    $PSNativeCommandUseErrorActionPreference = $false
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') exited with code $LASTEXITCODE"
    }
}

function Get-WindowsMSBuild {
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
    $msbuild = Join-Path $installation.Trim() 'MSBuild/Current/Bin/amd64/MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild -PathType Leaf)) {
        throw "64-bit MSBuild was not found at $msbuild."
    }
    return $msbuild
}

function Invoke-WindowsBuild {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [string] $Configuration = 'Release',
        [switch] $NoRestore
    )

    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path.TrimEnd('\', '/')
    Push-Location -LiteralPath $RepositoryRoot
    try {
        $msbuild = Get-WindowsMSBuild
        if (-not $NoRestore) {
            Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
            Invoke-WindowsBuildCommand 'dotnet' @('restore', (Join-Path $RepositoryRoot 'MFTLib.sln'))
        }

        Write-Host "Building native project ($Configuration|x64)..." -ForegroundColor Cyan
        # An absolute trailing-backslash SolutionDir places native output at root x64/.
        Invoke-WindowsBuildCommand $msbuild @(
            (Join-Path $RepositoryRoot 'MFTLibNative/MFTLibNative.vcxproj'),
            '-t:Build', "-p:Configuration=$Configuration", '-p:Platform=x64',
            '-p:PlatformToolset=v143', "-p:SolutionDir=$RepositoryRoot\", '-v:minimal', '-nologo'
        )

        Write-Host "Building managed projects ($Configuration|x64)..." -ForegroundColor Cyan
        foreach ($project in @(
            'MFTLib/MFTLib.csproj',
            'MFTLibTestExtensions/MFTLibTestExtensions.csproj',
            'TestProgram/TestProgram.csproj',
            'Benchmark/Benchmark.csproj',
            'MFTLib.Tests/MFTLib.Tests.csproj'
        )) {
            Invoke-WindowsBuildCommand 'dotnet' @(
                'build', (Join-Path $RepositoryRoot $project),
                '-c', $Configuration, '-p:Platform=x64', '--no-restore'
            )
        }
    }
    finally { Pop-Location }
}

if ($MyInvocation.InvocationName -eq '.') { return }
Invoke-WindowsBuild -RepositoryRoot (Split-Path -Parent $PSScriptRoot) -Configuration $Configuration -NoRestore:$NoRestore
exit 0
