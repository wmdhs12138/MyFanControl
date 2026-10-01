#Requires -RunAsAdministrator
<#
.SYNOPSIS
卸载 ClevoFan。停止服务时风扇交还 EC 自动控制。

.PARAMETER Purge
同时删除配置和日志（%ProgramData%\ClevoFan）。
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\ClevoFan",
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ClevoFan'

Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClevoFanTray' -ErrorAction SilentlyContinue

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Write-Host '已删除服务'
}
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "已删除 $InstallDir"
}
if ($Purge) {
    Remove-Item (Join-Path $env:ProgramData 'ClevoFan') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '已删除配置和日志'
}
