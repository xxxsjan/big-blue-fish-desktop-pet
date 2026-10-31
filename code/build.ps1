param([string]$SdkPath,[switch]$Locked,[ValidateSet('Ocr','Vision')][string]$InputMode='Ocr')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/../tools/Setup-Media.ps1"
    $sdk=& "$PSScriptRoot/tools/Resolve-Sdk.ps1" -SdkPath $SdkPath
    $project=Join-Path $PSScriptRoot 'BigBlueFish.Windows/BigBlueFish.Windows.csproj'
    $argsBuild=@('build',$project,'-c','Release','-p:Platform=x64',"-p:CompanionInputMode=$InputMode",'--nologo')
    if($Locked){$argsBuild+='-p:RestoreLockedMode=true'}
    & $sdk @argsBuild
    if($LASTEXITCODE -ne 0){throw '编译失败'}

    # Agent 连接需要同目录的内部 MCP 桥接；安装器会单独 publish，开发启动也需要这份产物。
    $tfm=if($InputMode -eq 'Ocr'){'net10.0-windows10.0.26100.0'}else{'net10.0-windows7.0'}
    $output=Join-Path $PSScriptRoot "BigBlueFish.Windows/bin/x64/Release/$tfm"
    $mcp=Join-Path $PSScriptRoot 'BigBlueFish.Mcp/BigBlueFish.Mcp.csproj'
    $argsMcp=@('publish',$mcp,'-c','Release','-p:Platform=x64','-r','win-x64','--self-contained','false','-o',$output,'--nologo')
    if($Locked){$argsMcp+='-p:RestoreLockedMode=true'}
    & $sdk @argsMcp
    if($LASTEXITCODE -ne 0){throw 'MCP 桥接编译失败'}
} finally {Pop-Location}
