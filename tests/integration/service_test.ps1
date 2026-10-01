#Requires -RunAsAdministrator
<#
.SYNOPSIS
在真实的 Clevo 笔记本上测试 ClevoFan 服务：安装、接管、模拟睡眠/唤醒、开关接管、强制冷却、重启、托盘界面、停止。
每个关键状态都用 tools\ecprobe\wmiprobe.exe 独立读回硬件确认。

.PARAMETER SourceDir
已发布的 ClevoFan 文件目录（dotnet publish 服务和托盘到同一目录）。

.PARAMETER LegacyDir
原 MyFanControl 所在目录。指定且程序正在运行时，额外测试“旧版运行时暂停接管”，并导入其配置；
测试结束后重新启动旧版程序、把新服务设为手动启动并停止（恢复测试前的状态）。

.NOTES
强制冷却测试会让风扇短暂升到 95%。需要先运行 tools\ecprobe\build.cmd。
#>
param(
    [Parameter(Mandatory)][string]$SourceDir,
    [string]$LegacyDir
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$wmi = Join-Path $repo 'tools\ecprobe\wmiprobe.exe'
$slog = Join-Path $env:ProgramData 'ClevoFan\service.log'
$out = Join-Path $env:TEMP 'ClevoFan_service_test.log'
"" | Set-Content $out -Encoding utf8
function Log($m) { $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; Add-Content $out $line -Encoding utf8; Write-Host $line }
$script:fail = 0
function Check($name, $ok, $detail = '') { if ($ok) { Log "PASS  $name  $detail" } else { $script:fail++; Log "FAIL  $name  $detail" } }

Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.Core, System.Private.Windows.GdiPlus -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class ClevoFanTestWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static string Capture(IntPtr h, string path) {
        SetThreadDpiAwarenessContext(new IntPtr(-4));   //按实际像素取窗口大小，否则高 DPI 下会被缩小
        RECT r; GetWindowRect(h, out r);
        using (var bmp = new Bitmap(r.R - r.L, r.B - r.T))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc);
            bmp.Save(path, ImageFormat.Png);
        }
        return (r.R - r.L) + "x" + (r.B - r.T);
    }
}
'@

function Pipe($request) {
    $p = New-Object IO.Pipes.NamedPipeClientStream('.', 'ClevoFan', [IO.Pipes.PipeDirection]::InOut)
    try {
        $p.Connect(3000)
        $enc = New-Object Text.UTF8Encoding($false)
        $w = New-Object IO.StreamWriter($p, $enc); $w.AutoFlush = $true
        $r = New-Object IO.StreamReader($p, $enc)
        $w.WriteLine(($request | ConvertTo-Json -Depth 5 -Compress))
        return $r.ReadLine() | ConvertFrom-Json
    } catch { return $null } finally { $p.Dispose() }
}
function Status { (Pipe @{ Command = 'status' }).Status }
#状态由下一轮控制刷新，等到条件满足或超时
function WaitStatus($cond, $seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do { $s = Status; if ($s -and (& $cond $s)) { return $s }; Start-Sleep -Milliseconds 500 } while ($sw.Elapsed.TotalSeconds -lt $seconds)
    return $s
}
function Fmt($s) { if (-not $s) { return '无状态' }; "state=$($s.State) taken=$($s.TakenOver) cpu=$($s.CpuTemp)℃/$($s.CpuDutyPercent)% gpu=$($s.GpuTemp)℃/$($s.GpuDutyPercent)% target=$($s.CpuTargetPercent)/$($s.GpuTargetPercent) forced=$($s.ForcedCooling) msg=$($s.Message)" }
function WmiRead {
    $t = (& $wmi 2>&1 | Out-String)
    $kv = @{}; foreach ($m in [regex]::Matches($t, '(\w+)=(\d+)\b')) { $kv[$m.Groups[1].Value] = [int]$m.Groups[2].Value }
    return $kv
}
function Pct($raw) { [int][math]::Floor($raw * 100 / 255.0 + 0.5) }
#EC 执行写入有延迟，轮询直到硬件读回的负载等于服务当前的目标
function WaitReadback($seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $s = Status; $h = WmiRead
        $ok = $s -and $null -ne $s.CpuTargetPercent -and [math]::Abs((Pct $h['fan1_duty']) - $s.CpuTargetPercent) -le 1 -and [math]::Abs((Pct $h['fan2_duty']) - $s.GpuTargetPercent) -le 1
        if ($ok) { break }
        Start-Sleep -Milliseconds 700
    } while ($sw.Elapsed.TotalSeconds -lt $seconds)
    return @{ Ok = $ok; Detail = "目标 $($s.CpuTargetPercent)/$($s.GpuTargetPercent)%，读回 $(Pct $h['fan1_duty'])/$(Pct $h['fan2_duty'])%，用时 $([int]$sw.Elapsed.TotalSeconds) 秒" }
}
function LogLines { if (-not (Test-Path $slog)) { return @() }; $fs = [IO.File]::Open($slog, 'Open', 'Read', 'ReadWrite, Delete'); try { @((New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Dispose() } }
function LogSince($mark, $pattern) { @(LogLines | Select-Object -Skip $mark | Where-Object { $_ -match $pattern }).Count }

if (-not (Test-Path $wmi)) { throw "找不到 $wmi，请先运行 tools\ecprobe\build.cmd" }
$legacy = Get-Process MyFanControl -ErrorAction SilentlyContinue | Select-Object -First 1
$withLegacy = $LegacyDir -and $legacy
#安装脚本会结束正在运行的托盘（这里用 -NoTray 安装，不会重新启动），测试结束后恢复
$trayWasRunning = [bool](Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue)
try {
    # 1. 安装（有旧版时导入其配置），并确保开启接管
    $mark = (LogLines).Count
    $installArgs = @{ SourceDir = $SourceDir; NoTray = $true }
    if ($withLegacy) { $installArgs.ImportLegacy = Join-Path $LegacyDir 'MyFanControl.cfg' }
    & (Join-Path $repo 'deploy\install.ps1') @installArgs *>&1 | ForEach-Object { Log "install: $_" }
    Check '1 服务已安装并运行' ((Get-Service ClevoFan).Status -eq 'Running')

    if ($withLegacy) {
        # 2. 旧版运行时暂停接管，且仍能读到温度
        $s = WaitStatus { param($s) $s.State -eq 'Blocked' } 8
        Check '2 旧版程序运行时暂停接管' ($s.State -eq 'Blocked' -and $s.CpuTemp -gt 0) (Fmt $s)
        Check '2 日志记录检测到旧版' ((LogSince $mark '检测到旧版 MyFanControl') -eq 1)
        # [NullString]::Value：PowerShell 会把 $null 转成 "" 传给 .NET 的 string 参数
        [ClevoFanTestWin]::PostMessage([ClevoFanTestWin]::FindWindow([NullString]::Value, '蓝天风扇监控'), 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
        Check '2 旧版程序正常退出' ($legacy.WaitForExit(15000))
    }
    $original = (Pipe @{ Command = 'getConfig' }).Config
    $config = (Pipe @{ Command = 'getConfig' }).Config
    if (-not $config.TakeOver) { $config.TakeOver = $true; Pipe @{ Command = 'setConfig'; Config = $config } | Out-Null }

    # 3. 接管并读回
    $s = WaitStatus { param($s) $s.State -eq 'Running' -and $s.TakenOver } 10
    Check '3 服务接管' ($s.State -eq 'Running' -and $s.TakenOver) (Fmt $s)
    $rb = WaitReadback 10
    Check '3 WMI 读回负载与目标一致' $rb.Ok $rb.Detail

    # 4/5. 模拟睡眠、唤醒（自定义控制码 200/201）
    $mark = (LogLines).Count
    & sc.exe control ClevoFan 200 | Out-Null
    $s = WaitStatus { param($s) $s.State -eq 'Suspended' } 5
    Check '4 模拟睡眠：交还 EC' ($s.State -eq 'Suspended' -and -not $s.TakenOver -and (LogSince $mark '已交还 EC 自动控制') -ge 1) (Fmt $s)
    Start-Sleep -Seconds 3
    & sc.exe control ClevoFan 201 | Out-Null
    $s = WaitStatus { param($s) $s.State -eq 'Running' -and $s.TakenOver } 6
    Check '5 模拟唤醒：立即恢复接管' ($s.State -eq 'Running' -and $s.TakenOver) (Fmt $s)

    # 6. 通过管道关闭、开启接管
    $config.TakeOver = $false
    $r = Pipe @{ Command = 'setConfig'; Config = $config }
    $s = WaitStatus { param($s) -not $s.TakenOver } 6
    Check '6 关闭接管后交还 EC' ($r.Ok -and -not $s.TakenOver) (Fmt $s)
    $config.TakeOver = $true
    $r = Pipe @{ Command = 'setConfig'; Config = $config }
    $s = WaitStatus { param($s) $s.TakenOver } 6
    Check '6 重新开启接管' ($r.Ok -and $s.TakenOver) (Fmt $s)

    # 7. 强制冷却（温度达到目标温度时才会生效）
    $before = Status
    Pipe @{ Command = 'forceCooling'; On = $true } | Out-Null
    if ($before.CpuTemp -ge $config.ForceCoolingTemp -or $before.GpuTemp -ge $config.ForceCoolingTemp) {
        $s = WaitStatus { param($s) $s.ForcedCooling -and $s.CpuTargetPercent -eq 95 } 6
        $rb = WaitReadback 10
        Check '7 强制冷却：读回 95%' ($s.ForcedCooling -and $rb.Ok -and $s.CpuTargetPercent -eq 95 -and $s.GpuTargetPercent -eq 95) $rb.Detail
    } else {
        Check '7 强制冷却：温度均低于目标温度，跳过' $true (Fmt $before)
    }
    Pipe @{ Command = 'forceCooling'; On = $false } | Out-Null
    $s = WaitStatus { param($s) -not $s.ForcedCooling -and $s.CpuTargetPercent -ne 95 } 6
    Check '7 关闭强制冷却后回到曲线' (-not $s.ForcedCooling) (Fmt $s)

    # 8. 重启服务
    $mark = (LogLines).Count
    Restart-Service ClevoFan
    $s = WaitStatus { param($s) $s.State -eq 'Running' -and $s.TakenOver } 10
    Check '8 重启后恢复接管' ($s.TakenOver) (Fmt $s)
    Check '8 停止时交还、启动时重新接管' ((LogSince $mark '控制循环结束') -eq 1 -and (LogSince $mark '控制循环开始') -eq 1)

    # 9. 托盘设置窗口
    $tray = Start-Process (Join-Path $env:ProgramFiles 'ClevoFan\ClevoFan.Tray.exe') -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 5
    $h = [ClevoFanTestWin]::FindWindow([NullString]::Value, 'Clevo 风扇控制')
    $png = Join-Path $env:TEMP 'ClevoFan_settings.png'
    Check '9 托盘设置窗口' ($h -ne [IntPtr]::Zero) ($(if ($h -ne [IntPtr]::Zero) { "截图 $([ClevoFanTestWin]::Capture($h, $png)) → $png" } else { '未找到窗口' }))
    Stop-Process -Id $tray.Id -Force

    # 10. 停止服务时交还 EC
    $mark = (LogLines).Count
    Stop-Service ClevoFan
    Check '10 停止服务时交还 EC' ((LogSince $mark '已交还 EC 自动控制') -ge 1 -and (LogSince $mark '控制循环结束') -eq 1)
}
catch { $script:fail++; Log "ERROR: $_ @ $($_.InvocationInfo.ScriptLineNumber)" }
finally {
    #恢复测试前的配置
    if ($original) {
        if ((Get-Service ClevoFan -ErrorAction SilentlyContinue).Status -ne 'Running') {
            Start-Service ClevoFan -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        $r = Pipe @{ Command = 'setConfig'; Config = $original }
        Log "恢复测试前的配置：$(if ($r.Ok) { '成功' } else { "失败 $($r.Error)" })"
    }
    if ($withLegacy) {
        #恢复测试前的状态：新服务手动启动并停止，重新启动旧版
        Stop-Service ClevoFan -ErrorAction SilentlyContinue
        Set-Service ClevoFan -StartupType Manual -ErrorAction SilentlyContinue
        if (-not (Get-Process MyFanControl -ErrorAction SilentlyContinue)) {
            Start-Process -FilePath (Join-Path $LegacyDir 'MyFanControl.exe') -WorkingDirectory $LegacyDir | Out-Null
        }
    }
    if ($trayWasRunning -and -not (Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue)) {
        #通过资源管理器启动，托盘以普通权限运行
        Start-Process explorer.exe -ArgumentList "`"$(Join-Path $env:ProgramFiles 'ClevoFan\ClevoFan.Tray.exe')`""
        Log '已重新启动托盘程序'
    }
    Log ("结束：服务 {0}/{1}，旧版运行中 {2}" -f (Get-Service ClevoFan -ErrorAction SilentlyContinue).Status, (Get-Service ClevoFan -ErrorAction SilentlyContinue).StartType, [bool](Get-Process MyFanControl -ErrorAction SilentlyContinue))
    Log "===== $script:fail 项失败，日志 $out"
}
exit $script:fail
