#Requires -RunAsAdministrator
<#
.SYNOPSIS
在真机上测试 ClevoFan 的 GPU 限频：每一步用 tools\gpuload 让独显满载，用 nvidia-smi 独立测量频率。
测试结束后恢复测试前的配置。

.PARAMETER SourceDir
指定时先用 deploy\install.ps1 安装该目录中已发布的 ClevoFan；不指定则测试已安装的服务。

.NOTES
需要先编译 tools\gpuload（dotnet build tools\gpuload -c Release -o tools\gpuload\bin\out）。
#>
param([string]$SourceDir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$load = Join-Path $repo 'tools\gpuload\bin\out\gpuload.exe'
$slog = Join-Path $env:ProgramData 'ClevoFan\service.log'
$out = Join-Path $env:TEMP 'ClevoFan_gpu_limit_test.log'
"" | Set-Content $out -Encoding utf8
function Log($m) { $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $m; Add-Content $out $line -Encoding utf8; Write-Host $line }
$script:fail = 0
function Check($name, $ok, $detail = '') { if ($ok) { Log "PASS  $name  $detail" } else { $script:fail++; Log "FAIL  $name  $detail" } }

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
function SetGpuLimit($enabled, $mhz) {
    $config = (Pipe @{ Command = 'getConfig' }).Config
    $config.GpuClockLimitEnabled = $enabled
    $config.GpuMaxClockMHz = $mhz
    return Pipe @{ Command = 'setConfig'; Config = $config }
}
#满载 8 秒，返回利用率 >= 90% 时的最高频率
function LoadMaxClock {
    $p = Start-Process $load -ArgumentList 8 -PassThru -WindowStyle Hidden
    $rows = while (-not $p.HasExited) {
        $c = (& nvidia-smi --query-gpu=clocks.gr,utilization.gpu --format=csv,noheader,nounits) -split ', '
        [pscustomobject]@{ Clock = [int]$c[0]; Util = [int]$c[1] }
        Start-Sleep -Milliseconds 400
    }
    $busy = @($rows | Where-Object Util -ge 90)
    if ($busy.Count -lt 3) { return -1 }
    return ($busy.Clock | Measure-Object -Maximum).Maximum
}
function LogLines { if (-not (Test-Path $slog)) { return @() }; $fs = [IO.File]::Open($slog, 'Open', 'Read', 'ReadWrite, Delete'); try { @((New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Dispose() } }
function LogSince($mark, $pattern) { @(LogLines | Select-Object -Skip $mark | Where-Object { $_ -match $pattern }).Count }

if (-not (Test-Path $load)) { throw "找不到 $load，请先编译 tools\gpuload" }
$original = $null
try {
    if ($SourceDir) {
        $mark = (LogLines).Count
        & (Join-Path $repo 'deploy\install.ps1') -SourceDir $SourceDir *>&1 | ForEach-Object { Log "install: $_" }
        Start-Sleep -Seconds 2
        Check '0 启动时检测到 NVIDIA GPU' ((LogSince $mark '检测到 NVIDIA .*可设频率') -eq 1)
    }
    $original = (Pipe @{ Command = 'getConfig' }).Config
    $s = WaitStatus { param($s) $s.GpuName } 6
    Check '1 状态包含 GPU 信息' ($s.GpuName -and $s.GpuMinClockMHz -gt 0 -and $s.GpuMaxClockMHz -gt $s.GpuMinClockMHz) "$($s.GpuName) $($s.GpuMinClockMHz)-$($s.GpuMaxClockMHz) MHz"
    $live = (Pipe @{ Command = 'gpuLive' }).GpuLive
    Check '1 实时状态接口' ($live -and ($live.PoweredOn -eq $false -or $live.ClockMHz -gt 0)) "powered=$($live.PoweredOn) clock=$($live.ClockMHz) util=$($live.UtilizationPercent)"

    SetGpuLimit $false 0 | Out-Null
    Start-Sleep -Seconds 3
    $baseline = LoadMaxClock
    Check '2 不限频时满载频率' ($baseline -gt 1300) "$baseline MHz"

    $mark = (LogLines).Count
    $r = SetGpuLimit $true 900
    $s = WaitStatus { param($s) $s.GpuClockLimitMHz -eq 900 } 6
    $clock = LoadMaxClock
    Check '3 限频 900 MHz' ($r.Ok -and $s.GpuClockLimitMHz -eq 900 -and $clock -gt 0 -and $clock -le 900) "满载最高 $clock MHz"
    Check '3 日志记录限频' ((LogSince $mark 'GPU 最高频率限制为 900 MHz') -eq 1)

    SetGpuLimit $true 1007 | Out-Null
    $s = WaitStatus { param($s) $s.GpuClockLimitMHz -ne 900 } 6
    $clock = LoadMaxClock
    Check '4 1007 MHz 取整到可用档位' ($s.GpuClockLimitMHz -le 1007 -and $s.GpuClockLimitMHz -gt 990 -and $clock -gt 900 -and $clock -le $s.GpuClockLimitMHz) "状态 $($s.GpuClockLimitMHz) MHz，满载最高 $clock MHz"

    SetGpuLimit $true 900 | Out-Null
    WaitStatus { param($s) $s.GpuClockLimitMHz -eq 900 } 6 | Out-Null
    $mark = (LogLines).Count
    & sc.exe control ClevoFan 200 | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe control ClevoFan 201 | Out-Null
    WaitStatus { param($s) $s.State -ne 'Suspended' } 6 | Out-Null
    Start-Sleep -Seconds 3
    $clock = LoadMaxClock
    Check '5 模拟睡眠唤醒后仍限频' ($clock -gt 0 -and $clock -le 900) "满载最高 $clock MHz"

    $mark = (LogLines).Count
    Restart-Service ClevoFan
    $s = WaitStatus { param($s) $s.GpuClockLimitMHz -eq 900 } 10
    $clock = LoadMaxClock
    Check '6 重启服务后重新应用' ($s.GpuClockLimitMHz -eq 900 -and $clock -gt 0 -and $clock -le 900) "满载最高 $clock MHz"
    Check '6 停止时解除、启动后重新限频' ((LogSince $mark '已解除 GPU 频率限制') -ge 1 -and (LogSince $mark 'GPU 最高频率限制为 900 MHz') -ge 1)

    Stop-Service ClevoFan
    $clock = LoadMaxClock
    Check '7 停止服务后解除限频' ($clock -gt 1300) "满载最高 $clock MHz"
    Start-Service ClevoFan
    WaitStatus { param($s) $s.GpuClockLimitMHz -eq 900 } 10 | Out-Null

    $r = SetGpuLimit $false 900
    $s = WaitStatus { param($s) $null -eq $s.GpuClockLimitMHz } 6
    $clock = LoadMaxClock
    Check '8 关闭限频后恢复' ($r.Ok -and $null -eq $s.GpuClockLimitMHz -and $clock -gt 1300) "满载最高 $clock MHz"
}
catch { $script:fail++; Log "ERROR: $_ @ $($_.InvocationInfo.ScriptLineNumber)" }
finally {
    if ((Get-Service ClevoFan).Status -ne 'Running') { Start-Service ClevoFan; Start-Sleep -Seconds 2 }
    if ($original) {
        $r = Pipe @{ Command = 'setConfig'; Config = $original }
        Log "恢复测试前的配置：$(if ($r.Ok) { '成功' } else { "失败 $($r.Error)" })"
    }
    Log "===== $script:fail 项失败，日志 $out"
}
exit $script:fail
