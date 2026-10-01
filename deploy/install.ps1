#Requires -RunAsAdministrator
<#
.SYNOPSIS
安装 ClevoFan：风扇控制服务（开机自动运行）和托盘程序。

.PARAMETER SourceDir
已发布文件所在目录。不指定时：脚本位于发布包中则使用脚本所在目录，否则从源码发布（需要 .NET 10 SDK）。

.PARAMETER ImportLegacy
导入原 MyFanControl 的配置文件（MyFanControl.cfg）。

.PARAMETER NoTray
不设置托盘程序登录时启动，也不立即启动托盘。

.EXAMPLE
.\deploy\install.ps1 -ImportLegacy "C:\Users\me\Desktop\MyFanControl-v1.0\MyFanControl.cfg"
#>
param(
    [string]$SourceDir,
    [string]$InstallDir = "$env:ProgramFiles\ClevoFan",
    [string]$ImportLegacy,
    [switch]$NoTray
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ClevoFan'

if (-not ((& dotnet --list-runtimes 2>$null) -match '^Microsoft\.WindowsDesktop\.App 10\.')) {
    throw '需要 .NET 10 桌面运行时：https://dotnet.microsoft.com/download/dotnet/10.0'
}

if (-not $SourceDir -and (Test-Path (Join-Path $PSScriptRoot 'ClevoFan.Service.exe'))) {
    # 发布包：脚本与程序在同一目录
    $SourceDir = $PSScriptRoot
}
if (-not $SourceDir) {
    $repo = Resolve-Path (Join-Path $PSScriptRoot '..')
    $SourceDir = Join-Path $env:TEMP 'ClevoFan_publish'
    if (Test-Path $SourceDir) { Remove-Item $SourceDir -Recurse -Force }
    foreach ($project in 'ClevoFan.Service', 'ClevoFan.Tray') {
        & dotnet publish (Join-Path $repo "src\$project") -c Release -o $SourceDir --nologo
        if ($LASTEXITCODE) { throw "发布 $project 失败" }
    }
}

# 停止正在运行的服务和托盘（服务停止时会交还 EC 自动控制）
$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service $ServiceName
    Write-Host '已停止服务'
}
Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $SourceDir '*') $InstallDir -Recurse -Force
Write-Host "已复制到 $InstallDir"

$exe = Join-Path $InstallDir 'ClevoFan.Service.exe'
if (-not $service) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Clevo 风扇控制' -StartupType Automatic `
        -Description '通过 BIOS 的 WMI 接口控制 Clevo 笔记本风扇。停止服务时风扇交还 EC 自动控制。' | Out-Null
    Write-Host '已创建服务'
} else {
    Set-Service $ServiceName -StartupType Automatic
}
# 服务异常退出后自动重启
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

if ($ImportLegacy) {
    & $exe --import-legacy $ImportLegacy
    if ($LASTEXITCODE) { throw '导入旧配置失败' }
}

Start-Service $ServiceName
Write-Host '已启动服务'

if (-not $NoTray) {
    $tray = Join-Path $InstallDir 'ClevoFan.Tray.exe'
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClevoFanTray' -Value "`"$tray`""
    # 通过资源管理器启动，托盘以普通权限运行
    Start-Process explorer.exe -ArgumentList "`"$tray`""
    Write-Host '已启动托盘程序，并设置为登录时启动'
}
