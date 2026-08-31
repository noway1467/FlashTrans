using System.Windows;
using System.Windows.Controls;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    /// <summary>展开后的单源编辑区：动态字段 + 测试按钮。</summary>
    UIElement BuildSourceEditor(ProviderConfig cfg, ProviderMetaInfo meta)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
        sep.SetResourceReference(Border.BackgroundProperty, "Border");
        panel.Children.Add(sep);

        // AI 源先给一排服务预设，省得手填地址
        if (meta.IsAi && meta.Kind == ProviderKind.OpenAiCompat)
            panel.Children.Add(BuildAiPresets(cfg));

        panel.Children.Add(Field("标签显示名",
            Input(cfg.Name, v => cfg.Name = v, meta.DisplayName),
            "留空用默认名"));

        foreach (var f in meta.Fields)
        {
            var editor = BuildFieldEditor(cfg, f);
            var row = Field(f.Label + (f.Required ? " *" : ""), editor, f.Hint);
            if (row is FrameworkElement fe) fe.Margin = new Thickness(0, 9, 0, 0);
            panel.Children.Add(row);
        }

        var timeout = Field("超时", Number(cfg.TimeoutMs, 800, 60000, v => cfg.TimeoutMs = v, "毫秒"),
            meta.IsAi ? "AI 生成较慢，建议 15000 以上" : null);
        if (timeout is FrameworkElement tfe) tfe.Margin = new Thickness(0, 9, 0, 0);
        panel.Children.Add(timeout);

        panel.Children.Add(BuildTestRow(cfg, meta));
        return panel;
    }

    UIElement BuildFieldEditor(ProviderConfig cfg, ProviderField f)
    {
        cfg.Options.TryGetValue(f.Key, out var current);
        current ??= f.Default ?? "";

        switch (f.Kind)
        {
            case FieldKind.Bool:
                var box = new CheckBox
                {
                    IsChecked = current is "1" or "true" or "True",
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                box.Checked += (_, _) => { cfg.Options[f.Key] = "true"; Invalidate(); };
                box.Unchecked += (_, _) => { cfg.Options[f.Key] = "false"; Invalidate(); };
                return box;

            case FieldKind.Number:
                return Number(int.TryParse(current, out var n) ? n : 0, 0, 1_000_000,
                    v => { cfg.Options[f.Key] = v.ToString(); Invalidate(); });

            case FieldKind.Multiline:
                var multi = new TextBox
                {
                    Text = current,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 62,
                    MaxHeight = 150,
                    FontSize = 12,
                    Padding = new Thickness(7, 5, 7, 5),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                multi.TextChanged += (_, _) => cfg.Options[f.Key] = multi.Text;
                multi.LostFocus += (_, _) => Invalidate();
                return multi;

            case FieldKind.Secret:
                return BuildSecretEditor(cfg, f, current);

            default:
                var text = Input(current, v => cfg.Options[f.Key] = v, f.Default);
                text.LostFocus += (_, _) => Invalidate();
                return text;
        }
    }

    UIElement BuildSecretEditor(ProviderConfig cfg, ProviderField f, string current)
    {
        var pwd = new PasswordBox
        {
            Password = current,
            FontSize = 12.5,
            Height = 28,
            Padding = new Thickness(7, 0, 7, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var plain = Input(current, v => { }, f.Hint);
        plain.Visibility = Visibility.Collapsed;

        pwd.PasswordChanged += (_, _) =>
        {
            cfg.Options[f.Key] = pwd.Password;
            plain.Text = pwd.Password;
        };
        pwd.LostFocus += (_, _) => Invalidate();
        plain.TextChanged += (_, _) =>
        {
            cfg.Options[f.Key] = plain.Text;
            if (pwd.Password != plain.Text) pwd.Password = plain.Text;
        };
        plain.LostFocus += (_, _) => Invalidate();

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.Children.Add(pwd);
        host.Children.Add(plain);

        var eye = SmallButton("显示", () => { });
        eye.Margin = new Thickness(6, 0, 0, 0);
        eye.Click += (_, _) =>
        {
            var showing = plain.Visibility == Visibility.Visible;
            plain.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            pwd.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
            eye.Content = showing ? "显示" : "隐藏";
        };
        UiKit.SetGrid(eye, col: 1);
        host.Children.Add(eye);
        return host;
    }

    UIElement BuildAiPresets(ProviderConfig cfg)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        wrap.Children.Add(UiKit.Text("快速填入：", 11, "TextFaint"));

        foreach (var preset in ProviderMeta.AiPresets)
        {
            var btn = SmallButton(preset.Name, () =>
            {
                cfg.Options["baseUrl"] = preset.BaseUrl;
                cfg.Options["model"] = preset.Model;
                if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = preset.Name;
                Invalidate();
                Save();
                RefreshSourceList();
                Toast($"{preset.Name}：{preset.Note}");
            }, "ChipBtn");
            btn.Margin = new Thickness(0, 0, 5, 5);
            btn.ToolTip = $"{preset.BaseUrl}\n模型 {preset.Model}\n{preset.Note}";
            wrap.Children.Add(btn);
        }
        return wrap;
    }

    UIElement BuildTestRow(ProviderConfig cfg, ProviderMetaInfo meta)
    {
        var status = UiKit.Text("", 11, "TextFaint", wrap: true);
        status.MaxWidth = 380;

        var test = SmallButton("测试", () => { }, "OutlineBtn");
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            status.Text = "正在测试…";
            status.SetResourceReference(ForegroundProperty, "TextFaint");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var r = await Engine.TestAsync(cfg, cts.Token);
                if (r.Ok)
                {
                    var sample = r.Texts.Values.FirstOrDefault() ?? "";
                    status.Text = $"成功 · {r.ElapsedMs} ms · {Trim(sample, 40)}";
                    status.SetResourceReference(ForegroundProperty, "Success");
                }
                else
                {
                    status.Text = "失败：" + (r.Error ?? "未知错误");
                    status.SetResourceReference(ForegroundProperty, "Danger");
                }
            }
            catch (Exception ex)
            {
                status.Text = "失败：" + ex.Message;
                status.SetResourceReference(ForegroundProperty, "Danger");
            }
            finally { test.IsEnabled = true; }
        };

        var setDefault = SmallButton("设为默认", () =>
        {
            S.PrimaryProviderId = cfg.Id;
            cfg.Enabled = true;
            Save();
            RefreshSourceList();
        });

        var clone = SmallButton("复制一份", () =>
        {
            var copy = cfg.Clone();
            copy.Id = Guid.NewGuid().ToString("N")[..8];
            copy.Name = (string.IsNullOrWhiteSpace(cfg.Name) ? meta.DisplayName : cfg.Name) + " 副本";
            S.Providers.Insert(S.Providers.IndexOf(cfg) + 1, copy);
            _expandedId = copy.Id;
            Save();
            RefreshSourceList();
        });

        var buttons = UiKit.Row(6, test, setDefault, clone);
        if (meta.DocUrl is { } url)
        {
            var doc = SmallButton("申请密钥 ↗", () => OpenUrl(url), "GhostBtn");
            buttons.Children.Add(doc);
            doc.Margin = new Thickness(6, 0, 0, 0);
        }

        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(buttons);
        status.Margin = new Thickness(1, 7, 0, 0);
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>配置改了要让 Registry 重建实例，否则还是用旧密钥。</summary>
    static void Invalidate()
    {
        Engine.Registry.Invalidate();
        Save();
    }

    static string Trim(string text, int max)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
