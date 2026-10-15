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

[xml]$nativeProject = Get-Content -LiteralPath (Join-Path $sourceRoot 'MFTLibNative/MFTLibNative.vcxproj') -Raw
Assert-Equal $nativeProject.SelectNodes("//*[local-name()='PostBuildEvent']").Count 0 'native project must not distribute managed outputs'
$solution = Get-Content -LiteralPath (Join-Path $sourceRoot 'MFTLib.sln') -Raw
Assert-Equal ([regex]::Matches($solution, 'ProjectSection\(ProjectDependencies\)').Count) 0 'project references own build ordering'
foreach ($relativeProject in @(
    'MFTLib/MFTLib.csproj',
    'MFTLib.Tests/MFTLib.Tests.csproj',
    'SampleProgram.Direct/SampleProgram.Direct.csproj',
    'SampleProgram.Watch/SampleProgram.Watch.csproj',
    'Benchmark/Benchmark.csproj'
)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot $relativeProject) -Raw
    $references = @($project.Project.ItemGroup.ProjectReference | Where-Object {
        $_.Include -eq '..\MFTLibNative\MFTLibNative.vcxproj'
    })
    Assert-Equal $references.Count 1 "$relativeProject retains native build ordering"
}

$sharedPropsName = 'MFTLibNative.Linux.props'
$sharedPropsPath = Join-Path $sourceRoot $sharedPropsName
Assert-Equal (Test-Path -LiteralPath $sharedPropsPath -PathType Leaf) $true 'shared Linux copy definition exists'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib native items ' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    Copy-Item -LiteralPath $sharedPropsPath -Destination $fixtureRoot
    $projects = @('MFTLib/MFTLib.csproj', 'MFTLib.Tests/MFTLib.Tests.csproj')
    foreach ($relativeProject in $projects) {
        $destination = Join-Path $fixtureRoot $relativeProject
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceRoot $relativeProject) -Destination $destination
    }
    $cases = @(
        @{ Name = 'absent'; OperatingSystem = 'Unix'; Sources = @(); Expected = '' },
        @{ Name = 'ordinary'; OperatingSystem = 'Unix'; Sources = @('linux'); Expected = 'linux' },
        @{ Name = 'coverage'; OperatingSystem = 'Unix'; Sources = @('linux-coverage'); Expected = 'linux-coverage' },
        @{ Name = 'coverage-wins'; OperatingSystem = 'Unix'; Sources = @('linux', 'linux-coverage'); Expected = 'linux-coverage' },
        @{ Name = 'windows'; OperatingSystem = 'Windows_NT'; Sources = @('linux', 'linux-coverage'); Expected = '' }
    )
    foreach ($case in $cases) {
        $buildDirectory = Join-Path $fixtureRoot 'build'
        if (Test-Path -LiteralPath $buildDirectory) { Remove-Item -LiteralPath $buildDirectory -Recurse -Force }
        foreach ($sourceDirectory in $case.Sources) {
            $directory = Join-Path $buildDirectory $sourceDirectory
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $directory 'libMFTLibNative.so') -Value $case.Name
        }
        foreach ($relativeProject in $projects) {
            $projectPath = Join-Path $fixtureRoot $relativeProject
            $output = & dotnet msbuild $projectPath -nologo -verbosity:quiet -getItem:None `
                "-p:OS=$($case.OperatingSystem)" -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false
            if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $relativeProject / $($case.Name)" }
            $evaluation = ($output -join [Environment]::NewLine) | ConvertFrom-Json
            $items = @($evaluation.Items.None | Where-Object { $_.Link -eq 'libMFTLibNative.so' })
            $label = "$relativeProject / $($case.Name)"
            $expectedCount = if ($case.Expected) { 1 } else { 0 }
            Assert-Equal $items.Count $expectedCount "$label selected library count"
            if ($expectedCount -eq 1) {
                $expectedPath = [IO.Path]::GetFullPath((Join-Path $fixtureRoot "build/$($case.Expected)/libMFTLibNative.so"))
                Assert-Equal $items[0].FullPath $expectedPath "$label selected source"
                Assert-Equal $items[0].CopyToOutputDirectory 'PreserveNewest' "$label output policy"
                Assert-Equal $items[0].DefiningProjectFullPath (Join-Path $fixtureRoot $sharedPropsName) "$label shared owner"
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

$projectPaths = @(& git -C $sourceRoot ls-files -- '*.csproj' '*.vcxproj')
if ($LASTEXITCODE -ne 0 -or $projectPaths.Count -eq 0) {
    throw 'Cannot enumerate tracked project files for the platform contract'
}
foreach ($relativePath in @('MFTLib.sln') + $projectPaths) {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot $relativePath) -Raw
    if ($text -match '\b(?:x86|Win32)\b') {
        throw "$relativePath declares an unsupported x86/Win32 configuration"
    }
    if ($relativePath.EndsWith('.csproj')) {
        [xml] $project = $text
        $platforms = @($project.SelectNodes('/Project/PropertyGroup/Platforms'))
        Assert-Equal $platforms.Count 1 "$relativePath platform declaration count"
        Assert-Equal $platforms[0].InnerText 'x64' "$relativePath supported platforms"
    }
}
$solutionText = Get-Content -LiteralPath (Join-Path $sourceRoot 'MFTLib.sln') -Raw
$solutionSection = [regex]::Match(
    $solutionText,
    '(?s)GlobalSection\(SolutionConfigurationPlatforms\) = preSolution(.*?)EndGlobalSection'
)
Assert-Equal $solutionSection.Success $true 'solution configuration section exists'
$solutionConfigurations = @(
    $solutionSection.Groups[1].Value -split '\r?\n' |
        ForEach-Object { $_.Trim() } | Where-Object { $_ }
)
Assert-Equal ($solutionConfigurations -join ';') `
    'Debug|x64 = Debug|x64;Release|x64 = Release|x64' 'solution platforms'
[xml] $nativeProject = Get-Content -LiteralPath (
    Join-Path $sourceRoot 'MFTLibNative/MFTLibNative.vcxproj'
) -Raw
$nativeConfigurations = @(
    $nativeProject.SelectNodes("//*[local-name()='ProjectConfiguration']") |
        ForEach-Object { $_.GetAttribute('Include') }
)
Assert-Equal ($nativeConfigurations -join ';') `
    'Debug|x64;Release|x64' 'native platforms'

$linuxInit = Get-Content -LiteralPath (Join-Path $sourceRoot 'init.sh') -Raw
$managedBuild = 'dotnet build "$ROOT/MFTLib.Tests/MFTLib.Tests.csproj" -c Release -p:Platform=x64'
if (-not $linuxInit.Contains($managedBuild)) {
    throw 'init.sh --build must select Release/x64 for the managed build'
}

Write-Host 'Windows build regression tests passed.'
