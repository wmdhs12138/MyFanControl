namespace ClevoFan.Tray;

internal static class Program
{
    /// <param name="args">--settings：启动后立即打开设置窗口。</param>
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ClevoFan.Tray", out bool created);
        if (!created)
            return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(showSettings: args.Contains("--settings")));
    }
}
