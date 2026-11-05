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

public sealed class DesktopShelfSettings
{
    public const double MinWidth = 200, MaxWidth = 800, MinHeight = 160, MaxHeight = 1000;

    public bool Enabled { get; set; }
    public bool SnapToEdge { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public bool Topmost { get; set; }
    public ShelfTheme Theme { get; set; } = ShelfTheme.Dark;
    public ShelfSort Sort { get; set; } = ShelfSort.Manual;
    public double CornerRadius { get; set; } = 12;
    // 只调整底色，避免文件图标和文字随背景一起变淡。
    public double BackgroundOpacity { get; set; } = 0.92;
    public int IconSize { get; set; } = 32;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 340;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public ShelfEdge Edge { get; set; } = ShelfEdge.Right;
    public List<DesktopShelfEntry> Items { get; set; } = [];
}
