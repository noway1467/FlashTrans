using System.Text.Json.Serialization;

namespace FlashTrans.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ShelfTheme>))]
public enum ShelfTheme { Sky, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter<ShelfSort>))]
public enum ShelfSort { Manual, NameAscending, NameDescending, Type, AddedNewest, MostUsed, RecentlyUsed }

[JsonConverter(typeof(JsonStringEnumConverter<ShelfEdge>))]
public enum ShelfEdge { None, Left, Right, Top, Bottom }

public sealed class DesktopShelfEntry : QuickLaunchEntry
{
    public string StorageId { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    [JsonIgnore] public bool IsStored => !string.IsNullOrEmpty(StorageId);
}

/// <summary>单个收纳窗自己的名称、项目、排序和位置；外观与贴边行为由 DesktopShelfSettings 统一决定。</summary>
public sealed class DesktopShelfBox
{
    public const int MaxNameLength = 24;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ShelfSort Sort { get; set; } = ShelfSort.Manual;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 340;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public ShelfEdge Edge { get; set; } = ShelfEdge.Right;
    public List<DesktopShelfEntry> Items { get; set; } = [];
}

public sealed class DesktopShelfSettings
{
    public const double MinWidth = 200, MaxWidth = 800, MinHeight = 160, MaxHeight = 1000;
    // 每个收纳窗都是常驻的独立窗口，限制数量，避免边缘挤满窄条。
    public const int MaxBoxes = 8;

    public bool Enabled { get; set; }
    public bool SnapToEdge { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public bool Topmost { get; set; }
    public ShelfTheme Theme { get; set; } = ShelfTheme.Dark;
    public double CornerRadius { get; set; } = 12;
    // 只调整底色，避免文件图标和文字随背景一起变淡。
    public double BackgroundOpacity { get; set; } = 0.92;
    public int IconSize { get; set; } = 32;
    public List<DesktopShelfBox> Boxes { get; set; } = [];

    // 1.9.7 只有一个收纳窗，项目和位置直接存在这一层。以下字段只用于读入旧配置：
    // Normalize 把它们并入第一个收纳窗后清空，为 null 时不再写出。
    [JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DesktopShelfEntry>? LegacyItems { get; set; }
    [JsonPropertyName("sort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShelfSort? LegacySort { get; set; }
    [JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyWidth { get; set; }
    [JsonPropertyName("height"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyHeight { get; set; }
    [JsonPropertyName("left"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyLeft { get; set; }
    [JsonPropertyName("top"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LegacyTop { get; set; }
    [JsonPropertyName("edge"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShelfEdge? LegacyEdge { get; set; }
}
