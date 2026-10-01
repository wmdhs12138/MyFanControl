using ClevoFan.Core;
using ClevoFan.Hardware;

namespace ClevoFan.Service;

/// <summary>初始化 NVML；没有可用的 NVIDIA GPU 时只禁用 GPU 限频，风扇控制照常运行。服务退出时由容器释放。</summary>
public sealed class GpuHolder : IDisposable
{
    public GpuHolder(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ClevoFan");
        try
        {
            Backend = new NvmlGpuBackend();
            logger.LogInformation("检测到 {Gpu}，可设频率 {Min}-{Max} MHz", Backend.Name, Backend.MinClockMHz, Backend.MaxClockMHz);
        }
        catch (Exception e)
        {
            UnavailableReason = "GPU 限频不可用：" + e.Message;
            logger.LogWarning("{Reason}", UnavailableReason);
        }
    }

    public IGpuBackend? Backend { get; }
    public string? UnavailableReason { get; }

    public void Dispose() => Backend?.Dispose();
}
