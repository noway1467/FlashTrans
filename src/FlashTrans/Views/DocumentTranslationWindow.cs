using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

public partial class DocumentTranslationWindow : Window
{
    readonly DocumentHistoryService _history;
    readonly LangPicker _from = new(includeAuto: true);
    readonly LangPicker _to = new();
    TranslationDocument? _document;
    CancellationTokenSource? _cts;
    bool _busy, _closeWhenIdle, _readyToSave, _saving, _syncDefault, _syncOptions;
    string _outputDirectory = "", _outputPath = "", _translatedTarget = "";
    DocumentHistoryEntry? _attempt;
    int _generation;
    ProviderConfig? SelectedProvider => (Providers.SelectedItem as ComboBoxItem)?.Tag as ProviderConfig;

    public DocumentTranslationWindow() : this(new DocumentHistoryService(SettingsService.Instance.ConfigDir)) { }
    internal DocumentTranslationWindow(DocumentHistoryService history)
    {
        _history = history;
        InitializeComponent();
        HeaderIcon.Content = UiKit.Icon(UiKit.IconDocument, 17);
        DropIcon.Content = UiKit.Icon(UiKit.IconDocument, 23);
        WindowTools.Children.Add(UiKit.IconButton(UiKit.IconMinimize, "最小化", (_, _) => WindowState = WindowState.Minimized));
        WindowTools.Children.Add(UiKit.IconButton(UiKit.IconClose, "关闭", (_, _) => Close()));
        RefreshHost.Content = UiKit.IconButton(UiKit.IconRefresh, "刷新翻译源", (_, _) => RefreshProviders());
        ResetFolderHost.Content = UiKit.IconButton(UiKit.IconRefresh, "使用源文件目录", (_, _) => SetOutputDirectory(""));
        FromHost.Content = _from; ToHost.Content = _to;
        var settings = SettingsService.Instance.Current;
        _from.SelectedCode = settings.SourceLang;
        _to.SelectedCode = string.IsNullOrWhiteSpace(settings.DocumentTargetLang) ? settings.TargetLang : settings.DocumentTargetLang;
        _from.SelectionChanged += _ => InvalidateTranslation();
        _to.SelectionChanged += RememberTargetLanguage;
        _syncOptions = true;
        BatchCharactersBox.Text = settings.DocumentBatchCharacters.ToString();
        RequestDelayBox.Text = settings.DocumentRequestDelayMs.ToString();
        TimeoutBox.Text = settings.DocumentTimeoutSeconds.ToString();
        _syncOptions = false;
        SetOutputDirectory(settings.DocumentOutputDirectory);
        RefreshProviders(); RefreshHistory(); UpdateButtons();
        Loaded += (_, _) =>
        {
            // 高 DPI/小屏时仍让操作按钮留在工作区内，主体可滚动。
            MaxHeight = SystemParameters.WorkArea.Height;
            Height = Math.Min(Height, MaxHeight);
        };
        SizeChanged += (_, _) =>
        {
            HistoryColumn.Width = new GridLength(ActualWidth < 850 ? 240 : 278);
            FileTitle.MaxWidth = Math.Max(240, ActualWidth - HistoryColumn.Width.Value - 110);
        };
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true; _closeWhenIdle = true; _cts?.Cancel();
            SetStatus("正在取消，完成清理后关闭…");
        };
    }

    void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not FrameworkElement) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    async void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = DocumentTranslation.FileFilter, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await LoadFileAsync(picker.FileName);
    }
    internal bool CanDrop(IDataObject data) => !_busy && data.GetDataPresent(DataFormats.FileDrop)
        && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && DocumentTranslation.Supports(paths[0]);
    void OnDragOver(object sender, DragEventArgs e)
    {
        var accepted = CanDrop(e.Data);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
        DropArea.SetResourceReference(Control.BorderBrushProperty, accepted ? "Accent" : "BorderStrong");
    }
    void OnDragLeave(object sender, DragEventArgs e) => DropArea.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");
    async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; OnDragLeave(sender, e);
        if (CanDrop(e.Data)) await LoadFileAsync(((string[])e.Data.GetData(DataFormats.FileDrop))[0]);
    }
    void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择译文输出文件夹", Multiselect = false };
        if (Directory.Exists(_outputDirectory)) picker.InitialDirectory = _outputDirectory;
        if (picker.ShowDialog(this) == true) SetOutputDirectory(picker.FolderName);
    }
    internal void SetOutputDirectory(string directory)
    {
        _outputDirectory = directory;
        OutputFolder.Text = directory.Length == 0 ? "源文件所在目录" : directory;
        OutputFolder.ToolTip = directory.Length == 0 ? _document is null ? "自动保存到原文件旁边" : Path.GetDirectoryName(_document.SourcePath) : directory;
        _syncDefault = true;
        DefaultFolder.IsChecked = string.Equals(directory, SettingsService.Instance.Current.DocumentOutputDirectory, StringComparison.OrdinalIgnoreCase);
        _syncDefault = false;
    }
    void OnDefaultFolderChanged(object sender, RoutedEventArgs e)
    {
        if (_syncDefault || !IsInitialized) return;
        if (DefaultFolder.IsChecked == true && _outputDirectory.Length > 0 && !Directory.Exists(_outputDirectory))
        { SetStatus("目录不可用，请重新选择。 "); SetOutputDirectory(SettingsService.Instance.Current.DocumentOutputDirectory); return; }
        SettingsService.Instance.Current.DocumentOutputDirectory = DefaultFolder.IsChecked == true ? _outputDirectory : "";
        SettingsService.Instance.Save();
        SetStatus(DefaultFolder.IsChecked == true ? "已设为默认输出位置" : "默认恢复为源文件目录");
    }
    void OnProviderChanged(object sender, SelectionChangedEventArgs e) { if (!IsInitialized) return; InvalidateTranslation(); UpdatePrivacy(); }
    void OnOptionChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncOptions || !IsInitialized) return;
        InvalidateTranslation();
        if (TryReadOptions(out var batch, out var delay, out var timeout))
        {
            SettingsService.Instance.Current.DocumentBatchCharacters = batch;
            SettingsService.Instance.Current.DocumentRequestDelayMs = delay;
            SettingsService.Instance.Current.DocumentTimeoutSeconds = timeout;
            SettingsService.Instance.Save();
        }
    }
    bool TryReadOptions(out int batchCharacters, out int requestDelayMs, out int timeoutSeconds)
    {
        var batchOk = int.TryParse(BatchCharactersBox.Text, out batchCharacters)
            && batchCharacters is >= DocumentTranslation.MinBatchCharacters and <= DocumentTranslation.MaxBatchCharacters;
        var delayOk = int.TryParse(RequestDelayBox.Text, out requestDelayMs)
            && requestDelayMs is >= 0 and <= DocumentTranslation.MaxRequestDelayMs;
        var timeoutOk = int.TryParse(TimeoutBox.Text, out timeoutSeconds)
            && timeoutSeconds is >= DocumentTranslation.MinTimeoutSeconds and <= DocumentTranslation.MaxTimeoutSeconds;
        return batchOk && delayOk && timeoutOk;
    }
    void InvalidateTranslation() { _readyToSave = false; _outputPath = ""; UpdateButtons(); }
    void RememberTargetLanguage(string code)
    {
        _to.SelectedCode = code;
        // 只保存文件窗口的偏好，不广播主窗口语言变更，也不干扰在途划词翻译。
        SettingsService.Instance.Current.DocumentTargetLang = code;
        SettingsService.Instance.Save();
        InvalidateTranslation();
    }
    void RefreshProviders()
    {
        var id = SelectedProvider?.Id ?? SettingsService.Instance.Current.PrimaryProviderId;
        var sources = SettingsService.Instance.Current.EnabledProviders.Select(p => p.Clone()).ToArray();
        Providers.Items.Clear();
        foreach (var source in sources) Providers.Items.Add(new ComboBoxItem { Content = source.DisplayName, Tag = source });
        Providers.SelectedIndex = sources.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(sources, p => p.Id == id));
        UpdatePrivacy(); UpdateButtons();
    }
    void UpdatePrivacy()
    {
        if (SelectedProvider is not { } cfg) { Privacy.Text = "请先在设置中启用翻译源，再刷新。"; return; }
        var local = cfg.Kind == ProviderKind.OpenAiCompat && Uri.TryCreate(cfg.Options.GetValueOrDefault("baseUrl"), UriKind.Absolute, out var uri) && uri.IsLoopback;
        Privacy.Text = local ? "本机接口 · 不自动切源" : "正文发往所选翻译源 · 不自动切源";
        Privacy.ToolTip = local ? "请确认服务使用已下载的本地模型，未转发云端。" : "使用在线服务可能产生费用；敏感文件请选择本地模型。";
    }
    void UpdateButtons()
    {
        if (StartButton is null) return;
        DropArea.IsEnabled = Options.IsEnabled = !_busy;
        StartButton.IsEnabled = !_busy && _document is { Count: > 0 } && SelectedProvider is not null;
        StartButton.Content = _readyToSave ? "重试保存" : _document is { Completed: > 0, IsComplete: false } ? "继续翻译" : "开始翻译";
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        OpenResult.Visibility = _outputPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.IsEnabled = !_busy;
    }
    void SetStatus(string message)
    {
        Status.Text = message; Status.ToolTip = message;
    }
    void Begin()
    {
        _generation++; _cts = new(); _busy = true; UpdateButtons();
    }
    void End()
    {
        _generation++; _cts?.Dispose(); _cts = null; _busy = _saving = false;
        Progress.IsIndeterminate = false; UpdateButtons();
        if (_closeWhenIdle) Close();
    }
    void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();
    async void OnStart(object sender, RoutedEventArgs e) => await TranslateFileAsync();
    void OnOpenResult(object sender, RoutedEventArgs e) => OpenPath(_outputPath, false);

    internal async Task LoadFileAsync(string path)
    {
        if (_busy) return;
        if (!TryReadOptions(out var batchCharacters, out _, out _))
        { SetStatus("每批 200–2000 字符，请求间隔 0–5000 毫秒，单段超时 10–600 秒。"); return; }
        Begin(); _document = null; _readyToSave = false; _outputPath = ""; _attempt = null;
        FileTitle.Text = Path.GetFileName(path); FileTitle.ToolTip = path;
        FileSummary.Text = "正在读取…"; Warnings.Text = ""; SetStatus("正在本机解析文件…"); Progress.IsIndeterminate = true;
        try
        {
            var ct = _cts!.Token;
            _document = await Task.Run(() => DocumentTranslation.Load(path, batchCharacters, ct), ct);
            FileSummary.Text = $"{_document.CharacterCount:N0} 字符 · {_document.Count:N0} 段 · 点击更换";
            Warnings.Text = string.Join("\n\n", _document.Warnings);
            Progress.Value = 0;
            SetStatus(_document.Count == 0 ? "没有可翻译的正文" : "完成后自动保存译文副本");
            SetOutputDirectory(_outputDirectory);
        }
        catch (OperationCanceledException) { SetStatus("已取消读取"); FileSummary.Text = "点击选择其他文件"; }
        catch (Exception ex) { SetStatus("读取失败：" + ex.Message); FileSummary.Text = "点击选择其他文件"; }
        finally { End(); }
    }

    internal async Task TranslateFileAsync()
    {
        if (_busy || _document is null || SelectedProvider is not { } provider) return;
        if (!TryReadOptions(out var batchCharacters, out var requestDelayMs, out var timeoutSeconds))
        { SetStatus("每批 200–2000 字符，请求间隔 0–5000 毫秒，单段超时 10–600 秒。"); return; }
        var directory = _outputDirectory.Length == 0 ? Path.GetDirectoryName(_document.SourcePath)! : _outputDirectory;
        if (!Directory.Exists(directory)) { SetStatus("输出目录不可用，请重新选择文件夹。"); return; }
        var from = _from.SelectedCode; var target = _to.SelectedCode;
        Begin(); _outputPath = "";
        if (!_readyToSave) _attempt = new DocumentHistoryEntry { SourcePath = _document.SourcePath, ProviderName = provider.DisplayName, SourceLanguage = from, TargetLanguage = target };
        var generation = _generation;
        var progress = new Progress<DocumentProgress>(p =>
        {
            if (!_busy || _saving || _generation != generation) return;
            Progress.Maximum = Math.Max(1, p.Total); Progress.Value = p.Completed;
            SetStatus($"{p.Message} · {p.Completed}/{p.Total}");
        });
        try
        {
            var ct = _cts!.Token;
            if (!_readyToSave)
            {
                if (_document.BatchCharacters != batchCharacters)
                {
                    SetStatus("分批设置已改变，正在重新分段…");
                    var source = _document.SourcePath;
                    _document = await Task.Run(() => DocumentTranslation.Load(source, batchCharacters, ct), ct);
                    _attempt = new DocumentHistoryEntry { SourcePath = source, ProviderName = provider.DisplayName, SourceLanguage = from, TargetLanguage = target };
                }
                await Task.Run(() => DocumentTranslation.TranslateAsync(_document, provider, from, target, progress, ct, timeoutSeconds, requestDelayMs), ct);
                _readyToSave = true; _translatedTarget = target;
            }
            _saving = true; SetStatus("正在生成译文副本…"); Progress.IsIndeterminate = true;
            _outputPath = await Task.Run(() => _document.SaveCopy(directory, _translatedTarget, ct), ct);
            _readyToSave = false;
            Progress.Maximum = Math.Max(1, _document.Count); Progress.Value = _document.Completed;
            SetStatus("已保存 · " + Path.GetFileName(_outputPath));
            Record("Completed");
        }
        catch (OperationCanceledException)
        {
            SetStatus(_readyToSave ? "保存已取消，可重试保存" : $"已取消 · {_document.Completed}/{_document.Count} 段，可继续");
            Record("Cancelled");
        }
        catch (Exception ex)
        {
            SetStatus((_readyToSave ? "保存失败，可换目录重试：" : "翻译失败：") + ex.Message);
            Record(_readyToSave ? "SaveFailed" : "Failed");
        }
        finally { End(); }
    }
    void Record(string status)
    {
        if (_attempt is null || _document is null) return;
        _attempt = _attempt with { Time = DateTimeOffset.Now, Status = status, OutputPath = _outputPath, Completed = _document.Completed, Total = _document.Count };
        try { _history.Add(_attempt); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus(Status.Text + "；历史未保存"); Status.ToolTip += "\n" + ex.Message; }
        RefreshHistory();
    }
    void RefreshHistory()
    {
        HistoryList.Children.Clear();
        var entries = _history.Load();
        if (entries.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(8, 58, 8, 0) };
            empty.Children.Add(UiKit.Icon(UiKit.IconDocument, 28));
            var text = UiKit.Text(_history.LoadWarning ?? "还没有翻译记录", 12, "TextDim", wrap: true);
            text.TextAlignment = TextAlignment.Center; text.Margin = new Thickness(0, 14, 0, 0); empty.Children.Add(text);
            HistoryList.Children.Add(empty); return;
        }
        foreach (var entry in entries)
        {
            var content = new StackPanel();
            var title = UiKit.Text(Path.GetFileName(entry.SourcePath), 12.5, "Text", FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = entry.SourcePath; content.Children.Add(title);
            var completed = entry.Status == "Completed";
            var state = entry.Status switch { "Completed" => "已保存", "Cancelled" => "已取消", "SaveFailed" => "保存失败", _ => "翻译失败" };
            var meta = UiKit.Text($"{Languages.NameOf(entry.TargetLanguage)} · {state}", 11, completed ? "Success" : "TextDim");
            meta.Margin = new Thickness(0, 7, 0, 3); content.Children.Add(meta);
            var when = UiKit.Text(entry.Time.LocalDateTime.ToString("MM-dd HH:mm") + " · " + entry.ProviderName, 10.5, "TextFaint");
            when.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(when);
            var actions = new WrapPanel { Margin = new Thickness(-5, 7, 0, 0) };
            if (completed)
            {
                actions.Children.Add(ActionButton("打开译文", () => OpenPath(entry.OutputPath, false)));
                actions.Children.Add(UiKit.IconButton(UiKit.IconFolder, "打开输出文件夹", (_, _) => OpenPath(entry.OutputPath, true)));
            }
            actions.Children.Add(ActionButton("重新翻译", async () =>
            {
                if (!File.Exists(entry.SourcePath)) { SetStatus("原文件已移动或删除，请重新选择。"); return; }
                _from.SelectedCode = entry.SourceLanguage; RememberTargetLanguage(entry.TargetLanguage);
                InvalidateTranslation(); await LoadFileAsync(entry.SourcePath);
            }));
            content.Children.Add(actions);
            var card = new Border { Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9) };
            card.SetResourceReference(Border.BackgroundProperty, "Bg"); HistoryList.Children.Add(card);
        }
    }
    static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, FontSize = 11, Padding = new Thickness(6, 3, 6, 3) };
        button.SetResourceReference(StyleProperty, "GhostBtn"); button.Click += (_, _) => action(); return button;
    }
    void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!AppDialog.Confirm(this, "清空翻译历史？", "只清空记录，不删除原文件或译文。", "清空")) return;
        try { _history.Clear(); RefreshHistory(); }
        catch (Exception ex) { SetStatus("清空失败：" + ex.Message); }
    }
    void OpenPath(string path, bool folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new FileNotFoundException();
            var target = folder ? Path.GetDirectoryName(path)! : path;
            if (folder ? !Directory.Exists(target) : !File.Exists(target)) throw new FileNotFoundException();
            if (!folder && !DocumentTranslation.Supports(target)) throw new InvalidDataException("不支持打开该类型。");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (FileNotFoundException) { SetStatus("文件或目录已移动、删除，请重新选择。 "); }
        catch (Exception ex) { SetStatus("无法打开：" + ex.Message); }
    }
}
