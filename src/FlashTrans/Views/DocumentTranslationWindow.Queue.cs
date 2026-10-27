using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>批量队列、语言互换和剩余时间估算。队列只存路径，轮到时才读取，避免一次把几十本书都解析进内存。</summary>
public partial class DocumentTranslationWindow
{
    const int MaxQueuedFiles = 100;
    readonly List<string> _queue = [];
    Button _swap = null!;
    bool _batchRunning, _cancelRequested;
    int _batchIndex;

    /// <summary>拖入或多选的文件：第一个立刻读取，其余排队；不支持的类型和重复路径直接略过。</summary>
    internal async Task OpenFilesAsync(IReadOnlyCollection<string> paths)
    {
        if (_busy || _batchRunning) return;
        var supported = paths.Where(DocumentTranslation.Supports).ToList();
        var files = supported.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) { SetStatus("没有可翻译的文件：支持 EPUB、TXT、Markdown、DOCX、文本层 PDF 和 SRT/VTT 字幕。"); return; }
        var overflow = Math.Max(0, files.Count - MaxQueuedFiles);
        _queue.Clear();
        _queue.AddRange(files.Skip(1).Take(MaxQueuedFiles - 1));
        RefreshQueue();
        await LoadFileAsync(files[0]);
        var notes = new List<string>();
        if (paths.Count > supported.Count) notes.Add($"已略过 {paths.Count - supported.Count} 个不支持的文件");
        if (overflow > 0) notes.Add($"一次最多 {MaxQueuedFiles} 个，其余 {overflow} 个未加入");
        if (notes.Count > 0) SetStatus(Status.Text + "；" + string.Join("，", notes));
    }

    /// <summary>
    /// 先翻当前文件，成功后依次读取并翻译队列里的下一个。
    /// 翻译失败或取消就停下，剩余文件留在队列里；读取失败或没有正文的文件跳过，最后汇总说明。
    /// </summary>
    internal async Task StartAsync()
    {
        if (_busy || _batchRunning) return;
        _batchRunning = true; _cancelRequested = false; _batchIndex = 1;
        var saved = 0;
        var skipped = new List<string>();
        try
        {
            var ok = await TranslateFileAsync();
            if (ok) saved++;
            while (ok && _queue.Count > 0 && !_closeWhenIdle && !_cancelRequested)
            {
                var next = _queue[0];
                _queue.RemoveAt(0); RefreshQueue();
                _batchIndex++;
                if (!await LoadFileAsync(next))
                {
                    if (_cancelRequested) { _queue.Insert(0, next); RefreshQueue(); break; }
                    skipped.Add(Path.GetFileName(next)); continue;
                }
                if (_document is not { Count: > 0 }) { skipped.Add(Path.GetFileName(next)); continue; }
                ok = await TranslateFileAsync();
                if (ok) saved++;
            }
            if (_batchIndex > 1 || skipped.Count > 0 || _queue.Count > 0)
            {
                var summary = ok && _queue.Count == 0 ? $"批量完成：已保存 {saved} 个文件" : $"队列已暂停：已保存 {saved} 个，剩余 {_queue.Count} 个";
                if (skipped.Count > 0) summary += $"；跳过 {skipped.Count} 个无法读取或没有正文的文件（{string.Join("、", skipped.Take(3))}{(skipped.Count > 3 ? " 等" : "")}）";
                SetStatus(ok ? summary : Status.Text + "；" + summary);
            }
        }
        finally
        {
            _batchRunning = false; _batchIndex = 0;
            UpdateButtons();
        }
    }

    string BatchPrefix() => _batchRunning && _batchIndex + _queue.Count > 1 ? $"第 {_batchIndex}/{_batchIndex + _queue.Count} 个 · " : "";

    void RefreshQueue()
    {
        QueueList.Children.Clear();
        QueuePanel.Visibility = _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueTitle.Text = $"待翻译队列 · {_queue.Count} 个";
        const int visible = 6;
        foreach (var path in _queue.Take(visible))
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var remove = UiKit.IconButton(UiKit.IconClose, "移出队列（不删除文件）", (_, _) => { _queue.Remove(path); RefreshQueue(); UpdateButtons(); }, 11);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var icon = UiKit.Icon(UiKit.IconDocument, 13);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var name = UiKit.Text(Path.GetFileName(path), 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = path;
            row.Children.Add(name);
            QueueList.Children.Add(row);
        }
        if (_queue.Count > visible)
        {
            var more = UiKit.Text($"… 还有 {_queue.Count - visible} 个", 11, "TextDim");
            more.Margin = new Thickness(21, 4, 0, 0);
            QueueList.Children.Add(more);
        }
        UpdateButtons();
    }

    void OnClearQueue(object sender, RoutedEventArgs e) { _queue.Clear(); RefreshQueue(); }

    void SwapLanguages()
    {
        if (_busy || _from.SelectedCode == Languages.Auto) return;
        var source = _from.SelectedCode;
        _from.SelectedCode = _to.SelectedCode;
        SettingsService.Instance.Current.DocumentSourceLang = _from.SelectedCode;
        RememberTargetLanguage(source); // 一并保存设置并让已有译文失效
        UpdateSwap();
    }

    void UpdateSwap()
    {
        var enabled = _from.SelectedCode != Languages.Auto;
        _swap.IsEnabled = enabled;
        ToolTipService.SetShowOnDisabled(_swap, true);
        _swap.ToolTip = enabled ? "互换原文与目标语言" : "原文为自动检测，选定具体语言后才能互换";
    }

    /// <summary>按本次实际完成的段数估算；续译时之前已完成的段不算进速度，免得一开头就显示「马上完成」。</summary>
    internal sealed class RemainingTimeEstimator
    {
        readonly Stopwatch _watch = Stopwatch.StartNew();
        int? _first;

        public string Describe(int completed, int total) => Describe(completed, total, _watch.Elapsed.TotalSeconds);

        internal string Describe(int completed, int total, double elapsedSeconds)
        {
            _first ??= completed;
            var done = completed - _first.Value;
            if (done < 2 || elapsedSeconds < 3 || completed >= total) return "";
            var seconds = (total - completed) * elapsedSeconds / done;
            if (seconds < 60) return " · 预计不到 1 分钟";
            if (seconds < 3600) return $" · 预计还需 {Math.Ceiling(seconds / 60)} 分钟";
            return $" · 预计还需 {(int)(seconds / 3600)} 小时 {Math.Ceiling(seconds % 3600 / 60)} 分钟";
        }
    }
}
