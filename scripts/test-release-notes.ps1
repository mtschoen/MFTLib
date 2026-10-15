$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Test-ReleasePackages.ps1"

# Parse production code without running the release preflight or publication pipeline.
$tokens = $null
$parseErrors = $null
$releaseScript = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'release.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Equal $parseErrors.Count 0 'Release script parse errors'
$destinationFunction = $releaseScript.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-ReleaseRepositoryUrl'
}, $false)
Assert-True ($null -ne $destinationFunction) 'Release defines destination validation.'
. ([scriptblock]::Create($destinationFunction.Extent.Text))
foreach ($destination in @(
    'git@forge.example.invalid:team/MFTLib.git',
    'ssh://git@forge.example.invalid:2222/team/MFTLib.git',
    'https://forge.example.invalid/team/MFTLib.git'
)) {
    Assert-Equal (Get-ReleaseRepositoryUrl $destination) $destination 'Explicit URL is preserved'
}
foreach ($destination in @($null, '', ' ', 'origin', 'gitea', 'file:///fixture/repo',
    "https://forge.example.invalid/team/MFTLib.git`n",
    "https://forge.example.invalid/team/MFTLib.git`r`n",
    "git@forge.example.invalid:team/MFTLib.git`n")) {
    $destinationFailure = $null
    try { Get-ReleaseRepositoryUrl $destination | Out-Null }
    catch { $destinationFailure = $_.Exception.Message }
    Assert-True ($null -ne $destinationFailure) 'Missing or non-network destination fails.'
    Assert-True ($destinationFailure.Contains('MFTLIB_GITEA_REPOSITORY_URL')) 'Error names the configuration input.'
}
$destinationAssignments = @($releaseScript.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$giteaRepositoryUrl'
}, $false))
Assert-Equal $destinationAssignments.Count 1 'Single canonical release destination'
Assert-Equal $destinationAssignments[0].Right.Extent.Text `
    'Get-ReleaseRepositoryUrl $env:MFTLIB_GITEA_REPOSITORY_URL' 'Release consumes configured URL'
Assert-True ($destinationAssignments[0].Extent.StartOffset -lt
    $releaseScript.Extent.Text.IndexOf('. "$PSScriptRoot\Test-ReleasePackages.ps1"')) `
    'Destination is validated before release pipeline setup.'

$function = $releaseScript.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ReleaseNotes'
}, $false)
Assert-True ($null -ne $function) 'Release script defines Get-ReleaseNotes.'
. ([scriptblock]::Create($function.Extent.Text))
$writeFunction = $releaseScript.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-ReleaseNotesFile'
}, $false)
Assert-True ($null -ne $writeFunction) 'Release script defines Write-ReleaseNotesFile.'
. ([scriptblock]::Create($writeFunction.Extent.Text))

$changelogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
$notes = Get-ReleaseNotes -ChangelogPath $changelogPath -Version '0.3.0'
Assert-True ($notes.StartsWith("## 0.3.0`n")) 'Notes start with the release heading.'
Assert-True (-not $notes.Contains('## 0.2.0')) 'Notes exclude the next release section.'
Assert-True ($notes.Contains('### Added')) 'Notes include release subsections.'

$failure = $null
try { Get-ReleaseNotes -ChangelogPath $changelogPath -Version 'missing-version' | Out-Null }
catch { $failure = $_.Exception.Message }
Assert-True ($null -ne $failure) 'A missing release heading fails extraction.'
Assert-True ($failure.Contains("## missing-version") -and $failure.Contains('missing')) 'Missing-heading error identifies the release.'

$notesFile = [IO.Path]::GetTempFileName()
try {
    Write-ReleaseNotesFile -Path $notesFile -Text $notes
    $bytes = [IO.File]::ReadAllBytes($notesFile)
    $hasByteOrderMark = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    Assert-True (-not $hasByteOrderMark) 'The written notes file has no UTF-8 byte order mark.'
    Assert-True ($bytes[0] -eq [byte][char]'#') 'The written notes file starts with the release heading.'
}
finally {
    Remove-Item -LiteralPath $notesFile -Force
}

function Invoke-GitHubReleaseTail {
    param(
        [int] $PushExitCode,
        [string] $Tail,
        [string] $NotesWriterDefinition
    )

    $setup = @'
$ErrorActionPreference = 'Stop'
$tag = 'v0.3.0-test'
$githubRepositoryUrl = 'git@example.invalid:fixture/MFTLib.git'
$releaseNotes = 'Release fixture'
$mftLibNupkg = 'MFTLib.nupkg'
$mftLibSnupkg = 'MFTLib.snupkg'
$testExtensionsNupkg = 'MFTLib.TestExtensions.nupkg'
$testExtensionsSnupkg = 'MFTLib.TestExtensions.snupkg'
function git {
    if (($args -join '|') -cne "push|$githubRepositoryUrl|refs/tags/$tag") {
        throw "Unexpected git invocation: $args"
    }
    Write-Output 'TEST_GITHUB_PUSH'
    $global:LASTEXITCODE = $script:pushExitCode
}
function gh {
    $expected = @(
        'release', 'create', $tag,
        $mftLibNupkg, $mftLibSnupkg,
        $testExtensionsNupkg, $testExtensionsSnupkg,
        '--title', $tag, '--notes-file', $releaseNotesPath
    )
    if (($args -join '|') -cne ($expected -join '|')) {
        throw "Unexpected gh invocation: $args"
    }
    if ([IO.File]::ReadAllText($releaseNotesPath) -cne $releaseNotes) {
        throw 'Release notes were not written before release creation.'
    }
    Write-Output 'TEST_RELEASE_CREATED'
    $global:LASTEXITCODE = 0
}
'@
    $source = $setup + "`n`$script:pushExitCode = $PushExitCode`n" +
        $NotesWriterDefinition + "`n" + $Tail + "`nexit 0`n"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($source))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = @(Get-Command pwsh -CommandType Application)[0].Source
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-OutputFormat', 'Text', '-EncodedCommand', $encodedCommand)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $outputTask.GetAwaiter().GetResult()
            ErrorOutput = $errorTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

$githubPushCommands = @($releaseScript.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq 'git' -and
        $node.CommandElements.Count -eq 4 -and
        $node.CommandElements[1].Extent.Text -ceq 'push' -and
        $node.CommandElements[2].Extent.Text -ceq '$githubRepositoryUrl'
}, $false))
Assert-Equal $githubPushCommands.Count 1 'GitHub tag-push command count'
$githubTail = $releaseScript.Extent.Text.Substring($githubPushCommands[0].Extent.StartOffset)
Assert-True ($githubTail.Contains('gh release create')) 'The tested tail includes release creation.'

foreach ($case in @(
    @{ Name = 'github-push-success'; PushExitCode = 0; ExpectedExitCode = 0; CreatesRelease = $true },
    @{ Name = 'issue-527-github-push-failure'; PushExitCode = 23; ExpectedExitCode = 1; CreatesRelease = $false }
)) {
    $result = Invoke-GitHubReleaseTail $case.PushExitCode $githubTail $writeFunction.Extent.Text
    Assert-Equal $result.ErrorOutput '' "$($case.Name): unexpected child error"
    Assert-True ($result.Output.Contains('TEST_GITHUB_PUSH')) "$($case.Name): push was exercised"
    Assert-Equal $result.ExitCode $case.ExpectedExitCode "$($case.Name): process exit"
    Assert-Equal ($result.Output.Contains('TEST_RELEASE_CREATED')) $case.CreatesRelease "$($case.Name): release creation"
    Assert-Equal ($result.Output.Contains('Release v0.3.0-test complete.')) $case.CreatesRelease "$($case.Name): completion"
    if (-not $case.CreatesRelease) {
        Assert-True ($result.Output.Contains('Failed to push tag v0.3.0-test to GitHub')) 'Push failure is diagnosed.'
    }
}

Write-Host 'Release notes regressions passed.'
