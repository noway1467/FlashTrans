using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>多语言目标选择：勾选若干语言，一次翻译成多个语言。</summary>
public static class MultiLangPopup
{
    static AppSettings S => SettingsService.Instance.Current;

    public static void Show(UIElement anchor, Action onChanged)
    {
        var selected = new List<string>(S.MultiTargets);

        var enable = new CheckBox
        {
            Content = "启用多语言翻译",
            IsChecked = S.MultiTargetEnabled,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var listPanel = new StackPanel();
        var boxes = new List<(CheckBox Box, string Code)>();

        // 常用语言 + 已选语言都列出来
        var codes = S.FavoriteLangs
            .Concat(selected)
            .Concat(["zh-CN", "en", "ja", "ko"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var code in codes)
        {
            var box = new CheckBox
            {
                Content = Languages.NameOf(code),
                IsChecked = selected.Contains(code, StringComparer.OrdinalIgnoreCase),
                FontSize = 12.5,
                Margin = new Thickness(0, 3, 0, 3),
            };
            boxes.Add((box, code));
            listPanel.Children.Add(box);
        }

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var picker = new LangPicker { SelectedCode = "fr" };
        var addBtn = new Button { Content = "添加", FontSize = 11.5, Height = 26, Padding = new Thickness(10, 0, 10, 0) };
        addBtn.SetResourceReference(FrameworkElement.StyleProperty, "ChipBtn");
        addRow.Children.Add(picker);
        addRow.Children.Add(addBtn);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 5,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
        };

        addBtn.Click += (_, _) =>
        {
            var code = picker.SelectedCode;
            if (boxes.Any(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase))) return;
            var box = new CheckBox
            {
                Content = Languages.NameOf(code),
                IsChecked = true,
                FontSize = 12.5,
                Margin = new Thickness(0, 3, 0, 3),
            };
            boxes.Add((box, code));
            listPanel.Children.Add(box);
        };

        var apply = new Button { Content = "应用", Padding = new Thickness(14, 5, 14, 5), FontSize = 12 };
        apply.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryBtn");
        apply.Click += (_, _) =>
        {
            var picked = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Code).ToList();
            S.MultiTargetEnabled = enable.IsChecked == true && picked.Count > 0;
            if (picked.Count > 0) S.MultiTargets = picked;
            SettingsService.Instance.Touch();
            popup.IsOpen = false;
            onChanged();
        };

        var hint = new TextBlock
        {
            Text = "勾选后一次请求翻译成多个语言，结果按语言分组显示。",
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.65,
        };

        var content = new StackPanel { Margin = new Thickness(12) };
        content.Children.Add(enable);
        content.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        content.Children.Add(addRow);
        content.Children.Add(hint);
        content.Children.Add(new Border
        {
            Child = apply,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        });

        var shell = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Width = 236,
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black
            },
        };
        shell.SetResourceReference(Border.BackgroundProperty, "BgCard");
        shell.SetResourceReference(Border.BorderBrushProperty, "Border");

        popup.Child = shell;
        popup.IsOpen = true;
    }
}
