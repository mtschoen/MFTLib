$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/coverage-status.ps1"
$root = Split-Path $PSScriptRoot -Parent
$repositoryApi = "$env:GITHUB_SERVER_URL/api/v1/repos/$env:GITHUB_REPOSITORY"
$headers = @{ Authorization = "token $env:GITHUB_TOKEN" }
$readApi = {
    param([string]$Path)
    $response = Invoke-RestMethod -Uri "$repositoryApi$Path" -Headers $headers -TimeoutSec 30 -ErrorAction Stop
    foreach ($item in $response) { $item }
}
$postStatus = {
    param([hashtable]$Body)
    Invoke-RestMethod -Method Post -Uri "$repositoryApi/statuses/$env:GITHUB_SHA" `
        -Headers $headers -ContentType 'application/json' `
        -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 30 -ErrorAction Stop | Out-Null
}
$targetUrl = "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
$exitCode = Publish-CoverageMeasurement `
    (Join-Path $root 'MFTLib.Tests/coverage-report/Summary.txt') `
    (Join-Path $root 'MFTLib.Tests/coverage.xml') `
    $env:COVERAGE_OUTCOME $targetUrl $readApi $postStatus
exit $exitCode
