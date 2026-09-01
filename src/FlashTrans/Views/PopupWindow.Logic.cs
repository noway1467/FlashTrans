using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class PopupWindow
{
    // ------------------------------------------------------------- 对外 API

    public void ShowFor(string text, Point? anchor)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        // 新翻译一来，之前收起的那个就作废：按收起快捷键该拿到这一次的结果，
        // 而不是把上一段译文又捞回来。
        _stashed = false;
        _text = text;
        _result.CopyRequested -= CopyText;
        _result.CopyRequested += CopyText;
        _result.LookupRequested -= Lookup;
        _result.LookupRequested += Lookup;

        ApplyLayoutSettings();
        RebuildTabs();
        UpdateLangLabel();
        _dictBtn.Visibility = S.EudicEnabled && IsWordLike(text) ? Visibility.Visible : Visibility.Collapsed;

        Place(anchor);
        Show();
        // Place 只能按估高摆，高度自适应下真实高度要等测量完才知道，
        // 上限放宽以后差得更多，摆完得照实际高度再收一次边。
        ClampIntoWorkArea();
        Activate();
        Run();
    }

    /// <summary>
    /// 设置里的上限能调到比屏幕还高（4K / 竖屏用得上），落到具体某块屏上还得再收一次，
    /// 不然窗口会伸到任务栏底下去。
    /// </summary>
    double CapHeight(Rect work) =>
        work.Height > 0 ? Math.Max(MinHeight, Math.Min(S.PopupMaxHeight, work.Height - 16))
                        : S.PopupMaxHeight;

    /// <summary>窗口测完真实高度后，把越过工作区的部分收回来。</summary>
    void ClampIntoWorkArea()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : MinHeight;
        if (double.IsNaN(w) || double.IsNaN(h)) return;

        var work = ScreenHelper.WorkAreaOf(this);
        if (work.Width <= 0 || work.Height <= 0) return;

        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - w));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - h));
    }

    /// <summary>用户主动关掉。内容作废，收起快捷键不会再把它叫回来。</summary>
    public void ClosePopup()
    {
        _cts?.Cancel();
        _stashed = false;
        if (!IsVisible) return;
        PersistGeometry();
        Hide();
    }

    /// <summary>
    /// 临时收起。位置、译文、正在跑的翻译都留着（不取消 _cts，
    /// 收起期间译完了，叫回来直接就是结果），等快捷键再叫回来。
    /// </summary>
    public void StashPopup()
    {
        if (!IsVisible) return;
        PersistGeometry();
        _stashed = true;
        Hide();
    }

    /// <summary>有没有被收起、等着叫回来的内容。</summary>
    public bool CanRestore => _stashed && _text.Length > 0 && !IsVisible;

    /// <summary>把收起的弹窗原样放回来，不重译。没有可恢复的就返回 false。</summary>
    public bool RestorePopup()
    {
        if (!CanRestore) return false;
        _stashed = false;
        Show();
        Activate();
        return true;
    }

    public void OnSettingsChanged()
    {
        ApplyLayoutSettings();
        RebuildTabs();
        UpdateLangLabel();
        if (_batch is not null) _result.ShowBatch(_batch, _aggregate);
    }

    void ApplyLayoutSettings()
    {
        // 程序自己套用宽度，不能算成"用户拖过"
        _applyingWidth = true;
        Width = S.PopupWidth;
        _applyingWidth = false;
        MaxHeight = CapHeight(ScreenHelper.WorkAreaOf(this));
        var hk = HotkeySpec.Parse(S.HkTogglePopup).ToString();
        _stashBtn.ToolTip = hk.Length == 0 ? "临时收起（在设置里配快捷键叫回）" : $"临时收起（{hk} 叫回）";
        Opacity = S.Opacity;
        FontSize = S.FontSize;
        if (!string.IsNullOrWhiteSpace(S.FontFamily))
        {
            try { FontFamily = new FontFamily(S.FontFamily); }
            catch { /* 字体名无效就用默认 */ }
        }
        _tabsHost.Visibility = S.PopupShowTabs ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------- 定位

    void Place(Point? anchor)
    {
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);

        MaxHeight = CapHeight(work);

        var h = _userResized && !double.IsNaN(S.PopupHeight) ? S.PopupHeight : Math.Min(360, MaxHeight);

        if (_userResized && !double.IsNaN(S.PopupHeight))
        {
            SizeToContent = SizeToContent.Manual;
            Height = Math.Min(S.PopupHeight, work.Height);
        }
        else SizeToContent = SizeToContent.Height;

        switch (S.PopupPlace)
        {
            case PopupPlace.ScreenCenter:
                Left = work.Left + (work.Width - Width) / 2;
                Top = work.Top + (work.Height - h) / 2;
                break;

            case PopupPlace.RememberLast when ScreenHelper.IsOnScreen(S.PopupLeft, S.PopupTop, Width, h):
                Left = S.PopupLeft;
                Top = S.PopupTop;
                break;

            default:
                var pt = anchor ?? ScreenHelper.ToDip(ScreenHelper.CursorPos(), this);
                var (left, top) = ScreenHelper.PlaceNear(pt, Width, h, work, gap: 12);
                Left = left;
                Top = top;
                break;
        }
    }

    void PersistGeometry()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        S.PopupLeft = Left;
        S.PopupTop = Top;
        // 宽高分开记：只拖宽度也要留住，不必先把高度调成手动。
        // 宽度取 ActualWidth——用户看到的是它，Width 可能还停在上一次请求的值。
        var w = ActualWidth > 0 ? ActualWidth : Width;
        if (_widthPinned && w >= MinWidth) S.PopupWidth = Math.Round(w);
        if (_userResized) S.PopupHeight = Math.Round(ActualHeight);
        SettingsService.Instance.Save();
    }

    // ------------------------------------------------------------- 标签页

    void RebuildTabs()
    {
        _tabStrip.Children.Clear();
        _tabs.Clear();

        if (S.AggregateTab) AddTab("聚合", null, "同时显示所有已启用源", "#7C8794", false);

        foreach (var cfg in S.EnabledProviders)
        {
            var meta = ProviderMeta.Get(cfg.Kind);
            var err = Engine.ConfigErrorOf(cfg);
            AddTab(cfg.DisplayName, cfg.Id, err ?? meta.FreeNote, meta.Accent, err is not null);
        }

        if (_tabs.Count == 0)
        {
            _tabStrip.Children.Add(UiKit.Text("没有启用的翻译源", 11, "TextFaint"));
            return;
        }

        var target = _tabs.FirstOrDefault(t => t.ProviderId == _activeProviderId);
        if (_aggregate && S.AggregateTab) target = _tabs.First(t => t.ProviderId is null);
        if (target.Btn is null) target = _tabs.FirstOrDefault(t => t.ProviderId == S.PrimaryProviderId);
        if (target.Btn is null) target = _tabs.FirstOrDefault(t => t.Btn.IsEnabled);
        if (target.Btn is null) target = _tabs[0];

        Select(target.Btn, translate: false);
    }

    void AddTab(string label, string? providerId, string tooltip, string accent, bool disabled)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (providerId is not null)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5, Height = 5,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(ThemeService.Parse(accent, Colors.SteelBlue)),
                Opacity = disabled || Engine.Health.IsCoolingDown(providerId) ? 0.35 : 1,
            };
            content.Children.Add(dot);
        }
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var btn = new ToggleButton
        {
            Content = content,
            ToolTip = tooltip,
            IsEnabled = !disabled,
            FontSize = 11,
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            Focusable = false,
        };
        btn.SetResourceReference(StyleProperty, "ProviderTab");
        btn.PreviewMouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            Select((ToggleButton)s, translate: true);
        };

        _tabStrip.Children.Add(btn);
        _tabs.Add((btn, providerId));
    }

    void Select(ToggleButton btn, bool translate)
    {
        foreach (var (b, _) in _tabs) b.IsChecked = ReferenceEquals(b, btn);
        var entry = _tabs.First(t => ReferenceEquals(t.Btn, btn));
        _activeProviderId = entry.ProviderId;
        _aggregate = entry.ProviderId is null;
        if (translate) Run();
    }

    void SelectNext()
    {
        if (_tabs.Count == 0) return;
        var idx = _tabs.FindIndex(t => t.Btn.IsChecked == true);
        for (int i = 1; i <= _tabs.Count; i++)
        {
            var next = _tabs[(idx + i + _tabs.Count) % _tabs.Count];
            if (next.Btn.IsEnabled) { Select(next.Btn, translate: true); return; }
        }
    }

    // ------------------------------------------------------------- 翻译

    void Run()
    {
        if (_text.Length == 0) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts);
    }

    async Task RunAsync(CancellationTokenSource cts)
    {
        try
        {
            TranslateBatch batch;
            if (_aggregate)
            {
                // 边到边显示：占位卡先摆好，谁先回来谁先填。收尾用 EndAggregate 只补说明，
                // 不重画——重画会丢掉已选中的文本，弹窗还会跟着闪一下高度。
                batch = await Engine.AggregateAsync(_text, cts.Token,
                    onStart: _result.BeginAggregate,
                    onResult: _result.UpdateOne);
                if (cts.IsCancellationRequested) return;
                _batch = batch;
                _result.EndAggregate(batch);
                UpdateStatus(batch);
                return;
            }

            var cfg = S.Find(_activeProviderId ?? S.PrimaryProviderId);
            if (cfg is null)
            {
                _result.ShowMessage("这个翻译源已被删除");
                return;
            }
            _result.ShowLoading(cfg.DisplayName);
            batch = await Engine.SingleAsync(cfg.Id, _text,
                perParagraph: S.Bilingual && S.BilingualByParagraph, cts.Token);

            if (cts.IsCancellationRequested) return;
            _batch = batch;
            _result.ShowBatch(batch, _aggregate);
            SyncTab(batch);
            UpdateStatus(batch);
        }
        catch (OperationCanceledException) { /* 换源或关闭了 */ }
        catch (Exception ex)
        {
            Log.Error("弹窗翻译失败", ex);
            _result.ShowMessage("翻译出错：" + ex.Message, dim: false);
        }
    }

    void SyncTab(TranslateBatch batch)
    {
        if (_aggregate) return;
        var winner = batch.Results.LastOrDefault(r => r.Ok);
        if (winner is null || winner.ProviderId == _activeProviderId) return;
        var tab = _tabs.FirstOrDefault(t => t.ProviderId == winner.ProviderId);
        if (tab.Btn is null) return;
        foreach (var (b, _) in _tabs) b.IsChecked = ReferenceEquals(b, tab.Btn);
        _activeProviderId = winner.ProviderId;
    }

    void UpdateStatus(TranslateBatch batch)
    {
        var parts = new List<string>();
        var ok = batch.Results.Count(r => r.Ok);
        if (_aggregate) parts.Add($"{ok}/{batch.Results.Count} 个源");
        else
        {
            var winner = batch.Results.LastOrDefault(r => r.Ok) ?? batch.Results.LastOrDefault();
            if (winner is not null) parts.Add(winner.ProviderName);
        }
        if (batch.TotalMs > 0 && S.ShowLatency) parts.Add(batch.TotalMs + " ms");
        if (batch.Notes.Count > 0) parts.Add(batch.Notes[^1]);
        parts.Add("Ctrl+Tab 换源 · Esc 关闭");

        _status.Text = string.Join("  ·  ", parts);
        _status.SetResourceReference(ForegroundProperty, ok == 0 ? "Danger" : "TextFaint");
    }

    // ------------------------------------------------------------- 交互

    void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Escape) { e.Handled = true; ClosePopup(); }
        else if (ctrl && e.Key == Key.Tab) { e.Handled = true; SelectNext(); }
        else if (ctrl && e.Key == Key.C) { e.Handled = true; CopyResult(); }
        else if (ctrl && e.Key == Key.D) { e.Handled = true; Lookup(_text); }
        else if (ctrl && e.Key == Key.E) { e.Handled = true; _host.ExpandToMain(_text); }
        else if (e.Key is >= Key.D1 and <= Key.D9 && ctrl)
        {
            var idx = e.Key - Key.D1;
            if (idx < _tabs.Count && _tabs[idx].Btn.IsEnabled)
            {
                Select(_tabs[idx].Btn, translate: true);
                e.Handled = true;
            }
        }
    }

    void ShowLangMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _langLabel, Placement = PlacementMode.Bottom };
        foreach (var code in S.FavoriteLangs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem
            {
                Header = Languages.NameOf(code),
                IsCheckable = true,
                IsChecked = string.Equals(code, S.TargetLang, StringComparison.OrdinalIgnoreCase),
            };
            var c = code;
            item.Click += (_, _) =>
            {
                S.TargetLang = c;
                S.MultiTargetEnabled = false;
                SettingsService.Instance.Touch();
                UpdateLangLabel();
                Run();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = "更多语言…" };
        more.Click += (_, _) => _host.ShowSettings("languages");
        menu.Items.Add(more);
        menu.IsOpen = true;
    }

    void UpdateLangLabel()
    {
        var from = S.SourceLang == Languages.Auto ? "自动检测" : Languages.NameOf(S.SourceLang);
        var to = S.MultiTargetEnabled && S.MultiTargets.Count > 0
            ? string.Join("/", S.MultiTargets.Take(3).Select(Languages.NameOf))
            : Languages.NameOf(S.TargetLang);
        _langLabel.Text = $"{from} → {to}";
    }

    void CopyResult()
    {
        if (_batch is null) return;
        CopyText(ResultView.AllText(_batch));
    }

    void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SelectionReader.SetText(text);
        _status.Text = "已复制到剪贴板";
    }

    void Lookup(string word)
    {
        _status.Text = EudicService.Lookup(word.Trim())
            ? "已发送到欧路词典"
            : "没有找到欧路词典，请在设置里指定路径";
    }

    static bool IsWordLike(string text) =>
        text.Length <= 40 && !text.Contains('\n') && text.Count(char.IsWhiteSpace) <= 2;
}
