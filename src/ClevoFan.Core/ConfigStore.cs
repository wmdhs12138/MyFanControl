using System.Text.Json;

namespace ClevoFan.Core;

/// <summary>以 JSON 保存配置。写入时先写临时文件再替换，避免写到一半断电导致配置损坏。</summary>
public sealed class ConfigStore(string path)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>读取配置；文件不存在时返回 null，内容无效时抛出 <see cref="FormatException"/>。</summary>
    public FanConfig? Load()
    {
        if (!File.Exists(Path))
            return null;
        FanConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<FanConfig>(File.ReadAllText(Path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new FormatException($"配置文件 {Path} 格式错误：{e.Message}", e);
        }
        if (config is null)
            throw new FormatException($"配置文件 {Path} 为空");
        if (config.Version > FanConfig.CurrentVersion)
            throw new FormatException($"配置文件版本 {config.Version} 高于本程序支持的 {FanConfig.CurrentVersion}");
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new FormatException($"配置文件 {Path} 内容无效：" + string.Join("；", errors));
        return config;
    }

    public void Save(FanConfig config)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("；", errors), nameof(config));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var copy = config.Clone();
        copy.Version = FanConfig.CurrentVersion;
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }
}
