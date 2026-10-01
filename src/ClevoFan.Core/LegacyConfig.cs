using System.Buffers.Binary;

namespace ClevoFan.Core;

/// <summary>读取原 MyFanControl 的 MyFanControl.cfg（CConfig 结构体按字节写入，27 个 int）。</summary>
public static class LegacyConfig
{
    public const int Size = 27 * sizeof(int);

    public static FanConfig Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size)
            throw new FormatException($"旧配置文件应为 {Size} 字节，实际为 {data.Length} 字节");
        var v = new int[27];
        for (int i = 0; i < v.Length; i++)
            v[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * sizeof(int)));

        // 布局：CPU 10 档、GPU 10 档、过渡温度、更新间隔、线性、接管、强制冷却温度、GPU 限频开关、GPU 频率
        // 原程序 GPU 频率为 0 表示不限频；高于默认频率表示超频，新版不支持超频，应用时会因超出范围而不生效
        bool gpuLimit = v[25] != 0 && v[26] > 0;
        var config = new FanConfig
        {
            CpuCurve = v[0..10],
            GpuCurve = v[10..20],
            TransitionTemp = v[20],
            UpdateIntervalSeconds = v[21],
            Linear = v[22] != 0,
            TakeOver = v[23] != 0,
            ForceCoolingTemp = v[24],
            GpuClockLimitEnabled = gpuLimit,
            GpuMaxClockMHz = gpuLimit ? v[26] : 0,
        };
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new FormatException("旧配置文件内容无效：" + string.Join("；", errors));
        return config;
    }
}
