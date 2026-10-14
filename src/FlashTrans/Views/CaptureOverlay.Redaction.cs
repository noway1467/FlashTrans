using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class CaptureOverlay
{
    enum RedactionState { Idle, Pending, Scanning, Ready, Failed }
    readonly Func<CapturedImage, CancellationToken, Task<IReadOnlyList<OcrTextRegion>>> _recognizeRegions;
    readonly DispatcherTimer _redactionTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    RedactionState _redactionState;
    CancellationTokenSource? _redactionCts;
    int _redactionEpoch;
    bool _redactionRequested;
    Rect? _redactionSelection;
    Panel? _actionRow;
    Border? _redactionStatusRow;
    TextBlock? _redactionStatus;

    void InitializeRedaction()
    {
        _redactionRequested = S.CaptureAutoRedact;
        _redactionTimer.Tick += (_, _) =>
        {
            // 拖选区时只标记待检测，释放鼠标后才抓定格像素，避免每帧启动一次 OCR。
            if (Mouse.Captured is not null) return;
            _redactionTimer.Stop();
            if (!_closing && _redactionRequested && _redactionState == RedactionState.Pending
                && _layer.HasSelection) _ = RedactAsync();
        };
    }

    Border BuildRedactionStatus()
    {
        _redactionStatus = new TextBlock
        {
            Foreground = BarText, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center,
        };
        var manual = ActionBtn("改为手动检查", "停止本次自动检测并保留已有遮挡；确认后自行检查再导出。", UseManualRedaction);
        var dock = new DockPanel();
        DockPanel.SetDock(manual, Dock.Right);
        dock.Children.Add(manual);
        dock.Children.Add(_redactionStatus);
        return _redactionStatusRow = new Border
        {
            Child = dock, BorderBrush = BarEdge, BorderThickness = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed,
        };
    }

    void RedactionMessage(string text)
    {
        if (_redactionStatus is null || _redactionStatusRow is null) return;
        _redactionStatus.Text = text;
        _redactionStatusRow.Visibility = Visibility.Visible;
        PlaceToolbar();
    }

    void SetRedactionBusy(bool busy)
    {
        _layer.IsHitTestVisible = !busy;
        if (_actionRow is not null) _actionRow.IsEnabled = !busy;
    }

    void CancelRedaction()
    {
        _redactionEpoch++;
        _redactionTimer.Stop();
        _redactionCts?.Cancel();
        _redactionCts = null;
        SetRedactionBusy(false);
    }

    void RefreshRedactionSelection()
    {
        if (_redactionSelection == _layer.Selection) return;
        _redactionSelection = _layer.Selection;
        CancelRedaction();
        _redactionState = RedactionState.Idle;
        if (!_redactionRequested || !_layer.HasSelection || _closing) return;
        _redactionState = RedactionState.Pending;
        RedactionMessage("等待选区稳定后在本机检测；完成前不会导出图片。");
        _redactionTimer.Start();
    }

    internal async Task RedactAsync()
    {
        if (_closing || !_layer.HasSelection) return;
        CancelRedaction();
        var epoch = _redactionEpoch;
        _redactionRequested = true;
        _redactionSelection = _layer.Selection;
        _redactionState = RedactionState.Scanning;
        CommitTextInput();
        _layer.Tool = CaptureTool.None;
        SyncToolbar();
        SetRedactionBusy(true);
        RedactionMessage("正在本地检测敏感文字…不上传截图；Esc 取消截图。");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _redactionCts = cts;
        var ct = cts.Token;
        try
        {
            var image = _layer.Export() ?? throw new InvalidOperationException();
            var keywords = S.CaptureRedactionKeywords;
            var keywordsLocked = S.CaptureRedactionKeywordsLocked;
            var result = await Task.Run(async () =>
            {
                if (keywordsLocked) throw new InvalidOperationException("自定义关键词无法解密。");
                _ = SensitiveContentDetector.Keywords(keywords);
                var regions = await _recognizeRegions(image, ct).ConfigureAwait(false);
                return (TextCount: regions.Count(r => !string.IsNullOrWhiteSpace(r.Text)),
                    Areas: SensitiveContentDetector.Detect(regions, image.Width, image.Height, keywords, ct));
            }, ct).WaitAsync(ct).ConfigureAwait(false);
            // 截图使用自建 DispatcherFrame；不能假定调用方总有 WPF SynchronizationContext。
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || epoch != _redactionEpoch) return;
                if (result.TextCount == 0)
                {
                    _redactionState = RedactionState.Failed;
                    RedactionMessage("未识别到文字，无法确认是否含敏感内容。请重试或改为手动检查后再导出。");
                    return;
                }
                var added = _layer.AddRedactions(result.Areas);
                _redactionState = RedactionState.Ready;
                RedactionMessage(added == 0
                    ? "未发现新的敏感文字，不代表没有隐私信息；请核对，可用实心遮挡补漏。"
                    : $"新增 {added} 块实心遮挡（整行/整块），可拖动、删除和撤销；仍需人工检查漏项。");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || epoch != _redactionEpoch) return;
                _redactionState = RedactionState.Failed;
                // 不展示或记录引擎异常正文，它可能包含原文、模型路径或其他敏感上下文。
                RedactionMessage(ex is OperationCanceledException
                    ? "检测已超时，未放行导出。请缩小选区重试，或改为手动检查。"
                    : "检测失败，未放行导出。请检查 OCR 引擎和关键词设置后重试，或改为手动检查。");
            });
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || epoch != _redactionEpoch) return;
                _redactionCts = null; SetRedactionBusy(false); SyncToolbar();
            });
        }
    }

    void UseManualRedaction()
    {
        CancelRedaction();
        _redactionRequested = false;
        _redactionState = RedactionState.Idle;
        RedactionMessage("本次自动检测已关闭，已有遮挡保留。请用实心遮挡手动补漏，并自行检查后导出。");
    }

    bool CanExportRedaction()
    {
        if (_redactionState is RedactionState.Pending or RedactionState.Scanning) return false;
        return _redactionState != RedactionState.Failed;
    }

    bool CanLeaveRedaction()
    {
        if (!_redactionRequested && !_layer.HasRedactions && CanExportRedaction()) return true;
        RedactionMessage("长截图和录制不继承此处的打码。请先改为手动检查并移除实心遮挡，或直接复制/保存当前图片。");
        return false;
    }
}
