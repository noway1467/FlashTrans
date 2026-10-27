using System.Windows;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

public partial class MainWindow
{
    // ------------------------------------------------------------- 对外 API

    public void SetInput(string text, bool translate)
    {
        _suppressInput = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressInput = false;
        CharCount.Text = text.Length == 0 ? "" : text.Length + " 字";

        if (translate)
        {
            _debounce.Stop();
            Translate(force: true);
        }
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    // ------------------------------------------------------------- 状态栏

    void UpdateStatus(TranslateBatch? batch = null)
    {
        var parts = new List<string>();

        if (_aggregateSelected)
        {
            var n = S.EnabledProviders.Count();
            parts.Add($"聚合 {n} 个源");
        }
        else
        {
            var cfg = S.Find(_activeProviderId ?? S.PrimaryProviderId);
            if (cfg is not null)
            {
                parts.Add(cfg.DisplayName);
                var cooldown = Engine.Health.SecondsLeft(cfg.Id);
                if (cooldown > 0) parts.Add($"冷却 {cooldown}s");
            }
        }

        if (batch is not null)
        {
            var ok = batch.Results.Count(r => r.Ok);
            var fail = batch.Results.Count - ok;
            if (_aggregateSelected) parts.Add($"成功 {ok}{(fail > 0 ? $" · 失败 {fail}" : "")}");
            if (batch.TotalMs > 0 && S.ShowLatency) parts.Add(batch.TotalMs + " ms");
            if (batch.Notes.Count > 0) parts.Add(batch.Notes[^1]);
        }

        var targets = S.MultiTargetEnabled
            ? string.Join("/", S.MultiTargets.Take(3).Select(Languages.NameOf))
              + (S.MultiTargets.Count > 3 ? "…" : "")
            : Languages.NameOf(S.TargetLang);
        parts.Add("→ " + targets);

        StatusText.Text = string.Join("  ·  ", parts);
        StatusText.SetResourceReference(ForegroundProperty,
            batch?.Results.Any(r => r.Ok) == false ? "Danger" : "TextFaint");
    }

    void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SelectionReader.SetText(text);
        Flash("已复制");
    }

    async void SpeakText(string text, string language)
    {
        var error = await SpeechService.SpeakAsync(text, language);
        Flash(error ?? "正在播放译文");
    }

    void LookupInEudic(string word)
    {
        if (EudicService.Lookup(word)) Flash("已发送到欧路词典");
        else Flash("没有找到欧路词典，请在设置里指定路径");
    }

    void Flash(string message)
    {
        var original = StatusText.Text;
        StatusText.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (StatusText.Text == message) StatusText.Text = original;
        };
        timer.Start();
    }

    // ------------------------------------------------------------- 几何 / 设置

    void RestoreGeometry()
    {
        Width = S.WinWidth;
        Height = S.WinHeight;

        if (ScreenHelper.IsOnScreen(S.WinLeft, S.WinTop, S.WinWidth, S.WinHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = S.WinLeft;
            Top = S.WinTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public void PersistGeometry()
    {
        if (WindowState != WindowState.Normal) return;
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        S.WinLeft = Left;
        S.WinTop = Top;
        S.WinWidth = Width;
        S.WinHeight = Height;
        SettingsService.Instance.Save();
    }

    void ApplySettings()
    {
        Topmost = S.AlwaysOnTop;
        Opacity = S.Opacity;
        FontSize = S.FontSize;
        Input.FontSize = S.FontSize;
        if (!string.IsNullOrWhiteSpace(S.FontFamily))
        {
            try { FontFamily = new FontFamily(S.FontFamily); }
            catch { /* 字体名无效就用默认 */ }
        }

        var pad = S.Compact ? 8.0 : 12.0;
        Input.Padding = new Thickness(pad, S.Compact ? 6 : 8, pad, S.Compact ? 6 : 8);
        Input.MaxHeight = S.Compact ? 132 : 180;
        // TextBox 内部还有约 2px 的文字边距，提示文字要对齐到光标起点。
        InputHint.Margin = new Thickness(pad + 2, Input.Padding.Top, pad, 0);
        InputHint.FontSize = S.FontSize;
        InputHint.Text = S.EnterToTranslate ? "输入或粘贴文本，回车翻译，Shift+回车换行" : "输入或粘贴文本，Ctrl+回车翻译";
        UpdateInputHint();
    }

    void UpdateInputHint() => InputHint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 结果区空白时给出上手指引。输入框里已有「回车翻译」的提示，这里不再重复，
    /// 改列用户当前设置的全局快捷键：划词、截图这些入口不在主窗口里，最容易被忽略。
    /// </summary>
    void ShowWelcome()
    {
        var tips = new List<(string, string)>();
        void Add(string hotkey, string label)
        {
            if (!string.IsNullOrWhiteSpace(hotkey)) tips.Add((HotkeySpec.Parse(hotkey).ToString(), label));
        }
        Add(S.HkTranslateSelection, "翻译其他程序里选中的文字");
        Add(S.HkCaptureOcr, "截图，可识别文字并翻译");
        Add(S.HkTranslateClipboard, "翻译剪贴板内容");
        Add(S.HkQuickLaunch, "呼出快速启动");
        if (tips.Count == 0) { _result.ShowMessage("输入或粘贴文本开始翻译；全局快捷键可在设置里配置"); return; }
        _result.ShowTips("全局快捷键", tips);
    }

    internal void ApplyResultViewSettings() => _result.ApplyViewSettings();

    public void OnSettingsChanged()
    {
        ApplyResultViewSettings();
        var signature = SettingsSignature();
        if (signature == _settingsSignature) return;
        _settingsSignature = signature;

        _pinBtn.IsChecked = S.AlwaysOnTop;
        _bilingualBtn.IsChecked = S.Bilingual;
        _from.SelectedCode = S.SourceLang;
        _to.SelectedCode = S.TargetLang;
        UpdateMultiButton();
        ApplySettings();
        RebuildTabs();
        UpdateStatus(_batch);
        Rerender();
    }

    static string SettingsSignature()
    {
        var s = S;
        var providers = string.Join(";", s.EnabledProviders.Select(p =>
        {
            var options = string.Join(",", p.Options.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => ProviderMeta.IsSecret(kv.Key)
                    ? $"{kv.Key}:{kv.Value.Length}"
                    : $"{kv.Key}:{kv.Value}"));
            return $"{p.Id}:{p.Kind}:{p.DisplayName}:{p.Enabled}:{p.TimeoutMs}:{options}";
        }));

        return string.Join("|",
            s.AggregateTab, s.PrimaryProviderId, s.SourceLang, s.TargetLang, s.SecondaryTarget,
            s.AutoSwapSameLang, s.MultiTargetEnabled, string.Join(",", s.MultiTargets),
            s.Bilingual, s.HideBilingualSource, s.BilingualByParagraph,
            s.FontSize, s.FontFamily, s.Compact, s.Opacity,
            s.AlwaysOnTop,
            // 词典开关也进签名：结果区的「欧路词典」按钮是渲染时按它决定的，
            // 漏掉的话关掉开关后那个按钮要等下一次翻译才消失。
            s.ShowLatency, s.ShowDictionary, s.EudicEnabled, providers);
    }
}
