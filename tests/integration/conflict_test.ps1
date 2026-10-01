#Requires -RunAsAdministrator
<#
.SYNOPSIS
在真机上测试 ClevoFan 对“其他程序也在控制风扇”的检测：用 tools\ecprobe\wmiprobe.exe 扮演另一个风扇控制程序，
反复把风扇改成 78%，检查服务判定冲突、只警告一次，停止后恢复自己的负载。设置窗口截图到 %TEMP%\ClevoFan_conflict.png。
结束时重启服务清除冲突状态，并重新启动托盘。

.PARAMETER SourceDir
指定时先用 deploy\install.ps1 安装该目录中已发布的 ClevoFan。
#>
param([string]$SourceDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$wmi = Join-Path $repo 'tools\ecprobe\wmiprobe.exe'
$tray = Join-Path $env:ProgramFiles 'ClevoFan\ClevoFan.Tray.exe'
$slog = Join-Path $env:ProgramData 'ClevoFan\service.log'
$out = Join-Path $env:TEMP 'ClevoFan_conflict_test.log'
"" | Set-Content $out -Encoding utf8
function Log($m) { $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; Add-Content $out $line -Encoding utf8; Write-Host $line }
$script:fail = 0
function Check($name, $ok, $detail = '') { if ($ok) { Log "PASS  $name  $detail" } else { $script:fail++; Log "FAIL  $name  $detail" } }

Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.Core, System.Private.Windows.GdiPlus -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class ConflictTestWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static string Capture(IntPtr h, string path) {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        RECT r; GetWindowRect(h, out r);
        using (var b = new Bitmap(r.R - r.L, r.B - r.T)) using (var g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); b.Save(path, ImageFormat.Png); }
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
function WaitStatus($cond, $seconds) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do { $s = Status; if ($s -and (& $cond $s)) { return $s }; Start-Sleep -Milliseconds 500 } while ($sw.Elapsed.TotalSeconds -lt $seconds)
    return $s
}
function Pct($raw) { [int][math]::Floor($raw * 100 / 255.0 + 0.5) }
function WmiRead { $t = (& $wmi 2>&1 | Out-String); $kv = @{}; foreach ($m in [regex]::Matches($t, '(\w+)=(\d+)\b')) { $kv[$m.Groups[1].Value] = [int]$m.Groups[2].Value }; $kv }
function LogLines { $fs = [IO.File]::Open($slog, 'Open', 'Read', 'ReadWrite, Delete'); try { @((New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Dispose() } }
function LogSince($mark, $pattern) { @(LogLines | Select-Object -Skip $mark | Where-Object { $_ -match $pattern }).Count }

$fake = 200   # 78%，不在默认曲线中，不会与目标碰巧相同
$trayWasRunning = [bool](Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue)
try {
    if ($SourceDir) { & (Join-Path $repo 'deploy\install.ps1') -SourceDir $SourceDir -NoTray *>&1 | ForEach-Object { Log "install: $_" } }
    Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
    $s = WaitStatus { param($s) $s.State -eq 'Running' -and $s.TakenOver } 10
    Check '1 服务已接管' ($s.TakenOver -and -not $s.ExternalControlMessage) "目标 $($s.CpuTargetPercent)/$($s.GpuTargetPercent)%"

    # 2. 另一个程序每 3 秒把风扇改成 78%
    $mark = (LogLines).Count
    $sw = [Diagnostics.Stopwatch]::StartNew(); $detectedAt = $null
    while ($sw.Elapsed.TotalSeconds -lt 40) {
        & $wmi --set-duty $fake $fake | Out-Null
        Start-Sleep -Seconds 3
        $s = Status
        if ($s.ExternalControlMessage) { $detectedAt = $sw.Elapsed.TotalSeconds; break }
    }
    Check '2 判定为冲突' ($null -ne $detectedAt) ("{0:N0} 秒；{1}" -f $detectedAt, $s.ExternalControlMessage)
    Check '2 提示包含双方的负载' ($s.ExternalControlMessage -match "读回 $(Pct $fake)%/$(Pct $fake)%")
    Check '2 状态说明' ($s.State -eq 'Running' -and $s.TakenOver)

    # 冲突持续时不重复警告
    for ($i = 0; $i -lt 3; $i++) { & $wmi --set-duty $fake $fake | Out-Null; Start-Sleep -Seconds 3 }
    Check '3 冲突持续时只警告一次' ((LogSince $mark '检测到其他程序也在控制风扇') -eq 1)

    # 4. 另一个程序停止后恢复本程序的负载
    $sw = [Diagnostics.Stopwatch]::StartNew(); $ok = $false
    while ($sw.Elapsed.TotalSeconds -lt 10) {
        $s = Status; $h = WmiRead
        if ((Pct $h['fan1_duty']) -eq $s.CpuTargetPercent -and (Pct $h['fan2_duty']) -eq $s.GpuTargetPercent) { $ok = $true; break }
        Start-Sleep -Milliseconds 700
    }
    Check '4 停止后恢复本程序设定的负载' $ok "目标 $($s.CpuTargetPercent)/$($s.GpuTargetPercent)%，读回 $(Pct $h['fan1_duty'])/$(Pct $h['fan2_duty'])%"

    # 5. 设置窗口截图
    $p = Start-Process $tray -ArgumentList '--settings' -PassThru
    Start-Sleep -Seconds 5
    $h = [ConflictTestWin]::FindWindow([NullString]::Value, 'Clevo 风扇控制')
    $png = Join-Path $env:TEMP 'ClevoFan_conflict.png'
    Check '5 设置窗口显示冲突' ($h -ne [IntPtr]::Zero) ($(if ($h -ne [IntPtr]::Zero) { "截图 $([ConflictTestWin]::Capture($h, $png)) → $png" }))
    Stop-Process -Id $p.Id -Force
}
catch { $script:fail++; Log "ERROR: $_ @ $($_.InvocationInfo.ScriptLineNumber)" }
finally {
    #重启服务清除冲突状态（否则 10 分钟内托盘仍会提示），再恢复托盘
    Restart-Service ClevoFan
    $s = WaitStatus { param($s) $s.TakenOver } 10
    Log "已重启服务：接管 $($s.TakenOver)，冲突提示 $([bool]$s.ExternalControlMessage)"
    if ($trayWasRunning -and -not (Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe -ArgumentList "`"$tray`""
        Log '已重新启动托盘程序'
    }
    Log "===== $script:fail 项失败，日志 $out"
}
exit $script:fail
