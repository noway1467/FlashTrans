using System.Text.Json.Serialization;

namespace FlashTrans.Core;

/// <summary>一个翻译源实例（同一类型可添加多个，比如两个不同的 AI 模型）。</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ProviderKind Kind { get; set; }
    /// <summary>标签页显示名，留空用默认名。</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; } = 6000;
    /// <summary>接口参数，键见 <see cref="ProviderMeta"/>。</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? ProviderMeta.Get(Kind).DisplayName : Name;

    public ProviderConfig Clone() => new()
    {
        Id = Id, Kind = Kind, Name = Name, Enabled = Enabled, TimeoutMs = TimeoutMs,
        Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase)
    };

    public static ProviderConfig Create(ProviderKind kind, string? name = null)
    {
        var meta = ProviderMeta.Get(kind);
        var cfg = new ProviderConfig { Kind = kind, Name = name ?? "" };
        foreach (var f in meta.Fields)
            if (!string.IsNullOrEmpty(f.Default)) cfg.Options[f.Key] = f.Default!;
        return cfg;
    }
}

public enum FieldKind { Text, Secret, Number, Bool, Multiline }

public sealed record ProviderField(
    string Key, string Label, FieldKind Kind = FieldKind.Text,
    bool Required = false, string? Default = null, string? Hint = null);

public sealed record ProviderMetaInfo(
    ProviderKind Kind,
    string DisplayName,
    string Badge,
    string Accent,
    string FreeNote,
    string? DocUrl,
    ProviderField[] Fields,
    bool DefaultEnabled = false,
    bool NeedsKey = true,
    bool IsAi = false);

/// <summary>OpenAI 兼容接口的常见服务预设，添加源时一键填好地址与模型。</summary>
public sealed record AiPreset(string Name, string BaseUrl, string Model, string Note);
