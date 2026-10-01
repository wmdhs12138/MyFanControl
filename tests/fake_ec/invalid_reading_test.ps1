# 用模拟的 ClevoEcInfo.dll 测试温度读数异常的处理，不接触真实硬件
# 先运行 tests\fake_ec\build.cmd 和 msbuild 编译出 Release\MyFanControl.exe
# 用法：pwsh -File tests\fake_ec\invalid_reading_test.ps1 [-StopRunning]
#   -StopRunning：先正常关闭正在运行的 MyFanControl，测试结束后从原路径重新启动
#                 （运行中的实例若以管理员身份运行，本脚本也需要以管理员身份运行）
param([switch]$StopRunning)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dir = Join-Path $env:TEMP 'MyFanControl_fake_ec_test'
$exe = Join-Path $dir 'MyFanControl.exe'
$scen = Join-Path $dir 'fake_ec.txt'
$flog = Join-Path $dir 'fake_ec.log'
$plog = Join-Path $dir 'MyFanControl.log'
try { [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance) } catch {}
$gbk = [Text.Encoding]::GetEncoding(936)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FakeEcTestWin {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
'@
function Read-Shared($path, $enc) {
    if (-not (Test-Path $path)) { return @() }
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
    try { return @((New-Object IO.StreamReader($fs, $enc)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Dispose() }
}
function FLines { Read-Shared $flog ([Text.Encoding]::ASCII) }
function PLines { Read-Shared $plog $gbk }
function Count($lines, $pattern, $from) { @($lines | Select-Object -Skip $from | Where-Object { $_ -match $pattern }).Count }
$script:fail = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { Write-Host "PASS  $name  $detail" } else { $script:fail++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red }
}
function Scenario($s) { [IO.File]::WriteAllText($scen, $s) }
function Stop-App($proc) {
    # [NullString]::Value：PowerShell 会把 $null 转成 "" 传给 .NET 的 string 参数
    $h = [FakeEcTestWin]::FindWindow([NullString]::Value, '蓝天风扇监控')
    [FakeEcTestWin]::PostMessage($h, 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
    if (-not $proc.WaitForExit(15000)) { Stop-Process -Id $proc.Id -Force; return $false }
    return $true
}

# 准备测试目录：程序、模拟 DLL、接管控制开启的默认曲线配置（不放 NVGPU_DLL.dll，GPU 功能不启用）
$restartPath = $null
$running = @(Get-Process MyFanControl -ErrorAction SilentlyContinue)
if ($running) {
    if (-not $StopRunning) { throw 'MyFanControl 正在运行（程序只允许一个实例），请关闭后重试或加 -StopRunning' }
    $restartPath = $running[0].Path
    $ok = Stop-App $running[0]
    Write-Host "已关闭正在运行的实例 $restartPath（正常退出=$ok）"
}
New-Item -ItemType Directory -Force $dir | Out-Null
Get-ChildItem $dir | Remove-Item -Force
Copy-Item (Join-Path $repo 'Release\MyFanControl.exe'), (Join-Path $repo 'tests\fake_ec\out\ClevoEcInfo.dll') $dir
# CConfig 的二进制布局：CPU、GPU 各 10 档负载，然后是 过渡温度、更新间隔、线性、接管、强制冷却温度、GPU限频、GPU频率
$duty = 95, 80, 70, 55, 35, 30, 25, 18, 18, 18
$cfg = $duty + $duty + @(3, 2, 0, 1, 50, 0, 0)
[IO.File]::WriteAllBytes((Join-Path $dir 'MyFanControl.cfg'), [byte[]]($cfg | ForEach-Object { [BitConverter]::GetBytes([int]$_) } | ForEach-Object { $_ }))

$proc = $null
try {
    # 1. 正常读数：按曲线接管（80℃ → 70% = 179/255，60℃ → 25% = 64/255）
    Scenario '80 60'
    $proc = Start-Process $exe -ArgumentList '/verbose' -WorkingDirectory $dir -PassThru
    Start-Sleep -Seconds 6
    $f = FLines
    Check '1 按曲线接管' ((Count $f 'SetFanDuty fan=1 duty=179' 0) -ge 1 -and (Count $f 'SetFanDuty fan=2 duty=64' 0) -ge 1) (($f | Select-String 'SetFanDuty ') -join '; ')

    # 2. 单次异常读数：1 秒后重读即恢复，不交还 EC
    $fm = (FLines).Count; $pm = (PLines).Count
    Scenario '80 60 1'
    Start-Sleep -Seconds 6
    $f = FLines; $p = PLines
    Check '2 发生了一次异常读数' ((Count $f '-> glitch' $fm) -eq 1)
    Check '2 单次异常不交还EC' ((Count $f 'SetFanDutyAuto' $fm) -eq 0 -and (Count $p '温度读数异常' $pm) -eq 0)

    # 3. 持续异常（1℃）：交还 EC 一次，期间不设置转速，日志只记一次
    $fm = (FLines).Count; $pm = (PLines).Count
    Scenario '1 1'
    Start-Sleep -Seconds 10
    $f = FLines; $p = PLines
    Check '3 交还EC（风扇1/2/3各一次）' ((Count $f 'SetFanDutyAuto fan=1' $fm) -eq 1 -and (Count $f 'SetFanDutyAuto fan=2' $fm) -eq 1 -and (Count $f 'SetFanDutyAuto fan=3' $fm) -eq 1)
    Check '3 异常期间不设置转速' ((Count $f 'SetFanDuty ' $fm) -eq 0)
    Check '3 异常只记录一次' ((Count $p '温度读数异常（CPU 1℃，GPU 1℃）' $pm) -eq 1)
    Check '3 保留上次有效温度' ((Count $p '读数（本轮接管前）：CPU 80℃.*读数有效=0' $pm) -ge 1)

    # 4. 恢复
    $fm = (FLines).Count; $pm = (PLines).Count
    Scenario '84 61'
    Start-Sleep -Seconds 6
    $f = FLines; $p = PLines
    Check '4 恢复后记录一次' ((Count $p '温度读数恢复正常（CPU 84℃，GPU 61℃）' $pm) -eq 1)
    Check '4 恢复后重新接管' ((Count $f 'SetFanDuty fan=1 duty=179' $fm) -ge 1 -and (Count $f 'SetFanDuty fan=2 duty=64' $fm) -ge 1)

    # 5. 超过上限的温度同样视为无效
    $fm = (FLines).Count; $pm = (PLines).Count
    Scenario '120 60'
    Start-Sleep -Seconds 6
    Check '5 120℃ 视为无效' ((Count (PLines) '温度读数异常（CPU 120℃' $pm) -eq 1 -and (Count (FLines) 'SetFanDutyAuto fan=1' $fm) -eq 1)

    # 6. 再次恢复，正常退出时交还 EC
    $fm = (FLines).Count
    Scenario '82 60'
    Start-Sleep -Seconds 5
    Check '6 再次恢复后接管' ((Count (FLines) 'SetFanDuty fan=1 duty=179' $fm) -ge 1)
    $fm = (FLines).Count
    $ok = Stop-App $proc
    Check '6 正常退出并交还EC' ($ok -and (Count (FLines) 'SetFanDutyAuto fan=1' $fm) -eq 1)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    if ($restartPath) {
        Start-Process -FilePath $restartPath -WorkingDirectory (Split-Path $restartPath) | Out-Null
        Write-Host "已重新启动 $restartPath"
    }
}
Write-Host "===== $script:fail 项失败"
exit $script:fail
