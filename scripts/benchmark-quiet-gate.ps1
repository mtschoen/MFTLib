<#
.SYNOPSIS
Waits until the benchmark host has been quiet for a sustained window, or gives up after a time limit.
.DESCRIPTION
Samples total CPU utilisation and the busy percentage of the volume that holds the temporary folder the
enumeration-walk benchmark writes to. The box is quiet once every sample in a window of QuietSeconds is at or under
both limits; one busy sample restarts the window. After TimeoutMinutes without a quiet window the gate reports the
busy observations and returns. Run standalone it prints the result and exits 0 when quiet and 3 when busy, which is
also the attended way to check the counter readings on an idle and a loaded host.
Counters: '\Processor Information(_Total)\% Processor Utility' is the counter Task Manager reports (work done
against nominal frequency, so boost and throttling are accounted for); '\LogicalDisk(<drive>)\% Idle Time' for the
temporary folder's drive, whose busy percentage is 100 minus the idle percentage.
.PARAMETER TimeoutMinutes
Longest total wait before the box is declared busy; defaults to 30.
.PARAMETER QuietSeconds
Length of the window in which every sample must be under both limits; defaults to 60.
.PARAMETER SampleIntervalSeconds
Pause between samples; defaults to 5.
.PARAMETER CpuPercentLimit
Highest total CPU utilisation, in percent, that still counts as quiet; defaults to 15.
.PARAMETER DiskBusyPercentLimit
Highest busy percentage of the temporary folder's volume that still counts as quiet; defaults to 10.
.PARAMETER TemporaryDirectory
The folder whose volume is watched; defaults to the process temporary folder.
.PARAMETER ResultPath
Optional path that receives the result as JSON.
#>
[CmdletBinding()]
param(
    [int] $TimeoutMinutes = 30,
    [int] $QuietSeconds = 60,
    [int] $SampleIntervalSeconds = 5,
    [double] $CpuPercentLimit = 15,
    [double] $DiskBusyPercentLimit = 10,
    [string] $TemporaryDirectory = [IO.Path]::GetTempPath(),
    [string] $ResultPath
)

$ErrorActionPreference = 'Stop'

function Get-BenchmarkTemporaryVolume {
    param([Parameter(Mandatory)][string] $Path)
    # The drive qualifier, for example "C:", names the LogicalDisk counter instance.
    Split-Path -Path ([IO.Path]::GetFullPath($Path)) -Qualifier
}

function Read-BenchmarkLoadSample {
    param([Parameter(Mandatory)][string] $Volume)
    $counters = @('\Processor Information(_Total)\% Processor Utility', "\LogicalDisk($Volume)\% Idle Time")
    $readings = (Get-Counter -Counter $counters -ErrorAction Stop).CounterSamples
    $cpu = $readings | Where-Object { $_.Path -like '*% processor utility' } | Select-Object -First 1
    $idle = $readings | Where-Object { $_.Path -like '*% idle time' } | Select-Object -First 1
    if ($null -eq $cpu -or $null -eq $idle) { throw 'The load counters returned no reading.' }
    $busy = [Math]::Min(100.0, [Math]::Max(0.0, 100.0 - [double] $idle.CookedValue))
    [pscustomobject]@{ CpuPercent = [double] $cpu.CookedValue; DiskBusyPercent = $busy }
}

function Wait-BenchmarkQuietBox {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock] $ReadSample,
        [scriptblock] $GetNow = { [DateTime]::UtcNow },
        [scriptblock] $Sleep = { param($Seconds) Start-Sleep -Seconds $Seconds },
        [int] $TimeoutMinutes = 30,
        [int] $QuietSeconds = 60,
        [int] $SampleIntervalSeconds = 5,
        [double] $CpuPercentLimit = 15,
        [double] $DiskBusyPercentLimit = 10
    )
    $started = & $GetNow
    $quietSince = $null
    $sampleCount = 0
    $busySampleCount = 0
    $peakCpu = 0.0
    $peakDisk = 0.0
    $windowPeakCpu = 0.0
    $windowPeakDisk = 0.0
    while ($true) {
        $sample = & $ReadSample
        $now = & $GetNow
        $sampleCount++
        $cpu = [double] $sample.CpuPercent
        $disk = [double] $sample.DiskBusyPercent
        $peakCpu = [Math]::Max($peakCpu, $cpu)
        $peakDisk = [Math]::Max($peakDisk, $disk)
        if (($cpu -gt $CpuPercentLimit) -or ($disk -gt $DiskBusyPercentLimit)) {
            $busySampleCount++
            $quietSince = $null
        } else {
            if ($null -eq $quietSince) {
                $quietSince = $now
                $windowPeakCpu = 0.0
                $windowPeakDisk = 0.0
            }
            $windowPeakCpu = [Math]::Max($windowPeakCpu, $cpu)
            $windowPeakDisk = [Math]::Max($windowPeakDisk, $disk)
        }
        $isQuiet = ($null -ne $quietSince) -and (($now - $quietSince).TotalSeconds -ge $QuietSeconds)
        if ($isQuiet -or (($now - $started).TotalMinutes -ge $TimeoutMinutes)) {
            return [pscustomobject]@{
                Quiet = $isQuiet
                SampleCount = $sampleCount
                BusySampleCount = $busySampleCount
                WaitedSeconds = [int] ($now - $started).TotalSeconds
                QuietSeconds = $QuietSeconds
                CpuPercentLimit = $CpuPercentLimit
                DiskBusyPercentLimit = $DiskBusyPercentLimit
                PeakCpuPercent = $peakCpu
                PeakDiskBusyPercent = $peakDisk
                WindowPeakCpuPercent = $windowPeakCpu
                WindowPeakDiskBusyPercent = $windowPeakDisk
                LastCpuPercent = $cpu
                LastDiskBusyPercent = $disk
            }
        }
        & $Sleep $SampleIntervalSeconds
    }
}

function Format-BenchmarkGateSummary {
    param([Parameter(Mandatory)] $Result, [string] $Volume)
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $number = { param([double] $Value) $Value.ToString('0.0', $culture) }
    $where = if ($Volume) { " on volume $Volume" } else { '' }
    if ($Result.Quiet) {
        return ('quiet for {0} s{1}: peak CPU {2} percent (limit {3}), peak disk busy {4} percent (limit {5}); {6} samples in {7} s' -f
            $Result.QuietSeconds, $where, (& $number $Result.WindowPeakCpuPercent), (& $number $Result.CpuPercentLimit),
            (& $number $Result.WindowPeakDiskBusyPercent), (& $number $Result.DiskBusyPercentLimit),
            $Result.SampleCount, $Result.WaitedSeconds)
    }
    ('busy for {0} s{1}: {2} of {3} samples over a limit; peak CPU {4} percent (limit {5}), peak disk busy {6} percent (limit {7}); last sample CPU {8} percent, disk busy {9} percent' -f
        $Result.WaitedSeconds, $where, $Result.BusySampleCount, $Result.SampleCount, (& $number $Result.PeakCpuPercent),
        (& $number $Result.CpuPercentLimit), (& $number $Result.PeakDiskBusyPercent), (& $number $Result.DiskBusyPercentLimit),
        (& $number $Result.LastCpuPercent), (& $number $Result.LastDiskBusyPercent))
}

# Dot-sourcing (the tests and the refresh script) loads the functions only.
if ($MyInvocation.InvocationName -ne '.') {
    $volume = Get-BenchmarkTemporaryVolume -Path $TemporaryDirectory
    $reader = { Read-BenchmarkLoadSample -Volume $volume }.GetNewClosure()
    $result = Wait-BenchmarkQuietBox -ReadSample $reader -TimeoutMinutes $TimeoutMinutes -QuietSeconds $QuietSeconds `
        -SampleIntervalSeconds $SampleIntervalSeconds -CpuPercentLimit $CpuPercentLimit -DiskBusyPercentLimit $DiskBusyPercentLimit
    Write-Host (Format-BenchmarkGateSummary -Result $result -Volume $volume)
    if ($ResultPath) { $result | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding utf8 }
    if ($result.Quiet) { exit 0 }
    exit 3
}
