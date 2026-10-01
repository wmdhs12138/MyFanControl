using ClevoFan.Core;

namespace ClevoFan.Tests;

public class ExternalControlTests
{
    private readonly FakeBackend _hw = new();
    private readonly ListLogger _log = new();
    private long _now;
    private string? _others;
    private readonly FanController _c;

    public ExternalControlTests()
    {
        _c = new FanController(_hw, _log, _ => { }, () => _now, () => _others) { Config = new FanConfig { TakeOver = true } };
    }

    //接管并让 EC 确认写入，然后被“其他程序”改动一次，下一轮本程序发现并重新写入
    private void ConfirmThenOverride()
    {
        _c.Tick();
        _hw.External(128, 128);
        _c.Tick();
    }

    private void Advance(TimeSpan t) => _now += (long)t.TotalMilliseconds;

    [Fact]
    public void 五分钟内被改动三次判定为冲突()
    {
        _c.Tick();
        for (int i = 0; i < ExternalControlDetector.Threshold - 1; i++)
        {
            ConfirmThenOverride();
            Assert.Null(_c.Status(FanState.Running).ExternalControlMessage);
            Advance(TimeSpan.FromSeconds(30));
        }
        ConfirmThenOverride();
        var message = _c.Status(FanState.Running).ExternalControlMessage;
        Assert.Contains("检测到其他程序也在控制风扇", message);
        Assert.Contains("本程序设定 25%/18%，读回 50%/50%", message);
        Assert.Equal(1, _log.Count("检测到其他程序也在控制风扇"));

        //冲突持续时不重复警告
        ConfirmThenOverride();
        Assert.Equal(1, _log.Count("检测到其他程序也在控制风扇"));
    }

    [Fact]
    public void 写入后的生效延迟不算外部修改()
    {
        _hw.ApplyWrites = false;
        _c.Tick();
        _c.Tick();
        _c.Tick();
        _hw.ApplyWrites = true;
        _c.Tick();
        _c.Tick();
        Assert.Equal(0, _log.Count("检测到其他程序"));
    }

    [Fact]
    public void 本程序调整目标不算外部修改()
    {
        foreach (var temp in new[] { 60, 66, 72, 78, 84, 88, 70, 62 })
        {
            _hw.CpuTemp = temp;
            _c.Tick();
            _c.Tick();
        }
        Assert.True(_hw.Calls.Count > 4);
        Assert.Null(_c.Status(FanState.Running).ExternalControlMessage);
    }

    [Fact]
    public void 分散在窗口之外的改动不累计()
    {
        _c.Tick();
        for (int i = 0; i < 6; i++)
        {
            ConfirmThenOverride();
            Advance(TimeSpan.FromMinutes(3));
        }
        Assert.Null(_c.Status(FanState.Running).ExternalControlMessage);
    }

    [Fact]
    public void 十分钟无改动后解除()
    {
        _c.Tick();
        for (int i = 0; i < ExternalControlDetector.Threshold; i++)
            ConfirmThenOverride();
        Assert.NotNull(_c.Status(FanState.Running).ExternalControlMessage);

        Advance(ExternalControlDetector.ClearAfter - TimeSpan.FromSeconds(1));
        _c.Tick();
        Assert.NotNull(_c.Status(FanState.Running).ExternalControlMessage);
        Advance(TimeSpan.FromSeconds(2));
        _c.Tick();
        Assert.Null(_c.Status(FanState.Running).ExternalControlMessage);
        Assert.Equal(1, _log.Count("未检测到其他程序修改风扇负载"));
    }

    [Fact]
    public void 提示中说明正在运行的Control_Center()
    {
        _others = "Control Center（CC40.exe）正在运行";
        _c.Tick();
        for (int i = 0; i < ExternalControlDetector.Threshold; i++)
            ConfirmThenOverride();
        Assert.Contains("Control Center（CC40.exe）正在运行", _c.Status(FanState.Running).ExternalControlMessage);
    }

    [Fact]
    public void 未接管时不检测()
    {
        _c.Config.TakeOver = false;
        for (int i = 0; i < 5; i++)
        {
            _c.Tick();
            _hw.External(30 + i, 40 + i);
        }
        Assert.Null(_c.Status(FanState.Running).ExternalControlMessage);
    }

    [Fact]
    public void 负载百分比与EC原始值往返一致()
    {
        //否则写入后读回永远对不上，既无法确认生效，也会每轮重写
        for (int p = 0; p <= 100; p++)
            Assert.Equal(p, FanReading.DutyToPercent(FanReading.PercentToDuty(p)));
    }
}
