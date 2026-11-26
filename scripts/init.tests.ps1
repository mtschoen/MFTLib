#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }
# In-process tests for the branch logic of init.ps1: one PowerShell process, Pester 6 mocks for every external
# command and for the two helper functions that reach the machine. Real launches that prove entry-point behavior
# (argument transport, exit codes through init.bat and pwsh, self-location) stay in
# scripts/optional-provisioning/tests. Run through scripts/test-init-pester.ps1.

BeforeAll {
    $script:repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    function dotnet { throw 'dot-sourcing invoked dotnet' }
    function aislop { throw 'dot-sourcing invoked aislop' }
    function git { throw 'dot-sourcing invoked git' }
    . (Join-Path $script:repositoryRoot 'init.ps1')
    function aislop { }
}

Describe 'init.ps1 in process' {
    BeforeAll {
        $null = New-Item -ItemType Directory -Path "$TestDrive/scripts"
        Set-Content "$TestDrive/scripts/build-windows.ps1" @'
$events.Add('build')
if ($buildFailure) { throw $buildFailure }
$buildLines
exit $buildExit
'@
    }

    BeforeEach {
        $global:LASTEXITCODE = 0
        $initStatus = -1
        $script:initParameters = @{ RepositoryRoot = $TestDrive; Status = [ref]$initStatus }
        $script:events = [System.Collections.Generic.List[string]]::new()
        $script:output = [System.Collections.Generic.List[string]]::new()
        $script:errors = [System.Collections.Generic.List[string]]::new()
        $script:warnings = [System.Collections.Generic.List[string]]::new()
        $script:present = @{ dotnet = $true; aislop = $true; reportgenerator = $true }
        $script:restoreLines = @()
        $script:provisioningExit = 0
        $script:provisioningLines = @()
        $script:aislopInstallExit = 0
        $script:aislopInstallLines = @()
        $script:gitDiffExit = 0
        $script:gitStatusLines = @()
        $script:gitRestoreLines = @()
        $script:buildFailure = $null
        $script:buildExit = 0
        $script:buildLines = @()
        $script:msbuildFailure = $null
        $script:pesterVersion = [version]'6.2.0'

        Mock Write-Host { $script:output.Add([string]$Object) }
        Mock Write-Warning { $script:warnings.Add([string]$Message) }
        Mock Write-InitError { $script:errors.Add($Message) }
        Mock Test-CommandAvailable { $script:present[$Name] -eq $true }
        Mock Get-InstalledPesterVersion { $script:pesterVersion }
        Mock Get-WindowsMSBuild {
            if ($script:msbuildFailure) { throw $script:msbuildFailure }
            'C:\fixture\MSBuild.exe'
        }
        Mock dotnet {
            switch ($args[0]) {
                '--version' { '10.0.100'; $global:LASTEXITCODE = 0 }
                'restore' { $script:restoreLines; $global:LASTEXITCODE = 0 }
                'run' { $script:events.Add('provision'); $script:provisioningLines; $global:LASTEXITCODE = $script:provisioningExit }
            }
        }
        Mock aislop {
            if ($args[0] -eq '--version') { 'fixture-aislop'; $global:LASTEXITCODE = 0 }
            else { $script:aislopInstallLines; $global:LASTEXITCODE = $script:aislopInstallExit }
        }
        Mock git {
            if ($args -contains 'diff') { $global:LASTEXITCODE = $script:gitDiffExit }
            elseif ($args -contains 'status') { $script:gitStatusLines; $global:LASTEXITCODE = 0 }
            else { $script:gitRestoreLines; $global:LASTEXITCODE = 0 }
        }
    }

    Context 'dot-sourcing' {
        It 'loads the functions without running init' {
            Get-Command Invoke-Init -CommandType Function | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Test-WorktreeOnlyPrerequisites' {
        It 'fails with one line when the dotnet SDK is missing' {
            $script:present.dotnet = $false
            Test-WorktreeOnlyPrerequisites -Build | Should -BeFalse
            $script:errors | Should -Be @('worktree-only init: dotnet SDK is missing (https://dotnet.microsoft.com/download)')
            Should -Invoke Get-WindowsMSBuild -Times 0 -Exactly
        }

        It 'fails with one collapsed line when -Build finds no amd64 MSBuild' {
            $script:msbuildFailure = "vswhere found`r`n  nothing"
            Test-WorktreeOnlyPrerequisites -Build | Should -BeFalse
            $script:errors | Should -Be @('worktree-only init: amd64 MSBuild with the MSVC C++ workload is missing (vswhere found nothing)')
        }

        It 'does not look for MSBuild without -Build' {
            $script:msbuildFailure = 'unreachable'
            Test-WorktreeOnlyPrerequisites | Should -BeTrue
            Should -Invoke Get-WindowsMSBuild -Times 0 -Exactly
        }

        It 'passes when everything is present' {
            Test-WorktreeOnlyPrerequisites -Build | Should -BeTrue
            $script:errors | Should -BeNullOrEmpty
        }
    }

    Context 'Write-PrerequisiteReport' {
        It 'reports every tool and an empty missing list when all are present' {
            $report = Write-PrerequisiteReport
            $report.Missing.Count | Should -Be 0
            $report.AislopAvailable | Should -BeTrue
            $script:output | Should -Contain '  dotnet SDK          10.0.100'
            $script:output | Should -Contain '  MSBuild (MSVC)      C:\fixture\MSBuild.exe'
            $script:output | Should -Contain '  aislop              fixture-aislop'
            $script:output | Should -Contain '  reportgenerator     present'
            $script:output | Should -Contain '  Pester              6.2.0'
        }

        It 'lists each missing tool with its install hint' {
            $script:present = @{ dotnet = $false; aislop = $false; reportgenerator = $false }
            $script:msbuildFailure = 'no Visual Studio'
            $script:pesterVersion = $null
            $report = Write-PrerequisiteReport
            $report.AislopAvailable | Should -BeFalse
            $report.Missing.Count | Should -Be 5
            $report.Missing[0] | Should -BeLike 'dotnet SDK 10 - *'
            $report.Missing[1] | Should -BeLike 'Visual Studio with the MSVC C++ workload*no Visual Studio'
            $report.Missing[2] | Should -BeLike 'aislop - *'
            $report.Missing[3] | Should -BeLike 'reportgenerator *'
            $report.Missing[4] | Should -BeLike 'Pester 6 *'
            $script:output | Should -Contain '  dotnet SDK          MISSING'
            $script:output | Should -Contain '  MSBuild (MSVC)      MISSING'
            $script:output | Should -Contain '  aislop              MISSING'
            $script:output | Should -Contain '  Pester              missing (version 6 or newer runs the in-process init tests)'
        }

        It 'treats Pester 5 as missing' {
            $script:pesterVersion = [version]'5.7.1'
            $report = Write-PrerequisiteReport
            $report.Missing.Count | Should -Be 1
            $report.Missing[0] | Should -BeLike 'Pester 6 *'
        }
    }

    Context 'Invoke-Init exit paths' {
        It 'returns 1 before any prerequisite report when worktree-only has no dotnet' {
            $script:present.dotnet = $false
            Invoke-Init @initParameters -WorktreeOnly
            $initParameters.Status.Value | Should -Be 1
            $script:errors.Count | Should -Be 1
            $script:output | Should -Not -Contain 'Checking prerequisites...'
            Should -Invoke dotnet -Times 0 -Exactly
        }

        It 'returns 1 when worktree-only -Build has no amd64 MSBuild' {
            $script:msbuildFailure = 'missing'
            Invoke-Init @initParameters -WorktreeOnly -Build
            $initParameters.Status.Value | Should -Be 1
            $script:errors.Count | Should -Be 1
            Should -Invoke dotnet -Times 0 -Exactly
        }

        It 'returns 1 and lists missing prerequisites when dotnet is absent' {
            $script:present.dotnet = $false
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 1
            $script:output | Should -Contain 'Cannot continue without the dotnet SDK.'
            $script:output | Should -Contain 'Missing prerequisites (install manually):'
            $script:output | Should -Contain '  - dotnet SDK 10 - https://dotnet.microsoft.com/download'
            Should -Invoke dotnet -Times 0 -Exactly -ParameterFilter { $args[0] -eq 'restore' }
        }

        It 'returns 1 when the aislop hook install fails and skips provisioning' {
            $script:aislopInstallExit = 5
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 1
            $script:output | Should -Contain 'aislop hook install failed.'
            Should -Invoke dotnet -Times 0 -Exactly -ParameterFilter { $args[0] -eq 'run' }
        }

        It 'fails after provisioning reports no changes and the build script <failure>' -ForEach @(
            @{ failure = 'throws' }, @{ failure = 'exits nonzero' }
        ) {
            $script:provisioningExit = 20
            $script:provisioningLines = @('Project-scope provisioned settings re-applied')
            if ($failure -eq 'throws') {
                $script:buildFailure = 'fixture build failed'
                { Invoke-Init @initParameters -Build } | Should -Throw -ExpectedMessage 'fixture build failed'
            } else {
                $script:buildExit = 3
                Invoke-Init @initParameters -Build
            }
            $initParameters.Status.Value | Should -Be 1
            $script:events | Should -Be @('provision', 'build')
            $script:warnings | Should -BeNullOrEmpty
            $script:output | Should -Not -Contain '  Project-scope provisioned settings re-applied'
            $script:output | Should -Not -Contain '  Solution built (Release|x64)'
            $script:output | Should -Not -Contain 'Done.'
        }

        It 'preserves native output on the success stream separately from status' {
            $script:restoreLines = @('fixture restore output')
            $script:aislopInstallLines = @('fixture hook output')
            $script:gitStatusLines = @(' M .claude/settings.json')
            $script:gitRestoreLines = @('fixture git output')
            $script:buildLines = @('fixture build output')
            $result = @(Invoke-Init @initParameters -Build)
            $result | Should -Be @('fixture restore output', 'fixture hook output', 'fixture git output', 'fixture build output')
            $initParameters.Status.Value | Should -Be 0
        }
    }

    Context 'agent instruction files' {
        It 'installs the hook and lists the step' {
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            Should -Invoke aislop -Times 1 -Exactly -ParameterFilter { $args -join ' ' -eq 'hook install claude --project' }
            $script:output | Should -Contain '  .claude/AISLOP.md and .claude/CLAUDE.md restored'
        }

        It 'restores settings.json when only line endings changed' {
            $script:gitDiffExit = 0
            $script:gitStatusLines = @(' M .claude/settings.json')
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            Should -Invoke git -Times 1 -Exactly -ParameterFilter { $args -contains 'restore' }
            $script:output | Should -Contain 'Normalized line endings the installer left in .claude/settings.json.'
        }

        It 'leaves settings.json alone after a real content change' {
            $script:gitDiffExit = 1
            $script:gitStatusLines = @(' M .claude/settings.json')
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            Should -Invoke git -Times 0 -Exactly -ParameterFilter { $args -contains 'restore' }
        }

        It 'leaves settings.json alone when it is not modified' {
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            Should -Invoke git -Times 0 -Exactly -ParameterFilter { $args -contains 'restore' }
        }

        It 'skips the hook with a hint when aislop is not installed' {
            $script:present.aislop = $false
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:output | Should -Contain 'Skipping agent instruction files: aislop is not installed.'
            $script:output | Should -Contain 'Once installed, run: aislop hook install claude --project'
            Should -Invoke aislop -Times 0 -Exactly -ParameterFilter { $args[0] -eq 'hook' }
            Should -Invoke dotnet -Times 1 -Exactly -ParameterFilter { $args[0] -eq 'run' }
        }
    }

    Context 'optional provisioning' {
        It 'adds the helper messages as completed steps on exit 0' {
            $script:provisioningLines = @('Project-scope provisioned settings re-applied', '')
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:output | Should -Contain '  Project-scope provisioned settings re-applied'
            $script:warnings | Should -BeNullOrEmpty
        }

        It 'stays silent for helper status <status>' -ForEach @(
            @{ status = 10 }, @{ status = 20 }
        ) {
            $script:provisioningExit = $status
            $script:provisioningLines = @('helper output')
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:warnings | Should -BeNullOrEmpty
            $script:output | Should -Not -Contain 'helper output'
            $script:output | Should -Not -Contain '  helper output'
        }

        It 'prints the helper output and warns, then continues, on another status' {
            $script:provisioningExit = 31
            $script:provisioningLines = @('fixture launcher failure')
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:output | Should -Contain 'fixture launcher failure'
            $script:warnings | Should -Be @('Optional provisioning launcher exited with 31; continuing.')
            $script:output | Should -Not -Contain '  fixture launcher failure'
            $script:output | Should -Not -Contain '  Project-scope provisioned settings re-applied'
        }
    }

    Context 'summary' {
        It 'points at -Build when not building' {
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:output | Should -Contain 'Done.'
            $script:output | Should -Contain 'Next: .\init.ps1 -Build (.\init.bat -Build from cmd.exe),'
        }

        It 'lists the build and omits the hint after -Build' {
            Invoke-Init @initParameters -Build
            $initParameters.Status.Value | Should -Be 0
            $script:events | Should -Be @('provision', 'build')
            $script:output | Should -Contain '  Solution built (Release|x64)'
            $script:output | Should -Not -Contain 'Next: .\init.ps1 -Build (.\init.bat -Build from cmd.exe),'
        }

        It 'reports missing optional prerequisites yet still succeeds' {
            $script:present.reportgenerator = $false
            $script:pesterVersion = $null
            Invoke-Init @initParameters
            $initParameters.Status.Value | Should -Be 0
            $script:output | Should -Contain 'Missing prerequisites (install manually):'
            $script:output | Should -Contain '  - Pester 6 (in-process init tests, scripts/init.tests.ps1) - Install-Module Pester -MinimumVersion 6.0 -Scope AllUsers'
        }
    }
}

Describe 'init.ps1 entry point' {
    BeforeAll {
        Copy-Item (Join-Path $repositoryRoot 'init.ps1') $TestDrive
        $null = New-Item -ItemType Directory -Path "$TestDrive/scripts"
        Set-Content "$TestDrive/scripts/windows-tools.ps1" 'function Get-WindowsMSBuild { "fixture MSBuild" }'
        Set-Content "$TestDrive/scripts/build-windows.ps1" @'
param([string]$Configuration, [switch]$NoRestore)
$calls.Add("build $Configuration NoRestore=$NoRestore")
exit 0
'@
        function dotnet {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq '--version') { return '10.0.100' }
            $calls.Add("dotnet $args")
            if ($args[0] -eq 'restore') { $global:LASTEXITCODE = $restoreExit }
        }
        function aislop { if ($args[0] -ne '--version') { throw "unexpected aislop $args" } }
        function git { throw "unexpected git $args" }
        function schoen-lab-onboard { throw 'unexpected schoen-lab-onboard' }
        function schoen-lab { throw 'unexpected schoen-lab' }
        function onboard { throw 'unexpected onboard' }
    }

    BeforeEach {
        $calls = [System.Collections.Generic.List[string]]::new()
        $restoreExit = 0
    }

    It 'restores the solution without provisioning (build: <build>)' -ForEach @(
        @{ build = $false }, @{ build = $true }
    ) {
        $messages = & "$TestDrive/init.ps1" -WorktreeOnly -Build:$build 6>&1
        $LASTEXITCODE | Should -Be 0
        $expected = @("dotnet restore $(Join-Path $TestDrive 'MFTLib.sln')")
        if ($build) { $expected += 'build Release NoRestore=True' }
        $calls | Should -Be $expected
        [string[]]$messages | Should -Not -Contain 'Restoring agent instruction files...'
        [string[]]$messages | Should -Contain '  NuGet packages restored'
    }

    It 'exits 1 after a failed restore (worktree-only: <worktreeOnly>, build: <build>)' -ForEach @(
        @{ worktreeOnly = $false; build = $true }
        @{ worktreeOnly = $true; build = $false }
        @{ worktreeOnly = $true; build = $true }
    ) {
        $restoreExit = 17
        $messages = & "$TestDrive/init.ps1" -WorktreeOnly:$worktreeOnly -Build:$build 6>&1
        $LASTEXITCODE | Should -Be 1
        $calls | Should -Be @("dotnet restore $(Join-Path $TestDrive 'MFTLib.sln')")
        [string[]]$messages | Should -Contain 'Restore failed.'
        [string[]]$messages | Should -Not -Contain 'Done.'
    }
}
