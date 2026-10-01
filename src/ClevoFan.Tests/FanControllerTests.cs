using ClevoFan.Core;

namespace ClevoFan.Tests;

public class FanControllerTests
{
    private readonly FakeBackend _hw = new();
    private readonly ListLogger _log = new();
    private readonly List<TimeSpan> _sleeps = [];
    private readonly FanController _c;

    public FanControllerTests()
    {
        _c = new FanController(_hw, _log, _sleeps.Add) { Config = new FanConfig { TakeOver = true } };
    }

    //默认曲线 80℃ → 70% = 179，60℃ → 25% = 64，55℃ → 18% = 46

    [Fact]
    public void 接管时按曲线写入负载()
    {
        _hw.CpuTemp = 80;
        _hw.GpuTemp = 60;
        var s = _c.Tick();
        Assert.Equal(["SetDuty 179 64"], _hw.Calls);
        Assert.True(_c.TakenOver);
        Assert.Equal(FanState.Running, s.State);
        Assert.Equal((70, 25), (s.CpuTargetPercent, s.GpuTargetPercent));
        Assert.Equal(1, _log.Count("接管风扇控制"));
    }

    [Fact]
    public void EC读回与目标一致时不重复写入()
    {
        _c.Tick();
        _c.Tick();
        _c.Tick();
        Assert.Single(_hw.Calls);
    }

    [Fact]
    public void 目标变化时再次写入()
    {
        _hw.CpuTemp = 60;
        _c.Tick();
        _hw.CpuTemp = 80;
        _c.Tick();
        Assert.Equal(["SetDuty 64 46", "SetDuty 179 46"], _hw.Calls);
    }

    [Fact]
    public void EC不执行写入时每轮重写并只警告一次()
    {
        _hw.ApplyWrites = false;
        for (int i = 0; i < 10; i++)
            _c.Tick();
        Assert.Equal(10, _hw.Calls.Count);
        Assert.Equal(1, _log.Count("EC 读回仍为"));
    }

    [Fact]
    public void 不接管时不写入()
    {
        _c.Config.TakeOver = false;
        _c.Tick();
        Assert.Empty(_hw.Calls);
        Assert.False(_c.TakenOver);
    }

    [Fact]
    public void 关闭接管时交还EC一次()
    {
        _c.Tick();
        _c.Config.TakeOver = false;
        _c.Tick();
        _c.Tick();
        Assert.Equal(["SetDuty 64 46", "SetAuto"], _hw.Calls);
        Assert.False(_c.TakenOver);
    }

    [Fact]
    public void 持续异常读数时交还EC_恢复后重新接管()
    {
        _hw.CpuTemp = 80;
        _c.Tick();
        _hw.CpuTemp = 1;
        _hw.GpuTemp = 1;
        var s = _c.Tick();
        _c.Tick();
        Assert.Equal(FanState.InvalidReading, s.State);
        Assert.Equal(80, s.CpuTemp);
        Assert.Equal(["SetDuty 179 46", "SetAuto"], _hw.Calls);
        Assert.Equal(1, _log.Count("温度读数异常"));
        Assert.Contains(TimeSpan.FromSeconds(1), _sleeps);

        _hw.CpuTemp = 84;
        _hw.GpuTemp = 61;
        _c.Tick();
        Assert.Equal("SetDuty 179 64", _hw.Calls[^1]);
        Assert.Equal(1, _log.Count("温度读数恢复正常"));
    }

    [Fact]
    public void 单次异常读数重读后恢复_不交还EC()
    {
        _c.Tick();
        _hw.Queue((1, 1));
        var s = _c.Tick();
        Assert.Equal(FanState.Running, s.State);
        Assert.DoesNotContain("SetAuto", _hw.Calls);
        Assert.Single(_sleeps);
    }

    [Fact]
    public void 超过上限的温度同样无效()
    {
        _c.Tick();
        _hw.CpuTemp = 120;
        Assert.Equal(FanState.InvalidReading, _c.Tick().State);
        Assert.Equal("SetAuto", _hw.Calls[^1]);
    }

    [Fact]
    public void 温度跳变超过30度时重读一次()
    {
        _hw.CpuTemp = 50;
        _c.Tick();
        _hw.Queue((85, 55));
        _hw.CpuTemp = 50;
        _c.Tick();
        Assert.Single(_sleeps);
        Assert.Equal(3, _hw.Reads);
    }

    [Fact]
    public void 强制冷却优先于接管开关_降到目标温度后自动结束()
    {
        _c.Config.TakeOver = false;
        _c.ForcedCooling = true;
        _hw.CpuTemp = 60;
        _hw.GpuTemp = 45;
        var s = _c.Tick();
        Assert.Equal("SetDuty 242 242", _hw.Calls[^1]);
        Assert.True(s.ForcedCooling);

        _hw.CpuTemp = 49;
        s = _c.Tick();
        Assert.False(_c.ForcedCooling);
        Assert.False(s.ForcedCooling);
        Assert.Equal("SetAuto", _hw.Calls[^1]);
        Assert.Equal(1, _log.Count("强制冷却结束"));
    }

    [Fact]
    public void 线性模式按插值写入()
    {
        _c.Config.Linear = true;
        _hw.CpuTemp = 62;
        _hw.GpuTemp = 64;
        var s = _c.Tick();
        Assert.Equal((27, 29), (s.CpuTargetPercent, s.GpuTargetPercent));
        Assert.Equal("SetDuty 69 74", _hw.Calls[^1]);
    }

    [Fact]
    public void ResetToAuto无条件交还()
    {
        _c.ResetToAuto();
        Assert.Equal(["SetAuto"], _hw.Calls);
    }
}
