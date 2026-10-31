# 开发环境启动蓝色大肥鱼（OCR + 仓库内模型 / 运行时）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'code\run.ps1') `
    -InputMode Ocr `
    -ModelsDirectory (Join-Path $root 'data\models') `
    -RuntimeDirectory (Join-Path $root 'runtime')
