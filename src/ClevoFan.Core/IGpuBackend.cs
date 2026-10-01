namespace ClevoFan.Core;

/// <summary>GPU 频率控制。调用者负责串行化。</summary>
public interface IGpuBackend : IDisposable
{
    string Name { get; }

    /// <summary>可设的最低、最高图形频率（MHz）。</summary>
    int MinClockMHz { get; }
    int MaxClockMHz { get; }

    /// <summary>
    /// 独显当前是否通电。必须不会唤醒显卡：双显卡笔记本的独显空闲时会断电，频繁访问驱动会让它一直通电。
    /// </summary>
    bool IsPoweredOn();

    /// <summary>把图形频率锁定在 [MinClockMHz, maxMHz]，空闲时仍可降到最低频率。返回实际生效的上限（可能取整到显卡支持的档位）。</summary>
    int LockMaxClock(int maxMHz);

    /// <summary>解除频率锁定，恢复驱动默认。</summary>
    void ResetClocks();

    /// <summary>读取当前频率和利用率；独显未通电时会唤醒它，调用前先检查 <see cref="IsPoweredOn"/>。</summary>
    (int ClockMHz, int UtilizationPercent) ReadLive();
}
