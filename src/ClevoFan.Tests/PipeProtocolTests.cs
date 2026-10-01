using System.Text.Json;
using ClevoFan.Core;

namespace ClevoFan.Tests;

public class PipeProtocolTests
{
    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, FanPipe.JsonOptions), FanPipe.JsonOptions)!;

    [Fact]
    public void 状态往返()
    {
        var status = new FanStatus
        {
            State = FanState.InvalidReading,
            Message = "测试",
            Backend = "Clevo WMI",
            CpuTemp = 80,
            GpuTemp = 60,
            CpuDutyPercent = 70,
            GpuDutyPercent = 25,
            CpuRpm = 1573,
            GpuRpm = null,
            TakenOver = true,
            CpuTargetPercent = 70,
            GpuTargetPercent = null,
            CpuLevel = 8,
            GpuLevel = 4,
            ForcedCooling = true,
        };
        var copy = RoundTrip(new PipeResponse { Ok = true, Status = status }).Status!;
        Assert.Equivalent(status, copy);
        Assert.Contains("\"InvalidReading\"", JsonSerializer.Serialize(status, FanPipe.JsonOptions));
    }

    [Fact]
    public void 配置请求往返()
    {
        var config = new FanConfig { TakeOver = true, Linear = true, CpuCurve = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10] };
        var copy = RoundTrip(new PipeRequest { Command = FanPipe.Commands.SetConfig, Config = config });
        Assert.Equal(FanPipe.Commands.SetConfig, copy.Command);
        Assert.Equivalent(config, copy.Config);
    }
}
