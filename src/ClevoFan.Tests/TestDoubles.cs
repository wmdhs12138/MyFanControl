using ClevoFan.Core;
using Microsoft.Extensions.Logging;

namespace ClevoFan.Tests;

/// <summary>模拟硬件：读数由测试设定，记录所有写操作；默认写入后读回的负载随之变化。</summary>
internal sealed class FakeBackend : IFanBackend
{
    private readonly Queue<FanReading> _queued = new();

    public int CpuTemp { get; set; } = 60;
    public int GpuTemp { get; set; } = 55;
    public int AutoDuty { get; set; } = 128;
    public int? CpuDuty { get; private set; }
    public int? GpuDuty { get; private set; }

    /// <summary>为 false 时模拟 EC 不执行写入，读回的负载不变。</summary>
    public bool ApplyWrites { get; set; } = true;

    public Exception? ThrowOnRead { get; set; }
    public List<string> Calls { get; } = [];
    public int Reads { get; private set; }

    public string Name => "Fake";

    /// <summary>接下来的读取依次返回这些温度（负载照常），用完后恢复为 CpuTemp/GpuTemp。</summary>
    public void Queue(params (int Cpu, int Gpu)[] temps)
    {
        foreach (var (cpu, gpu) in temps)
            _queued.Enqueue(new FanReading(cpu, gpu, 0, 0, 0, 0));
    }

    public FanReading Read()
    {
        Reads++;
        if (ThrowOnRead is { } e)
            throw e;
        int cpu = CpuTemp, gpu = GpuTemp;
        if (_queued.TryDequeue(out var q))
            (cpu, gpu) = (q.CpuTemp, q.GpuTemp);
        return new FanReading(cpu, gpu, CpuDuty ?? AutoDuty, GpuDuty ?? AutoDuty, 1335, 1746);
    }

    public void SetDuty(int cpuDuty, int gpuDuty)
    {
        Calls.Add($"SetDuty {cpuDuty} {gpuDuty}");
        if (ApplyWrites)
            (CpuDuty, GpuDuty) = (cpuDuty, gpuDuty);
    }

    public void SetAuto()
    {
        Calls.Add("SetAuto");
        (CpuDuty, GpuDuty) = (null, null);
    }

    public void Dispose()
    {
    }
}

/// <summary>模拟 GPU：频率范围 300-2100 MHz，记录所有操作。</summary>
internal sealed class FakeGpu : IGpuBackend
{
    public bool PoweredOn { get; set; } = true;
    public Exception? ThrowOnLock { get; set; }
    public List<string> Calls { get; } = [];

    public string Name => "Fake GPU";
    public int MinClockMHz => 300;
    public int MaxClockMHz => 2100;

    public bool IsPoweredOn() => PoweredOn;

    /// <summary>模拟按 15 MHz 档位向下取整。</summary>
    public int LockMaxClock(int maxMHz)
    {
        if (ThrowOnLock is { } e)
            throw e;
        Calls.Add($"Lock {maxMHz}");
        return maxMHz - (maxMHz - MinClockMHz) % 15;
    }

    public void ResetClocks() => Calls.Add("Reset");

    public (int ClockMHz, int UtilizationPercent) ReadLive()
    {
        Calls.Add("ReadLive");
        return (900, 50);
    }

    public void Dispose()
    {
    }
}

internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    public int Count(string contains) => Entries.Count(e => e.Message.Contains(contains));
}
