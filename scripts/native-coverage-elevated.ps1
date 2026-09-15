# Run native coverage with elevation (for USN journal tests that need admin).
# Self-elevates if needed. The elevated process runs hidden and streams its output
# to a log file as it goes; the visible parent prints new log lines and a heartbeat
# while waiting, so a healthy long run is distinguishable from a hung one.
#
# Usage:
#   .\scripts\native-coverage-elevated.ps1
#   .\scripts\native-coverage-elevated.ps1 -TimeoutSeconds 3600
#   .\scripts\native-coverage-elevated.ps1 -HeartbeatSeconds 15
#
# Note: -TimeoutSeconds is a warning threshold when the child process is alive;
# the parent logs a warning but continues waiting while the child process runs.
# Heartbeats are emitted at a 2-second polling granularity (due to the parent's
# 2-second polling interval), so values like -HeartbeatSeconds 1 evaluate on the
# 2-second polling loop rather than producing 1-second updates.

param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TimeoutSeconds = 1800,
    # Polling runs on a 2-second interval, so heartbeats are emitted at 2-second granularity.
    [ValidateRange(1, [int]::MaxValue)]
    [int]$HeartbeatSeconds = 30,
    [string]$TargetScript,
    [switch]$NoElevation,
    [switch]$ForceNonAdmin,
    [switch]$FunctionsOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputFile = Join-Path $repoRoot "native-coverage-elevated.log"

# Incremental log tailer state: retains reader/stream across polls, preserves
# incomplete trailing lines, and avoids re-reading earlier log lines.
$script:logStream = $null
$script:logReader = $null
$script:pendingText = ""
$script:childProcess = $null

function Reset-LogTailer {
    if ($null -ne $script:logReader) {
        $script:logReader.Dispose()
        $script:logReader = $null
    }
    if ($null -ne $script:logStream) {
        $script:logStream.Dispose()
        $script:logStream = $null
    }
    $script:pendingText = ""
}

function Process-NewLogOutput {
    param(
        [string]$Path = $outputFile,
        [switch]$Drain,
        [switch]$PassThru
    )

    if ($null -eq $script:logReader -and (Test-Path $Path)) {
        try {
            $encoding = [System.Text.Encoding]::Unicode
            $sample = [byte[]]::new(4)
            $probeFs = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite
            )
            try {
                if (-not $Drain -and $probeFs.Length -lt 2) {
                    return
                }
                $readCount = $probeFs.Read($sample, 0, 4)
                if ($readCount -ge 2 -and $sample[0] -eq 0xFF -and $sample[1] -eq 0xFE) {
                    $encoding = [System.Text.Encoding]::Unicode
                }
                elseif ($readCount -ge 3 -and $sample[0] -eq 0xEF -and $sample[1] -eq 0xBB -and $sample[2] -eq 0xBF) {
                    $encoding = [System.Text.Encoding]::UTF8
                }
                elseif ($readCount -ge 2 -and $sample[1] -ne 0x00) {
                    $encoding = [System.Text.Encoding]::UTF8
                }
            }
            finally {
                $probeFs.Dispose()
            }

            $script:logStream = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite
            )
            $script:logReader = [System.IO.StreamReader]::new(
                $script:logStream,
                $encoding,
                $true
            )
        }
        catch {
            $script:logStream = $null
            $script:logReader = $null
        }
    }

    if ($null -ne $script:logReader) {
        try {
            $chunk = $script:logReader.ReadToEnd()
            if ($chunk.Length -gt 0) {
                $script:pendingText += $chunk
            }
        }
        catch {}

        if ($script:pendingText.Length -gt 0) {
            if ($Drain) {
                $strReader = [System.IO.StringReader]::new($script:pendingText)
                while ($null -ne ($line = $strReader.ReadLine())) {
                    if ($PassThru) {
                        $line
                    }
                    else {
                        Write-Host "  $line"
                    }
                }
                $strReader.Dispose()
                $script:pendingText = ""
            }
            else {
                $lastNewline = $script:pendingText.LastIndexOf("`n")
                if ($lastNewline -ge 0) {
                    $completePortion = $script:pendingText.Substring(0, $lastNewline + 1)
                    $script:pendingText = $script:pendingText.Substring($lastNewline + 1)

                    $strReader = [System.IO.StringReader]::new($completePortion)
                    while ($null -ne ($line = $strReader.ReadLine())) {
                        if ($PassThru) {
                            $line
                        }
                        else {
                            Write-Host "  $line"
                        }
                    }
                    $strReader.Dispose()
                }
            }
        }
    }
}

function Test-ChildAlive {
    if ($null -eq $script:childProcess) { return $false }
    try {
        return -not $script:childProcess.HasExited
    }
    catch {
        return $false
    }
}

if ($FunctionsOnly -or $MyInvocation.InvocationName -eq '.') {
    return
}

$isAdmin = if ($ForceNonAdmin) {
    $false
} else {
    try {
        ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch {
        $false
    }
}

if (-not $isAdmin) {
    Write-Host "Not elevated. Launching elevated shell..."
    $script = if ($TargetScript) { $TargetScript } else { Join-Path $repoRoot "scripts\native-coverage.ps1" }
    $doneMarker = Join-Path $repoRoot "native-coverage-elevated.done"
    $doneMarkerTemp = "$doneMarker.tmp"

    # Clean up from previous runs
    if (Test-Path $outputFile) { Remove-Item $outputFile }
    if (Test-Path $doneMarker) { Remove-Item $doneMarker }
    if (Test-Path $doneMarkerTemp) { Remove-Item $doneMarkerTemp }

    # These paths are interpolated into single-quoted literals in the child command
    # below, which runs elevated. Doubling an embedded apostrophe is the only escape
    # a single-quoted PowerShell string accepts (backtick does not apply there), so
    # without this a checkout under a path like "D:\Matt's Projects" would terminate
    # the literal early and run the remainder of the path as administrator.
    $repoRootLiteral = $repoRoot -replace "'", "''"
    $scriptLiteral = $script -replace "'", "''"
    $outputFileLiteral = $outputFile -replace "'", "''"
    $doneMarkerLiteral = $doneMarker -replace "'", "''"
    $doneMarkerTempLiteral = $doneMarkerTemp -replace "'", "''"

    # Launch elevated - streams all output to the log as it is produced (Tee-Object
    # flushes per line, unlike *> which Windows PowerShell writes once at the end),
    # records child exit code in .done marker, then exits.
    # -Verb RunAs triggers UAC prompt (must be visible). Window auto-closes via exit.
    # try/catch/finally: a terminating error in the child script must still land in
    # the log and still create the marker, or the parent below polls forever.
    # Atomic write-then-rename in finally avoids a race where the parent observes an empty marker.
    # Retain the launched process via -PassThru so the parent can check process liveness.
    $psExe = if (Get-Command powershell.exe -ErrorAction SilentlyContinue) { "powershell" } else { "pwsh" }
    $childCommand = "& { Set-Location '$repoRootLiteral'; `$childExit = 0; try { & '$scriptLiteral' *>&1 | Tee-Object -FilePath '$outputFileLiteral'; if (`$LASTEXITCODE) { `$childExit = `$LASTEXITCODE } } catch { `$_ | Out-File '$outputFileLiteral' -Append; `$childExit = 1 } finally { Set-Content -Path '$doneMarkerTempLiteral' -Value `$childExit; Move-Item -Force '$doneMarkerTempLiteral' '$doneMarkerLiteral' }; exit `$childExit }"
    $procArgs = @("-ExecutionPolicy", "Bypass", "-Command", $childCommand)
    if ($NoElevation) {
        $childProcess = Start-Process $psExe -ArgumentList $procArgs -PassThru
    }
    else {
        $childProcess = Start-Process $psExe -Verb RunAs -ArgumentList $procArgs -PassThru
    }
    $script:childProcess = $childProcess

    # Poll for completion: wait until the marker exists and contains a valid integer exit code.
    # While waiting, print log lines incrementally as they arrive, plus a heartbeat every 30
    # seconds so a silent child still shows the run is alive.
    Write-Host "Waiting for elevated coverage run (live log: $outputFile)..."
    $childExit = $null
    $nextHeartbeatSeconds = $HeartbeatSeconds
    $deadlineWarned = $false
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        while ($null -eq $childExit) {
            Start-Sleep -Seconds 2

            if (Test-Path $doneMarker) {
                $markerContent = (Get-Content $doneMarker -ErrorAction SilentlyContinue | Out-String).Trim()
                if ($markerContent -match '^-?\d+$') {
                    $childExit = [int64]$markerContent
                }
            }

            $isAlive = Test-ChildAlive

            if ($null -eq $childExit -and -not $isAlive) {
                # Child process exited; final check for the completion marker
                if (Test-Path $doneMarker) {
                    $markerContent = (Get-Content $doneMarker -ErrorAction SilentlyContinue | Out-String).Trim()
                    if ($markerContent -match '^-?\d+$') {
                        $childExit = [int64]$markerContent
                    }
                }

                if ($null -eq $childExit) {
                    Process-NewLogOutput -Drain
                    $exitCodeMsg = ""
                    try {
                        if ($null -ne $childProcess -and $null -ne $childProcess.ExitCode) {
                            $exitCodeMsg = " with exit code $($childProcess.ExitCode)"
                        }
                    } catch {}
                    Write-Host "Elevated child process exited$exitCodeMsg without writing a completion marker."
                    exit 1
                }
            }

            Process-NewLogOutput

            if ($null -ne $childExit) {
                break
            }

            if ($stopwatch.Elapsed.TotalSeconds -ge $nextHeartbeatSeconds) {
                Write-Host ("  [waiting] {0:mm\:ss} elapsed, child still running..." -f $stopwatch.Elapsed)
                $nextHeartbeatSeconds = $stopwatch.Elapsed.TotalSeconds + $HeartbeatSeconds
            }

            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                if ($isAlive) {
                    # Process is still running: continue waiting while alive
                    if (-not $deadlineWarned) {
                        Write-Host ("  [waiting] {0:mm\:ss} elapsed (configured timeout of {1}s reached; child still alive, continuing to wait)..." -f $stopwatch.Elapsed, $TimeoutSeconds)
                        $deadlineWarned = $true
                    }
                }
                else {
                    Process-NewLogOutput -Drain
                    Write-Error "Elevated coverage run timed out after $TimeoutSeconds seconds. See log at $outputFile"
                    exit 1
                }
            }
        }

        Remove-Item $doneMarker -ErrorAction SilentlyContinue

        if (Test-Path $outputFile) {
            # Drain any trailing output produced before child completion
            Process-NewLogOutput -Drain
        }
        else {
            Write-Error "Elevated coverage run produced no output log at $outputFile"
            exit 1
        }
    }
    finally {
        Reset-LogTailer
    }

    Write-Host ("`n=== Elevated coverage run finished in {0:mm\:ss} with exit code $childExit ===" -f $stopwatch.Elapsed)

    if ($childExit -ne 0) {
        # Write-Host, not Write-Error: under $ErrorActionPreference = "Stop" a
        # Write-Error terminates the script with exit code 1 before the exit
        # below can run, which would hide the child's real exit code.
        Write-Host "Elevated coverage run failed with exit code $childExit."
        exit $childExit
    }
    exit 0
}

# Already elevated - just run it
& (Join-Path $repoRoot "scripts\native-coverage.ps1")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
