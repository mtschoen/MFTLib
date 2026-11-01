#requires -Version 7.0
param([string]$RequestBase64)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/coverage-log.ps1"

function Invoke-CoverageExecutable {
    param([hashtable]$Request, [object]$Outcome)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Request.Target
    $start.WorkingDirectory = $Request.WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Request.Arguments) { $start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $readers = [Collections.Generic.List[IO.StreamReader]]::new()
        $pending = [Collections.Generic.List[Threading.Tasks.Task[string]]]::new()
        foreach ($reader in @($process.StandardOutput, $process.StandardError)) {
            $readers.Add($reader)
            $pending.Add($reader.ReadLineAsync())
        }
        while ($pending.Count -gt 0) {
            $index = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]$pending.ToArray())
            $line = $pending[$index].GetAwaiter().GetResult()
            if ($null -eq $line) {
                $pending.RemoveAt($index)
                $readers.RemoveAt($index)
            } else {
                Write-Output $line
                $pending[$index] = $readers[$index].ReadLineAsync()
            }
        }
        $process.WaitForExit()
        $Outcome.ExitCode = $process.ExitCode
    } finally { $process.Dispose() }
}

function Invoke-CoverageTarget {
    param([hashtable]$Request)
    $outcome = [pscustomobject]@{ ExitCode = 1 }
    $originalDirectory = (Get-Location).Path
    $savedEnvironment = @{}
    $savedExitCode = $global:LASTEXITCODE
    try {
        Set-Content -LiteralPath $Request.LogPath -Value '' -NoNewline -Encoding utf8
        Set-Location -LiteralPath $Request.WorkingDirectory
        if ($null -ne $Request.Environment) {
            foreach ($name in $Request.Environment.Keys) {
                $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
                [Environment]::SetEnvironmentVariable($name, [string]$Request.Environment[$name], 'Process')
            }
        }
        & {
            if ([IO.Path]::GetExtension($Request.Target) -ieq '.ps1') {
                $global:LASTEXITCODE = 0
                $targetArguments = [string[]]$Request.Arguments
                & $Request.Target @targetArguments
                $outcome.ExitCode = [int]$LASTEXITCODE
            } else {
                Invoke-CoverageExecutable $Request $outcome
            }
        } *>&1 | Tee-Object -LiteralPath $Request.LogPath |
            ForEach-Object { Write-Host $_ }
    } catch {
        $outcome.ExitCode = 1
        $message = $_.ToString()
        Write-Host $message
        try { Add-Content -LiteralPath $Request.LogPath -Value $message -Encoding utf8 }
        catch { Write-Host "Cannot append child error to log: $_" }
    } finally {
        # Marker publication must not be skipped by a cleanup failure.
        try {
            $temporaryMarker = "$($Request.DonePath).tmp"
            [IO.File]::WriteAllText($temporaryMarker, [string]$outcome.ExitCode)
            Move-Item -LiteralPath $temporaryMarker -Destination $Request.DonePath -Force
        } finally {
            if ($null -ne $savedEnvironment) {
                foreach ($name in $savedEnvironment.Keys) {
                    [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
                }
            }
            $global:LASTEXITCODE = $savedExitCode
            Set-Location -LiteralPath $originalDirectory
        }
    }
    return [int]$outcome.ExitCode
}

# Popup return codes (WScript.Shell): 1 OK, 2 Cancel, -1 timeout.
function Invoke-ElevationPopup {
    param([string]$Message, [string]$Title, [int]$TimeoutSeconds)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $shell = New-Object -ComObject WScript.Shell
    # 1 OK/Cancel + 64 information icon (plays its sound) + 4096 system modal (takes foreground).
    $result = $shell.Popup($Message, $TimeoutSeconds, $Title, 1 + 64 + 4096)
    return [pscustomobject]@{ Result = [int]$result; ElapsedSeconds = $clock.Elapsed.TotalSeconds }
}

function Get-CoverageEntryScriptName {
    $frames = @(Get-PSCallStack | Where-Object { $_.ScriptName })
    if ($frames.Count -eq 0) { return 'coverage-child.ps1' }
    return [IO.Path]::GetFileName($frames[$frames.Count - 1].ScriptName)
}

# System-modal heads-up before the UAC prompt, matching the samples (docs/elevation.md):
# a dismissal within 0.75 seconds is shown again; five minutes unanswered counts as Cancel.
function Confirm-CoverageElevation {
    param([hashtable]$Request, [string]$ScriptName = (Get-CoverageEntryScriptName))
    $command = (@([string]$Request.Target) + @($Request.Arguments | ForEach-Object { [string]$_ })) -join ' '
    $message = "$ScriptName needs administrator rights to run the elevated admin tests and collect coverage.`n`n" +
        "A Windows UAC prompt follows this message.`n`nElevated command:`n$command`n`n" +
        'OK continues to the UAC prompt. Cancel stops without elevating.'
    while ($true) {
        $answer = Invoke-ElevationPopup $message "$ScriptName : UAC prompt next" 300
        if ($answer.Result -eq 1 -and $answer.ElapsedSeconds -lt 0.75) { continue }
        if ($answer.Result -eq 2 -and $answer.ElapsedSeconds -lt 0.75) { continue }
        return $answer.Result -eq 1
    }
}

function Start-CoverageChild {
    param([hashtable]$Request, [switch]$NoElevation)
    if (-not $NoElevation) {
        if ($env:MFTLIB_SAMPLE_UNATTENDED -eq '1') {
            throw 'Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped. Run from an elevated shell or use run-coverage.ps1 -NonInteractive.'
        }
        if (-not (Confirm-CoverageElevation $Request)) {
            throw 'Elevation cancelled at the heads-up dialog (Cancel or five minutes unanswered); no UAC prompt was raised.'
        }
    }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
        ($Request | ConvertTo-Json -Depth 6 -Compress)))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
    $start.UseShellExecute = -not $NoElevation
    if ($NoElevation) { $start.CreateNoWindow = $true }
    else { $start.Verb = 'runas'; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal }
    foreach ($argument in @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $PSScriptRoot 'coverage-child.ps1'), '-RequestBase64', $encoded)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $null = $process.Handle
    return $process
}

function Start-CoverageClock { [Diagnostics.Stopwatch]::StartNew() }

function Test-ChildAlive {
    param([object]$Process)
    try { return $null -ne $Process -and -not $Process.HasExited }
    catch { return $false }
}

function Read-CoverageExitCode {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $value = 0
    $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if ([int]::TryParse($text, [ref]$value)) { return $value }
    return $null
}

function Wait-CoverageChild {
    param([object]$Process, [string]$LogPath, [string]$DonePath,
        [int]$TimeoutSeconds = 1800, [int]$HeartbeatSeconds = 30)
    $clock = Start-CoverageClock
    $nextHeartbeatSeconds = $HeartbeatSeconds
    $deadlineWarned = $false
    Write-Host "Waiting for elevated coverage run (live log: $LogPath)..."
    try {
        while ($true) {
            Start-Sleep -Seconds 2
            $childExit = Read-CoverageExitCode $DonePath
            $isAlive = Test-ChildAlive $Process
            Process-NewLogOutput -Path $LogPath
            if (-not $isAlive) {
                # Re-read after observing death: publication can race the first read.
                $childExit = Read-CoverageExitCode $DonePath
                Process-NewLogOutput -Path $LogPath -Drain
                $processExit = $null
                try { $processExit = $Process.ExitCode }
                catch { Write-Host 'Child exit status could not be read.' }
                if ($null -eq $childExit) {
                    $detail = if ($null -ne $processExit) { " with exit code $processExit" } else { '' }
                    Write-Host "Elevated child process exited$detail without writing a completion marker containing a valid exit code."
                    return 1
                }
                if (-not (Test-Path -LiteralPath $LogPath)) {
                    Write-Host "Elevated coverage run produced no output log at $LogPath"
                    return 1
                }
                if ($null -eq $processExit) {
                    Write-Host 'Elevated child process exit code is unavailable.'
                    return 1
                }
                if ($processExit -ne 0) { $childExit = [int]$processExit }
                Write-Host ("`n=== Elevated coverage run finished in {0:mm\:ss} with exit code $childExit ===" -f $clock.Elapsed)
                if ($childExit -ne 0) { Write-Host "Elevated coverage run failed with exit code $childExit." }
                return [int]$childExit
            }
            if ($clock.Elapsed.TotalSeconds -ge $nextHeartbeatSeconds) {
                Write-Host ("  [waiting] {0:mm\:ss} elapsed, child still running..." -f $clock.Elapsed)
                $nextHeartbeatSeconds = $clock.Elapsed.TotalSeconds + $HeartbeatSeconds
            }
            if (-not $deadlineWarned -and $clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                Write-Host ("  [waiting] {0:mm\:ss} elapsed (configured timeout of {1}s reached; child still alive, continuing to wait)..." -f $clock.Elapsed, $TimeoutSeconds)
                $deadlineWarned = $true
            }
        }
    } finally { Reset-LogTailer }
}

function Invoke-CoverageChild {
    param([hashtable]$Request, [switch]$NoElevation,
        [int]$TimeoutSeconds = 1800, [int]$HeartbeatSeconds = 30)
    $process = $null
    try {
        foreach ($path in @($Request.LogPath, $Request.DonePath, "$($Request.DonePath).tmp")) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        $process = Start-CoverageChild $Request -NoElevation:$NoElevation
        return Wait-CoverageChild $process $Request.LogPath $Request.DonePath $TimeoutSeconds $HeartbeatSeconds
    } catch {
        Write-Host "Unable to complete coverage child: $_"
        return 1
    } finally {
        Reset-LogTailer
        if ($null -ne $process) { $process.Dispose() }
        foreach ($path in @($Request.DonePath, "$($Request.DonePath).tmp")) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
try {
    $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) |
        ConvertFrom-Json -AsHashtable
    exit (Invoke-CoverageTarget $request)
} catch {
    Write-Host "Coverage child protocol failed: $_"
    exit 1
}
