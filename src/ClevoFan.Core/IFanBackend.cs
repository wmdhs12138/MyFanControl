namespace ClevoFan.Core;

/// <summary>风扇硬件访问。调用者负责串行化，实现不需要考虑并发。</summary>
public interface IFanBackend : IDisposable
{
    string Name { get; }

    FanReading Read();

    /// <summary>设置 CPU、GPU 风扇负载（EC 原始值 0-255）。</summary>
    void SetDuty(int cpuDuty, int gpuDuty);

    /// <summary>所有风扇交还 EC 自动控制。</summary>
    void SetAuto();
}
