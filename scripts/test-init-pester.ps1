$ErrorActionPreference = 'Stop'

# Runs scripts/init.tests.ps1 in this one process. Pester is a checked prerequisite (init.ps1 reports it); a
# missing or older module is fixed on the machine or runner, never installed from here.
$pester = Get-Module -ListAvailable -Name Pester | Where-Object { $_.Version -ge [version]'6.0' } |
    Sort-Object -Property Version -Descending | Select-Object -First 1
if (-not $pester) {
    throw 'Pester 6 or newer is not installed on this machine. Install it (Install-Module Pester -MinimumVersion 6.0 -Scope AllUsers) or fix the runner at the service; this script never installs it.'
}
Import-Module $pester.Path -ErrorAction Stop

$configuration = New-PesterConfiguration
$configuration.Run.Path = Join-Path $PSScriptRoot 'init.tests.ps1'
$configuration.Run.Exit = $true
$configuration.Output.Verbosity = 'Normal'
Invoke-Pester -Configuration $configuration
