@echo off
rem 开发环境启动蓝色大肥鱼（OCR + 仓库内模型 / 运行时）
setlocal

set "ROOT=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%code\run.ps1" ^
    -InputMode Ocr ^
    -ModelsDirectory "%ROOT%data\models" ^
    -RuntimeDirectory "%ROOT%runtime"

endlocal
