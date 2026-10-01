using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClevoFan.Core;

namespace ClevoFan.Service;

/// <summary>
/// 命名管道服务端，供托盘程序读取状态和修改配置。
/// 所有已登录用户都可以连接（托盘以普通用户身份运行）；只有 SYSTEM 和管理员可以创建管道实例，防止被其他程序冒充。
/// </summary>
public sealed class PipeServer(FanSupervisor supervisor, ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly ILogger _logger = loggerFactory.CreateLogger("ClevoFan.Pipe");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(FanPipe.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = HandleAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception e)
            {
                pipe?.Dispose();
                _logger.LogError(e, "命名管道出错，1 秒后重试");
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                    return;
                var response = Handle(line);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, FanPipe.JsonOptions).AsMemory(), cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                //客户端断开或超时，忽略
            }
            catch (Exception e)
            {
                //本任务不被等待，异常必须在这里记录，否则会被静默丢弃
                _logger.LogError(e, "处理托盘请求出错");
            }
        }
    }

    private PipeResponse Handle(string line)
    {
        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(line, FanPipe.JsonOptions);
        }
        catch (JsonException e)
        {
            return PipeResponse.Fail("请求格式错误：" + e.Message);
        }
        try
        {
            switch (request?.Command)
            {
                case FanPipe.Commands.Status:
                    return new PipeResponse { Ok = true, Status = supervisor.Status };
                case FanPipe.Commands.GetConfig:
                    return new PipeResponse { Ok = true, Config = supervisor.Config };
                case FanPipe.Commands.SetConfig when request.Config is { } config:
                    supervisor.UpdateConfig(config);
                    return new PipeResponse { Ok = true, Config = supervisor.Config };
                case FanPipe.Commands.ForceCooling when request.On is { } on:
                    supervisor.SetForcedCooling(on);
                    return new PipeResponse { Ok = true };
                default:
                    return PipeResponse.Fail("未知命令：" + request?.Command);
            }
        }
        catch (ArgumentException e)
        {
            return PipeResponse.Fail(e.Message);
        }
        catch (IOException e)
        {
            _logger.LogError(e, "保存配置失败");
            return PipeResponse.Fail("保存配置失败：" + e.Message);
        }
    }
}
