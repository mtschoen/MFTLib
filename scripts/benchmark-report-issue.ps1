<#
.SYNOPSIS
Function library that files, updates and closes the "benchmark needed" issue through the forge REST API.
.DESCRIPTION
The issue is found by its exact fixed title among open issues (pull requests excluded), never by fuzzy search. When
one is open the helper adds a comment instead of opening another. A later successful capture comments with the new
report name and closes it. Every call goes through the InvokeApi seam, a script block taking (Method, Path, Body) and
returning the parsed response, so the tests run without a server. New-BenchmarkIssueApi builds the real one from the
server URL, repository and token that the workflow environment supplies. Dot-source this file; it runs nothing by
itself.
#>

$script:BenchmarkNeededIssueTitle = 'Benchmark needed when the box is quiet'

function New-BenchmarkIssueApi {
    param(
        [Parameter(Mandatory)][string] $ServerUrl,
        [Parameter(Mandatory)][string] $Repository,
        [Parameter(Mandatory)][string] $Token
    )
    $root = "$($ServerUrl.TrimEnd('/'))/api/v1/repos/$Repository"
    {
        param($Method, $Path, $Body)
        $request = @{ Method = $Method; Uri = "$root$Path"; Headers = @{ Authorization = "token $Token" } }
        if ($null -ne $Body) {
            $request.ContentType = 'application/json'
            $request.Body = $Body | ConvertTo-Json -Depth 5
        }
        Invoke-RestMethod @request
    }.GetNewClosure()
}

function Find-BenchmarkNeededIssue {
    param([Parameter(Mandatory)][scriptblock] $InvokeApi)
    $pageSize = 50
    for ($page = 1; $page -le 40; $page++) {
        $issues = @(& $InvokeApi 'GET' "/issues?state=open&type=issues&limit=$pageSize&page=$page" $null | Where-Object { $null -ne $_ })
        foreach ($issue in $issues) {
            if ($issue.title -ceq $script:BenchmarkNeededIssueTitle) { return $issue }
        }
        if ($issues.Count -lt $pageSize) { break }
    }
    $null
}

function Publish-BenchmarkNeededIssue {
    param(
        [Parameter(Mandatory)][scriptblock] $InvokeApi,
        [Parameter(Mandatory)][string] $Observation,
        [Parameter(Mandatory)][string] $Date,
        [string] $RunUrl
    )
    $evidence = "- $Date`: $Observation"
    if ($RunUrl) { $evidence += "`n- Run: $RunUrl" }
    $existing = Find-BenchmarkNeededIssue -InvokeApi $InvokeApi
    if ($existing) {
        & $InvokeApi 'POST' "/issues/$($existing.number)/comments" @{ body = "Still busy.`n`n$evidence" } | Out-Null
        return [pscustomobject]@{ Action = 'commented'; Number = $existing.number }
    }
    $body = @"
## Mechanism

The scheduled benchmark report refresh found main ahead of the committed release report, then watched the benchmark host for the full wait and never saw it stay quiet. Timing numbers from a busy box are not comparable, so no report was captured and the committed one is stale.

## Evidence

$evidence

## Detection

The refresh workflow files this issue, or comments on it, each time the quiet gate times out.

## Done when

A refresh run captures a report on a quiet box and publishes it. That run comments with the new report name and closes this issue.
"@
    $created = & $InvokeApi 'POST' '/issues' @{ title = $script:BenchmarkNeededIssueTitle; body = $body }
    [pscustomobject]@{ Action = 'created'; Number = $created.number }
}

function Close-BenchmarkNeededIssue {
    param([Parameter(Mandatory)][scriptblock] $InvokeApi, [Parameter(Mandatory)][string] $ReportName)
    $existing = Find-BenchmarkNeededIssue -InvokeApi $InvokeApi
    if (-not $existing) { return [pscustomobject]@{ Action = 'none'; Number = 0 } }
    & $InvokeApi 'POST' "/issues/$($existing.number)/comments" @{ body = "A refresh run captured and published the report $ReportName on a quiet box." } | Out-Null
    & $InvokeApi 'PATCH' "/issues/$($existing.number)" @{ state = 'closed' } | Out-Null
    [pscustomobject]@{ Action = 'closed'; Number = $existing.number }
}
