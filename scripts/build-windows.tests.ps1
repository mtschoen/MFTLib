# One regression entry point owns Windows build/discovery callers and the existing project-output contracts.
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

function Get-ScriptSection([string] $Path, [string] $Start, [string] $End = '') {
    $text = Get-Content -LiteralPath $Path -Raw
    $startIndex = $text.IndexOf($Start, [StringComparison]::Ordinal)
    if ($startIndex -lt 0) { throw "Missing section start in ${Path}: $Start" }
    $endIndex = if ($End) {
        $text.IndexOf($End, $startIndex + $Start.Length, [StringComparison]::Ordinal)
    } else { $text.Length }
    if ($endIndex -le $startIndex) { throw "Missing section end in ${Path}: $End" }
    return $text.Substring($startIndex, $endIndex - $startIndex)
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

    $coverageExecutable = Join-Path $visualStudio 'Common7/IDE/Extensions/Microsoft/CodeCoverage.Console/Microsoft.CodeCoverage.Console.exe'
    Assert-Throws { Get-WindowsCodeCoverage } 'Microsoft.CodeCoverage.Console was not found'
    Assert-Equal (Get-WindowsMSBuild) $msbuild 'ordinary discovery needs no coverage component'
    New-Item -ItemType Directory -Path (Split-Path $coverageExecutable) -Force | Out-Null
    New-Item -ItemType Directory -Path $coverageExecutable | Out-Null
    Assert-Throws { Get-WindowsCodeCoverage } 'Microsoft.CodeCoverage.Console was not found'
    Remove-Item -LiteralPath $coverageExecutable
    New-Item -ItemType File -Path $coverageExecutable | Out-Null
    $discoveryResult = "  $visualStudio`r`n"
    Assert-Equal (Get-WindowsVisualStudioInstallation) $visualStudio 'trim installation output'
    Assert-Equal (Get-WindowsCodeCoverage) $coverageExecutable 'coverage executable in selected installation'
    Assert-Equal ($discoveryArguments -join '|') '-products|*|-requires|Microsoft.Component.MSBuild|-property|installationPath|-latest' 'coverage uses canonical discovery'
    $discoveryResult = $visualStudio

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
    Assert-Throws { Get-WindowsCodeCoverage } 'vswhere exited with code 23'
    $discoveryFailure = $false
    $discoveryResult = " `r`n "
    Assert-Throws { Get-WindowsMSBuild } 'Visual Studio with MSBuild was not found'
    Assert-Throws { Get-WindowsCodeCoverage } 'Visual Studio with MSBuild was not found'
    $discoveryResult = $visualStudio

    $initProbe = Get-ScriptSection (Join-Path (Split-Path $PSScriptRoot) 'init.ps1') `
        "Write-Host 'Checking prerequisites...'" "if (-not (Test-CommandAvailable 'dotnet'))"
    $discoveryFailure = $true
    try {
        & {
            $missingPrerequisites = [Collections.Generic.List[string]]::new()
            function Test-CommandAvailable { param([string] $Name) return $false }
            & ([scriptblock]::Create($initProbe)) 6>$null
            Assert-Equal $missingPrerequisites.Count 4 'init collects every missing prerequisite'
            Assert-Equal ($missingPrerequisites[1].Contains('vswhere exited with code 23')) $true 'init reports discovery error'
            Assert-Equal ($missingPrerequisites[3].StartsWith('reportgenerator')) $true 'init continues after discovery failure'
        }
    }
    finally { $discoveryFailure = $false }

    Remove-Item -LiteralPath $msbuild
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } '64-bit MSBuild was not found'
    Remove-Item -LiteralPath $vswhere
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'vswhere was not found'
    Assert-Throws { Get-WindowsCodeCoverage } 'vswhere was not found'
    ${env:ProgramFiles(x86)} = $null
    Assert-Throws { Invoke-WindowsBuild -RepositoryRoot $repository } 'ProgramFiles(x86) is not set'
    Assert-Throws { Get-WindowsCodeCoverage } 'ProgramFiles(x86) is not set'
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
    @{ Path = '.gitea/workflows/aislop.yml'; Invocation = '& "$env:GITHUB_WORKSPACE/scripts/build-windows.ps1" -Configuration Release' },
    @{ Path = 'scripts/native-coverage.ps1'; Invocation = '& "$PSScriptRoot/build-windows.ps1" -Configuration Debug' }
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

$nativeBody = Get-ScriptSection (Join-Path $PSScriptRoot 'native-coverage.ps1') '$coverageTool ='
$callerRoot = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib coverage caller ' + [guid]::NewGuid().ToString('N'))
$savedEvents = Get-Variable WindowsCallerEvents -Scope Global -ErrorAction SilentlyContinue | Select-Object Name, Value
$savedBuildExit = Get-Variable WindowsCallerBuildExit -Scope Global -ErrorAction SilentlyContinue | Select-Object Name, Value
$callerExitCode = $global:LASTEXITCODE
try {
    New-Item -ItemType Directory -Path (Join-Path $callerRoot 'scripts') -Force | Out-Null
    @'
param([string] $Configuration, [switch] $NoRestore)
$global:WindowsCallerEvents.Add([pscustomobject]@{
    Tool = 'build'; Arguments = @($Configuration, [bool]$NoRestore)
})
if ($global:WindowsCallerBuildExit -eq 0) {
    $outputDirectory = Join-Path (Split-Path $PSScriptRoot) 'MFTLib.Tests/bin/x64/Debug/net10.0'
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    foreach ($name in @('MFTLibNative.dll', 'MFTLibNative.pdb')) {
        Set-Content -LiteralPath (Join-Path $outputDirectory $name) -Value 'fixture'
    }
}
exit $global:WindowsCallerBuildExit
'@ | Set-Content -LiteralPath (Join-Path $callerRoot 'scripts/build-windows.ps1')

    foreach ($case in @(
        @{ Name = 'native-success'; BuildExit = 0; Html = $false },
        @{ Name = 'native-html'; BuildExit = 0; Html = $true },
        @{ Name = 'issue-541-build-failure'; BuildExit = 23; Html = $false }
    )) {
        $global:WindowsCallerEvents = [Collections.Generic.List[object]]::new()
        $global:WindowsCallerBuildExit = $case.BuildExit
        & {
            $fixtureScripts = Join-Path $callerRoot 'scripts'
            $HtmlReport = $case.Html
            $nativeInvocation = [scriptblock]::Create('$PSScriptRoot = $fixtureScripts' + "`n" + $nativeBody)
            function Get-WindowsCodeCoverage { return 'Invoke-FakeNativeCoverageTool' }
            function Invoke-FakeNativeCoverageTool {
                $arguments = @($args)
                $global:WindowsCallerEvents.Add([pscustomobject]@{ Tool = 'coverage'; Arguments = $arguments })
                if ($arguments[0] -eq 'instrument') {
                    Assert-Equal (Test-Path -LiteralPath $arguments[1] -PathType Leaf) $true 'build produced copied DLL before instrument'
                    Assert-Equal (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($arguments[1], 'pdb')) -PathType Leaf) $true 'build produced copied PDB'
                } elseif ($arguments[0] -eq 'collect') {
                    $outputPath = $arguments[[Array]::IndexOf($arguments, '--output') + 1]
                    '<coverage><packages><package name="MFTLibNative" line-rate="1" branch-rate="1"><classes /></package></packages></coverage>' |
                        Set-Content -LiteralPath $outputPath
                } else { throw "Unexpected coverage command: $arguments" }
                $global:LASTEXITCODE = 0
            }
            function dotnet {
                $global:WindowsCallerEvents.Add([pscustomobject]@{ Tool = 'dotnet'; Arguments = @($args) })
                $global:LASTEXITCODE = 0
            }
            if ($case.BuildExit) {
                Assert-Throws { & $nativeInvocation } 'Build failed'
            } else {
                & $nativeInvocation
            }
        }
        $expected = @([pscustomobject]@{ Tool = 'build'; Arguments = @('Debug', $false) })
        if (-not $case.BuildExit) {
            $expected += [pscustomobject]@{
                Tool = 'coverage'; Arguments = @('instrument', (Join-Path $callerRoot 'MFTLib.Tests\bin\x64\Debug\net10.0\MFTLibNative.dll'))
            }
            $expected += [pscustomobject]@{
                Tool = 'coverage'; Arguments = @(
                    'collect', '--settings', (Join-Path $callerRoot 'native-coverage.runsettings'),
                    '--output', (Join-Path $callerRoot 'native-coverage.cobertura.xml'),
                    '--output-format', 'cobertura', '--include-files', 'MFTLibNative.dll',
                    'dotnet', 'test', (Join-Path $callerRoot 'MFTLib.Tests\MFTLib.Tests.csproj'),
                    '-p:', 'Platform=x64', '--no-build', '-c', 'Debug'
                )
            }
            if ($case.Html) {
                $expected += [pscustomobject]@{
                    Tool = 'dotnet'; Arguments = @(
                        'tool', 'run', 'reportgenerator',
                        "-reports:$(Join-Path $callerRoot 'native-coverage.cobertura.xml')",
                        "-targetdir:$(Join-Path $callerRoot 'native-coverage-html')", '-reporttypes:Html'
                    )
                }
            }
        }
        Assert-Equal ($global:WindowsCallerEvents.ToArray() | ConvertTo-Json -Depth 5 -Compress) `
            ($expected | ConvertTo-Json -Depth 5 -Compress) $case.Name
    }
}
finally {
    $global:LASTEXITCODE = $callerExitCode
    if ($savedEvents) { Set-Variable WindowsCallerEvents $savedEvents.Value -Scope Global }
    else { Remove-Variable WindowsCallerEvents -Scope Global -ErrorAction SilentlyContinue }
    if ($savedBuildExit) { Set-Variable WindowsCallerBuildExit $savedBuildExit.Value -Scope Global }
    else { Remove-Variable WindowsCallerBuildExit -Scope Global -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $callerRoot -Recurse -Force
}

$tokens = $null
$parseErrors = $null
$nativeSyntax = [Management.Automation.Language.Parser]::ParseInput($nativeBody, [ref]$tokens, [ref]$parseErrors)
Assert-Equal $parseErrors.Count 0 'native coverage parses'
$collectCommands = @($nativeSyntax.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.CommandElements.Count -gt 1 -and
        $node.CommandElements[0].Extent.Text -ceq '$coverageTool' -and
        $node.CommandElements[1].Extent.Text -ceq 'collect'
}, $true))
Assert-Equal $collectCommands.Count 1 'one coverage collection command'
Assert-Equal (($collectCommands[0].CommandElements | ForEach-Object { $_.Extent.Text }) -join '|') `
    '$coverageTool|collect|--settings|$settings|--output|$coberturaOutput|--output-format|cobertura|--include-files|MFTLibNative.dll|--|dotnet|test|$testProject|-p:|Platform=x64|--no-build|-c|Debug' 'native collect executable tokens'
Assert-Equal $collectCommands[0].Extent.Text.Contains('-p:Platform=x64 --no-build -c Debug') $true 'native platform argument remains contiguous'
$htmlCommands = @($nativeSyntax.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq 'dotnet'
}, $true))
Assert-Equal $htmlCommands.Count 1 'one HTML tool command'
Assert-Equal (($htmlCommands[0].CommandElements | ForEach-Object { $_.Extent.Text }) -join '|') `
    'dotnet|tool|run|reportgenerator|--|"-reports:$coberturaOutput"|"-targetdir:$htmlOutput"|"-reporttypes:Html"' 'HTML executable tokens'

$discoveryCallers = @(
    @{ Path = 'scripts/build-windows.ps1'; Import = '. "$PSScriptRoot/windows-tools.ps1"' },
    @{ Path = 'init.ps1'; Import = '. "$PSScriptRoot/scripts/windows-tools.ps1"' },
    @{ Path = 'scripts/native-coverage.ps1'; Import = '. "$PSScriptRoot/windows-tools.ps1"' },
    @{ Path = 'scripts/release.ps1'; Import = '. "$PSScriptRoot/windows-tools.ps1"' }
)
foreach ($caller in $discoveryCallers) {
    $text = Get-Content -LiteralPath (Join-Path $sourceRoot $caller.Path) -Raw
    Assert-Equal $text.Contains($caller.Import) $true "$($caller.Path) imports shared discovery"
    Assert-Equal ($text -match 'vswhere|Microsoft Visual Studio[/\\]|function Get-WindowsMSBuild') $false "$($caller.Path) delegates discovery"
}
$releaseText = Get-Content -LiteralPath (Join-Path $sourceRoot 'scripts/release.ps1') -Raw
Assert-Equal $releaseText.Contains('$msbuild = Get-WindowsMSBuild') $true 'release selects shared MSBuild'
Assert-Equal ([regex]::Matches($releaseText, '(?m)^& \$msbuild .* -t:Pack ').Count) 2 'both packs retain shared MSBuild'
$preflightText = Get-Content -LiteralPath (Join-Path $sourceRoot 'tools/ci-preflight.sh') -Raw
Assert-Equal $preflightText.Contains('build-script) pwsh -NoProfile -File scripts/build-windows.tests.ps1 ;;') $true 'preflight retains build regression gate'

$releasePreparation = Get-ScriptSection (Join-Path $PSScriptRoot 'release.ps1') `
    '# --- Clean' '# --- Build the linux-x64 native library ---'
$releaseRoot = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib release caller ' + [guid]::NewGuid().ToString('N'))
$savedReleaseEvents = Get-Variable WindowsReleaseEvents -Scope Global -ErrorAction SilentlyContinue | Select-Object Name, Value
$releaseExitCode = $global:LASTEXITCODE
$releaseLocation = (Get-Location).Path
try {
    New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'scripts') -Force | Out-Null
    @'
param([string] $Configuration)
if ($Configuration -cne 'Release') { throw "Unexpected coverage configuration: $Configuration" }
$global:WindowsReleaseEvents.Add('coverage')
Invoke-WindowsBuild -RepositoryRoot (Split-Path $PSScriptRoot) -Configuration $Configuration
exit 0
'@ | Set-Content -LiteralPath (Join-Path $releaseRoot 'scripts/run-coverage.ps1')
    foreach ($cleanExitCode in @(0, 23)) {
        $global:WindowsReleaseEvents = [Collections.Generic.List[string]]::new()
        & {
            $fixtureScripts = Join-Path $releaseRoot 'scripts'
            $repoRoot = $releaseRoot
            $releaseInvocation = [scriptblock]::Create('$PSScriptRoot = $fixtureScripts' + "`n" + $releasePreparation)
            function Get-WindowsMSBuild { return 'Invoke-FakeReleaseMSBuild' }
            function Invoke-FakeReleaseMSBuild {
                $arguments = @($args)
                if ($arguments -contains '-t:Clean') {
                    Assert-Equal ($arguments -join '|') `
                        (@("$repoRoot\MFTLib.sln", '-t:Clean', '-p:Configuration=Release', '-p:Platform=x64', '-v:q', '-nologo') -join '|') 'release clean arguments'
                    $global:WindowsReleaseEvents.Add('clean')
                    $global:LASTEXITCODE = $cleanExitCode
                } elseif ($arguments -contains '-t:Build') {
                    $global:WindowsReleaseEvents.Add('native-build')
                    $global:LASTEXITCODE = 0
                } else { throw "Unexpected MSBuild operation: $arguments" }
            }
            function dotnet {
                $arguments = @($args)
                if ($arguments[0] -eq 'restore') {
                    Assert-Equal $arguments[1] (Join-Path $releaseRoot 'MFTLib.sln') 'release restore target'
                    $global:WindowsReleaseEvents.Add('restore')
                } elseif ($arguments[0] -eq 'build') {
                    Assert-Equal ($arguments -contains '--no-restore') $true 'release managed build reuses restore'
                    $global:WindowsReleaseEvents.Add('managed-build')
                } else { throw "Unexpected dotnet operation: $arguments" }
                $global:LASTEXITCODE = 0
            }
            if ($cleanExitCode) {
                Assert-Throws { & $releaseInvocation } 'exited with code 23'
            } else {
                & $releaseInvocation
            }
        }
        $expected = if ($cleanExitCode) { @('clean') } else {
            @('clean', 'coverage', 'restore', 'native-build') + @('managed-build') * 6
        }
        Assert-Equal ($global:WindowsReleaseEvents -join '|') ($expected -join '|') "release Clean exit $cleanExitCode"
        Assert-Equal (Get-Location).Path $releaseLocation 'release build restores caller location'
    }
}
finally {
    $global:LASTEXITCODE = $releaseExitCode
    if ($savedReleaseEvents) { Set-Variable WindowsReleaseEvents $savedReleaseEvents.Value -Scope Global }
    else { Remove-Variable WindowsReleaseEvents -Scope Global -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

$workflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.gitea/workflows/test.yml') -Raw
if (-not $workflow.Contains('run: pwsh -NoProfile -File scripts/build-windows.tests.ps1')) {
    throw 'Windows CI must run the build regression script'
}

[xml]$nativeProject = Get-Content -LiteralPath (Join-Path $sourceRoot 'MFTLibNative/MFTLibNative.vcxproj') -Raw
Assert-Equal $nativeProject.SelectNodes("//*[local-name()='PostBuildEvent']").Count 0 'native project must not distribute managed outputs'
$debugGroup = $nativeProject.SelectSingleNode("//*[local-name()='ItemDefinitionGroup' and contains(@Condition, 'Debug|x64')]")
Assert-Equal $debugGroup.Link.Profile 'true' 'Debug native instrumentation uses PROFILE'
Assert-Equal $debugGroup.ClCompile.DebugInformationFormat 'ProgramDatabase' 'Debug native symbols use PDB'
$debugProperties = $nativeProject.SelectSingleNode("//*[local-name()='PropertyGroup' and contains(@Condition, 'Debug|x64') and *[local-name()='LinkIncremental']]")
Assert-Equal $debugProperties.LinkIncremental 'false' 'PROFILE disables incremental linking'
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
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'AGENTS.md') -Value 'fixture document for the test project copy item'
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
        @{ Name = 'release-flavor'; OperatingSystem = 'Unix'; Flavor = 'Release'; Sources = @('linux', 'linux-coverage'); Expected = 'linux' },
        @{ Name = 'release-flavor-no-release'; OperatingSystem = 'Unix'; Flavor = 'Release'; Sources = @('linux-coverage'); Expected = '' },
        @{ Name = 'coverage-flavor'; OperatingSystem = 'Unix'; Flavor = 'Coverage'; Sources = @('linux', 'linux-coverage'); Expected = 'linux-coverage' },
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
            $msbuildArguments = @($projectPath, '-nologo', '-verbosity:quiet', '-getItem:None', "-p:OS=$($case.OperatingSystem)",
                '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false')
            if ($case.Flavor) { $msbuildArguments += "-p:MFTLibNativeLinuxFlavor=$($case.Flavor)" }
            $output = & dotnet msbuild @msbuildArguments
            if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $relativeProject / $($case.Name)" }
            $evaluation = ($output -join [Environment]::NewLine) | ConvertFrom-Json
            $items = @($evaluation.Items.None | Where-Object { $_.Link -eq 'libMFTLibNative.so' })
            $label = "$relativeProject / $($case.Name)"
            $expectedCount = if ($case.Expected) { 1 } else { 0 }
            Assert-Equal $items.Count $expectedCount "$label selected library count"
            if ($expectedCount -eq 1) {
                $expectedPath = [IO.Path]::GetFullPath((Join-Path $fixtureRoot "build/$($case.Expected)/libMFTLibNative.so"))
                Assert-Equal $items[0].FullPath $expectedPath "$label selected source"
                Assert-Equal $items[0].CopyToOutputDirectory 'Always' "$label output policy"
                Assert-Equal $items[0].DefiningProjectFullPath (Join-Path $fixtureRoot $sharedPropsName) "$label shared owner"
            }
        }
    }

    # Copy content: a selected library that is OLDER than the library already in the output must still replace it
    # (Coverage-to-Release leaves the coverage build in bin, Release-to-Coverage leaves the release build).
    $switches = @(
        @{ Name = 'coverage-to-release'; Flavor = 'Release'; Selected = 'linux'; Previous = 'linux-coverage' },
        @{ Name = 'release-to-coverage'; Flavor = 'Coverage'; Selected = 'linux-coverage'; Previous = 'linux' }
    )
    foreach ($switch in $switches) {
        foreach ($relativeProject in $projects) {
            $buildDirectory = Join-Path $fixtureRoot 'build'
            if (Test-Path -LiteralPath $buildDirectory) { Remove-Item -LiteralPath $buildDirectory -Recurse -Force }
            foreach ($sourceDirectory in @('linux', 'linux-coverage')) {
                $directory = Join-Path $buildDirectory $sourceDirectory
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                $source = Join-Path $directory 'libMFTLibNative.so'
                Set-Content -LiteralPath $source -Value "source-$sourceDirectory" -NoNewline
                (Get-Item -LiteralPath $source).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-2)
            }
            $outputDirectory = Join-Path $fixtureRoot "output/$($switch.Name)/"
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
            $existing = Join-Path $outputDirectory 'libMFTLibNative.so'
            Set-Content -LiteralPath $existing -Value "source-$($switch.Previous)" -NoNewline
            (Get-Item -LiteralPath $existing).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
            $projectPath = Join-Path $fixtureRoot $relativeProject
            $copyOutput = & dotnet msbuild $projectPath -nologo -verbosity:quiet -restore '-p:OS=Unix' "-p:MFTLibNativeLinuxFlavor=$($switch.Flavor)" `
                "-p:OutDir=$outputDirectory" '-p:ImportDirectoryBuildProps=false' '-p:ImportDirectoryBuildTargets=false' `
                '-t:GetCopyToOutputDirectoryItems;_CopyOutOfDateSourceItemsToOutputDirectoryAlways;_CopyOutOfDateSourceItemsToOutputDirectory' |
                Out-String
            if ($LASTEXITCODE -ne 0) { throw "MSBuild copy failed: $relativeProject / $($switch.Name)`n$copyOutput" }
            Assert-Equal (Get-Content -LiteralPath $existing -Raw) "source-$($switch.Selected)" "$relativeProject / $($switch.Name) copied content"
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
