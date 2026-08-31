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
    }

    public void OnSettingsChanged()
    {
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
            s.Bilingual, s.BilingualByParagraph, s.FontSize, s.FontFamily, s.Compact, s.Opacity,
            s.AlwaysOnTop,
            s.ShowLatency, s.ShowDictionary, providers);
    }
}
