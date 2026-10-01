using ClevoFan.Core;

namespace ClevoFan.Service;

public sealed class FanWorker(FanSupervisor supervisor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //让出线程，避免第一轮硬件访问阻塞服务启动
        await Task.Yield();
        await supervisor.RunAsync(stoppingToken);
    }
}
