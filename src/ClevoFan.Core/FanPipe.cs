using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClevoFan.Core;

/// <summary>服务与托盘程序之间的命名管道协议：每行一个 JSON 请求，对应一行 JSON 回复。</summary>
public static class FanPipe
{
    public const string Name = "ClevoFan";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static class Commands
    {
        public const string Status = "status";
        public const string GetConfig = "getConfig";
        public const string SetConfig = "setConfig";
        public const string ForceCooling = "forceCooling";

        /// <summary>GPU 实时频率和利用率，供设置窗口打开时使用；独显未通电时不读取。</summary>
        public const string GpuLive = "gpuLive";
    }
}

public sealed class PipeRequest
{
    public string Command { get; set; } = "";
    public FanConfig? Config { get; set; }
    public bool? On { get; set; }
}

public sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public FanStatus? Status { get; set; }
    public FanConfig? Config { get; set; }
    public GpuLiveStatus? GpuLive { get; set; }

    public static PipeResponse Fail(string error) => new() { Error = error };
}

/// <summary>托盘程序用来连接服务的客户端，每次请求建立一次连接。</summary>
public static class FanServiceClient
{
    public static async Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", FanPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        var utf8 = new UTF8Encoding(false);
        await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, FanPipe.JsonOptions).AsMemory(), cts.Token);
        var line = await reader.ReadLineAsync(cts.Token) ?? throw new IOException("服务关闭了连接");
        return JsonSerializer.Deserialize<PipeResponse>(line, FanPipe.JsonOptions) ?? throw new IOException("服务返回了空回复");
    }
}
