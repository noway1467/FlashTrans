using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Providers;
using FlashTrans.Services;

namespace FlashTrans.Views;

public partial class MainWindow
{
    // ------------------------------------------------------------- 标签页

    void RebuildTabs()
    {
        TabStrip.Children.Clear();
        _tabs.Clear();

        if (S.AggregateTab)
            AddTab("聚合", null, "同时显示所有已启用源的结果", "#7C8794");

        foreach (var cfg in S.EnabledProviders)
        {
            var meta = ProviderMeta.Get(cfg.Kind);
            var err = Engine.ConfigErrorOf(cfg);
            AddTab(cfg.DisplayName, cfg.Id, err ?? meta.FreeNote, meta.Accent, err is not null);
        }

        if (_tabs.Count == 0)
        {
            var hint = UiKit.Text("没有启用的翻译源 →", 11.5, "TextFaint");
            hint.Margin = new Thickness(2, 0, 6, 0);
            TabStrip.Children.Add(hint);

            var add = new Button { Content = "去设置", FontSize = 11.5, Height = 25 };
            add.SetResourceReference(StyleProperty, "ChipBtn");
            add.Click += (_, _) => _host.ShowSettings("sources");
            TabStrip.Children.Add(add);
            return;
        }

        // 恢复上次选中的标签
        var target = _tabs.FirstOrDefault(t => t.ProviderId == _activeProviderId);
        if (_aggregateSelected && S.AggregateTab) target = _tabs.First(t => t.ProviderId is null);
        if (target.Btn is null)
            target = _tabs.FirstOrDefault(t => t.ProviderId == S.PrimaryProviderId);
        if (target.Btn is null)
            target = _tabs.FirstOrDefault(t => t.ProviderId is not null && t.Btn.IsEnabled);
        if (target.Btn is null) target = _tabs[0];

        SelectTab(target.Btn, translate: false);
    }

    void AddTab(string label, string? providerId, string tooltip, string accent, bool disabled = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (providerId is not null)
        {
            var cfg = S.Find(providerId);
            var meta = ProviderMeta.Get(cfg?.Kind ?? ProviderKind.GoogleFree);
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new System.Windows.Media.SolidColorBrush(
                    ThemeService.Parse(meta.Accent, System.Windows.Media.Colors.SteelBlue)),
            };
            if (disabled) dot.Opacity = 0.35;
            else if (Engine.Health.IsCoolingDown(providerId)) dot.Opacity = 0.35;
            content.Children.Add(dot);
        }
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var btn = new ToggleButton
        {
            Content = content,
            ToolTip = tooltip,
            IsEnabled = !disabled,
            Focusable = false,
        };
        btn.SetResourceReference(StyleProperty, "ProviderTab");
        btn.PreviewMouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            SelectTab((ToggleButton)s, translate: true);
        };
        btn.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (providerId is not null) ShowTabMenu(providerId);
        };

        TabStrip.Children.Add(btn);
        _tabs.Add((btn, providerId));
    }

    void SelectTab(ToggleButton btn, bool translate)
    {
        foreach (var (b, _) in _tabs) b.IsChecked = ReferenceEquals(b, btn);
        var entry = _tabs.First(t => ReferenceEquals(t.Btn, btn));
        _activeProviderId = entry.ProviderId;
        _aggregateSelected = entry.ProviderId is null;

        if (entry.ProviderId is not null)
        {
            S.PrimaryProviderId = entry.ProviderId;
            SettingsService.Instance.Save();
        }
        UpdateStatus();
        if (translate) Translate(force: true);
    }

    public void SelectNextProvider()
    {
        if (_tabs.Count == 0) return;
        var idx = _tabs.FindIndex(t => t.Btn.IsChecked == true);
        for (int i = 1; i <= _tabs.Count; i++)
        {
            var next = _tabs[(idx + i + _tabs.Count) % _tabs.Count];
            if (next.Btn.IsEnabled) { SelectTab(next.Btn, translate: true); return; }
        }
    }

    void ShowTabMenu(string providerId)
    {
        var cfg = S.Find(providerId);
        if (cfg is null) return;

        var menu = new ContextMenu();
        var setPrimary = new MenuItem { Header = "设为默认源" };
        setPrimary.Click += (_, _) =>
        {
            S.PrimaryProviderId = providerId;
            SettingsService.Instance.Touch();
            UpdateStatus();
        };
        menu.Items.Add(setPrimary);

        var config = new MenuItem { Header = "配置这个源…" };
        config.Click += (_, _) => _host.ShowSettings("sources");
        menu.Items.Add(config);

        if (Engine.Health.SecondsLeft(providerId) is var left && left > 0)
        {
            var reset = new MenuItem { Header = $"清除冷却（还剩 {left}s）" };
            reset.Click += (_, _) => { Engine.Health.Reset(providerId); RebuildTabs(); };
            menu.Items.Add(reset);
        }

        var disable = new MenuItem { Header = "停用这个源" };
        disable.Click += (_, _) =>
        {
            cfg.Enabled = false;
            SettingsService.Instance.Touch();
            RebuildTabs();
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(disable);

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------- 输入

    void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        CharCount.Text = Input.Text.Length == 0 ? "" : Input.Text.Length + " 字";
        if (_suppressInput) return;

        if (S.TranslateOnType && Input.Text.Trim().Length > 0)
        {
            _debounce.Interval = TimeSpan.FromMilliseconds(S.TypeDelayMs);
            _debounce.Stop();
            _debounce.Start();
        }
        else if (Input.Text.Trim().Length == 0)
        {
            _debounce.Stop();
            _cts?.Cancel();
            _batch = null;
            _result.ShowMessage("输入或粘贴文本，回车开始翻译");
        }
    }

    void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.Enter)
        {
            // 回车翻译，Shift+回车换行（可在设置里反转）
            var wantsTranslate = S.EnterToTranslate ? !shift : ctrl;
            if (wantsTranslate)
            {
                e.Handled = true;
                _debounce.Stop();
                Translate(force: true);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (Input.Text.Length > 0) Input.Clear();
            else Close();
        }
    }

    void OnWindowKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && shift && e.Key == Key.S) { SwapLanguages(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab) { SelectNextProvider(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D) { _bilingualBtn.IsChecked = !_bilingualBtn.IsChecked; e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { _host.ShowSettings(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { Input.Clear(); Input.Focus(); e.Handled = true; }
        else if (e.Key is >= Key.D1 and <= Key.D9 && ctrl)
        {
            var idx = e.Key - Key.D1;
            if (idx < _tabs.Count && _tabs[idx].Btn.IsEnabled)
            {
                SelectTab(_tabs[idx].Btn, translate: true);
                e.Handled = true;
            }
        }
    }

    void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        Translate(force: true);
    }

    void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { /* 拖拽偶发异常，忽略 */ }
    }

    // ------------------------------------------------------------- 翻译

    void Translate(bool force = false)
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunTranslateAsync(text, cts, force);
    }

    async Task RunTranslateAsync(string text, CancellationTokenSource cts, bool force)
    {
        var aggregate = _aggregateSelected;
        var providerId = _activeProviderId ?? S.PrimaryProviderId;

        try
        {
            if (aggregate)
            {
                // 不用 ShowLoading：BeginAggregate 直接摆出每个源的占位卡，
                // 谁先回来谁先显示。快的源（有道 ~80ms）不再陪着慢的源一起等。
                var batch = await Engine.AggregateAsync(text, cts.Token,
                    onStart: _result.BeginAggregate,
                    onResult: _result.UpdateOne);
                if (cts.IsCancellationRequested) return;
                _batch = batch;
                _result.EndAggregate(batch);
                UpdateStatus(batch);
                RefreshTabDots();
                return;
            }

            var cfg = S.Find(providerId);
            if (cfg is null)
            {
                _result.ShowMessage("这个翻译源已被删除，请重新选择");
                return;
            }

            // AI 源支持流式，边出边显
            if (TryStream(cfg, text, cts, out var streamTask))
            {
                await streamTask;
                return;
            }

            _result.ShowLoading(cfg.DisplayName);
            var single = await Engine.SingleAsync(providerId, text,
                perParagraph: S.Bilingual && S.BilingualByParagraph, cts.Token);
            if (cts.IsCancellationRequested) return;

            _batch = single;
            _result.ShowBatch(single, aggregate: false);
            UpdateStatus(single);
            SyncTabToResult(single);
            RefreshTabDots();
        }
        catch (OperationCanceledException) { /* 被新的输入取代 */ }
        catch (Exception ex)
        {
            Log.Error("翻译流程异常", ex);
            _result.ShowMessage("翻译出错：" + ex.Message, dim: false);
        }
    }

    bool TryStream(ProviderConfig cfg, string text, CancellationTokenSource cts, out Task task)
    {
        task = Task.CompletedTask;
        if (S.MultiTargetEnabled && S.MultiTargets.Count > 1) return false;
        if (Engine.Impl(cfg) is not OpenAiCompatTranslator ai) return false;
        if (!ai.StreamEnabled || ai.ConfigError is not null) return false;

        task = StreamAsync(ai, cfg, text, cts);
        return true;
    }

    async Task StreamAsync(OpenAiCompatTranslator ai, ProviderConfig cfg, string text,
                           CancellationTokenSource cts)
    {
        var (from, targets) = Engine.Resolve(text);
        var req = new TranslateRequest { Text = text, From = from, Targets = targets };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new System.Text.StringBuilder();

        _result.BeginStream(text, cfg.DisplayName);
        StatusText.Text = $"{cfg.DisplayName} 正在输出…";

        try
        {
            await foreach (var piece in ai.StreamAsync(req, cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                buffer.Append(piece);
                _result.AppendStream(piece);
            }

            var final = AiPrompt.Cleanup(buffer.ToString());
            var batch = new TranslateBatch
            {
                SourceText = text, From = from, Targets = [.. targets],
                TotalMs = sw.ElapsedMilliseconds,
            };
            var r = new TranslateResult
            {
                ProviderId = cfg.Id, ProviderName = cfg.DisplayName,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
            if (final.Length > 0) r.Texts[targets[0]] = final;
            else r.Error = "模型未返回译文";
            batch.Results.Add(r);

            _batch = batch;
            if (r.Ok) Engine.Health.Success(cfg.Id, r.ElapsedMs);
            else Engine.Health.Failure(cfg.Id, r.Error!);

            _result.ShowBatch(batch, aggregate: false);
            UpdateStatus(batch);
        }
        catch (OperationCanceledException) { /* 用户改了输入 */ }
        catch (Exception ex)
        {
            Engine.Health.Failure(cfg.Id, ex.Message);
            // 流式失败就退回普通模式，顺带走降级链
            if (S.AutoFallback && !cts.IsCancellationRequested)
            {
                var fallback = await Engine.SingleAsync(cfg.Id, text, false, cts.Token);
                if (cts.IsCancellationRequested) return;
                _batch = fallback;
                _result.ShowBatch(fallback, aggregate: false);
                UpdateStatus(fallback);
                SyncTabToResult(fallback);
            }
            else _result.ShowMessage("翻译失败：" + ex.Message, dim: false);
        }
    }

    /// <summary>降级发生时，把标签选中项同步到真正出结果的那个源。</summary>
    void SyncTabToResult(TranslateBatch batch)
    {
        var winner = batch.Results.LastOrDefault(r => r.Ok);
        if (winner is null || winner.ProviderId == _activeProviderId) return;
        var tab = _tabs.FirstOrDefault(t => t.ProviderId == winner.ProviderId);
        if (tab.Btn is null) return;

        foreach (var (b, _) in _tabs) b.IsChecked = ReferenceEquals(b, tab.Btn);
        _activeProviderId = winner.ProviderId;
        _aggregateSelected = false;
    }

    void RefreshTabDots()
    {
        foreach (var (btn, id) in _tabs)
        {
            if (id is null) continue;
            if (btn.Content is not StackPanel sp) continue;
            if (sp.Children.Count > 0 && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
                dot.Opacity = Engine.Health.IsCoolingDown(id) ? 0.3
                            : Engine.Health.LastOk(id) ? 1.0 : 0.5;
        }
    }

    void Rerender()
    {
        if (_batch is not null) _result.ShowBatch(_batch, _aggregateSelected);
        else if (Input.Text.Trim().Length > 0) Translate(force: true);
    }
}
