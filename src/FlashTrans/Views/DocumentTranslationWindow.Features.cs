using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlashTrans.Services;

namespace FlashTrans.Views;

public partial class DocumentTranslationWindow
{
    CheckBox _rememberProgress = null!, _bilingualOutput = null!;
    TextBlock _glossaryLabel = null!;
    DocumentCheckpointStore _checkpoints = null!;
    string _glossaryPath = "";

    void BuildDocumentFeatures()
    {
        _checkpoints = new DocumentCheckpointStore(_history.DirectoryPath);
        var settings = SettingsService.Instance.Current;
        _glossaryPath = settings.DocumentGlossaryPath;
        _rememberProgress = new CheckBox
        {
            Content = "保存续译进度（本机加密）", IsChecked = settings.DocumentRememberProgress,
            ToolTip = "包含已译正文，使用当前 Windows 账户加密；换电脑/账户不能恢复。默认不保存。",
            Margin = new Thickness(0, 3, 0, 8)
        };
        _bilingualOutput = new CheckBox
        {
            Content = "输出双语对照 HTML", IsChecked = settings.DocumentBilingualOutput,
            ToolTip = "逐段正文对照，独立文件，可在浏览器阅读/打印；不保留原图片、表格、公式和版式。关闭时输出原格式的译文副本。",
            Margin = new Thickness(0, 0, 0, 8)
        };
        _rememberProgress.Click += (_, _) =>
        { SettingsService.Instance.Current.DocumentRememberProgress = _rememberProgress.IsChecked == true; SettingsService.Instance.Save(); };
        _bilingualOutput.Click += (_, _) =>
        { SettingsService.Instance.Current.DocumentBilingualOutput = _bilingualOutput.IsChecked == true; SettingsService.Instance.Save(); };
        DocumentExtras.Children.Add(_rememberProgress);
        DocumentExtras.Children.Add(_bilingualOutput);
        _glossaryLabel = UiKit.Text("", 11, "TextDim", wrap: true);
        DocumentExtras.Children.Add(_glossaryLabel);
        var actions = new WrapPanel { Margin = new Thickness(-5, 4, 0, 0) };
        actions.Children.Add(ActionButton("选择术语表", ChooseGlossary));
        actions.Children.Add(ActionButton("新建 / 编辑", EditGlossary));
        actions.Children.Add(ActionButton("不使用术语", () => SelectGlossary("")));
        actions.Children.Add(ActionButton("公开术语资料", () => SettingsWindow.OpenUrl("https://learn.microsoft.com/en-us/globalization/reference/microsoft-terminology")));
        actions.Children.Add(ActionButton("清除本机进度", () =>
        {
            if (!AppDialog.Confirm(this, "清除全部续译进度？", "仅删除本机加密进度，不删除文档、历史或术语表；当前窗口的内存结果不变。", "清除")) return;
            try { SetStatus($"已清除 {_checkpoints.Clear()} 份进度"); }
            catch (Exception ex) { SetStatus("无法清除进度：" + ex.Message); }
        }));
        DocumentExtras.Children.Add(actions);
        UpdateGlossaryLabel();
    }

    void UpdateGlossaryLabel()
    {
        _glossaryLabel.Text = _glossaryPath.Length == 0 ? "术语表：未选择（支持 TSV、CSV、TBX）" : "术语表：" + Path.GetFileName(_glossaryPath);
        _glossaryLabel.ToolTip = _glossaryPath.Length == 0 ? "下载公开 TBX 后可直接导入；请遵守资料授权。不会自动联网下载或上传正文。" : _glossaryPath;
    }
    void SelectGlossary(string path)
    {
        _glossaryPath = path; SettingsService.Instance.Current.DocumentGlossaryPath = path;
        SettingsService.Instance.Save(); UpdateGlossaryLabel(); InvalidateTranslation();
    }
    async void ChooseGlossary()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "术语表|*.tsv;*.csv;*.tbx", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var from = _from.SelectedCode; var target = _to.SelectedCode;
        Begin(); SetStatus("正在本机读取术语表…");
        try
        {
            var ct = _cts!.Token;
            var glossary = await Task.Run(() => DocumentGlossary.Load(picker.FileName, from, target), ct);
            ct.ThrowIfCancellationRequested();
            SelectGlossary(picker.FileName); SetStatus($"已载入 {glossary.Entries.Count:N0} 条术语；开始前会重新校验文件。" + string.Join(" ", glossary.Warnings));
        }
        catch (OperationCanceledException) { SetStatus("已取消载入术语表，原选择不变"); }
        catch (Exception ex) { SetStatus("术语表未载入：" + ex.Message); }
        finally { End(); }
    }
    void EditGlossary()
    {
        try
        {
            var glossary = DocumentGlossary.Load(_glossaryPath, _from.SelectedCode, _to.SelectedCode);
            var editor = new GlossaryWindow(glossary, _glossaryPath) { Owner = this };
            if (editor.ShowDialog() == true) SelectGlossary(editor.SavedPath);
        }
        catch (Exception ex) { SetStatus("术语编辑失败：" + ex.Message + " 可先点“不使用术语”再新建。"); }
    }
}
