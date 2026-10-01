namespace ClevoFan.Core;

public enum FanState
{
    /// <summary>尚未完成第一次读取。</summary>
    Starting,
    /// <summary>正常运行（是否接管见 <see cref="FanStatus.TakenOver"/>）。</summary>
    Running,
    /// <summary>温度读数无效，已交还 EC 自动控制。</summary>
    InvalidReading,
    /// <summary>系统睡眠中，已交还 EC 自动控制。</summary>
    Suspended,
    /// <summary>检测到旧版 MyFanControl 正在运行，暂停接管。</summary>
    Blocked,
    /// <summary>访问硬件出错，已尝试交还 EC 自动控制。</summary>
    Error,
}

/// <summary>服务向托盘程序报告的状态。</summary>
public sealed record FanStatus
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public FanState State { get; init; } = FanState.Starting;
    public string? Message { get; init; }
    public string? Backend { get; init; }

    /// <summary>最近一次有效读数；读数无效时保留上一次的有效值。</summary>
    public int CpuTemp { get; init; }
    public int GpuTemp { get; init; }
    public int CpuDutyPercent { get; init; }
    public int GpuDutyPercent { get; init; }
    public int? CpuRpm { get; init; }
    public int? GpuRpm { get; init; }

    public bool TakenOver { get; init; }
    public int? CpuTargetPercent { get; init; }
    public int? GpuTargetPercent { get; init; }
    public int CpuLevel { get; init; }
    public int GpuLevel { get; init; }
    public bool ForcedCooling { get; init; }

    /// <summary>检测到其他程序也在控制风扇时的说明；为空表示没有冲突。</summary>
    public string? ExternalControlMessage { get; init; }

    /// <summary>显卡名称；为空表示没有可用的 NVIDIA GPU（原因见 <see cref="GpuMessage"/>）。</summary>
    public string? GpuName { get; init; }
    public int? GpuMinClockMHz { get; init; }
    public int? GpuMaxClockMHz { get; init; }

    /// <summary>当前生效的 GPU 频率上限，为空表示未限频。</summary>
    public int? GpuClockLimitMHz { get; init; }
    public string? GpuMessage { get; init; }
}

/// <summary>GPU 实时状态。独显未通电时不读取（读取会唤醒它）。</summary>
public sealed record GpuLiveStatus(bool PoweredOn, int? ClockMHz, int? UtilizationPercent);
