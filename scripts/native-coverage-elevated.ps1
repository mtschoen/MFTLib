# Run native coverage with elevation (for USN journal tests that need admin).
# Self-elevates if needed. The elevated process runs hidden and writes results to a log file.
#
# Usage:
#   .\scripts\native-coverage-elevated.ps1
#   .\scripts\native-coverage-elevated.ps1 -TimeoutSeconds 1200

param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Not elevated. Launching elevated shell..."
    $script = Join-Path $repoRoot "scripts\native-coverage.ps1"
    $outputFile = Join-Path $repoRoot "native-coverage-elevated.log"
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

    # Launch elevated - redirects all output to log, records child exit code in .done marker, then exits.
    # -Verb RunAs triggers UAC prompt (must be visible). Window auto-closes via exit.
    # try/catch/finally: a terminating error in the child script must still land in
    # the log and still create the marker, or the parent below polls forever.
    # Atomic write-then-rename in finally avoids a race where the parent observes an empty marker.
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-ExecutionPolicy", "Bypass",
        "-Command", "& { Set-Location '$repoRootLiteral'; `$childExit = 0; try { & '$scriptLiteral' *> '$outputFileLiteral'; if (`$LASTEXITCODE) { `$childExit = `$LASTEXITCODE } } catch { `$_ | Out-File '$outputFileLiteral' -Append; `$childExit = 1 } finally { Set-Content -Path '$doneMarkerTempLiteral' -Value `$childExit; Move-Item -Force '$doneMarkerTempLiteral' '$doneMarkerLiteral' }; exit `$childExit }"
    )

    # Poll for completion: wait until the marker exists and contains a valid integer exit code
    Write-Host "Waiting for elevated coverage run..."
    $childExit = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $childExit) {
        Start-Sleep -Seconds 2
        if (Test-Path $doneMarker) {
            $markerContent = (Get-Content $doneMarker -ErrorAction SilentlyContinue | Out-String).Trim()
            if ($markerContent -match '^-?\d+$') {
                $childExit = [int64]$markerContent
            }
        }

        if ($null -eq $childExit -and $stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            Write-Error "Elevated coverage run timed out after $TimeoutSeconds seconds. See log at $outputFile"
            exit 1
        }
    }
    Remove-Item $doneMarker -ErrorAction SilentlyContinue

    if (Test-Path $outputFile) {
        Write-Host "`n=== Elevated coverage results ==="
        Get-Content $outputFile -Encoding Unicode
    }
    else {
        Write-Error "Elevated coverage run produced no output log at $outputFile"
        exit 1
    }

    if ($childExit -ne 0) {
        Write-Error "Elevated coverage run failed with exit code $childExit."
        exit $childExit
    }
    exit 0
}

# Already elevated - just run it
& (Join-Path $repoRoot "scripts\native-coverage.ps1")
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
