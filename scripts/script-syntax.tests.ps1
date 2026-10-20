$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-script-syntax.ps1"

function Assert-SyntaxFailure([string] $Root, [string] $Expected) {
    $failure = $null
    try { Invoke-ScriptSyntaxCheck $Root } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($Expected)) {
        throw "Expected '$Expected', observed '$failure'"
    }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('MFTLib syntax ' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    & git -C $temporary init -q
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git initialization failed' }
    $scripts = Join-Path $temporary 'scripts/nested space'
    [IO.Directory]::CreateDirectory($scripts) | Out-Null
    Assert-SyntaxFailure $temporary 'No scripts found'
    $powerShell = Join-Path $scripts 'attended.ps1'
    $bash = Join-Path $scripts 'release.sh'
    Set-Content -LiteralPath $powerShell -Value 'throw "The gate executed PowerShell"'
    Set-Content -LiteralPath $bash -Value "#!/usr/bin/env bash`nexit 71"
    Invoke-ScriptSyntaxCheck $temporary
    & git -C $temporary add -- scripts
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git staging failed' }
    Invoke-ScriptSyntaxCheck $temporary
    Set-Content -LiteralPath $powerShell -Value 'function Broken {'
    Assert-SyntaxFailure $temporary 'attended.ps1'
    Set-Content -LiteralPath $powerShell -Value 'throw "Must only parse"'
    Set-Content -LiteralPath $bash -Value "#!/usr/bin/env bash`nif then"
    Assert-SyntaxFailure $temporary 'release.sh'
    Set-Content -LiteralPath $bash -Value "#!/usr/bin/env bash`nexit 71"
    $newScript = Join-Path $scripts 'new untracked.ps1'
    Set-Content -LiteralPath $newScript -Value 'function Broken {'
    Assert-SyntaxFailure $temporary 'new untracked.ps1'
    Remove-Item -LiteralPath $newScript
    $unsupported = Join-Path $scripts 'future.py'
    Set-Content -LiteralPath $unsupported -Value 'print("fixture")'
    Assert-SyntaxFailure $temporary 'Unsupported script language'
    Remove-Item -LiteralPath $unsupported
    $extensionless = Join-Path $scripts 'future-tool'
    Set-Content -LiteralPath $extensionless -Value "#!/usr/bin/env python3`nprint('fixture')"
    Assert-SyntaxFailure $temporary 'Unsupported script language'
    Remove-Item -LiteralPath $extensionless
    Set-Content -LiteralPath (Join-Path $temporary '.gitignore') -Value 'scripts/generated/'
    $generated = Join-Path $temporary 'scripts/generated'
    [IO.Directory]::CreateDirectory($generated) | Out-Null
    Set-Content -LiteralPath (Join-Path $generated 'broken.ps1') -Value 'function Broken {'
    Invoke-ScriptSyntaxCheck $temporary
    Remove-Item -LiteralPath $powerShell
    Assert-SyntaxFailure $temporary 'Missing tracked file'
    Assert-SyntaxFailure (Join-Path $temporary 'absent') 'Cannot enumerate scripts'
    Write-Host 'Script syntax regression tests passed.'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
