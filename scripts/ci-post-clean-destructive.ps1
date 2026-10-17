<#
.SYNOPSIS
Destructively verifies post-clean initialization: git clean -ffxd, then init with a clean-tree check after each step.
Run it only in a disposable checkout; CI runs it in the original checkout after build and tests so tracked build dirt shows in the first status check.
#>
[CmdletBinding()]
param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location -LiteralPath $RepositoryRoot
$operations = [ordered]@{
    'git clean -ffxd'   = @('git', 'clean', '-ffxd')
    'init.ps1 first'    = @('pwsh', '-NoProfile', '-File', '.\init.ps1')
    'init.ps1 second'   = @('pwsh', '-NoProfile', '-File', '.\init.ps1')
    'init.bat'          = @($env:ComSpec, '/d', '/c', '.\init.bat')
    'init.ps1 -Build'   = @('pwsh', '-NoProfile', '-File', '.\init.ps1', '-Build')
}
foreach ($stage in $operations.Keys) {
    $command = $operations[$stage]
    & $command[0] $command[1..($command.Count - 1)]
    if ($LASTEXITCODE -ne 0) { Write-Host "::error::$stage exited with code $LASTEXITCODE"; exit 1 }
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { Write-Host "::error::git status exited with code $LASTEXITCODE after ${stage}"; exit 1 }
    if ($status.Count -gt 0) { Write-Host "::error::Dirty tree after ${stage}:`n$($status -join "`n")"; exit 1 }
    Write-Host "Clean tree after $stage."
}
