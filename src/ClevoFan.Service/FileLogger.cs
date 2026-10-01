using System.Text;

namespace ClevoFan.Service;

/// <summary>写入 UTF-8 文本日志，超过 1MB 时转存为 .old，只保留一份旧日志。</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long MaxSize = 1024 * 1024;
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSize)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                //日志写不进去时不影响风扇控制
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var level = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "FATAL",
                _ => "INFO",
            };
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {formatter(state, exception)}");
            if (exception is not null)
                sb.Append(Environment.NewLine).Append(exception);
            sb.Append(Environment.NewLine);
            provider.Write(sb.ToString());
        }
    }
}
