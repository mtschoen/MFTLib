[CmdletBinding()]
param([string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

function Invoke-ScriptSyntaxCheck {
    param([Parameter(Mandatory)] [string] $RepositoryRoot)
    $PSNativeCommandUseErrorActionPreference = $false
    $listing = @(& git -C $RepositoryRoot ls-files --cached --others --exclude-standard -z -- scripts)
    if ($LASTEXITCODE -ne 0) { throw "Cannot enumerate scripts: $RepositoryRoot" }
    $paths = @(($listing -join "`n").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) |
        Sort-Object -Unique)
    $count = 0
    foreach ($relative in $paths) {
        $path = Join-Path $RepositoryRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing tracked file: $relative" }
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        $text = [IO.File]::ReadAllText($path)
        if ($extension -notin @('.ps1', '.psm1', '.psd1', '.sh', '.bash', '.py', '.cmd', '.bat') -and
            -not $text.StartsWith('#!')) { continue }
        $count++
        switch ($extension) {
            { $_ -in @('.ps1', '.psm1', '.psd1') } {
                $tokens = $null
                $errors = $null
                $null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) {
                    $details = $errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }
                    throw "${relative}: $($details -join '; ')"
                }
            }
            { $_ -in @('.sh', '.bash') } {
                $bashPath = [IO.Path]::GetFullPath($path).Replace('\', '/')
                & bash -n -- $bashPath
                if ($LASTEXITCODE -ne 0) { throw "${relative}: bash -n failed ($LASTEXITCODE)" }
            }
            default { throw "Unsupported script language: $relative" }
        }
    }
    if ($count -eq 0) { throw 'No scripts found under scripts/' }
    Write-Host "Parsed $count maintained scripts without executing them."
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-ScriptSyntaxCheck -RepositoryRoot $RepositoryRoot
}
