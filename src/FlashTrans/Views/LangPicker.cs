using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>语言选择按钮：点开是带搜索的列表，常用语言置顶。</summary>
public sealed class LangPicker : Button
{
    readonly Popup _popup = new();
    readonly TextBox _search = new();
    readonly ListBox _list = new();
    readonly TextBlock _label = new();
    bool _includeAuto;
    string _code = "zh-CN";

    public event Action<string>? SelectionChanged;

    public string SelectedCode
    {
        get => _code;
        set
        {
            _code = value;
            _label.Text = value == Languages.Auto ? "自动检测" : Languages.NameOf(value);
        }
    }

    public LangPicker(bool includeAuto = false)
    {
        _includeAuto = includeAuto;
        SetResourceReference(StyleProperty, "GhostBtn");
        Height = 26;
        Padding = new Thickness(8, 0, 6, 0);
        Focusable = false;

        _label.FontSize = 12.5;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 3.5,3.5 L 7,0"),
            StrokeThickness = 1.3,
            Margin = new Thickness(5, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFaint");

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _label, arrow }
        };

        BuildPopup();
        Click += (_, _) => Open();
        SelectedCode = _code;
    }

    void BuildPopup()
    {
        _search.SetResourceReference(StyleProperty, typeof(TextBox));
        _search.Height = 28;
        _search.Margin = new Thickness(8, 8, 8, 6);
        _search.TextChanged += (_, _) => Filter(_search.Text);
        _search.PreviewKeyDown += OnSearchKey;

        _list.MaxHeight = 300;
        _list.Margin = new Thickness(5, 0, 5, 6);
        _list.MouseLeftButtonUp += (_, _) => Commit();
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key is Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
        };

        var panel = new StackPanel();
        panel.Children.Add(_search);
        panel.Children.Add(_list);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Width = 232,
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black
            },
        };
        shell.SetResourceReference(Border.BackgroundProperty, "BgCard");
        shell.SetResourceReference(Border.BorderBrushProperty, "Border");

        _popup.Child = shell;
        _popup.AllowsTransparency = true;
        _popup.StaysOpen = false;
        _popup.Placement = PlacementMode.Bottom;
        _popup.PlacementTarget = this;
        _popup.VerticalOffset = 4;
        _popup.PopupAnimation = PopupAnimation.Fade;
    }

    void Open()
    {
        _search.Text = "";
        Filter("");
        _popup.IsOpen = true;
        _search.Focus();

        // 定位到当前选中项
        var idx = _list.Items.OfType<Lang>().ToList().FindIndex(l => l.Code == _code);
        if (idx >= 0)
        {
            _list.SelectedIndex = idx;
            _list.ScrollIntoView(_list.Items[idx]);
        }
    }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Enter:
                if (_list.SelectedItem is null && _list.Items.Count > 0) _list.SelectedIndex = 0;
                Commit();
                e.Handled = true;
                break;
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1);
                if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    void Filter(string query)
    {
        var items = new List<Lang>();
        if (_includeAuto) items.Add(Languages.AutoLang);

        var favorites = SettingsService.Instance.Current.FavoriteLangs;
        IEnumerable<Lang> pool = Languages.All;

        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (var code in favorites)
                if (Languages.All.FirstOrDefault(l => l.Code == code) is { } f) items.Add(f);
            items.AddRange(pool.Where(l => !favorites.Contains(l.Code, StringComparer.OrdinalIgnoreCase)));
        }
        else
        {
            var q = query.Trim();
            items.AddRange(pool.Where(l =>
                l.NameZh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                l.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                l.Code.Contains(q, StringComparison.OrdinalIgnoreCase)));
            if (_includeAuto && "自动检测auto".Contains(q, StringComparison.OrdinalIgnoreCase))
                items.Insert(0, Languages.AutoLang);
        }

        _list.ItemsSource = items;
        _list.ItemTemplate = ItemTemplate();
        if (items.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;
    }

    static DataTemplate? _template;

    static DataTemplate ItemTemplate()
    {
        if (_template is not null) return _template;

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NameZh"));
        name.SetValue(TextBlock.FontSizeProperty, 12.5);
        name.SetValue(DockPanel.DockProperty, Dock.Left);

        var code = new FrameworkElementFactory(typeof(TextBlock));
        code.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Code"));
        code.SetValue(TextBlock.FontSizeProperty, 10.5);
        code.SetValue(TextBlock.OpacityProperty, 0.55);
        code.SetValue(DockPanel.DockProperty, Dock.Right);
        code.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(DockPanel.LastChildFillProperty, false);
        dock.AppendChild(name);
        dock.AppendChild(code);

        _template = new DataTemplate { VisualTree = dock };
        _template.Seal();
        return _template;
    }

    void Commit()
    {
        if (_list.SelectedItem is not Lang lang) return;
        _popup.IsOpen = false;
        if (lang.Code == _code) return;
        SelectedCode = lang.Code;
        SelectionChanged?.Invoke(lang.Code);
    }
}
