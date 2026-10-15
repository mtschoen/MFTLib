# Incremental log tailer state: retains reader/stream across polls, preserves
# incomplete trailing lines, and avoids re-reading earlier log lines.
$script:logStream = $null
$script:logReader = $null
$script:pendingText = ""

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
        [string]$Path,
        [switch]$Drain,
        [switch]$PassThru
    )

    if ($null -eq $script:logReader -and (Test-Path -LiteralPath $Path)) {
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
