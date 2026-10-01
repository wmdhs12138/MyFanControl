namespace ClevoFan.Core;

/// <summary>风扇控制配置，各项含义与原 MyFanControl 的 CConfig 相同。</summary>
public sealed class FanConfig
{
    /// <summary>2：增加 GPU 限频。旧版本文件缺少的字段取默认值。</summary>
    public const int CurrentVersion = 2;

    public const int MinGpuClockMHz = 100;
    public const int MaxGpuClockMHz = 5000;

    /// <summary>各档位的温度阈值：90+、85+、80+、75+、70+、65+、60+、55+、50+、50 以下。</summary>
    public static IReadOnlyList<int> Thresholds { get; } = [90, 85, 80, 75, 70, 65, 60, 55, 50, 45];

    public const int LevelCount = 10;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>CPU 风扇各档负载（%），下标与 <see cref="Thresholds"/> 对应。</summary>
    public int[] CpuCurve { get; set; } = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];

    /// <summary>GPU 风扇各档负载（%）。</summary>
    public int[] GpuCurve { get; set; } = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];

    /// <summary>过渡温度：降温时要比档位阈值再低这么多才降档，避免在两档之间来回切换。</summary>
    public int TransitionTemp { get; set; } = 3;

    public int UpdateIntervalSeconds { get; set; } = 2;

    /// <summary>线性控制：在相邻两档之间按温度线性插值，而不是阶梯切换。</summary>
    public bool Linear { get; set; }

    /// <summary>接管控制；关闭时只读取状态，风扇由 EC 自动控制。</summary>
    public bool TakeOver { get; set; }

    /// <summary>强制冷却的目标温度：CPU 和 GPU 都低于此温度后结束强制冷却。</summary>
    public int ForceCoolingTemp { get; set; } = 50;

    /// <summary>限制 GPU 最高频率（只限制，不超频）。</summary>
    public bool GpuClockLimitEnabled { get; set; }

    /// <summary>GPU 最高频率（MHz）。实际可设范围取决于显卡，超出时不应用。</summary>
    public int GpuMaxClockMHz { get; set; }

    public FanConfig Clone() => new()
    {
        Version = Version,
        CpuCurve = (int[])CpuCurve.Clone(),
        GpuCurve = (int[])GpuCurve.Clone(),
        TransitionTemp = TransitionTemp,
        UpdateIntervalSeconds = UpdateIntervalSeconds,
        Linear = Linear,
        TakeOver = TakeOver,
        ForceCoolingTemp = ForceCoolingTemp,
        GpuClockLimitEnabled = GpuClockLimitEnabled,
        GpuMaxClockMHz = GpuMaxClockMHz,
    };

    /// <summary>检查配置，返回错误说明；为空表示有效。取值范围与原程序的界面校验相同。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        CheckCurve(CpuCurve, "CPU", errors);
        CheckCurve(GpuCurve, "GPU", errors);
        if (TransitionTemp is < 0 or > 10)
            errors.Add("过渡温度必须为 0-10");
        if (UpdateIntervalSeconds is < 1 or > 5)
            errors.Add("更新间隔必须为 1-5 秒");
        if (ForceCoolingTemp is < 40 or > 90)
            errors.Add("强制冷却温度必须为 40-90");
        if (GpuClockLimitEnabled && GpuMaxClockMHz is < MinGpuClockMHz or > MaxGpuClockMHz)
            errors.Add($"GPU 频率限制必须为 {MinGpuClockMHz}-{MaxGpuClockMHz} MHz");
        return errors;
    }

    private static void CheckCurve(int[]? curve, string name, List<string> errors)
    {
        if (curve is null || curve.Length != LevelCount)
            errors.Add($"{name} 风扇曲线必须有 {LevelCount} 档");
        else if (curve.Any(d => d is < 0 or > 100))
            errors.Add($"{name} 风扇负载必须为 0-100");
    }
}
