using System.Windows;
using System.Windows.Controls;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    bool _startupChanging;
    string? _startupError;

    UIElement BuildStartupOptions()
    {
        var panel = new StackPanel();
        var enabled = new CheckBox { Name = "StartupEnabled", Content = "开机自动启动", FontSize = 12.5 };
        var administrator = new CheckBox { Name = "StartupAsAdmin", Content = "以管理员权限自启动",
            FontSize = 12.5, Margin = new Thickness(0, 9, 0, 0) };
        var hint = Description("用于任务管理器、火绒等高权限窗口；首次开启需确认系统授权。");
        hint.Margin = new Thickness(0, 4, 0, 0);
        var status = Description("");
        status.Name = "StartupStatus";
        status.Margin = new Thickness(0, 6, 0, 0);
        var retry = new Button { Content = "重新应用启动设置", FontSize = 11.5, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        retry.SetResourceReference(StyleProperty, "OutlineBtn");
        panel.Children.Add(enabled);
        panel.Children.Add(administrator);
        panel.Children.Add(hint);
        panel.Children.Add(status);
        panel.Children.Add(retry);
        Refresh(_startupError);

        enabled.Click += async (_, _) => await Apply(enabled.IsChecked == true, S.StartupAsAdmin);
        administrator.Click += async (_, _) => await Apply(S.RunAtStartup, administrator.IsChecked == true);
        retry.Click += async (_, _) => await Apply(S.RunAtStartup, S.StartupAsAdmin);
        return panel;

        void Refresh(string? error = null)
        {
            enabled.IsChecked = S.RunAtStartup;
            administrator.IsChecked = S.StartupAsAdmin;
            enabled.IsEnabled = !_startupChanging;
            administrator.IsEnabled = !_startupChanging && S.RunAtStartup;
            retry.IsEnabled = !_startupChanging;
            var matches = error is null && StartupService.IsConfigured(S.RunAtStartup, S.StartupAsAdmin);
            status.Text = error ?? (!matches ? "启动登记与设置不一致，请重新应用。"
                : !S.RunAtStartup ? "已关闭开机自启动。"
                : S.StartupAsAdmin ? "已启用管理员自启动，下次登录生效。" : "已启用普通权限自启动。");
            status.SetResourceReference(TextBlock.ForegroundProperty, matches ? "TextDim" : "Warn");
            retry.Visibility = matches ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task Apply(bool want, bool asAdmin)
        {
            if (_startupChanging) return;
            _startupChanging = true;
            enabled.IsEnabled = administrator.IsEnabled = retry.IsEnabled = false;
            status.Text = "正在应用启动设置，请完成可能出现的系统授权…";
            _startupError = null;
            try
            {
                var error = await StartupService.ChangeAsync(want, asAdmin);
                _startupError = error;
                if (error is null)
                {
                    S.RunAtStartup = want;
                    S.StartupAsAdmin = asAdmin;
                    Save();
                    Toast(want && asAdmin ? "管理员自启动已启用，下次登录自动生效。" : "启动设置已更新。");
                }
            }
            finally
            {
                _startupChanging = false;
                Refresh(_startupError);
                // 授权期间切出再切回通用页时，恢复新页面上的控件状态。
                if (IsLoaded && _selectedPageKey == "general" && !panel.IsLoaded) SelectTab("general");
            }
        }
    }
}
