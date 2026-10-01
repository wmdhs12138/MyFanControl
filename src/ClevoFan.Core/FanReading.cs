namespace ClevoFan.Core;

/// <summary>一次从 EC 读到的风扇状态。负载为 EC 原始值 0-255，转速为 EC 的周期计数。</summary>
public readonly record struct FanReading(int CpuTemp, int GpuTemp, int CpuDuty, int GpuDuty, int CpuRpmRaw, int GpuRpmRaw)
{
    /// <summary>EC 偶尔会连续数秒返回 1℃ 等异常读数（见 docs/ec-protocol.md），超出此范围视为无效。</summary>
    public const int MinValidTemp = 10;
    public const int MaxValidTemp = 110;

    public bool IsValid => IsValidTemp(CpuTemp) && IsValidTemp(GpuTemp);

    public static bool IsValidTemp(int temp) => temp is >= MinValidTemp and <= MaxValidTemp;

    /// <summary>EC 负载（0-255）换算为百分比，取整方式与原程序相同。</summary>
    public static int DutyToPercent(int duty) => (int)(duty * 100 / 255.0 + 0.5);

    public static int PercentToDuty(int percent) => (int)(percent * 255.0 / 100 + 0.5);

    /// <summary>转速：原程序按 Control Center 显示值拟合的公式，计数不在 300-5000 之间时无法换算。</summary>
    public static int? Rpm(int raw) => raw == 0 ? 0 : raw is > 300 and < 5000 ? 2100000 / raw : null;
}
