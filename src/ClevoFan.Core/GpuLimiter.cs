using Microsoft.Extensions.Logging;

namespace ClevoFan.Core;

/// <summary>
/// 按配置限制 GPU 最高频率。由 <see cref="FanSupervisor"/> 每轮调用 <see cref="Tick"/>，不是线程安全的。
/// 只在独显通电时操作，不为限频唤醒它；独显从断电变为通电、系统唤醒后重新应用，因为驱动可能已丢弃之前的设置。
/// </summary>
public sealed class GpuLimiter(IGpuBackend gpu, ILogger logger)
{
    /// <summary>应用失败后等待这么多轮再重试，避免每轮都报错。</summary>
    public const int RetryTicks = 15;

    private int? _desired;
    private bool _desiredKnown;
    private int? _applied;
    private bool _pending;
    private bool _wasOn;
    private int _backoff;
    private string? _message;
    private string? _reported;

    public void Tick(FanConfig config)
    {
        int? desired = config.GpuClockLimitEnabled ? config.GpuMaxClockMHz : null;
        if (!_desiredKnown || desired != _desired)
        {
            _desired = desired;
            _desiredKnown = true;
            _pending = true;
            _backoff = 0;
        }

        bool on = gpu.IsPoweredOn();
        if (on && !_wasOn && _applied is not null)
            _pending = true;
        _wasOn = on;

        if (!_pending)
            return;
        if (_backoff > 0)
        {
            _backoff--;
            return;
        }

        int? target = _desired;
        string? invalid = null;
        if (target is int mhz && (mhz < gpu.MinClockMHz || mhz > gpu.MaxClockMHz))
        {
            invalid = $"GPU 频率限制 {mhz} MHz 超出显卡支持的 {gpu.MinClockMHz}-{gpu.MaxClockMHz} MHz，未应用";
            target = null;
        }

        if (target == _applied && !(target is not null && on))
        {
            //已经是目标状态（例如未限频且不需要限频），无需访问显卡
            _pending = false;
            SetMessage(invalid, LogLevel.Warning);
            return;
        }
        if (!on)
        {
            //不为限频唤醒独显，等它下次通电时再应用
            SetMessage(invalid ?? (target is null ? null : $"独显未通电，将在通电时限制为 {target} MHz"), LogLevel.Information);
            return;
        }

        try
        {
            if (target is int limit)
            {
                int actual = gpu.LockMaxClock(limit);
                if (_applied != actual)
                    logger.LogInformation("GPU 最高频率限制为 {Limit} MHz", actual);
                _applied = actual;
            }
            else
            {
                gpu.ResetClocks();
                logger.LogInformation("已解除 GPU 频率限制");
                _applied = null;
            }
            _pending = false;
            SetMessage(invalid, LogLevel.Warning);
        }
        catch (Exception e)
        {
            _backoff = RetryTicks;
            SetMessage("GPU 限频失败：" + e.Message, LogLevel.Error);
        }
    }

    /// <summary>系统唤醒后显卡驱动可能已重置，下次通电时重新应用。</summary>
    public void OnResume()
    {
        if (_applied is not null)
            _pending = true;
        _wasOn = false;
        _backoff = 0;
    }

    /// <summary>服务停止时解除限频，恢复驱动默认（与原程序退出时一致）。</summary>
    public void Stop()
    {
        if (_applied is null)
            return;
        try
        {
            gpu.ResetClocks();
            logger.LogInformation("已解除 GPU 频率限制");
        }
        catch (Exception e)
        {
            logger.LogError(e, "解除 GPU 频率限制失败");
        }
        _applied = null;
    }

    public FanStatus Describe(FanStatus status) => status with
    {
        GpuName = gpu.Name,
        GpuMinClockMHz = gpu.MinClockMHz,
        GpuMaxClockMHz = gpu.MaxClockMHz,
        GpuClockLimitMHz = _applied,
        GpuMessage = _message,
    };

    public GpuLiveStatus ReadLive()
    {
        if (!gpu.IsPoweredOn())
            return new GpuLiveStatus(false, null, null);
        var (clock, utilization) = gpu.ReadLive();
        return new GpuLiveStatus(true, clock, utilization);
    }

    //同一条消息只记录一次日志
    private void SetMessage(string? message, LogLevel level)
    {
        _message = message;
        if (message is not null && message != _reported)
            logger.Log(level, "{Message}", message);
        _reported = message;
    }
}
