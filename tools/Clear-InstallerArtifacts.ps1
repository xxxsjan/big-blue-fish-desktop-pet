param([string]$Root)
$ErrorActionPreference = 'Stop'
if (!$Root) { $Root = Split-Path $PSScriptRoot -Parent }

$work = Join-Path $Root '.work'
if (Test-Path -LiteralPath $work) {
    Get-ChildItem -LiteralPath $work -Force |
        Where-Object { $_.Name -like 'BigBlueFish-*' } |
        ForEach-Object {
            Write-Host "清理中间产物：$($_.FullName)"
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
}

$release = Join-Path $Root 'release'
if (Test-Path -LiteralPath $release) {
    Get-ChildItem -LiteralPath $release -Force -File |
        Where-Object { $_.Name -like 'BigBlueFish-*-Setup.exe' -or $_.Name -like 'BigBlueFish-*-Setup.sha256' } |
        ForEach-Object {
            Write-Host "清理发布产物：$($_.FullName)"
            Remove-Item -LiteralPath $_.FullName -Force
        }
}
