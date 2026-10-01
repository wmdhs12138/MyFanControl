using System.Diagnostics;
using ClevoFan.Core;
using ClevoFan.Hardware;
using ClevoFan.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClevoFan");
var store = new ConfigStore(Path.Combine(dataDir, "config.json"));

//ClevoFan.Service.exe --import-legacy <MyFanControl.cfg>：导入原程序的配置后退出（安装脚本使用）
if (args is ["--import-legacy", var legacyPath])
{
    var config = LegacyConfig.Parse(File.ReadAllBytes(legacyPath));
    store.Save(config);
    Console.WriteLine($"已从 {legacyPath} 导入配置到 {store.Path}（接管控制：{config.TakeOver}）");
    return 0;
}

//ClevoFan.Service.exe --gpu-info：只读显示 GPU 信息，用于诊断（读取实时频率会唤醒未通电的独显）
if (args is ["--gpu-info"])
{
    using var gpu = new NvmlGpuBackend();
    bool on = gpu.IsPoweredOn();
    Console.WriteLine($"name={gpu.Name}\nmin_mhz={gpu.MinClockMHz}\nmax_mhz={gpu.MaxClockMHz}\npowered_on={on}");
    if (on)
    {
        var (clock, utilization) = gpu.ReadLive();
        Console.WriteLine($"clock_mhz={clock}\nutilization={utilization}");
    }
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ClevoFan");
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, PowerAwareServiceLifetime>();
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "service.log")));
if (!WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<IFanBackend>(_ => new ClevoWmiBackend());
builder.Services.AddSingleton<GpuHolder>();
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ClevoFan");
    var controller = new FanController(sp.GetRequiredService<IFanBackend>(), logger);
    var gpu = sp.GetRequiredService<GpuHolder>();
    return new FanSupervisor(controller, LoadConfig(store, logger), store, logger, IsLegacyRunning, NativeMethods.AwakeMilliseconds,
        gpu.Backend is { } backend ? new GpuLimiter(backend, logger) : null, gpu.UnavailableReason);
});
builder.Services.AddHostedService<FanWorker>();
builder.Services.AddHostedService<PipeServer>();

var host = builder.Build();
try
{
    host.Run();
    return 0;
}
catch (Exception e)
{
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ClevoFan").LogCritical(e, "服务异常退出");
    return 1;
}

//配置损坏时不覆盖原文件（改名保留），使用默认配置：默认不接管，风扇由 EC 自动控制
static FanConfig LoadConfig(ConfigStore store, ILogger logger)
{
    try
    {
        if (store.Load() is { } config)
            return config;
        logger.LogInformation("没有配置文件，使用默认配置（不接管）");
    }
    catch (FormatException e)
    {
        var bad = store.Path + ".bad";
        File.Move(store.Path, bad, overwrite: true);
        logger.LogError("{Error}；已改名为 {Bad}，使用默认配置（不接管）", e.Message, bad);
    }
    var defaults = new FanConfig();
    store.Save(defaults);
    return defaults;
}

static bool IsLegacyRunning()
{
    var processes = Process.GetProcessesByName("MyFanControl");
    foreach (var p in processes)
        p.Dispose();
    return processes.Length > 0;
}
