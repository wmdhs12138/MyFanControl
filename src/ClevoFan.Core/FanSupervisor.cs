using Microsoft.Extensions.Logging;

namespace ClevoFan.Core;

/// <summary>
/// 控制循环和安全状态：睡眠时交还 EC、检测旧版程序、出错时交还 EC、配置更新。
/// 所有硬件访问都在 <c>_gate</c> 锁内串行执行，电源事件、配置更新可以从任意线程调用。
/// </summary>
public sealed class FanSupervisor
{
    /// <summary>只收到睡眠通知、醒着超过这么久仍无唤醒通知（例如睡眠失败）时，自动恢复控制。</summary>
    public static readonly TimeSpan SuspendTimeout = TimeSpan.FromMinutes(2);

    private readonly FanController _controller;
    private readonly ConfigStore? _store;
    private readonly ILogger _logger;
    private readonly Func<bool> _legacyRunning;
    private readonly Func<long> _awakeMs;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _suspended;
    private long _suspendedAt;
    private bool _blockedReported;
    private bool _errorReported;
    private FanStatus _status;

    /// <param name="legacyRunning">旧版 MyFanControl 是否在运行。</param>
    /// <param name="awakeMs">不含睡眠时间的单调时钟（毫秒）。</param>
    public FanSupervisor(FanController controller, FanConfig config, ConfigStore? store, ILogger logger,
        Func<bool> legacyRunning, Func<long> awakeMs)
    {
        _controller = controller;
        _controller.Config = config.Clone();
        _store = store;
        _logger = logger;
        _legacyRunning = legacyRunning;
        _awakeMs = awakeMs;
        _status = controller.Status(FanState.Starting);
    }

    public FanStatus Status => Volatile.Read(ref _status);

    public FanConfig Config
    {
        get { lock (_gate) return _controller.Config.Clone(); }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            try
            {
                _controller.ResetToAuto();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "启动时交还 EC 自动控制失败");
            }
        }
        _logger.LogInformation("控制循环开始，硬件接口 {Backend}", _controller.BackendName);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Cycle();
                try
                {
                    await _wake.WaitAsync(TimeSpan.FromSeconds(Config.UpdateIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_gate)
                TryHandBack("停止时");
            _logger.LogInformation("控制循环结束");
        }
    }

    /// <summary>执行一轮控制，由 <see cref="RunAsync"/> 定时调用，测试中可直接调用。</summary>
    public void Cycle()
    {
        lock (_gate)
        {
            if (_suspended)
            {
                if (_awakeMs() - _suspendedAt < SuspendTimeout.TotalMilliseconds)
                {
                    SetStatus(_controller.Status(FanState.Suspended));
                    return;
                }
                _suspended = false;
                _logger.LogWarning("睡眠通知后醒着超过 {Minutes} 分钟仍未收到唤醒通知，自动恢复控制", SuspendTimeout.TotalMinutes);
            }

            if (_legacyRunning())
            {
                if (!_blockedReported)
                {
                    _blockedReported = true;
                    _logger.LogWarning("检测到旧版 MyFanControl 正在运行，为避免两个程序同时控制风扇，暂停接管");
                }
                TryHandBack("检测到旧版程序时");
                const string blocked = "旧版 MyFanControl 正在运行，已暂停接管";
                try
                {
                    //只读不写，让托盘仍能显示温度
                    SetStatus(_controller.Observe(FanState.Blocked, blocked));
                }
                catch (Exception)
                {
                    SetStatus(_controller.Status(FanState.Blocked, message: blocked));
                }
                return;
            }
            if (_blockedReported)
            {
                _blockedReported = false;
                _logger.LogInformation("旧版 MyFanControl 已退出，恢复控制");
            }

            try
            {
                SetStatus(_controller.Tick());
                if (_errorReported)
                {
                    _errorReported = false;
                    _logger.LogInformation("硬件访问恢复正常");
                }
            }
            catch (Exception e)
            {
                if (!_errorReported)
                {
                    _errorReported = true;
                    _logger.LogError(e, "访问硬件出错");
                }
                TryHandBack("出错后");
                SetStatus(_controller.Status(FanState.Error, message: e.Message));
            }
        }
    }

    /// <summary>系统即将睡眠：交还 EC 自动控制，睡眠期间不访问硬件。</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            if (_suspended)
                return;
            TryHandBack("睡眠前");
            _suspended = true;
            _suspendedAt = _awakeMs();
            SetStatus(_controller.Status(FanState.Suspended));
            _logger.LogInformation("系统即将睡眠，已交还 EC 自动控制");
        }
    }

    /// <summary>系统已唤醒：立即恢复控制。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_suspended)
                return;
            _suspended = false;
            _logger.LogInformation("系统已唤醒，恢复控制");
        }
        Wake();
    }

    public void SetForcedCooling(bool on)
    {
        lock (_gate)
        {
            if (_controller.ForcedCooling == on)
                return;
            _controller.ForcedCooling = on;
            _logger.LogInformation(on ? "开启强制冷却" : "关闭强制冷却");
        }
        Wake();
    }

    /// <summary>校验、保存并应用新配置。配置无效时抛出 <see cref="ArgumentException"/>。</summary>
    public void UpdateConfig(FanConfig config)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors), nameof(config));
        lock (_gate)
        {
            _store?.Save(config);
            _controller.Config = config.Clone();
            _logger.LogInformation("配置已更新：接管 {TakeOver}，线性 {Linear}，CPU [{Cpu}]，GPU [{Gpu}]",
                config.TakeOver, config.Linear, string.Join(",", config.CpuCurve), string.Join(",", config.GpuCurve));
        }
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private void TryHandBack(string when)
    {
        try
        {
            _controller.HandBack();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{When}交还 EC 自动控制失败", when);
        }
    }

    private void SetStatus(FanStatus status) => Volatile.Write(ref _status, status);
}
