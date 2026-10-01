namespace ClevoFan.Core;

/// <summary>温度到负载的换算，逐行移植自原 MyFanControl 的 CalcStdDuty / CalcLinearDuty，行为保持一致。</summary>
public static class FanCurve
{
    /// <summary>
    /// 阶梯控制。档位 1-10（10 为 90℃ 以上）。升温到阈值立即升档；降温时温度处于
    /// [阈值 - 过渡温度, 阈值) 区间内，若上一次的档位不低于该档，则保持该档。
    /// </summary>
    /// <param name="lastLevel">上一次的档位，0 表示没有（例如刚接管）。</param>
    public static (int Percent, int Level) Step(int temp, IReadOnlyList<int> curve, int transition, int lastLevel)
    {
        var thresholds = FanConfig.Thresholds;
        int k, level = 0;
        for (k = 0; k < FanConfig.LevelCount; k++)
        {
            level = FanConfig.LevelCount - k;
            if (temp >= thresholds[k])
                break;
            if (temp < thresholds[k] - transition)
                continue;
            if (lastLevel >= level)
                break;
        }
        k = Math.Min(FanConfig.LevelCount - 1, k);
        return (curve[k], level);
    }

    /// <summary>
    /// 线性控制。用于计算的温度在升温时立即跟随，降温时最多比当前温度高出过渡温度；
    /// 在相邻两档之间按该温度线性插值。
    /// </summary>
    /// <param name="lastTemp">上一次用于计算的温度，首次为 0。</param>
    public static (int Percent, int Level, int UsedTemp) Linear(int temp, IReadOnlyList<int> curve, int transition, int lastTemp)
    {
        var thresholds = FanConfig.Thresholds;
        int j = Math.Max(lastTemp, temp);
        j = Math.Min(j, temp + transition);

        if (j < 45)
            return (curve[9], 0, j);
        if (j >= 90)
            return (curve[0], 10, j);

        int idx = j switch
        {
            < 50 => 8,
            < 55 => 7,
            < 60 => 6,
            < 65 => 5,
            < 70 => 4,
            < 75 => 3,
            < 80 => 2,
            < 85 => 1,
            _ => 0,
        };
        int tempLow = thresholds[idx + 1], tempHigh = thresholds[idx];
        int dutyLow = curve[idx + 1], dutyHigh = curve[idx];
        int percent = (int)((dutyHigh - dutyLow) / (double)(tempHigh - tempLow) * (j - tempLow) + 0.5) + dutyLow;
        return (percent, 9 - idx, j);
    }
}
