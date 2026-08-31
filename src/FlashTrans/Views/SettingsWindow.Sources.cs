using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    StackPanel _sourceList = null!;
    string? _expandedId;

    // ============================================================= 翻译源页

    UIElement BuildSourcesPage()
    {
        var page = Page();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(UiKit.Text("已配置的翻译源", 12.5, "Text", FontWeights.SemiBold));
        title.Children.Add(UiKit.Text("勾选启用，拖不动就用上下箭头调顺序；顺序决定失败降级的先后。",
            10.5, "TextFaint", wrap: true));
        UiKit.SetGrid(title, col: 0);
        head.Children.Add(title);

        var addBtn = new Button { Content = "＋ 添加源", FontSize = 12, Padding = new Thickness(12, 4, 12, 4) };
        addBtn.SetResourceReference(StyleProperty, "PrimaryBtn");
        addBtn.VerticalAlignment = VerticalAlignment.Center;
        addBtn.Click += (_, _) => ShowAddMenu(addBtn);
        UiKit.SetGrid(addBtn, col: 1);
        head.Children.Add(addBtn);

        head.Margin = new Thickness(2, 0, 2, 9);
        page.Children.Add(head);

        _sourceList = new StackPanel();
        page.Children.Add(_sourceList);
        RefreshSourceList();

        var tips = new StackPanel { Margin = new Thickness(2, 14, 2, 0) };
        tips.Children.Add(UiKit.Text("免费源不需要任何配置，装好就能用；带额度的接口需要自己申请密钥。",
            11, "TextDim", wrap: true));
        tips.Children.Add(UiKit.Text("密钥用 Windows DPAPI 加密后存到 settings.json，换机器需要重新填。",
            10.5, "TextFaint", wrap: true));
        page.Children.Add(tips);

        return page;
    }

    void RefreshSourceList()
    {
        _sourceList.Children.Clear();
        if (S.Providers.Count == 0)
        {
            _sourceList.Children.Add(UiKit.Card(
                UiKit.Text("还没有翻译源，点右上角「添加源」。", 12, "TextFaint")));
            return;
        }

        for (int i = 0; i < S.Providers.Count; i++)
            _sourceList.Children.Add(BuildSourceCard(S.Providers[i], i));
    }

    UIElement BuildSourceCard(ProviderConfig cfg, int index)
    {
        var meta = ProviderMeta.Get(cfg.Kind);
        var expanded = _expandedId == cfg.Id;
        var configError = Engine.ConfigErrorOf(cfg);

        var body = new StackPanel();
        body.Children.Add(BuildSourceHeader(cfg, meta, index, configError));
        if (expanded) body.Children.Add(BuildSourceEditor(cfg, meta));

        var card = UiKit.Card(body, new Thickness(10, 8, 10, expanded ? 12 : 8));
        card.Margin = new Thickness(0, 0, 0, 6);
        if (!cfg.Enabled) card.Opacity = 0.62;
        return card;
    }

    UIElement BuildSourceHeader(ProviderConfig cfg, ProviderMetaInfo meta, int index, string? configError)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 勾选
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 徽标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 工具

        var enable = new CheckBox
        {
            IsChecked = cfg.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "在标签页中显示这个源",
        };
        enable.Checked += (_, _) => { cfg.Enabled = true; Save(); RefreshSourceList(); };
        enable.Unchecked += (_, _) => { cfg.Enabled = false; Save(); RefreshSourceList(); };
        UiKit.SetGrid(enable, col: 0);
        grid.Children.Add(enable);

        var badge = UiKit.Badge(meta.Badge, meta.Accent);
        badge.Margin = new Thickness(0, 0, 9, 0);
        UiKit.SetGrid(badge, col: 1);
        grid.Children.Add(badge);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(UiKit.Text(cfg.DisplayName, 12.5, "Text", FontWeights.SemiBold));

        if (cfg.Id == S.PrimaryProviderId)
        {
            var def = UiKit.Text("默认", 10, "Accent");
            def.Margin = new Thickness(7, 1, 0, 0);
            nameRow.Children.Add(def);
        }
        if (meta.IsAi)
        {
            var ai = UiKit.Text("AI", 10, "TextFaint");
            ai.Margin = new Thickness(7, 1, 0, 0);
            nameRow.Children.Add(ai);
        }
        var ms = Engine.Health.LastMs(cfg.Id);
        if (ms > 0)
        {
            var lat = UiKit.Text(ms + "ms", 10, "TextFaint");
            lat.Margin = new Thickness(7, 1, 0, 0);
            nameRow.Children.Add(lat);
        }
        info.Children.Add(nameRow);

        var note = configError ?? Engine.Health.LastError(cfg.Id) ?? meta.FreeNote;
        var noteText = UiKit.Text(note, 10.5, configError is not null ? "Danger" : "TextFaint", wrap: true);
        noteText.Margin = new Thickness(0, 1, 8, 0);
        info.Children.Add(noteText);

        info.Cursor = Cursors.Hand;
        info.MouseLeftButtonUp += (_, _) => ToggleExpand(cfg.Id);
        UiKit.SetGrid(info, col: 2);
        grid.Children.Add(info);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(UiKit.IconButton(UiKit.IconUp, "上移", (_, _) => Move(index, -1), 11));
        tools.Children.Add(UiKit.IconButton(UiKit.IconDown, "下移", (_, _) => Move(index, +1), 11));
        tools.Children.Add(UiKit.IconButton(UiKit.IconSettings, "展开设置", (_, _) => ToggleExpand(cfg.Id), 12));
        tools.Children.Add(UiKit.IconButton(UiKit.IconTrash, "删除这个源", (_, _) => Remove(cfg), 11));
        UiKit.SetGrid(tools, col: 3);
        grid.Children.Add(tools);

        return grid;
    }

    void ToggleExpand(string id)
    {
        _expandedId = _expandedId == id ? null : id;
        RefreshSourceList();
    }

    void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= S.Providers.Count) return;
        (S.Providers[index], S.Providers[target]) = (S.Providers[target], S.Providers[index]);
        Save();
        RefreshSourceList();
    }

    void Remove(ProviderConfig cfg)
    {
        if (!AppDialog.Confirm(this, "删除翻译源",
                $"确定要删除「{cfg.DisplayName}」吗？",
                okText: "删除", tone: DialogTone.Danger, icon: UiKit.IconTrash,
                detail: "这个源填过的 API 密钥会一起清掉，之后要重新添加并再填一次。"))
            return;

        S.Providers.Remove(cfg);
        if (S.PrimaryProviderId == cfg.Id)
            S.PrimaryProviderId = S.Providers.FirstOrDefault(p => p.Enabled)?.Id ?? "";
        if (_expandedId == cfg.Id) _expandedId = null;
        Save();
        RefreshSourceList();
    }

    // ------------------------------------------------------------- 添加源

    void ShowAddMenu(UIElement anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MaxHeight = 460,
        };

        AddGroupLabel(menu, "免费 · 无需密钥");
        foreach (var m in ProviderMeta.All.Where(m => !m.NeedsKey)) menu.Items.Add(AddItem(m));

        menu.Items.Add(new Separator());
        AddGroupLabel(menu, "需要密钥 · 有免费额度");
        foreach (var m in ProviderMeta.All.Where(m => m.NeedsKey && !m.IsAi)) menu.Items.Add(AddItem(m));

        menu.Items.Add(new Separator());
        AddGroupLabel(menu, "AI 翻译");
        foreach (var m in ProviderMeta.All.Where(m => m.IsAi)) menu.Items.Add(AddItem(m));

        menu.IsOpen = true;
    }

    static void AddGroupLabel(ContextMenu menu, string text)
    {
        var label = UiKit.Text(text, 10.5, "TextFaint", FontWeights.SemiBold);
        label.Margin = new Thickness(10, 5, 10, 3);
        menu.Items.Add(new MenuItem { Header = label, IsEnabled = false, StaysOpenOnClick = true });
    }

    MenuItem AddItem(ProviderMetaInfo meta)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var badge = UiKit.Badge(meta.Badge, meta.Accent);
        badge.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(badge);

        var text = new StackPanel();
        text.Children.Add(UiKit.Text(meta.DisplayName, 12.5));
        text.Children.Add(UiKit.Text(meta.FreeNote, 10, "TextFaint"));
        header.Children.Add(text);

        var item = new MenuItem { Header = header };
        item.Click += (_, _) => AddProvider(meta.Kind);
        return item;
    }

    void AddProvider(ProviderKind kind)
    {
        var cfg = ProviderConfig.Create(kind);
        // 同类型重复时加序号，标签页才分得清
        var same = S.Providers.Count(p => p.Kind == kind);
        if (same > 0) cfg.Name = ProviderMeta.Get(kind).DisplayName + " " + (same + 1);

        S.Providers.Add(cfg);
        if (string.IsNullOrEmpty(S.PrimaryProviderId)) S.PrimaryProviderId = cfg.Id;
        _expandedId = cfg.Id;
        Save();
        RefreshSourceList();
    }
}
