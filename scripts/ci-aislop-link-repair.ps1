<#
.SYNOPSIS
Retargets restored absolute dependency links of an aislop checkout into this job's tree, then verifies its CLI.
#>
[CmdletBinding()]
param([string] $ForkDirectory, [string] $EvidenceLogPath)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Repair-AislopDependencyLinks {
    param([string] $ForkDirectory, [string] $EvidenceLogPath)
    $root = (Resolve-Path -LiteralPath $ForkDirectory).Path
    $links = [Collections.Generic.List[string]]::new()
    $stack = [Collections.Generic.Stack[string]]::new([string[]]@($root))
    while ($stack.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($stack.Pop())) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $links.Add($entry) }
            elseif (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $stack.Push($entry) }
        }
    }
    $repaired = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    do {
        $progressed = $false
        $wrongKind = $null
        $unrepairable = [Collections.Generic.List[string]]::new()
        foreach ($entry in $links) {
            $target = (Get-Item -LiteralPath $entry -Force).LinkTarget
            if ([string]::IsNullOrEmpty($target)) { continue }
            $normalized = $target -replace '^\\\\\?\\', '' -replace '^\\\?\?\\', ''
            $entryIsDirectory = (([IO.File]::GetAttributes($entry) -band [IO.FileAttributes]::Directory) -ne 0)
            if ([IO.Path]::IsPathRooted($normalized)) {
                $components = $normalized.Split('\', [StringSplitOptions]::RemoveEmptyEntries)
                $candidate = $null
                for ($index = 1; $index -lt $components.Count; $index++) {
                    $candidatePath = Join-Path $root ([string]::Join('\', $components, $index, $components.Count - $index))
                    if (Test-Path -LiteralPath $candidatePath) { $candidate = $candidatePath; break }
                }
                if ($null -eq $candidate) {
                    if (-not (Test-Path -LiteralPath $normalized)) { $unrepairable.Add("$entry -> $target") }
                    continue
                }
                $candidateIsDirectory = Test-Path -LiteralPath $candidate -PathType Container
                if (($candidate -ieq $normalized) -and ($entryIsDirectory -eq $candidateIsDirectory)) { continue }
                $retargetValue = $candidate
            } else {
                $candidate = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $entry) $normalized))
                if (-not (Test-Path -LiteralPath $candidate)) {
                    $unrepairable.Add("$entry -> $target")
                    continue
                }
                $candidateIsDirectory = Test-Path -LiteralPath $candidate -PathType Container
                if ($entryIsDirectory -eq $candidateIsDirectory) { continue }
                $retargetValue = $target
            }
            if ($entryIsDirectory) {
                if (-not $candidateIsDirectory) {
                    if ($null -eq $wrongKind) { $wrongKind = "A directory is required, but $candidate is not a directory" }
                    continue
                }
                [IO.Directory]::Delete($entry)
                New-Item -ItemType Junction -Path $entry -Target $retargetValue | Out-Null
            } else {
                [IO.File]::Delete($entry)
                New-Item -ItemType SymbolicLink -Path $entry -Target $retargetValue | Out-Null
            }
            [void] $repaired.Add($entry)
            $progressed = $true
        }
    } while ($progressed)
    if ($null -ne $wrongKind) { throw $wrongKind }
    $line = "Repaired $($repaired.Count) of $($links.Count) restored dependency links"
    Write-Host $line
    Set-Content -LiteralPath $EvidenceLogPath -Value $line -Encoding utf8
    if ($unrepairable.Count -gt 0) {
        $unrepairable | ForEach-Object { Write-Host "UNREPAIRABLE: $_" }
        throw "Restored aislop tree has dependency links that do not map into this job's tree"
    }
}

function Invoke-AislopLinkRepair {
    param([string] $ForkDirectory, [string] $EvidenceLogPath)
    Repair-AislopDependencyLinks $ForkDirectory $EvidenceLogPath
    & node (Join-Path $ForkDirectory 'dist/cli.js') --version
    if ($LASTEXITCODE -ne 0) { throw 'Restored aislop CLI failed to start after link repair' }
}

if ($MyInvocation.InvocationName -eq '.') { return }
try { Invoke-AislopLinkRepair $ForkDirectory $EvidenceLogPath }
catch { Write-Host "::error::$($_.Exception.Message)"; exit 1 }
