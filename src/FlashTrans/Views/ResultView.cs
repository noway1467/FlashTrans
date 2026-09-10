using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>译文渲染区。主窗口和划词弹窗共用。</summary>
public sealed class ResultView : ScrollViewer
{
    readonly ResultPanel _list = new();
    Button? _viewButton;
    TextBox? _streamTarget;
    readonly DispatcherTimer _streamFlush = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly StringBuilder _streamBuffer = new();
    bool _streamFlushQueued;

    // 聚合边到边显示用：源 Id -> 它在 _list 里的位置
    readonly Dictionary<string, int> _liveSlots = new(StringComparer.Ordinal);
    TranslateBatch? _liveBatch;

    public event Action<string>? CopyRequested;
    public event Action<string, string>? SpeakRequested;
    public event Action<string>? LookupRequested;

    static AppSettings S => SettingsService.Instance.Current;

    public ResultView()
    {
        SetResourceReference(StyleProperty, "PlainScrollViewer");
        Padding = new Thickness(0);
        Content = _list;
        _list.Margin = new Thickness(0, 0, 2, 0);
        _streamFlush.Tick += (_, _) => FlushStream();
        ApplyViewSettings();
    }

    /// <summary>两种翻译窗口共用同一入口，避免菜单文案和当前状态不一致。</summary>
    public Button CreateViewButton(Action onChanged)
    {
        _viewButton = new Button
        {
            Name = "ResultViewButton",
            FontSize = 11.5,
            Height = 26,
            Padding = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _viewButton.SetResourceReference(StyleProperty, "GhostBtn");
        System.Windows.Automation.AutomationProperties.SetName(_viewButton, "翻译结果视图");
        _viewButton.Click += (_, _) =>
        {
            var menu = new ContextMenu
            {
                PlacementTarget = _viewButton,
                Placement = PlacementMode.Bottom,
            };
            AddView("单列列表", false, "SingleColumnView");
            AddView("多列自适应", true, "MultiColumnView");
            _viewButton.ContextMenu = menu;
            menu.IsOpen = true;

            void AddView(string label, bool multiColumn, string name)
            {
                var item = new MenuItem
                {
                    Name = name, Header = label,
                    IsCheckable = true, IsChecked = S.MultiColumnResults == multiColumn,
                    ToolTip = multiColumn ? "按窗口宽度自动分列；拉宽显示更多列，变窄回到单列" : "所有翻译源从上到下排列",
                };
                item.Click += (_, _) =>
                {
                    if (S.MultiColumnResults == multiColumn) return;
                    S.MultiColumnResults = multiColumn;
                    // 只保存并同步结果布局，不走 Touch 的全局设置广播：后者会重画结果、
                    // 重绑热键，甚至在翻译途中重新发起请求，丢掉当前选区和流式状态。
                    SettingsService.Instance.Save();
                    ApplyViewSettings();
                    onChanged();
                };
                menu.Items.Add(item);
            }
        };
        ApplyViewSettings();
        return _viewButton;
    }

    /// <summary>只改变卡片的测量和排列，不重建文本控件或清空进行中的翻译。</summary>
    public void ApplyViewSettings() => RunOnUi(() =>
    {
        _list.MultiColumn = S.MultiColumnResults;
        _list.MinimumColumnWidth = Math.Max(320, S.FontSize * 22);
        _list.InvalidateMeasure();
        if (_viewButton is null) return;
        _viewButton.Content = UiKit.Row(5,
            UiKit.Icon(S.MultiColumnResults ? UiKit.IconColumns : UiKit.IconList, 13),
            UiKit.Text("视图", 11.5, "TextDim"), UiKit.Icon(UiKit.IconDown, 9));
        _viewButton.ToolTip = S.MultiColumnResults
            ? "结果视图：多列自适应（拉宽窗口可显示更多列）" : "结果视图：单列列表";
    });

    // 普通 WrapPanel 不会平分剩余宽度，UniformGrid 又会把所有行拉成最长卡片的高度。
    // 在测量阶段按实际视口分列，每行只取本行最大高度；源顺序不变，说明文字独占整行。
    sealed class ResultPanel : Panel
    {
        const double ColumnGap = 8;
        int _columns = 1;
        public bool MultiColumn { get; set; }
        public double MinimumColumnWidth { get; set; } = 320;
        public int CardCount { get; set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = Math.Min(CardCount, InternalChildren.Count);
            _columns = MultiColumn && count > 1 && double.IsFinite(availableSize.Width)
                ? Math.Min(count, Math.Max(1, (int)((availableSize.Width + ColumnGap) / (MinimumColumnWidth + ColumnGap))))
                : 1;
            var columnWidth = Math.Max(0, (availableSize.Width - (_columns - 1) * ColumnGap) / _columns);
            double height = 0, desiredWidth = 0;
            for (var start = 0; start < count; start += _columns)
            {
                double rowHeight = 0;
                for (var i = start; i < Math.Min(start + _columns, count); i++)
                {
                    var child = InternalChildren[i];
                    child.Measure(new Size(columnWidth, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                    desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
                }
                height += rowHeight;
            }
            for (var i = count; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                height += child.DesiredSize.Height;
                desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            }
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : desiredWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var count = Math.Min(CardCount, InternalChildren.Count);
            var columnWidth = Math.Max(0, (finalSize.Width - (_columns - 1) * ColumnGap) / _columns);
            double top = 0;
            for (var start = 0; start < count; start += _columns)
            {
                var end = Math.Min(start + _columns, count);
                double rowHeight = 0;
                for (var i = start; i < end; i++)
                    rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
                for (var i = start; i < end; i++)
                    InternalChildren[i].Arrange(new Rect((i - start) * (columnWidth + ColumnGap), top, columnWidth, rowHeight));
                top += rowHeight;
            }
            for (var i = count; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
                top += child.DesiredSize.Height;
            }
            return finalSize;
        }
    }

    void ClearCore()
    {
        _streamFlush.Stop();
        _streamFlushQueued = false;
        _streamBuffer.Clear();
        _list.Children.Clear();
        _list.CardCount = 0;
        _streamTarget = null;
        _liveSlots.Clear();
        _liveBatch = null;
    }

    public void Clear() => RunOnUi(ClearCore);

    public void ShowMessage(string message, bool dim = true) =>
        RunOnUi(() => ShowMessageCore(message, dim));

    public void ShowLoading(string providerName) =>
        RunOnUi(() => ShowLoadingCore(providerName));

    public void BeginStream(string sourceText, string providerName) =>
        RunOnUi(() => BeginStreamCore(sourceText, providerName));

    public void AppendStream(string piece) => RunOnUi(() =>
    {
        if (_streamTarget is null) return;
        _streamBuffer.Append(piece);
        if (_streamFlushQueued) return;
        _streamFlushQueued = true;
        _streamFlush.Start();
    });

    public void ShowBatch(TranslateBatch batch, bool aggregate, string? onlyProviderId = null) =>
        RunOnUi(() => ShowBatchCore(batch, aggregate, onlyProviderId));

    // --------------------------------------------------------- 聚合：边到边显示

    /// <summary>
    /// 聚合开始：先按配置顺序摆好占位卡片，每张写「正在翻译…」。
    /// 之后 <see cref="UpdateOne"/> 把占位原地换成真结果，位置不随回来的先后跳动。
    /// </summary>
    public void BeginAggregate(TranslateBatch batch, IReadOnlyList<ProviderConfig> configs) =>
        RunOnUi(() =>
        {
            ClearCore();
            _liveBatch = batch;
            for (var i = 0; i < configs.Count; i++)
            {
                _liveSlots[configs[i].Id] = i;
                var card = PendingCard(configs[i]);
                card.Margin = new Thickness(0, 0, 0, 7);
                _list.Children.Add(card);
            }
            _list.CardCount = configs.Count;
        });

    /// <summary>某个源回来了：换掉它那张占位卡。</summary>
    public void UpdateOne(TranslateResult r) => RunOnUi(() =>
    {
        if (_liveBatch is null) return;
        if (!_liveSlots.TryGetValue(r.ProviderId, out var i)) return;
        if (i < 0 || i >= _list.Children.Count) return;

        var card = ProviderCard(_liveBatch, r, aggregate: true);
        card.Margin = new Thickness(0, 0, 0, 7);

        // 不能写 Children[i] = card：那个索引赋值会先挂新元素再摘旧元素，
        // WPF 当场抛「Visual 已经是另一个 Visual 的子级」。先删后插才干净。
        _list.Children.RemoveAt(i);
        _list.Children.Insert(i, card);
    });

    /// <summary>
    /// 聚合收尾：只补状态说明，不重画已经显示的卡片。
    /// 重画会丢掉用户已经选中的文本和滚动位置，而且会闪一下。
    /// </summary>
    public void EndAggregate(TranslateBatch batch) => RunOnUi(() =>
    {
        _liveBatch = null;
        _liveSlots.Clear();

        if (_list.Children.Count == 0)
        {
            ShowMessageCore("没有可用的翻译源，请到设置里添加或启用");
            return;
        }
        foreach (var note in batch.Notes.Take(3))
        {
            var n = UiKit.Text(note, S.FontSize - 3, "TextFaint", wrap: true);
            n.Margin = new Thickness(2, 2, 2, 0);
            _list.Children.Add(n);
        }
    });

    void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    void FlushStream()
    {
        _streamFlush.Stop();
        _streamFlushQueued = false;
        if (_streamTarget is null || _streamBuffer.Length == 0) return;
        _streamTarget.AppendText(_streamBuffer.ToString());
        _streamBuffer.Clear();
        ScrollToBottom();
    }

    void ShowMessageCore(string message, bool dim = true)
    {
        Clear();
        var tb = UiKit.Text(message, S.FontSize - 1, dim ? "TextFaint" : "Text", wrap: true);
        tb.Margin = new Thickness(2, 6, 2, 0);
        _list.Children.Add(tb);
    }

    void ShowLoadingCore(string providerName)
    {
        Clear();
        var row = UiKit.Row(7,
            UiKit.Text("···", S.FontSize + 2, "Accent", FontWeights.Bold),
            UiKit.Text($"{providerName} 正在翻译…", S.FontSize - 1, "TextFaint"));
        row.Margin = new Thickness(2, 6, 0, 0);
        _list.Children.Add(row);
    }

    // ------------------------------------------------------------- 流式

    /// <summary>开始流式显示（AI 源），返回后用 <see cref="AppendStream"/> 追加。</summary>
    void BeginStreamCore(string sourceText, string providerName)
    {
        Clear();
        var panel = new StackPanel();
        if (S.Bilingual && !S.HideBilingualSource) panel.Children.Add(SourceBlock(sourceText));

        _streamTarget = UiKit.SelectableText("", S.FontSize + 1);
        panel.Children.Add(_streamTarget);

        var caption = UiKit.Text(providerName + " · 输出中", S.FontSize - 3, "TextFaint");
        caption.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(caption);

        _list.Children.Add(panel);
    }

    // AppendStream wrappers are defined above.

    // ------------------------------------------------------------- 批量结果

    void ShowBatchCore(TranslateBatch batch, bool aggregate, string? onlyProviderId = null)
    {
        Clear();
        var results = batch.Results;
        if (onlyProviderId is not null)
            results = results.Where(r => r.ProviderId == onlyProviderId || r.Ok).ToList();

        if (results.Count == 0)
        {
            ShowMessage("没有可用的翻译源，请到设置里添加或启用");
            return;
        }

        // 单源单语言：最干净的排版
        if (!aggregate && results.Count(r => r.Ok) <= 1 && batch.Targets.Count == 1)
        {
            var r = results.FirstOrDefault(x => x.Ok) ?? results[^1];
            _list.Children.Add(SingleBlock(batch, r));
            return;
        }

        foreach (var r in results)
        {
            var card = ProviderCard(batch, r, aggregate);
            card.Margin = new Thickness(0, 0, 0, 7);
            _list.Children.Add(card);
        }
        _list.CardCount = results.Count;
    }

    // ------------------------------------------------------------- 组件

    UIElement SingleBlock(TranslateBatch batch, TranslateResult r)
    {
        var panel = new StackPanel();

        if (!r.Ok)
        {
            panel.Children.Add(ErrorRow(r));
            foreach (var note in batch.Notes.Take(3))
            {
                var n = UiKit.Text(note, S.FontSize - 3, "TextFaint", wrap: true);
                n.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(n);
            }
            return panel;
        }

        foreach (var lang in batch.Targets)
        {
            var text = r.Get(lang);
            if (text is null) continue;
            if (batch.Targets.Count > 1) panel.Children.Add(LangLabel(lang, text));
            panel.Children.Add(Body(batch.SourceText, text, S.FontSize + 1));
        }

        if (S.ShowDictionary) AppendDictionary(panel, r);
        panel.Children.Add(Footer(batch, r));
        return panel;
    }

    /// <summary>占位卡：头部和 <see cref="ProviderCard"/> 一致，换上真结果时不会跳版。</summary>
    Border PendingCard(ProviderConfig cfg)
    {
        var meta = ProviderMeta.Get(cfg.Kind);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = UiKit.Badge(meta.Badge, meta.Accent);
        UiKit.SetGrid(badge, col: 0);
        head.Children.Add(badge);

        var name = UiKit.Text(cfg.DisplayName, S.FontSize - 2, "TextDim", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = cfg.DisplayName;
        name.Margin = new Thickness(7, 0, 0, 0);
        UiKit.SetGrid(name, col: 1);
        head.Children.Add(name);

        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(UiKit.Text("正在翻译…", S.FontSize - 1, "TextFaint"));

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        return UiKit.Card(stack);
    }

    Border ProviderCard(TranslateBatch batch, TranslateResult r, bool aggregate)
    {
        // 只要 Kind 拿徽标和配色，直接读配置就行。
        // 以前绕 Registry.Get 建实例，源被删掉时那句 Create 每渲染一次都生成新 Guid，
        // registry 缓存会一直涨，还白造一堆 translator。
        var meta = ProviderMeta.Get(S.Find(r.ProviderId)?.Kind ?? ProviderKind.GoogleFree);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = UiKit.Badge(meta.Badge, meta.Accent);
        UiKit.SetGrid(badge, col: 0);
        head.Children.Add(badge);

        var name = UiKit.Text(r.ProviderName, S.FontSize - 2, "TextDim", FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = r.ProviderName;
        name.Margin = new Thickness(7, 0, 0, 0);
        UiKit.SetGrid(name, col: 1);
        head.Children.Add(name);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (S.ShowLatency && r.Ok)
        {
            var ms = r.FromCache ? "缓存" : r.ElapsedMs + " ms";
            right.Children.Add(UiKit.Text(ms, S.FontSize - 3.5, "TextFaint"));
        }
        if (r.Ok)
        {
            if (S.Bilingual)
            {
                var sourceCopy = UiKit.IconButton(UiKit.IconCopy, "复制原文",
                    (_, _) => CopyRequested?.Invoke(batch.SourceText), 14, "SourceCopyBtn");
                sourceCopy.Width = 30;
                sourceCopy.Height = 27;
                sourceCopy.Margin = new Thickness(4, 0, 0, 0);
                right.Children.Add(sourceCopy);
            }

            if (S.Bilingual) right.Children.Add(CopySeparator());

            if (batch.Targets.Count == 1)
            {
                AddSpeechButton(right, FirstText(r, batch), batch.Targets[0]);
                var copy = UiKit.IconButton(UiKit.IconCopy, "复制译文",
                    (_, _) => CopyRequested?.Invoke(ProviderText(r, batch)), 14, "TranslationCopyBtn");
                copy.Width = 30; copy.Height = 27;
                copy.Margin = new Thickness(4, 0, 0, 0);
                right.Children.Add(copy);
            }
            else
            {
                var copy = UiKit.IconButton(UiKit.IconCopy, "复制此源全部译文",
                    (_, _) => CopyRequested?.Invoke(ProviderText(r, batch)), 14, "ProviderCopyBtn");
                copy.Width = 30;
                copy.Height = 27;
                copy.Margin = new Thickness(4, 0, 0, 0);
                right.Children.Add(copy);
            }
        }
        UiKit.SetGrid(right, col: 2);
        head.Children.Add(right);

        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (r.Ok)
        {
            foreach (var lang in batch.Targets)
            {
                var text = r.Get(lang);
                if (text is null) continue;
                if (batch.Targets.Count > 1) body.Children.Add(LangLabel(lang, text));
                body.Children.Add(Body(batch.SourceText, text, S.FontSize));
            }
            if (S.ShowDictionary) AppendDictionary(body, r);
        }
        else body.Children.Add(ErrorRow(r));

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        return UiKit.Card(stack);
    }

    UIElement Body(string source, string translated, double fontSize)
    {
        if (!S.Bilingual || S.HideBilingualSource) return UiKit.SelectableText(translated, fontSize);

        var panel = new StackPanel();
        var srcLines = TranslateEngine.SplitLines(source);
        var dstLines = TranslateEngine.SplitLines(translated);

        // 行数一致时逐行对照，否则整段对照
        if (srcLines.Count > 1 && srcLines.Count == dstLines.Count)
        {
            for (int i = 0; i < srcLines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(srcLines[i]) && string.IsNullOrWhiteSpace(dstLines[i])) continue;
                var pair = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
                pair.Children.Add(DimLine(srcLines[i], fontSize - 1));
                pair.Children.Add(UiKit.SelectableText(dstLines[i], fontSize));
                panel.Children.Add(pair);
            }
        }
        else
        {
            panel.Children.Add(SourceBlock(source));
            panel.Children.Add(UiKit.SelectableText(translated, fontSize));
        }
        return panel;
    }

    static UIElement SourceBlock(string source)
    {
        var tb = UiKit.SelectableText(source, S.FontSize - 1);
        tb.SetResourceReference(ForegroundProperty, "TextFaint");
        tb.Margin = new Thickness(0, 0, 0, 5);
        return tb;
    }

    static UIElement DimLine(string text, double size)
    {
        var tb = UiKit.SelectableText(text, size);
        tb.SetResourceReference(ForegroundProperty, "TextFaint");
        tb.Margin = new Thickness(0, 0, 0, 2);
        return tb;
    }

    UIElement LangLabel(string lang, string text)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 3),
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = UiKit.Text(Languages.NameOf(lang), S.FontSize - 4, "TextDim", FontWeights.SemiBold),
        };
        border.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        row.Children.Add(border);

        row.Children.Add(CopySeparator());
        AddSpeechButton(row, text, lang);
        var copy = UiKit.IconButton(UiKit.IconCopy, $"复制{Languages.NameOf(lang)}译文",
            (_, _) => CopyRequested?.Invoke(text), 13, "TranslationCopyBtn");
        copy.Width = 30;
        copy.Height = 27;
        copy.Margin = new Thickness(5, 0, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(copy);
        return row;
    }

    UIElement ErrorRow(TranslateResult r)
    {
        // 横向 StackPanel 会给错误文字无限宽，分列后长错误会越过卡片边界。
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(UiKit.StatusDot(false));
        var text = UiKit.Text(r.Error ?? "翻译失败", S.FontSize - 2, "Danger", wrap: true);
        text.Margin = new Thickness(6, 0, 0, 0);
        UiKit.SetGrid(text, col: 1);
        row.Children.Add(text);
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    void AppendDictionary(Panel panel, TranslateResult r)
    {
        if (!string.IsNullOrWhiteSpace(r.Phonetic))
        {
            var ph = UiKit.Text("/" + r.Phonetic!.Trim('/') + "/", S.FontSize - 2, "TextDim");
            ph.Margin = new Thickness(0, 5, 0, 0);
            panel.Children.Add(ph);
        }
        if (r.Dict is not { Count: > 0 }) return;

        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var d in r.Dict.Take(4))
        {
            // 释义也必须在列宽内换行，不能被横向 StackPanel 按无限宽测量。
            var line = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!string.IsNullOrWhiteSpace(d.Pos))
            {
                var pos = UiKit.Text(d.Pos, S.FontSize - 3, "Accent", FontWeights.SemiBold);
                pos.Margin = new Thickness(0, 0, 6, 0);
                pos.MinWidth = 26;
                line.Children.Add(pos);
            }
            var terms = UiKit.Text(string.Join("；", d.Terms), S.FontSize - 2, "TextDim", wrap: true);
            UiKit.SetGrid(terms, col: 1);
            line.Children.Add(terms);
            box.Children.Add(line);
        }
        panel.Children.Add(box);
    }

    UIElement Footer(TranslateBatch batch, TranslateResult r)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0),
        };

        if (batch.Targets.Count == 1)
        {
            if (S.Bilingual)
            {
                var sourceCopy = UiKit.IconButton(UiKit.IconCopy, "复制原文",
                    (_, _) => CopyRequested?.Invoke(batch.SourceText), 14, "SourceCopyBtn");
                sourceCopy.Width = 30;
                sourceCopy.Height = 27;
                row.Children.Add(sourceCopy);
                row.Children.Add(CopySeparator());
            }

            AddSpeechButton(row, FirstText(r, batch), batch.Targets[0]);
            var copy = UiKit.IconButton(UiKit.IconCopy, "复制译文",
                (_, _) => CopyRequested?.Invoke(ProviderText(r, batch)), 14, "TranslationCopyBtn");
            copy.Width = 30;
            copy.Height = 27;
            row.Children.Add(copy);
        }

        if (S.EudicEnabled && LangDetect.LooksLikeWord(batch.SourceText))
        {
            var dict = UiKit.IconButton(UiKit.IconBook, "在欧路词典中查询",
                (_, _) => LookupRequested?.Invoke(batch.SourceText.Trim()), 13);
            row.Children.Add(dict);
        }

        var info = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var detected = r.DetectedFrom is { Length: > 0 } d ? Languages.NameOf(Canon(d)) : null;
        var parts = new List<string> { r.ProviderName };
        if (detected is not null && batch.From == Languages.Auto) parts.Add("检测：" + detected);
        if (S.ShowLatency) parts.Add(r.FromCache ? "缓存" : r.ElapsedMs + " ms");
        info.Children.Add(UiKit.Text(string.Join(" · ", parts), S.FontSize - 3.5, "TextFaint"));
        row.Children.Add(info);

        return row;
    }

    static string Canon(string code) => code.ToLowerInvariant() switch
    {
        "zh-hans" or "zh" or "zh-chs" => "zh-CN",
        "zh-hant" or "cht" or "zh-cht" => "zh-TW",
        "iw" => "he",
        "jp" => "ja",
        _ => code
    };

    static string FirstText(TranslateResult r, TranslateBatch batch)
    {
        foreach (var lang in batch.Targets)
            if (r.Get(lang) is { } t) return t;
        return r.Texts.Values.FirstOrDefault() ?? "";
    }

    void AddSpeechButton(Panel panel, string text, string language)
    {
        if (!SpeechService.HasVoice(language)) return;
        var speak = UiKit.IconButton(UiKit.IconVolume, $"播放{Languages.NameOf(language)}译文",
            (_, _) => SpeakRequested?.Invoke(text, language), 13, "SpeechBtn");
        speak.Width = 30;
        speak.Height = 27;
        speak.Margin = new Thickness(4, 0, 0, 0);
        panel.Children.Add(speak);
    }

    static Border CopySeparator()
    {
        var line = new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
        return line;
    }

    /// <summary>复制当前翻译源的全部目标语言结果，不附带语言名称。</summary>
    static string ProviderText(TranslateResult r, TranslateBatch batch)
    {
        var sb = new StringBuilder();
        foreach (var lang in batch.Targets)
        {
            if (r.Get(lang) is not { } text) continue;
            sb.AppendLine(text);
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : FirstText(r, batch);
    }

    /// <summary>当前显示的全部译文（用于「复制全部」）。</summary>
    public static string AllText(TranslateBatch batch)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in batch.Results.Where(x => x.Ok))
        {
            foreach (var lang in batch.Targets)
            {
                if (r.Get(lang) is not { } t) continue;
                if (batch.Results.Count(x => x.Ok) > 1 || batch.Targets.Count > 1)
                    sb.Append('[').Append(r.ProviderName);
                if (batch.Results.Count(x => x.Ok) > 1 || batch.Targets.Count > 1) sb.Append("] ");
                sb.AppendLine(t);
            }
        }
        return sb.ToString().TrimEnd();
    }
}
