# The local gate requires an already-built checkout at the workflow's exact pin.
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repositoryRoot
$expectedCommit = (Get-Content .aislop/fork-commit -Raw).Trim()
if ($expectedCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw '.aislop/fork-commit must be one lowercase 40-character commit.'
}
$checkout = if ($env:AISLOP_HOME) { $env:AISLOP_HOME } else { Join-Path $HOME 'aislop' }
$actualCommit = & git -C $checkout rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw "Cannot identify aislop checkout commit at $checkout; expected $expectedCommit." }
if ($actualCommit -cne $expectedCommit) {
    throw "Refusing a different scanner: checkout $actualCommit, CI requires $expectedCommit. Build the pinned fork and set AISLOP_HOME to its checkout."
}
$changes = & git -C $checkout status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Pinned aislop checkout has modified tracked files or cannot be inspected.' }
$cli = Join-Path $checkout 'dist/cli.js'
if (-not (Test-Path -LiteralPath $cli)) { throw "Pinned aislop is not built: $cli" }
$checkoutVersion = & node $cli --version
if ($LASTEXITCODE -ne 0) { throw 'Pinned aislop checkout could not report its version.' }
Write-Host "aislop checkout version $checkoutVersion, checkout commit $actualCommit; CI pin $expectedCommit"
$reportDirectory = Join-Path $repositoryRoot '.aislop/preflight'
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = Join-Path $reportDirectory 'report.json'
& node $cli ci . --json | Set-Content -LiteralPath $report -Encoding utf8
$gateExit = $LASTEXITCODE
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$files = if ($result.summary.files) { $result.summary.files } else { 0 }
Write-Host "aislop score $($result.score) over $files files"
if ($files -lt 10) { throw "aislop only scanned $files files; the C# engine did not run." }
if ($gateExit -ne 0) {
    foreach ($diagnostic in @($result.diagnostics | Where-Object { $_.scoreImpact.multiplier -gt 0 })) {
        Write-Host "[$($diagnostic.engine)/$($diagnostic.rule)] $($diagnostic.filePath):$($diagnostic.line) $($diagnostic.message)"
    }
    exit $gateExit
}
