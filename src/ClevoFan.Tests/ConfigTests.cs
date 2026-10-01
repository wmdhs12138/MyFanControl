using System.Buffers.Binary;
using ClevoFan.Core;

namespace ClevoFan.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClevoFanTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static byte[] LegacyBytes(params int[] values)
    {
        var data = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), values[i]);
        return data;
    }

    [Fact]
    public void LegacyConfig_导入原程序配置()
    {
        int[] cpu = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];
        int[] gpu = [90, 75, 65, 50, 30, 25, 20, 15, 15, 15];
        //过渡温度 3、间隔 2、线性 0、接管 1、强制冷却 50、GPU 限频 0、频率 2590
        var config = LegacyConfig.Parse(LegacyBytes([.. cpu, .. gpu, 3, 2, 0, 1, 50, 0, 2590]));
        Assert.Equal(cpu, config.CpuCurve);
        Assert.Equal(gpu, config.GpuCurve);
        Assert.Equal(3, config.TransitionTemp);
        Assert.Equal(2, config.UpdateIntervalSeconds);
        Assert.False(config.Linear);
        Assert.True(config.TakeOver);
        Assert.Equal(50, config.ForceCoolingTemp);
        //原程序限频开关关闭：不限频，忽略残留的频率值
        Assert.False(config.GpuClockLimitEnabled);
        Assert.Equal(0, config.GpuMaxClockMHz);
    }

    [Fact]
    public void LegacyConfig_导入GPU限频()
    {
        int[] curve = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];
        var config = LegacyConfig.Parse(LegacyBytes([.. curve, .. curve, 3, 2, 0, 1, 50, 1, 1200]));
        Assert.True(config.GpuClockLimitEnabled);
        Assert.Equal(1200, config.GpuMaxClockMHz);
    }

    [Fact]
    public void LegacyConfig_限频开启但频率为0表示不限频()
    {
        int[] curve = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];
        Assert.False(LegacyConfig.Parse(LegacyBytes([.. curve, .. curve, 3, 2, 0, 1, 50, 1, 0])).GpuClockLimitEnabled);
    }

    [Fact]
    public void ConfigStore_读取版本1配置_新字段取默认值并以当前版本保存()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "v1.json");
        File.WriteAllText(path, """{ "Version": 1, "TakeOver": true }""");
        var store = new ConfigStore(path);
        var config = store.Load()!;
        Assert.True(config.TakeOver);
        Assert.False(config.GpuClockLimitEnabled);
        store.Save(config);
        Assert.Equal(FanConfig.CurrentVersion, store.Load()!.Version);
    }

    [Theory]
    [InlineData(true, 99, false)]
    [InlineData(true, 900, true)]
    [InlineData(false, 0, true)]
    public void GPU限频配置校验(bool enabled, int mhz, bool valid)
    {
        var config = new FanConfig { GpuClockLimitEnabled = enabled, GpuMaxClockMHz = mhz };
        Assert.Equal(valid, config.Validate().Count == 0);
    }

    [Fact]
    public void LegacyConfig_长度不对时报错()
    {
        Assert.Throws<FormatException>(() => LegacyConfig.Parse(new byte[100]));
    }

    [Fact]
    public void LegacyConfig_数值越界时报错()
    {
        int[] curve = [95, 80, 70, 55, 35, 30, 25, 18, 18, 180];
        Assert.Throws<FormatException>(() => LegacyConfig.Parse(LegacyBytes([.. curve, .. curve, 3, 2, 0, 1, 50, 0, 0])));
    }

    [Fact]
    public void ConfigStore_保存后读回相同()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var config = new FanConfig { TakeOver = true, Linear = true, TransitionTemp = 5, CpuCurve = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10] };
        store.Save(config);
        var loaded = store.Load()!;
        Assert.Equal(config.CpuCurve, loaded.CpuCurve);
        Assert.Equal(config.GpuCurve, loaded.GpuCurve);
        Assert.True(loaded.TakeOver);
        Assert.True(loaded.Linear);
        Assert.Equal(5, loaded.TransitionTemp);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void ConfigStore_文件不存在时返回null()
    {
        Assert.Null(new ConfigStore(Path.Combine(_dir, "none.json")).Load());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("""{ "Version": 99 }""")]
    [InlineData("""{ "UpdateIntervalSeconds": 0 }""")]
    public void ConfigStore_内容无效时报错(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, json);
        Assert.Throws<FormatException>(() => new ConfigStore(path).Load());
    }

    [Fact]
    public void ConfigStore_拒绝保存无效配置()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Throws<ArgumentException>(() => store.Save(new FanConfig { CpuCurve = [1, 2, 3] }));
        Assert.False(File.Exists(store.Path));
    }
}
