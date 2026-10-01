using ClevoFan.Core;
using Microsoft.Win32;

namespace ClevoFan.Tray;

internal static class ServiceConnection
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>发送请求；服务未运行时返回 null。</summary>
    public static async Task<PipeResponse?> TrySendAsync(PipeRequest request)
    {
        try
        {
            return await FanServiceClient.SendAsync(request, Timeout);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class StatusText
{
    public static string State(FanStatus s) => s.State switch
    {
        FanState.Starting => "启动中",
        FanState.Running when s.ForcedCooling => "强制冷却中",
        FanState.Running when s.TakenOver => "已接管",
        FanState.Running => "EC 自动控制",
        FanState.InvalidReading => "温度读数异常，EC 自动控制",
        FanState.Suspended => "睡眠中，EC 自动控制",
        FanState.Blocked => "旧版 MyFanControl 正在运行，已暂停接管",
        FanState.Error => "硬件访问出错，EC 自动控制：" + s.Message,
        _ => s.State.ToString(),
    };

    /// <summary>托盘提示最多 127 个字符。</summary>
    public static string Tooltip(FanStatus s)
    {
        var gpuLimit = s.GpuClockLimitMHz is int limit ? $"，GPU 限频 {limit} MHz" : "";
        var text = $"CPU {s.CpuTemp}℃ {s.CpuDutyPercent}%   GPU {s.GpuTemp}℃ {s.GpuDutyPercent}%\n{State(s)}{gpuLimit}";
        return text.Length <= 127 ? text : text[..127];
    }

    public static string Fan(string name, int temp, int duty, int? rpm, int? target) =>
        $"{name}：{temp}℃   负载 {duty}%   转速 {(rpm is { } r ? r + " RPM" : "-")}   目标 {(target is { } t ? t + "%" : "-")}";
}

/// <summary>登录时启动托盘：写入当前用户的 Run 注册表项。</summary>
internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ClevoFanTray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
            key.SetValue(Name, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(Name, false);
    }
}
