using ClevoFan.Core;

namespace ClevoFan.Tests;

public class FanSupervisorTests : IDisposable
{
    private readonly FakeBackend _hw = new();
    private readonly ListLogger _log = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClevoFanTests_" + Guid.NewGuid().ToString("N"));
    private bool _legacy;
    private long _awakeMs;
    private readonly FanSupervisor _s;

    public FanSupervisorTests()
    {
        var controller = new FanController(_hw, _log, _ => { });
        _s = new FanSupervisor(controller, new FanConfig { TakeOver = true }, new ConfigStore(Path.Combine(_dir, "config.json")),
            _log, () => _legacy, () => _awakeMs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task 启动时先交还EC_停止时交还EC()
    {
        using var cts = new CancellationTokenSource();
        var run = _s.RunAsync(cts.Token);
        await WaitUntil(() => _hw.Calls.Count >= 2);
        cts.Cancel();
        await run;
        Assert.Equal(["SetAuto", "SetDuty 64 46", "SetAuto"], _hw.Calls);
    }

    [Fact]
    public void 睡眠时交还EC且不访问硬件_唤醒后恢复()
    {
        _s.Cycle();
        _s.Suspend();
        int reads = _hw.Reads;
        _s.Cycle();
        _s.Cycle();
        Assert.Equal(reads, _hw.Reads);
        Assert.Equal(["SetDuty 64 46", "SetAuto"], _hw.Calls);
        Assert.Equal(FanState.Suspended, _s.Status.State);

        _s.Resume();
        _s.Cycle();
        Assert.Equal("SetDuty 64 46", _hw.Calls[^1]);
        Assert.Equal(FanState.Running, _s.Status.State);
    }

    [Fact]
    public void 没有唤醒通知时醒着2分钟后自动恢复()
    {
        _s.Cycle();
        _s.Suspend();
        _awakeMs += 119_000;
        _s.Cycle();
        Assert.Equal(FanState.Suspended, _s.Status.State);
        _awakeMs += 2_000;
        _s.Cycle();
        Assert.Equal(FanState.Running, _s.Status.State);
        Assert.Equal("SetDuty 64 46", _hw.Calls[^1]);
        Assert.Equal(1, _log.Count("自动恢复控制"));
    }

    [Fact]
    public void 旧版程序运行时暂停接管_退出后恢复()
    {
        _s.Cycle();
        _legacy = true;
        _s.Cycle();
        _s.Cycle();
        Assert.Equal(FanState.Blocked, _s.Status.State);
        Assert.Equal(60, _s.Status.CpuTemp);
        Assert.Equal(["SetDuty 64 46", "SetAuto"], _hw.Calls);
        Assert.Equal(1, _log.Count("旧版 MyFanControl 正在运行"));

        _legacy = false;
        _s.Cycle();
        Assert.Equal(FanState.Running, _s.Status.State);
        Assert.Equal("SetDuty 64 46", _hw.Calls[^1]);
    }

    [Fact]
    public void 硬件出错时交还EC并只记录一次()
    {
        _s.Cycle();
        _hw.ThrowOnRead = new InvalidOperationException("WMI 调用失败");
        _s.Cycle();
        _s.Cycle();
        Assert.Equal(FanState.Error, _s.Status.State);
        Assert.Equal("WMI 调用失败", _s.Status.Message);
        Assert.Equal(["SetDuty 64 46", "SetAuto"], _hw.Calls);
        Assert.Equal(1, _log.Count("访问硬件出错"));

        _hw.ThrowOnRead = null;
        _s.Cycle();
        Assert.Equal(FanState.Running, _s.Status.State);
        Assert.Equal(1, _log.Count("硬件访问恢复正常"));
    }

    [Fact]
    public void 更新配置后保存并立即生效()
    {
        _s.Cycle();
        var config = _s.Config;
        config.TakeOver = false;
        _s.UpdateConfig(config);
        _s.Cycle();
        Assert.Equal("SetAuto", _hw.Calls[^1]);
        Assert.False(new ConfigStore(Path.Combine(_dir, "config.json")).Load()!.TakeOver);
    }

    [Fact]
    public void 拒绝无效配置()
    {
        Assert.Throws<ArgumentException>(() => _s.UpdateConfig(new FanConfig { UpdateIntervalSeconds = 9 }));
        Assert.True(_s.Config.TakeOver);
    }

    [Fact]
    public void 强制冷却开关()
    {
        _s.SetForcedCooling(true);
        _s.Cycle();
        Assert.Equal("SetDuty 242 242", _hw.Calls[^1]);
        Assert.True(_s.Status.ForcedCooling);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
