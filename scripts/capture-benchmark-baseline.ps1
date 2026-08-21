# scripts/capture-benchmark-baseline.ps1
# Captures a throughput and memory baseline for Benchmark.exe before representation changes.
#
# Usage:
#   .\scripts\capture-benchmark-baseline.ps1
#   .\scripts\capture-benchmark-baseline.ps1 -OutputPath "Benchmark\baseline-before-compact.txt" -RecordCount 1000000 -IterationCount 5

param(
    [Parameter(Position = 0)]
    [string]$OutputPath,

    [Parameter(Position = 1)]
    [ulong]$RecordCount = 1000000,

    [Parameter(Position = 2)]
    [int]$IterationCount = 5
)

$ErrorActionPreference = "Stop"

$repoRoot = if ($env:GITHUB_WORKSPACE) {
    [string]$env:GITHUB_WORKSPACE
} else {
    [string](Resolve-Path "$PSScriptRoot\..")
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "Benchmark\baseline-before-compact.txt"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
}

$gitSha = (& git -C $repoRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($gitSha -notmatch '^[0-9a-f]{40}$') {
    Write-Error "Failed to obtain 40-character hexadecimal git commit SHA from git rev-parse HEAD."
    exit 1
}

$benchmarkExe = Join-Path $repoRoot "Benchmark\bin\x64\Release\net8.0\Benchmark.exe"
if (-not (Test-Path $benchmarkExe)) {
    $benchmarkExe = Join-Path $repoRoot "Benchmark\bin\Release\net8.0\Benchmark.exe"
}
if (-not (Test-Path $benchmarkExe)) {
    Write-Error "Benchmark.exe not found at $benchmarkExe. Please build the solution first with: MSBuild.exe MFTLib.sln -p:Configuration=Release -p:Platform=x64"
    exit 1
}

$baselineFile = Join-Path $repoRoot "Benchmark\baseline.txt"
$baselineBytes = if (Test-Path $baselineFile) { [System.IO.File]::ReadAllBytes($baselineFile) } else { $null }

$tempStdout = [System.IO.Path]::GetTempFileName()
$tempStderr = [System.IO.Path]::GetTempFileName()

try {
    Write-Host "Running Benchmark.exe ($RecordCount records, $IterationCount iterations)..." -ForegroundColor Cyan

    $process = Start-Process `
        -FilePath $benchmarkExe `
        -ArgumentList @("$RecordCount", "$IterationCount") `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $tempStdout `
        -RedirectStandardError $tempStderr

    $process.WaitForExit()
    $exitCode = $process.ExitCode

    $peakWorkingSet = $null
    $peakPrivateBytes = $null

    try {
        $peakWorkingSet = $process.PeakWorkingSet64
        $peakPrivateBytes = $process.PeakPagedMemorySize64
    } catch {
        # Process property read can fail or return stale values after exit; fall back to GetProcessMemoryInfo P/Invoke below.
    }

    if ($null -eq $peakWorkingSet -or $peakWorkingSet -le 0 -or $null -eq $peakPrivateBytes -or $peakPrivateBytes -le 0) {
        if (-not ([System.Management.Automation.PSTypeName]'NativeProcessMemory').Type) {
            Add-Type -TypeDefinition @"
            using System;
            using System.Runtime.InteropServices;
            public class NativeProcessMemory {
                [StructLayout(LayoutKind.Sequential)]
                public struct PROCESS_MEMORY_COUNTERS {
                    public uint cb;
                    public uint PageFaultCount;
                    public UIntPtr PeakWorkingSetSize;
                    public UIntPtr WorkingSetSize;
                    public UIntPtr QuotaPeakPagedPoolUsage;
                    public UIntPtr QuotaPagedPoolUsage;
                    public UIntPtr QuotaPeakNonPagedPoolUsage;
                    public UIntPtr QuotaNonPagedPoolUsage;
                    public UIntPtr PagefileUsage;
                    public UIntPtr PeakPagefileUsage;
                }
                [DllImport("psapi.dll", SetLastError = true)]
                public static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint size);
            }
"@
        }
        $mem = New-Object NativeProcessMemory+PROCESS_MEMORY_COUNTERS
        $mem.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($mem)
        if ([NativeProcessMemory]::GetProcessMemoryInfo($process.Handle, [ref]$mem, $mem.cb)) {
            $peakWorkingSet = [long]$mem.PeakWorkingSetSize.ToUInt64()
            $peakPrivateBytes = [long]$mem.PeakPagefileUsage.ToUInt64()
        }
    }

    $stdoutContent = if (Test-Path $tempStdout) { [System.IO.File]::ReadAllText($tempStdout) } else { "" }
    $stderrContent = if (Test-Path $tempStderr) { [System.IO.File]::ReadAllText($tempStderr) } else { "" }

    if ($exitCode -ne 0) {
        [Console]::Error.WriteLine("Benchmark.exe exited with non-zero exit code $exitCode.`n$stderrContent")
        exit $exitCode
    }

    if ($null -eq $peakWorkingSet -or $null -eq $peakPrivateBytes -or $peakWorkingSet -le 0 -or $peakPrivateBytes -le 0) {
        Write-Error "Failed to capture peak memory metrics for Benchmark.exe (PeakWorkingSet64=$peakWorkingSet, PeakPagedMemorySize64=$peakPrivateBytes)."
        exit 1
    }

    if (-not ($stdoutContent -match 'Throughput:')) {
        Write-Error "Captured stdout does not contain required 'Throughput:' line.`n$stdoutContent"
        exit 1
    }

    $outputDir = [System.IO.Path]::GetDirectoryName($OutputPath)
    if (-not [string]::IsNullOrEmpty($outputDir) -and -not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $header = "Git: $gitSha`r`nBenchmark arguments: $RecordCount $IterationCount`r`nPeak working set: $peakWorkingSet`r`nPeak private bytes: $peakPrivateBytes`r`n`r`n"
    $fullReport = $header + $stdoutContent

    [System.IO.File]::WriteAllText($OutputPath, $fullReport)
    Write-Host "Benchmark baseline captured to $OutputPath" -ForegroundColor Green
} finally {
    if ($null -ne $baselineBytes) {
        [System.IO.File]::WriteAllBytes($baselineFile, $baselineBytes)
    } elseif (Test-Path $baselineFile) {
        Remove-Item $baselineFile -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $tempStdout) {
        Remove-Item $tempStdout -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $tempStderr) {
        Remove-Item $tempStderr -Force -ErrorAction SilentlyContinue
    }
}
