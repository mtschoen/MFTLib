$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Test-ReleasePackages.ps1"

# Load only the production extraction function, without running release operations.
$tokens = $null
$parseErrors = $null
$releaseScript = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'release.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Equal $parseErrors.Count 0 'Release script parse errors'
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

Write-Host 'Release notes regressions passed.'
