using ClevoFan.Core;

namespace ClevoFan.Tests;

public class GpuLimiterTests
{
    private readonly FakeGpu _gpu = new();
    private readonly ListLogger _log = new();
    private readonly GpuLimiter _limiter;
    private readonly FanConfig _config = new() { GpuClockLimitEnabled = true, GpuMaxClockMHz = 900 };

    public GpuLimiterTests()
    {
        _limiter = new GpuLimiter(_gpu, _log);
    }

    private FanStatus Status => _limiter.Describe(new FanStatus());

    [Fact]
    public void 未开启时不访问显卡()
    {
        _config.GpuClockLimitEnabled = false;
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Empty(_gpu.Calls);
        Assert.Null(Status.GpuClockLimitMHz);
    }

    [Fact]
    public void 通电时应用一次()
    {
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900"], _gpu.Calls);
        Assert.Equal(900, Status.GpuClockLimitMHz);
        Assert.Null(Status.GpuMessage);
        Assert.Equal(1, _log.Count("GPU 最高频率限制为 900 MHz"));
    }

    [Fact]
    public void 断电时不唤醒_通电后应用()
    {
        _gpu.PoweredOn = false;
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Empty(_gpu.Calls);
        Assert.Contains("将在通电时", Status.GpuMessage);

        _gpu.PoweredOn = true;
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900"], _gpu.Calls);
        Assert.Null(Status.GpuMessage);
    }

    [Fact]
    public void 断电后再通电时重新应用()
    {
        _limiter.Tick(_config);
        _gpu.PoweredOn = false;
        _limiter.Tick(_config);
        _gpu.PoweredOn = true;
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Lock 900"], _gpu.Calls);
        Assert.Equal(1, _log.Count("GPU 最高频率限制为 900 MHz"));
    }

    [Fact]
    public void 关闭后解除一次()
    {
        _limiter.Tick(_config);
        _config.GpuClockLimitEnabled = false;
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Reset"], _gpu.Calls);
        Assert.Null(Status.GpuClockLimitMHz);
    }

    [Fact]
    public void 断电时关闭_通电后解除()
    {
        _limiter.Tick(_config);
        _gpu.PoweredOn = false;
        _config.GpuClockLimitEnabled = false;
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900"], _gpu.Calls);
        _gpu.PoweredOn = true;
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Reset"], _gpu.Calls);
    }

    [Fact]
    public void 修改上限后重新应用()
    {
        _limiter.Tick(_config);
        _config.GpuMaxClockMHz = 1200;
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Lock 1200"], _gpu.Calls);
        Assert.Equal(1200, Status.GpuClockLimitMHz);
    }

    [Fact]
    public void 报告实际生效的上限()
    {
        _config.GpuMaxClockMHz = 1007;
        _limiter.Tick(_config);
        Assert.Equal(1005, Status.GpuClockLimitMHz);
        Assert.Equal(1, _log.Count("GPU 最高频率限制为 1005 MHz"));
        _limiter.Tick(_config);
        Assert.Single(_gpu.Calls);
    }

    [Fact]
    public void 超出显卡范围时不应用并只警告一次()
    {
        _config.GpuMaxClockMHz = 2590;
        _limiter.Tick(_config);
        _limiter.Tick(_config);
        Assert.Empty(_gpu.Calls);
        Assert.Contains("超出显卡支持的 300-2100 MHz", Status.GpuMessage);
        Assert.Equal(1, _log.Count("超出显卡支持"));
    }

    [Fact]
    public void 改为超出范围时解除已有限频()
    {
        _limiter.Tick(_config);
        _config.GpuMaxClockMHz = 2590;
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Reset"], _gpu.Calls);
        Assert.Null(Status.GpuClockLimitMHz);
    }

    [Fact]
    public void 失败后隔一段时间重试且只记录一次()
    {
        _gpu.ThrowOnLock = new InvalidOperationException("NVML 错误");
        _limiter.Tick(_config);
        Assert.Contains("NVML 错误", Status.GpuMessage);
        _gpu.ThrowOnLock = null;
        for (int i = 0; i < GpuLimiter.RetryTicks; i++)
            _limiter.Tick(_config);
        Assert.Empty(_gpu.Calls);
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900"], _gpu.Calls);
        Assert.Null(Status.GpuMessage);
        Assert.Equal(1, _log.Count("GPU 限频失败"));
    }

    [Fact]
    public void 唤醒后重新应用()
    {
        _limiter.Tick(_config);
        _limiter.OnResume();
        _limiter.Tick(_config);
        Assert.Equal(["Lock 900", "Lock 900"], _gpu.Calls);
    }

    [Fact]
    public void 停止时解除()
    {
        _limiter.Tick(_config);
        _limiter.Stop();
        _limiter.Stop();
        Assert.Equal(["Lock 900", "Reset"], _gpu.Calls);
    }

    [Fact]
    public void 未限频时停止不访问显卡()
    {
        _limiter.Stop();
        Assert.Empty(_gpu.Calls);
    }

    [Fact]
    public void 实时状态_断电时不读取()
    {
        _gpu.PoweredOn = false;
        Assert.Equal(new GpuLiveStatus(false, null, null), _limiter.ReadLive());
        Assert.Empty(_gpu.Calls);
        _gpu.PoweredOn = true;
        Assert.Equal(new GpuLiveStatus(true, 900, 50), _limiter.ReadLive());
    }
}

public class GpuSupervisorTests
{
    private readonly FakeBackend _hw = new();
    private readonly FakeGpu _gpu = new();
    private readonly ListLogger _log = new();
    private bool _legacy;
    private readonly FanSupervisor _s;

    public GpuSupervisorTests()
    {
        var config = new FanConfig { TakeOver = true, GpuClockLimitEnabled = true, GpuMaxClockMHz = 900 };
        _s = new FanSupervisor(new FanController(_hw, _log, _ => { }), config, null, _log, () => _legacy, () => 0,
            new GpuLimiter(_gpu, _log));
    }

    [Fact]
    public void 每轮应用GPU限频并报告状态()
    {
        _s.Cycle();
        Assert.Equal(["Lock 900"], _gpu.Calls);
        Assert.Equal(900, _s.Status.GpuClockLimitMHz);
        Assert.Equal("Fake GPU", _s.Status.GpuName);
        Assert.Equal((300, 2100), (_s.Status.GpuMinClockMHz, _s.Status.GpuMaxClockMHz));
    }

    [Fact]
    public void 风扇出错不影响GPU限频()
    {
        _hw.ThrowOnRead = new InvalidOperationException("WMI 调用失败");
        _s.Cycle();
        Assert.Equal(FanState.Error, _s.Status.State);
        Assert.Equal(["Lock 900"], _gpu.Calls);
    }

    [Fact]
    public void GPU出错不影响风扇控制()
    {
        _gpu.ThrowOnLock = new InvalidOperationException("NVML 错误");
        _s.Cycle();
        Assert.Equal(FanState.Running, _s.Status.State);
        Assert.Equal(["SetDuty 64 46"], _hw.Calls);
        Assert.Contains("NVML 错误", _s.Status.GpuMessage);
    }

    [Fact]
    public void 睡眠和旧版运行期间不操作GPU()
    {
        _s.Suspend();
        _s.Cycle();
        Assert.Empty(_gpu.Calls);
        _s.Resume();
        _legacy = true;
        _s.Cycle();
        Assert.Empty(_gpu.Calls);
        _legacy = false;
        _s.Cycle();
        Assert.Equal(["Lock 900"], _gpu.Calls);
    }

    [Fact]
    public async Task 停止服务时解除限频()
    {
        using var cts = new CancellationTokenSource();
        var run = _s.RunAsync(cts.Token);
        for (int i = 0; i < 200 && _gpu.Calls.Count == 0; i++)
            await Task.Delay(10);
        cts.Cancel();
        await run;
        Assert.Equal(["Lock 900", "Reset"], _gpu.Calls);
    }

    [Fact]
    public void 没有可用GPU时报告原因()
    {
        var s = new FanSupervisor(new FanController(_hw, _log, _ => { }), new FanConfig(), null, _log, () => false, () => 0,
            gpu: null, gpuUnavailable: "未找到 nvml.dll");
        s.Cycle();
        Assert.Null(s.Status.GpuName);
        Assert.Equal("未找到 nvml.dll", s.Status.GpuMessage);
        Assert.Null(s.ReadGpuLive());
    }
}
