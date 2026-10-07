$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/build-windows.ps1"

function Assert-Equal($Actual, $Expected, [string] $Label) {
    if ($Actual -cne $Expected) { throw "$Label`: expected $Expected, got $Actual" }
}

function Assert-Throws([scriptblock] $Action, [string] $Message) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        $caught = $true
        if (-not $_.Exception.Message.Contains($Message)) { throw }
    }
    if (-not $caught) { throw "Expected failure containing: $Message" }
}

function Invoke-FakeBuildTool {
    $script:observedArguments = @($args)
    $global:LASTEXITCODE = $script:toolExitCode
    'tool output'
}

$savedExitCode = $global:LASTEXITCODE
$savedProgramFiles = ${env:ProgramFiles(x86)}
$savedWorkspace = $env:GITHUB_WORKSPACE
$savedCommand = (Get-Item Function:Invoke-WindowsBuildCommand).ScriptBlock
$originalLocation = (Get-Location).Path
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib-build-' + [guid]::NewGuid().ToString('N'))
$repository = Join-Path $temporaryDirectory 'repository with spaces'
$programFiles = Join-Path $temporaryDirectory 'Program Files (x86)'
$visualStudio = Join-Path $temporaryDirectory 'Visual Studio'
$vswhere = Join-Path $programFiles 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = Join-Path $visualStudio 'MSBuild/Current/Bin/amd64/MSBuild.exe'
$commands = [Collections.Generic.List[object]]::new()

function Get-ExpectedBuildCommands([string] $Configuration, [bool] $NoRestore) {
    if (-not $NoRestore) {
        [pscustomobject]@{
            FilePath = 'dotnet'
            Arguments = @('restore', (Join-Path $repository 'MFTLib.sln'))
        }
    }
    [pscustomobject]@{
        FilePath = $msbuild
        Arguments = @(
            (Join-Path $repository 'MFTLibNative/MFTLibNative.vcxproj'),
            '-t:Build', "-p:Configuration=$Configuration", '-p:Platform=x64',
            '-p:PlatformToolset=v143', "-p:SolutionDir=$repository\", '-v:minimal', '-nologo'
        )
    }
    foreach ($project in @(
        'MFTLib/MFTLib.csproj',
        'MFTLibTestExtensions/MFTLibTestExtensions.csproj',
        'SampleProgram.Direct/SampleProgram.Direct.csproj',
        'SampleProgram.Watch/SampleProgram.Watch.csproj',
        'Benchmark/Benchmark.csproj',
        'MFTLib.Tests/MFTLib.Tests.csproj'
    )) {
        [pscustomobject]@{
            FilePath = 'dotnet'
            Arguments = @('build', (Join-Path $repository $project), '-c', $Configuration, '-p:Platform=x64', '--no-restore')
        }
    }
}

try {
    $toolExitCode = 0
    $output = Invoke-WindowsBuildCommand 'Invoke-FakeBuildTool' @('one argument with spaces', '-p:Platform=x64')
    Assert-Equal $output 'tool output' 'tool output is streamed'
    Assert-Equal ($observedArguments | ConvertTo-Json -Compress) ('one argument with spaces', '-p:Platform=x64' | ConvertTo-Json -Compress) 'argument boundaries'
    $toolExitCode = 23
    Assert-Throws { Invoke-WindowsBuildCommand 'Invoke-FakeBuildTool' @('failure') } 'exited with code 23'

    New-Item -ItemType Directory -Path $repository, (Split-Path $vswhere), (Split-Path $msbuild) -Force | Out-Null
    New-Item -ItemType File -Path $vswhere, $msbuild | Out-Null
    ${env:ProgramFiles(x86)} = $programFiles
    $env:GITHUB_WORKSPACE = Join-Path $temporaryDirectory 'unrelated checkout'
    $discoveryResult = $visualStudio
    $discoveryFailure = $false
    $failureArguments = $null

    Set-Item Function:Invoke-WindowsBuildCommand {
        param([string] $FilePath, [string[]] $Arguments)
        if ($FilePath -eq $script:vswhere) {
            $script:discoveryArguments = $Arguments
            if ($script:discoveryFailure) { throw 'vswhere exited with code 23' }
            return $script:discoveryResult
        }
        if ($FilePath -ne 'dotnet' -and $FilePath -ne $script:msbuild) {
            throw "Unexpected executable: $FilePath"
        }
        Assert-Equal (Get-Location).Path $script:repository 'build working directory'
        $script:commands.Add([pscustomobject]@{ FilePath = $FilePath; Arguments = $Arguments })
        if (($Arguments -join '|') -ceq $script:failureArguments) { throw 'build tool exited with code 23' }
    }

    foreach ($configuration in @('Release', 'Debug')) {
        foreach ($noRestore in @($false, $true)) {
            $commands.Clear()
            $parameters = @{ RepositoryRoot = "$repository\"; NoRestore = $noRestore }
            if ($configuration -eq 'Debug') { $parameters.Configuration = 'Debug' }
            Invoke-WindowsBuild @parameters
            $expected = @(Get-ExpectedBuildCommands $configuration $noRestore)
            Assert-Equal ($commands.ToArray() | ConvertTo-Json -Depth 5 -Compress) ($expected | ConvertTo-Json -Depth 5 -Compress) "$configuration NoRestore=$noRestore"
            Assert-Equal ($discoveryArguments -join '|') '-products|*|-requires|Microsoft.Component.MSBuild|-property|installationPath|-latest' 'BuildTools discovery'
            Assert-Equal (Get-Location).Path $originalLocation 'success restores location'
        }
    }

    $expected = @(Get-ExpectedBuildCommands 'Release' $false)
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $commands.Clear()
        $failureArguments = $expected[$index].Arguments -join '|'
        Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'exited with code 23'
        $prefix = @($expected[0..$index])
        Assert-Equal ($commands.ToArray() | ConvertTo-Json -Depth 5 -Compress) ($prefix | ConvertTo-Json -Depth 5 -Compress) "failure stops at $failureArguments"
        Assert-Equal (Get-Location).Path $originalLocation 'failure restores location'
    }
    $failureArguments = $null

    $commands.Clear()
    $discoveryFailure = $true
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'vswhere exited with code 23'
    $discoveryFailure = $false
    $discoveryResult = ''
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'Visual Studio with MSBuild was not found'
    $discoveryResult = $visualStudio
    Remove-Item -LiteralPath $msbuild
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } '64-bit MSBuild was not found'
    Remove-Item -LiteralPath $vswhere
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'vswhere was not found'
    ${env:ProgramFiles(x86)} = $null
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'ProgramFiles(x86) is not set'
    Assert-Equal $commands.Count 0 'discovery failure launches no build'
    Assert-Equal (Get-Location).Path $originalLocation 'discovery failure restores location'
}
finally {
    Set-Item Function:Invoke-WindowsBuildCommand $savedCommand
    ${env:ProgramFiles(x86)} = $savedProgramFiles
    $env:GITHUB_WORKSPACE = $savedWorkspace
    $global:LASTEXITCODE = $savedExitCode
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

$sourceRoot = Split-Path -Parent $PSScriptRoot
$callers = @(
    @{ Path = 'scripts/run-coverage.ps1'; Invocation = '& "$PSScriptRoot/build-windows.ps1" -Configuration $Configuration' },
    @{ Path = 'init.ps1'; Invocation = '& "$repositoryRoot/scripts/build-windows.ps1" -Configuration Release -NoRestore' },
    @{ Path = '.gitea/workflows/aislop.yml'; Invocation = '& "$env:GITHUB_WORKSPACE/scripts/build-windows.ps1" -Configuration Release' }
)
foreach ($caller in $callers) {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot $caller.Path) -Raw
    if (-not $text.Contains($caller.Invocation)) {
        throw "$($caller.Path) must delegate to the shared Windows build"
    }
    if ($text -match '(?im)^\s*(?:&\s+)?\$msbuild\b|^\s*(?:&\s+)?dotnet\s+build\b|\$managedProjects\s*=') {
        throw "$($caller.Path) still owns a duplicate build recipe"
    }
}
$workflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.gitea/workflows/test.yml') -Raw
if (-not $workflow.Contains('run: pwsh -NoProfile -File scripts/build-windows.tests.ps1')) {
    throw 'Windows CI must run the build regression script'
}

Write-Host 'Windows build regression tests passed.'
