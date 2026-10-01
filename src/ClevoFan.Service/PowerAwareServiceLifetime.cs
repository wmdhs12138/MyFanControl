using System.ServiceProcess;
using ClevoFan.Core;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace ClevoFan.Service;

/// <summary>在默认的 Windows 服务生命周期上增加电源事件处理：睡眠前交还 EC 自动控制，唤醒后恢复。</summary>
public sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    /// <summary>
    /// 自定义控制码，用于在不真正睡眠的情况下测试：sc.exe control ClevoFan 200（模拟睡眠）/ 201（模拟唤醒）。
    /// </summary>
    public const int SimulateSuspend = 200;
    public const int SimulateResume = 201;

    private readonly FanSupervisor _supervisor;
    private readonly ILogger _logger;

    public PowerAwareServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, FanSupervisor supervisor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _supervisor = supervisor;
        _logger = loggerFactory.CreateLogger("ClevoFan");
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _logger.LogInformation("收到系统睡眠通知");
                _supervisor.Suspend();
                break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend:
                _logger.LogInformation("收到系统唤醒通知（{Status}）", powerStatus);
                _supervisor.Resume();
                break;
        }
        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnCustomCommand(int command)
    {
        switch (command)
        {
            case SimulateSuspend:
                _logger.LogInformation("收到模拟睡眠控制码");
                _supervisor.Suspend();
                break;
            case SimulateResume:
                _logger.LogInformation("收到模拟唤醒控制码");
                _supervisor.Resume();
                break;
        }
        base.OnCustomCommand(command);
    }
}
