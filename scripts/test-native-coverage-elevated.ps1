# Regression tests for native-coverage-elevated.ps1
# Covers:
#   1. Fake child surviving past configured deadline (-TimeoutSeconds) with deterministic heartbeat and eventual exit 0 propagation
#   2. Fake child surviving past configured deadline and eventual non-zero exit code propagation
#   3. Incremental streaming and live delivery of child log output through logging pipeline
#   4. Premature child exit detection without completion marker
#   5. Incomplete line buffering and final drain via production Process-NewLogOutput (UTF-8 and Unicode)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$parentScript = Join-Path $repoRoot "scripts\native-coverage-elevated.ps1"

# Dot-source parent script to export production functions for direct testing
. $parentScript -FunctionsOnly

$psExe = if (Get-Command powershell.exe -ErrorAction SilentlyContinue) { "powershell" } else { "pwsh" }
Write-Host "Running native-coverage-elevated tests with $psExe..."

$testDir = Join-Path ([System.IO.Path]::GetTempPath()) ("mftlib_cov_test_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

try {
    # Test 1: Fake child outliving -TimeoutSeconds with deterministic heartbeat (exit code 0)
    Write-Host "`n[Test 1] Fake child outliving -TimeoutSeconds with coordinated heartbeat (exit code 0)..."
    $heartbeatAck1 = Join-Path $testDir "heartbeatAck1.txt"
    $heartbeatAck1Escaped = $heartbeatAck1 -replace "'", "''"
    $fakeChild1 = Join-Path $testDir "fakeChild1.ps1"
    @"
Write-Host "Child starting..."
`$waited = 0
while (-not (Test-Path '$heartbeatAck1Escaped') -and `$waited -lt 150) {
    Start-Sleep -Milliseconds 100
    `$waited++
}
if (-not (Test-Path '$heartbeatAck1Escaped')) {
    Write-Host "Timeout waiting for heartbeat from parent."
    exit 1
}
Write-Host "Child finished successfully."
exit 0
"@ | Set-Content -Path $fakeChild1 -Encoding UTF8

    $sawHeartbeat1 = $false
    $output1Lines = [System.Collections.Generic.List[string]]::new()
    & $psExe -NoProfile -File $parentScript -TargetScript $fakeChild1 -NoElevation -ForceNonAdmin -TimeoutSeconds 2 -HeartbeatSeconds 1 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $output1Lines.Add($line)
        if ($line -match '\[waiting\] \d{2}:\d{2} elapsed, child still running\.\.\.') {
            $sawHeartbeat1 = $true
            Set-Content -Path $heartbeatAck1 -Value "heartbeat_seen"
        }
    }
    $exitCode1 = $LASTEXITCODE

    if ($exitCode1 -ne 0) {
        throw "Test 1 failed: expected exit code 0, got $exitCode1. Output:`n$($output1Lines -join "`n")"
    }
    if (-not $sawHeartbeat1) {
        throw "Test 1 failed: heartbeat was not observed. Output:`n$($output1Lines -join "`n")"
    }
    $outputText1 = $output1Lines -join "`n"
    if ($outputText1 -notmatch "Child starting") {
        throw "Test 1 failed: output did not contain 'Child starting'. Output:`n$outputText1"
    }
    if ($outputText1 -notmatch "Child finished successfully") {
        throw "Test 1 failed: output did not contain 'Child finished successfully'. Output:`n$outputText1"
    }
    if ($outputText1 -match "Elevated coverage run timed out") {
        throw "Test 1 failed: parent reported timed out while child was alive. Output:`n$outputText1"
    }
    if ($outputText1 -notmatch "finished in .* with exit code 0") {
        throw "Test 1 failed: expected completion banner with exit code 0. Output:`n$outputText1"
    }
    Write-Host "  PASSED: Child outlived deadline, heartbeat fired, parent waited and propagated exit code 0."

    # Test 2: Fake child outliving -TimeoutSeconds with eventual non-zero exit code (exit 3)
    Write-Host "`n[Test 2] Fake child outliving -TimeoutSeconds (exit code 3)..."
    $timeoutAck2 = Join-Path $testDir "timeoutAck2.txt"
    $timeoutAck2Escaped = $timeoutAck2 -replace "'", "''"
    $fakeChild2 = Join-Path $testDir "fakeChild2.ps1"
    @"
Write-Host "Failing child starting..."
`$waited = 0
while (-not (Test-Path '$timeoutAck2Escaped') -and `$waited -lt 150) {
    Start-Sleep -Milliseconds 100
    `$waited++
}
if (-not (Test-Path '$timeoutAck2Escaped')) {
    Write-Host "Timeout waiting for deadline warning from parent."
    exit 1
}
Write-Host "Failing child exiting with 3."
exit 3
"@ | Set-Content -Path $fakeChild2 -Encoding UTF8

    $sawTimeoutWarn2 = $false
    $output2Lines = [System.Collections.Generic.List[string]]::new()
    & $psExe -NoProfile -File $parentScript -TargetScript $fakeChild2 -NoElevation -ForceNonAdmin -TimeoutSeconds 2 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $output2Lines.Add($line)
        if ($line -match 'configured timeout of \d+s reached; child still alive') {
            $sawTimeoutWarn2 = $true
            Set-Content -Path $timeoutAck2 -Value "timeout_warned"
        }
    }
    $exitCode2 = $LASTEXITCODE

    if ($exitCode2 -ne 3) {
        throw "Test 2 failed: expected exit code 3, got $exitCode2. Output:`n$($output2Lines -join "`n")"
    }
    if (-not $sawTimeoutWarn2) {
        throw "Test 2 failed: timeout warning was not observed. Output:`n$($output2Lines -join "`n")"
    }
    $outputText2 = $output2Lines -join "`n"
    if ($outputText2 -notmatch "Failing child starting") {
        throw "Test 2 failed: output did not contain 'Failing child starting'. Output:`n$outputText2"
    }
    if ($outputText2 -notmatch "with exit code 3") {
        throw "Test 2 failed: output did not contain child exit code 3. Output:`n$outputText2"
    }
    if ($outputText2 -match "Elevated coverage run timed out") {
        throw "Test 2 failed: parent reported timed out while child was alive. Output:`n$outputText2"
    }
    Write-Host "  PASSED: Child outlived deadline, parent waited and propagated exit code 3."

    # Test 3: Incremental streaming of child log output through logging pipeline with live delivery check
    Write-Host "`n[Test 3] Incremental streaming through logging pipeline..."
    $syncFile3 = Join-Path $testDir "childSync3.txt"
    $syncFile3Escaped = $syncFile3 -replace "'", "''"
    $ackFile3 = Join-Path $testDir "childAck3.txt"
    $ackFile3Escaped = $ackFile3 -replace "'", "''"
    $fakeChild3 = Join-Path $testDir "fakeChild3.ps1"
    @"
Write-Host "Streaming line 1..."
Set-Content -Path '$syncFile3Escaped' -Value "waiting"
`$waited = 0
while (-not (Test-Path '$ackFile3Escaped') -and `$waited -lt 150) {
    Start-Sleep -Milliseconds 100
    `$waited++
}
Remove-Item '$syncFile3Escaped' -Force -ErrorAction SilentlyContinue
if (-not (Test-Path '$ackFile3Escaped')) {
    Write-Host "Timeout waiting for parent to deliver line 1 live."
    exit 1
}
Write-Host "Streaming line 2..."
Write-Host "Second complete line."
exit 0
"@ | Set-Content -Path $fakeChild3 -Encoding UTF8

    $sawLine1Live = $false
    $output3Lines = [System.Collections.Generic.List[string]]::new()
    & $psExe -NoProfile -File $parentScript -TargetScript $fakeChild3 -NoElevation -ForceNonAdmin -TimeoutSeconds 15 2>&1 | ForEach-Object {
        $line = $_.ToString()
        $output3Lines.Add($line)
        if ($line -match "Streaming line 1\.\.\.") {
            # Verify child is still waiting for ack (proves line 1 was delivered while child was running)
            if (Test-Path $syncFile3) {
                $sawLine1Live = $true
                Set-Content -Path $ackFile3 -Value "line1_acked"
            }
        }
    }
    $exitCode3 = $LASTEXITCODE

    if ($exitCode3 -ne 0) {
        throw "Test 3 failed: expected exit code 0, got $exitCode3. Output:`n$($output3Lines -join "`n")"
    }
    if (-not $sawLine1Live) {
        throw "Test 3 failed: line 1 was not delivered live while child was running. Output:`n$($output3Lines -join "`n")"
    }
    $outputText3 = $output3Lines -join "`n"
    if ($outputText3 -notmatch "Streaming line 1\.\.\.") {
        throw "Test 3 failed: output did not contain first streaming line. Output:`n$outputText3"
    }
    if ($outputText3 -notmatch "Streaming line 2\.\.\.") {
        throw "Test 3 failed: output did not contain second streaming line. Output:`n$outputText3"
    }
    if ($outputText3 -notmatch "Second complete line\.") {
        throw "Test 3 failed: output missing second line. Output:`n$outputText3"
    }
    Write-Host "  PASSED: Incremental log output streamed live through logging pipeline while child was running."

    # Test 4: Premature child death without completion marker
    Write-Host "`n[Test 4] Premature child exit detection without completion marker..."
    $fakeChild4 = Join-Path $testDir "fakeChild4.ps1"
    @'
Write-Host "Abrupt child failure."
[System.Environment]::Exit(42)
'@ | Set-Content -Path $fakeChild4 -Encoding UTF8

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $output4 = & $psExe -NoProfile -File $parentScript -TargetScript $fakeChild4 -NoElevation -ForceNonAdmin -TimeoutSeconds 60 2>&1
    $exitCode4 = $LASTEXITCODE
    $elapsed = $stopwatch.Elapsed.TotalSeconds

    if ($exitCode4 -eq 0) {
        throw "Test 4 failed: expected non-zero exit code, got 0. Output:`n$($output4 -join "`n")"
    }
    if ($elapsed -ge 50) {
        throw "Test 4 failed: parent hung waiting for deadline ($elapsed s) instead of detecting dead child promptly."
    }
    $outputText4 = $output4 -join "`n"
    if ($outputText4 -notmatch "without writing a completion marker") {
        throw "Test 4 failed: output did not diagnose missing marker. Output:`n$outputText4"
    }
    if ($outputText4 -match "Elevated coverage run timed out") {
        throw "Test 4 failed: parent reported timed out instead of detecting premature child death. Output:`n$outputText4"
    }
    Write-Host "  PASSED: Detected premature child exit promptly ($([int]$elapsed)s) without waiting for deadline."

    # Test 5: Incomplete-line retention and final draining for UTF-8 and Unicode (UTF-16)
    # Directly exercises the production Process-NewLogOutput function from native-coverage-elevated.ps1.
    Write-Host "`n[Test 5] Incomplete-line retention and final draining via Process-NewLogOutput (UTF-8 and Unicode)..."
    foreach ($testEncoding in @("UTF8", "Unicode")) {
        $probeFile = Join-Path $testDir "tailerProbe_$testEncoding.log"
        $encodingObj = if ($testEncoding -eq "Unicode") { [System.Text.Encoding]::Unicode } else { [System.Text.Encoding]::UTF8 }

        Reset-LogTailer

        $bytesPrefix = $encodingObj.GetBytes("Prefix without newline")
        $initialBytes = if ($testEncoding -eq "Unicode") { [System.Text.Encoding]::Unicode.GetPreamble() + $bytesPrefix } else { $bytesPrefix }
        [System.IO.File]::WriteAllBytes($probeFile, $initialBytes)

        # First call: incomplete prefix should remain in pendingText without emitting complete lines
        $emitted1 = @(Process-NewLogOutput -Path $probeFile -PassThru)
        if ($emitted1.Count -ne 0) {
            throw "Test 5 ($testEncoding) failed: incomplete line was emitted prematurely: $($emitted1 -join "; ")"
        }
        if ($script:pendingText -ne "Prefix without newline") {
            throw "Test 5 ($testEncoding) failed: pending text mismatch: '$($script:pendingText)'"
        }

        # Append completion and second line and trailing unterminated text
        $moreBytes = $encodingObj.GetBytes(" completed.`nSecond complete line.`nTrailing unterminated")
        $appendFs = [System.IO.FileStream]::new($probeFile, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
        $appendFs.Write($moreBytes, 0, $moreBytes.Length)
        $appendFs.Dispose()

        # Second call: complete lines emitted, trailing unterminated remains in pendingText
        $emitted2 = @(Process-NewLogOutput -Path $probeFile -PassThru)
        if ($emitted2.Count -ne 2 -or $emitted2[0] -ne "Prefix without newline completed." -or $emitted2[1] -ne "Second complete line.") {
            throw "Test 5 ($testEncoding) failed: complete lines mismatch: $($emitted2 -join "; ")"
        }
        if ($script:pendingText -ne "Trailing unterminated") {
            throw "Test 5 ($testEncoding) failed: pending text mismatch: '$($script:pendingText)'"
        }

        # Final drain: trailing unterminated line is drained
        $emitted3 = @(Process-NewLogOutput -Path $probeFile -Drain -PassThru)
        if ($emitted3.Count -ne 1 -or $emitted3[0] -ne "Trailing unterminated") {
            throw "Test 5 ($testEncoding) failed: drained line mismatch: $($emitted3 -join "; ")"
        }
        if ($script:pendingText -ne "") {
            throw "Test 5 ($testEncoding) failed: pendingText should be empty after Drain"
        }

        Reset-LogTailer
        Remove-Item $probeFile -Force
    }
    Write-Host "  PASSED: Incomplete-line retention and final draining verified for UTF-8 and Unicode via production Process-NewLogOutput."

    Write-Host "`nAll native-coverage-elevated regression tests passed successfully!"
}
finally {
    if (Test-Path $testDir) {
        Remove-Item -Recurse -Force $testDir -ErrorAction SilentlyContinue
    }
}
