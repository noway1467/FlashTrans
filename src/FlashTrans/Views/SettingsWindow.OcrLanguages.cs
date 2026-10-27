using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 系统 OCR 语言包：列出已装的，并给常用但 RapidOCR 认不了的脚本一条安装路径。
/// 本程序不自己提权、不自己联网下载——只打开系统设置或复制命令，装不装由用户决定。
/// </summary>
public sealed partial class SettingsWindow
{
    // 只列 RapidOCR 不覆盖的脚本；拉丁语系和中日英已由本地模型识别，不必再装系统包。
    internal static readonly (string Tag, string Name)[] SuggestedOcrLanguages =
        [("ko-KR", "韩语"), ("ru-RU", "俄语"), ("ar-SA", "阿拉伯语"), ("el-GR", "希腊语")];

    internal static string OcrInstallCommand(string tag) =>
        $"Get-WindowsCapability -Online | Where-Object Name -like 'Language.OCR*{tag}*' | Add-WindowsCapability -Online";

    /// <summary>按主语言子标签比对：系统可能报 ko 或 ko-KR，都算装了韩语。</summary>
    internal static bool OcrLanguageInstalled(IEnumerable<string> installed, string tag)
    {
        var primary = tag.Split('-')[0];
        return installed.Any(t => t.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    UIElement[] BuildOcrLanguageRows()
    {
        var installed = OcrService.AvailableLanguages;
        var rows = new List<UIElement>();
        var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var tag in installed) chips.Children.Add(LanguageChip(OcrService.DisplayName(tag)));
        if (installed.Length == 0) chips.Children.Add(UiKit.Text("还没有安装任何系统 OCR 语言包", 12, "TextDim"));
        rows.Add(Field("已安装", chips));

        foreach (var (tag, name) in SuggestedOcrLanguages)
        {
            var ok = OcrLanguageInstalled(installed, tag);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            if (!ok)
            {
                var copy = SmallButton("复制安装命令", () => CopyOcrCommand(tag, name));
                copy.ToolTip = OcrInstallCommand(tag);
                DockPanel.SetDock(copy, Dock.Right);
                row.Children.Add(copy);
            }
            var state = UiKit.Text(ok ? "已安装" : "未安装", 11, ok ? "Success" : "TextFaint");
            var label = UiKit.Row(10, UiKit.Text(name, 12.5), state);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            rows.Add(row);
        }

        var open = SmallButton("打开 Windows 语言设置", () =>
        {
            try { using var _ = Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
            catch (Exception ex) { Toast("无法打开系统设置：" + ex.Message); }
        });
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 6, 0, 0);
        rows.Add(open);
        rows.Add(Hint("命令需在管理员 PowerShell 中运行，会从 Windows 更新下载语言包；装好后重启闪译生效。"));
        return rows.ToArray();
    }

    static Border LanguageChip(string text)
    {
        var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 2, 6, 2), Child = UiKit.Text(text, 11.5, "Text") };
        chip.SetResourceReference(Border.BackgroundProperty, "BgHover");
        return chip;
    }

    void CopyOcrCommand(string tag, string name)
    {
        try
        {
            Clipboard.SetDataObject(OcrInstallCommand(tag), true);
            Toast($"已复制{name}安装命令，请在管理员 PowerShell 中粘贴运行");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        { Toast("剪贴板被其他程序占用，请稍后再试"); }
    }
}
