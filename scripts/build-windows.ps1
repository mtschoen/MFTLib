<#
.SYNOPSIS
Restores and builds the Windows native and managed projects.
.PARAMETER Configuration
Build configuration; defaults to Release. The platform is always x64.
.PARAMETER NoRestore
Reuse a restore already completed by the caller, as init.ps1 does.
.PARAMETER NativeOutputSupplied
The caller restored x64/<Configuration>/MFTLibNative.{dll,pdb,lib} from the CI native build cache. The native
build is skipped after those files are validated as present and non-empty; a missing or empty file builds natively.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [switch] $NativeOutputSupplied
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/windows-tools.ps1"

function Get-MissingNativeOutput {
    param([Parameter(Mandatory)][string] $RepositoryRoot, [string] $Configuration = 'Release')

    foreach ($extension in @('dll', 'pdb', 'lib')) {
        $path = Join-Path $RepositoryRoot "x64/$Configuration/MFTLibNative.$extension"
        $file = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if (-not $file -or $file -isnot [IO.FileInfo] -or $file.Length -eq 0) { $path }
    }
}

function Invoke-WindowsBuild {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [string] $Configuration = 'Release',
        [switch] $NoRestore,
        [switch] $NativeOutputSupplied
    )

    $RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path.TrimEnd('\', '/')
    Push-Location -LiteralPath $RepositoryRoot
    try {
        # Discover before restoring so a missing toolchain fails first; a supplied output needs no MSBuild unless it falls back.
        $msbuild = if ($NativeOutputSupplied) { $null } else { Get-WindowsMSBuild }
        if (-not $NoRestore) {
            Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
            Invoke-WindowsBuildCommand 'dotnet' @('restore', (Join-Path $RepositoryRoot 'MFTLib.sln'))
        }

        $missingNativeOutput = @()
        if ($NativeOutputSupplied) {
            $missingNativeOutput = @(Get-MissingNativeOutput -RepositoryRoot $RepositoryRoot -Configuration $Configuration)
        }
        if ($NativeOutputSupplied -and $missingNativeOutput.Count -eq 0) {
            Write-Host "Using the supplied native output ($Configuration|x64); skipping the native build." -ForegroundColor Cyan
        } else {
            if ($missingNativeOutput.Count -gt 0) {
                Write-Host "Supplied native output is incomplete ($($missingNativeOutput -join ', ')); building natively." -ForegroundColor Yellow
            }
            if (-not $msbuild) { $msbuild = Get-WindowsMSBuild }
            Write-Host "Building native project ($Configuration|x64)..." -ForegroundColor Cyan
            # An absolute trailing-backslash SolutionDir places native output at root x64/.
            Invoke-WindowsBuildCommand $msbuild @(
                (Join-Path $RepositoryRoot 'MFTLibNative/MFTLibNative.vcxproj'),
                '-t:Build', "-p:Configuration=$Configuration", '-p:Platform=x64',
                '-p:PlatformToolset=v143', "-p:SolutionDir=$RepositoryRoot\", '-v:minimal', '-nologo'
            )
        }

        Write-Host "Building managed projects ($Configuration|x64)..." -ForegroundColor Cyan
        foreach ($project in @(
            'MFTLib/MFTLib.csproj',
            'MFTLibTestExtensions/MFTLibTestExtensions.csproj',
            'SampleProgram.Direct/SampleProgram.Direct.csproj',
            'SampleProgram.Watch/SampleProgram.Watch.csproj',
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
Invoke-WindowsBuild -RepositoryRoot (Split-Path -Parent $PSScriptRoot) -Configuration $Configuration -NoRestore:$NoRestore -NativeOutputSupplied:$NativeOutputSupplied
exit 0
