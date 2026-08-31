namespace FlashTrans.Core;

/// <summary>
/// 标注参数的取值范围和工具条上摆的那几档。
///
/// 为什么单独拎出来：同一个数字有三处要认——工具条上调、设置页上调、读配置时夹。
/// 三处各写一遍 Math.Clamp 的话，工具条能调到 24 而设置页只到 12，
/// 用户在工具条上调粗的线，一进设置页就被那个滑块悄悄改回去了。
/// </summary>
public static class CaptureLimits
{
    public const double MinPenWidth = 1;
    public const double MaxPenWidth = 24;

    public const double MinFontSize = 8;
    public const double MaxFontSize = 120;

    public const int MinMosaicBlock = 4;
    public const int MaxMosaicBlock = 64;

    /// <summary>粗细那一排预设。挨着的几档差一个像素，粗的那头跨大步——细线要精调，粗线不用。</summary>
    public static readonly double[] PenWidths = [1, 2, 3, 5, 8, 12];

    /// <summary>字号预设。跨度按「小注解 → 标题」排，中间那几档才是常用的。</summary>
    public static readonly double[] FontSizes = [14, 18, 24, 32, 48];

    /// <summary>马赛克格子预设。</summary>
    public static readonly int[] MosaicBlocks = [8, 12, 20, 32];

    public static double ClampPenWidth(double v) => Math.Clamp(v, MinPenWidth, MaxPenWidth);
    public static double ClampFontSize(double v) => Math.Clamp(v, MinFontSize, MaxFontSize);
    public static int ClampMosaicBlock(int v) => Math.Clamp(v, MinMosaicBlock, MaxMosaicBlock);

    /// <summary>
    /// 老版本里字号是从粗细算出来的（没有独立字号这个设置）。
    /// 升级时拿它把用户当时看到的字号接过来，见 SettingsService.Migrate——
    /// 直接用新默认值的话，老用户升上来会发现自己的文字标注忽然变大了一号。
    /// </summary>
    public static double FontSizeForWidth(double penWidth) => Math.Max(12, penWidth * 5);
}
