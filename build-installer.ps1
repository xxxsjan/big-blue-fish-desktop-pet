# 打包标准版 Windows 安装器（含 Agent）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

try {
    & (Join-Path $root 'tools\Clear-InstallerArtifacts.ps1') -Root $root
    & (Join-Path $root 'tools\Build-Installer.ps1') `
        -Edition Standard `
        -RuntimeDirectory (Join-Path $root 'runtime') `
        -VisualCppDirectory (Join-Path $env:SystemRoot 'System32')
    Write-Host ''
    Write-Host '打包完成，安装器在 release 目录。'
}
catch {
    Write-Host ''
    Write-Host "打包失败：$($_.Exception.Message)"
    exit 1
}
finally {
    if ($Host.Name -eq 'ConsoleHost') { pause }
}
