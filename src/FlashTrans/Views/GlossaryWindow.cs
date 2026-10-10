using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>本地两列编辑器；TBX 作为资料导入，编辑后另存 TSV，不覆盖原词库。</summary>
internal sealed class GlossaryWindow : Window
{
    internal string SavedPath { get; private set; } = "";
    internal GlossaryWindow(DocumentGlossary glossary, string path)
    {
        Title = "用户术语表"; Width = 660; Height = 480; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Text");
        var root = new DockPanel { Margin = new Thickness(18) };
        var hint = UiKit.Text("每行两列：原词〈Tab〉译词。英文不区分大小写、整词匹配；长词优先。术语按精确译法保护，不自动变形。", 12, "TextDim", wrap: true);
        hint.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var status = UiKit.Text("", 12, "TextDim", wrap: true);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(status);
        var save = new Button { Content = "校验并另存 TSV", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        save.SetResourceReference(StyleProperty, "PrimaryBtn"); bottom.Children.Add(save);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var box = new TextBox
        {
            Text = glossary.ToTsv(), AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(10),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        root.Children.Add(box); Content = root;
        save.Click += (_, _) =>
        {
            string? temporary = null;
            try
            {
                var parsed = DocumentGlossary.ParseDelimited(box.Text);
                var picker = new Microsoft.Win32.SaveFileDialog
                { Filter = "TSV 术语表|*.tsv", DefaultExt = ".tsv", AddExtension = true, OverwritePrompt = true,
                    FileName = path.Length == 0 ? "我的术语表.tsv" : Path.GetFileNameWithoutExtension(path) + ".tsv" };
                if (picker.ShowDialog(this) != true) return;
                if (!Path.GetExtension(picker.FileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("请另存为 .tsv 文件，不能覆盖原 TBX/CSV 词库。");
                temporary = Path.Combine(Path.GetDirectoryName(picker.FileName)!, ".flashtrans-terms-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporary, parsed.ToTsv(), new UTF8Encoding(false));
                File.Move(temporary, picker.FileName, overwrite: true);
                SavedPath = picker.FileName; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
        };
    }
}
