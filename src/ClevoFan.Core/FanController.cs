using Microsoft.Extensions.Logging;

namespace ClevoFan.Core;

/// <summary>
/// 每次 <see cref="Tick"/> 完成一轮控制：读取、校验、按配置计算目标负载并写入。
/// 不是线程安全的，由 <see cref="FanSupervisor"/> 串行调用。
/// </summary>
public sealed class FanController(IFanBackend backend, ILogger logger, Action<TimeSpan>? sleep = null)
{
    public const int ForceCoolingPercent = 95;

    /// <summary>温度相比上一次有效读数跳变超过此值时，等 1 秒重读一次（与原程序相同）。</summary>
    public const int SuspiciousJump = 30;

    /// <summary>连续这么多轮写入后 EC 读回的负载仍与目标不同，记录一次警告。</summary>
    public const int MismatchWarnTicks = 5;

    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private readonly int[] _level = new int[2];
    private readonly int[] _linearTemp = new int[2];
    private FanReading? _lastValid;
    private int? _cpuTarget, _gpuTarget;
    private bool _invalidReported;
    private int _mismatchTicks;

    public FanConfig Config { get; set; } = new();

    /// <summary>强制冷却：两个风扇固定 95%，直到 CPU 和 GPU 都低于设定温度后自动结束。</summary>
    public bool ForcedCooling { get; set; }

    /// <summary>最后一次对硬件的操作是设置负载（而不是交还自动）。</summary>
    public bool TakenOver { get; private set; }

    public string BackendName => backend.Name;

    public FanStatus Tick()
    {
        var reading = Read();
        if (!reading.IsValid)
        {
            //不能据此调节风扇（否则会按低温把风扇降到最低档），交还 EC 自动控制，读数恢复后再接管
            if (!_invalidReported)
            {
                _invalidReported = true;
                logger.LogWarning("温度读数异常（CPU {Cpu}℃，GPU {Gpu}℃），交还 EC 自动控制", reading.CpuTemp, reading.GpuTemp);
            }
            HandBack();
            return Status(FanState.InvalidReading, reading);
        }
        if (_invalidReported)
        {
            _invalidReported = false;
            logger.LogInformation("温度读数恢复正常（CPU {Cpu}℃，GPU {Gpu}℃）", reading.CpuTemp, reading.GpuTemp);
        }
        _lastValid = reading;

        if (ForcedCooling)
        {
            if (reading.CpuTemp >= Config.ForceCoolingTemp || reading.GpuTemp >= Config.ForceCoolingTemp)
            {
                _level[0] = _level[1] = FanConfig.LevelCount;
                Apply(reading, ForceCoolingPercent, ForceCoolingPercent);
                return Status(FanState.Running, reading);
            }
            ForcedCooling = false;
            logger.LogInformation("强制冷却结束：CPU {Cpu}℃、GPU {Gpu}℃ 均已低于 {Target}℃", reading.CpuTemp, reading.GpuTemp, Config.ForceCoolingTemp);
        }

        if (!Config.TakeOver)
        {
            HandBack();
            return Status(FanState.Running, reading);
        }

        int cpu, gpu;
        if (Config.Linear)
        {
            (cpu, _level[0], _linearTemp[0]) = FanCurve.Linear(reading.CpuTemp, Config.CpuCurve, Config.TransitionTemp, _linearTemp[0]);
            (gpu, _level[1], _linearTemp[1]) = FanCurve.Linear(reading.GpuTemp, Config.GpuCurve, Config.TransitionTemp, _linearTemp[1]);
        }
        else
        {
            (cpu, _level[0]) = FanCurve.Step(reading.CpuTemp, Config.CpuCurve, Config.TransitionTemp, _level[0]);
            (gpu, _level[1]) = FanCurve.Step(reading.GpuTemp, Config.GpuCurve, Config.TransitionTemp, _level[1]);
        }
        Apply(reading, cpu, gpu);
        return Status(FanState.Running, reading);
    }

    /// <summary>只读取并报告状态，不做任何控制（例如旧版程序正在运行时）。</summary>
    public FanStatus Observe(FanState state, string? message = null)
    {
        var reading = backend.Read();
        if (reading.IsValid)
            _lastValid = reading;
        return Status(state, reading, message);
    }

    /// <summary>如果当前处于接管状态，交还 EC 自动控制。</summary>
    public void HandBack()
    {
        if (!TakenOver)
            return;
        ResetToAuto();
        logger.LogInformation("已交还 EC 自动控制");
    }

    /// <summary>无条件交还 EC 自动控制，用于启动时进入确定状态（上次可能异常退出，EC 停在手动模式）。</summary>
    public void ResetToAuto()
    {
        backend.SetAuto();
        TakenOver = false;
        _level[0] = _level[1] = 0;
        _cpuTarget = _gpuTarget = null;
        _mismatchTicks = 0;
    }

    /// <summary>生成状态；读数无效时显示上一次的有效读数。</summary>
    public FanStatus Status(FanState state, FanReading? reading = null, string? message = null)
    {
        var r = reading is { IsValid: true } ? reading.Value : _lastValid ?? default;
        return new FanStatus
        {
            State = state,
            Message = message,
            Backend = backend.Name,
            CpuTemp = r.CpuTemp,
            GpuTemp = r.GpuTemp,
            CpuDutyPercent = FanReading.DutyToPercent(r.CpuDuty),
            GpuDutyPercent = FanReading.DutyToPercent(r.GpuDuty),
            CpuRpm = FanReading.Rpm(r.CpuRpmRaw),
            GpuRpm = FanReading.Rpm(r.GpuRpmRaw),
            TakenOver = TakenOver,
            CpuTargetPercent = _cpuTarget,
            GpuTargetPercent = _gpuTarget,
            CpuLevel = _level[0],
            GpuLevel = _level[1],
            ForcedCooling = ForcedCooling,
        };
    }

    private FanReading Read()
    {
        var r = backend.Read();
        bool suspicious = !r.IsValid || _lastValid is { } last &&
            (Math.Abs(r.CpuTemp - last.CpuTemp) > SuspiciousJump || Math.Abs(r.GpuTemp - last.GpuTemp) > SuspiciousJump);
        if (suspicious)
        {
            _sleep(TimeSpan.FromSeconds(1));
            r = backend.Read();
        }
        return r;
    }

    private void Apply(FanReading reading, int cpuPercent, int gpuPercent)
    {
        _cpuTarget = cpuPercent;
        _gpuTarget = gpuPercent;
        bool matches = FanReading.DutyToPercent(reading.CpuDuty) == cpuPercent && FanReading.DutyToPercent(reading.GpuDuty) == gpuPercent;
        if (TakenOver && matches)
        {
            _mismatchTicks = 0;
            return;
        }
        //EC 生效有延迟，写入后立即读回可能仍是旧值，所以只在连续多轮不一致时才警告
        if (TakenOver && ++_mismatchTicks == MismatchWarnTicks)
            logger.LogWarning("已连续 {Ticks} 轮写入负载 {Cpu}%/{Gpu}%，EC 读回仍为 {ReadCpu}%/{ReadGpu}%",
                MismatchWarnTicks, cpuPercent, gpuPercent, FanReading.DutyToPercent(reading.CpuDuty), FanReading.DutyToPercent(reading.GpuDuty));
        backend.SetDuty(FanReading.PercentToDuty(cpuPercent), FanReading.PercentToDuty(gpuPercent));
        if (!TakenOver)
        {
            TakenOver = true;
            _mismatchTicks = 0;
            logger.LogInformation("接管风扇控制：CPU {Cpu}℃ → {CpuPct}%，GPU {Gpu}℃ → {GpuPct}%", reading.CpuTemp, cpuPercent, reading.GpuTemp, gpuPercent);
        }
    }
}
