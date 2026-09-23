using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans;

public sealed partial class AppHost
{
    bool _capturing;

    /// <summary>
    /// 截图。框选、画标注，然后由用户从工具条挑一个动作：复制、保存、识别、
    /// 识别并翻译，或者转成长截图接着往下滚。
    /// </summary>
    public async Task CaptureAsync()
    {
        if (_capturing) return;   // 热键连按两次不要叠两层蒙层

        _capturing = true;
        try
        {
            // 弹窗和划词图标会挡住要截的内容，先收走。
            // 主窗口留着：用户可能就是想截它里面的东西。
            // 收起而不是关掉：这是我们为了拍图把它挪开，不是用户不要了，
            // 取消截图后还能按快捷键把它叫回来。
            _popup?.StashPopup();
            HideSelectionIcon();
            // 让上面两个窗口真的从屏幕上消失再抓图，否则会被拍进去
            await Task.Yield();
            await Dispatcher.Yield(DispatcherPriority.Render);

            var picked = CaptureOverlay.Pick();

            if (picked.WantsLongShot)
            {
                await LongShotAsync(picked.Region);
                return;
            }
            if (picked.WantsRecord)
            {
                await RecordAsync(picked.Region);
                return;
            }
            await HandleAsync(picked.Action, picked.Image, picked.Region);
        }
        catch (Exception ex)
        {
            Log.Error("截图失败", ex);
            Toast("截图失败：" + ex.Message);
        }
        finally
        {
            _capturing = false;
        }
    }

    /// <summary>把截好的图交给用户挑的那个动作。image 为 null（取消）就什么都不做。</summary>
    async Task HandleAsync(CaptureAction action, CapturedImage? image, RECT? region = null)
    {
        if (image is null || action == CaptureAction.None) return;

        switch (action)
        {
            case CaptureAction.Copy:
                if (TrySetClipboardImage(image)) Toast($"截图 {image.Width}×{image.Height} 已复制");
                break;

            case CaptureAction.Save:
                var path = S.CaptureSaveAsk ? SaveShotAs(image) : SaveShot(image);
                if (path is not null) ToastSaved(path);
                break;

            case CaptureAction.Pin:
                ShowPinnedShot(image, region);
                break;

            case CaptureAction.Ocr:
            case CaptureAction.OcrCopy:
            case CaptureAction.OcrTranslate:
                await OcrAsync(image,
                    translate: action == CaptureAction.OcrTranslate,
                    copyDirectly: action == CaptureAction.OcrCopy);
                break;
        }
    }

    /// <summary>识别这块图里的文字，送进主窗口或者直接弹翻译。</summary>
    async Task OcrAsync(CapturedImage shot, bool translate, bool copyDirectly = false)
    {
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        PopupWindow? popup = null;
        OcrResultWindow? result = null;
        if (translate)
        {
            Point? anchor = S.PopupPlace == PopupPlace.NearMouse
                ? ScreenHelper.ToDip(ScreenHelper.CursorPos(), _popup)
                : null;
            HideSelectionIcon();
            popup = EnsurePopup();
            token = popup.ShowOcrLoading(anchor);
        }
        else if (!copyDirectly)
        {
            result = CreateOcrResult("");
            result.ShowRecognizing();
            result.Closed += OnResultClosed;
            result.Show();
            result.Activate();
        }

        try
        {
            // 模型枚举也可能冷启动 WinRT，先让窗口完成首轮布局再到后台检查。
            await Dispatcher.Yield(DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            var available = await Task.Run(() => OcrService.RapidModelsPresent || OcrService.IsAvailable, token);
            token.ThrowIfCancellationRequested();
            if (!available)
            {
                popup?.ClosePopup();
                result?.Close();
                // 图还在手上，别让用户白截一次——先塞进剪贴板再报错。
                var copied = TrySetClipboardImage(shot);
                AppDialog.Info(_main is { IsVisible: true } ? _main : null, "缺少文字识别引擎",
                    "系统 OCR 语言包和 RapidOCR 本地模型都没就位，暂时不能识别截图里的文字。",
                    tone: DialogTone.Warning,
                    detail: OcrService.NoEngineHint() + "\n\n" + OcrService.RapidModelsHint()
                        + (copied ? "\n刚才那张图已经复制到剪贴板了，可以先粘出去。" : ""));
                return;
            }

            var text = await RecognizeAsync(shot, null, token, ReportError);
            token.ThrowIfCancellationRequested();
            if (text is null) return;

            if (popup is not null)
            {
                // CompleteOcr 会切换到翻译令牌；先确认归属，旧结果不能覆盖新翻译或剪贴板。
                if (!popup.IsOcrRequestCurrent(token)) return;
                if (S.OcrCopyText) TrySetClipboardBoth(shot, text);
                popup.CompleteOcr(token, text);
            }
            else if (copyDirectly)
            {
                if (TrySetClipboard(text)) Toast(CopiedToast(text));
                else Toast("复制 OCR 结果失败");
            }
            else result?.SetRecognitionResult(text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* 关闭或被新内容取代 */ }
        catch (Exception ex)
        {
            Log.Error("截图文字识别失败", ex);
            ReportError("识别失败：" + ex.Message);
        }
        finally
        {
            if (result is not null) result.Closed -= OnResultClosed;
        }

        void OnResultClosed(object? sender, EventArgs e) => lifetime.Cancel();
        void ReportError(string message)
        {
            if (token.IsCancellationRequested) return;
            if (popup is not null) popup.FailOcr(token, message);
            else if (result is not null) result.SetRecognitionError(message);
            else Toast(message);
        }
    }

    /// <summary>摆出识别结果，让用户改完再挑复制还是翻译。</summary>
    OcrResultWindow CreateOcrResult(string text)
    {
        var win = new OcrResultWindow(text);
        win.Copy += t =>
        {
            TrySetClipboard(t);
            Toast(CopiedToast(t));
        };
        win.Translate += t =>
        {
            Point? anchor = S.PopupPlace == PopupPlace.NearMouse
                ? ScreenHelper.ToDip(ScreenHelper.CursorPos(), _popup)
                : null;
            ShowPopupFor(t, anchor);
        };
        win.OpenSettings += () => ShowSettings();
        return win;
    }

    /// <summary>钉住截图。复制和保存继续走主程序的既有路径，销毁只关钉住窗。</summary>
    void ShowPinnedShot(CapturedImage image, RECT? region)
    {
        var win = new PinnedShotWindow(image, region);
        win.CopyRequested += () =>
        {
            if (TrySetClipboardImage(image))
                Toast($"截图 {image.Width}×{image.Height} 已复制");
        };
        win.SaveRequested += () =>
        {
            // 另存为对话框不是这个置顶贴图的拥有者，不先撤顶会被挡在后面。
            var wasTopmost = win.Topmost;
            try
            {
                win.Topmost = false;
                var path = S.CaptureSaveAsk ? SaveShotAs(image) : SaveShot(image);
                if (path is not null) ToastSaved(path);
            }
            finally { win.Topmost = wasTopmost; }
        };
        win.Show();
        win.Activate();
    }

    /// <summary>复制成功的提示。字少直接显示，字多显示前一截加字数。</summary>
    static string CopiedToast(string text)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 22
            ? $"已复制：{one}"
            : $"已复制 {text.Length} 个字：{one[..22]}…";
    }

    /// <summary>
    /// 识别，顺带把「没认出字」和「引擎缺失」分开提示。返回 null 表示不用往下走了。
    /// saved 是图片存到了哪儿：识别失败时也要告诉用户图还在，别让人以为整个白截了。
    /// </summary>
    async Task<string?> RecognizeAsync(CapturedImage shot, string? saved,
        CancellationToken token, Action<string> reportError)
    {
        // OCR 的自动检测独立于翻译源语言；不能被主窗口上次选中的语种锁住。
        var lang = S.OcrLang;
        var engine = S.OcrEngine;
        var kept = saved is null ? "" : $"（图片已存：{Path.GetFileName(saved)}）";
        string text;
        try
        {
            // 识别是 CPU 活儿，别占着界面线程
            text = await Task.Run(() => OcrService.RecognizeAsync(shot, lang, token, engine), token);
            token.ThrowIfCancellationRequested();
        }
        catch (InvalidOperationException ex)
        {
            reportError(ex.Message + kept);
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            reportError($"没识别出文字（{shot.Width}×{shot.Height}）。试试选大一点，或者换个识别语言{kept}");
            return null;
        }
        return text.TrimEnd();
    }

    /// <summary>存图。返回存到哪儿了，失败返回 null（已经提示过用户）。</summary>
    string? SaveShot(CapturedImage shot)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(ShotName());
            var path = Path.Combine(CaptureDir(), name + ".png");
            // 同一秒内截第二张不要覆盖前一张
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(CaptureDir(), $"{name}({i}).png");

            shot.SavePng(path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
            Toast("保存截图失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 长截图：在刚选好的区域上滚着截，拼成一张长图，然后弹预览让用户挑动作。
    /// </summary>
    async Task LongShotAsync(RECT region)
    {
        var hud = new LongShotHud(region);
        hud.Show();

        // 蒙层刚关，还没真从屏幕上下去。不等一下，第一屏拍到的是那层黑蒙层和工具条，
        // 后面每一屏都是干净页面——拼出来就是「开头一段带着工具，下面才是正文」。
        // 跟录制那条一样等一拍：先让 WPF 把关闭渲染完，再给 DWM 一点时间合成。
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(250);

        LongShotResult result;
        try
        {
            result = await LongShotService.RunAsync(region,
                onProgress: hud.Report,
                cancelled: () => hud.Cancelled);
        }
        finally
        {
            hud.Close();
        }

        if (result.Image is null)
        {
            Toast("长截图没成功。这块区域可能滚不动，换个能滚的地方再试");
            return;
        }
        var note = result.Stopped switch
        {
            LongShotStop.Cancelled => "已停在这里。",
            LongShotStop.Limit => "到长度上限了，后面的没接。",
            LongShotStop.Diverged => "页面仍在刷新，已停在最后完整区域。",
            _ => "",
        };
        // 一帧都没接上（这块区域滚不动）也照样弹预览。以前这种情况直接按
        // 「回车动作」处理掉了，默认那个是复制，结果就是「有时候不弹保存框」——
        // 弹不弹取决于页面滚没滚动，用户根本没法预料。存图的按钮在预览里，
        // 所以只要有图就得让预览出来。
        if (result.Frames <= 1)
            note = string.IsNullOrEmpty(note) ? "这块区域没滚动，就截到这一屏。" : note;

        ShowLongShotPreview(result.Image, note);
    }

    /// <summary>
    /// 录制动图：在刚选好的区域上按节拍抓帧，编成 WebP（或 GIF）存进截图目录。
    ///
    /// 不弹「另存为」：录完已经等了一段时间，再拦一个对话框太啰嗦。
    /// 直接存进截图目录，用提示条给个「点一下定位到文件」。
    /// </summary>
    async Task RecordAsync(RECT region)
    {
        var fps = RecordService.ClampFps(S.RecordFps);
        var maxSec = RecordService.ClampSeconds(S.RecordMaxSeconds);
        var captureAudio = S.RecordAudio && S.RecordFormat == RecordFormat.Mp4;

        var hud = new RecordHud(region, maxSec, captureAudio);
        // 编码阶段的取消要靠它去掐：那时候录制循环已经退出，没人再轮询 hud.Cancelled 了。
        using var cancelEncode = new CancellationTokenSource();
        hud.CancelRequested += () =>
        {
            // Cancel() 可能在编码已经结束之后才被按下，这时 CTS 已经 Dispose 了。
            try { cancelEncode.Cancel(); }
            catch (ObjectDisposedException) { }
        };
        hud.Show();

        // 蒙层刚关，还没真从屏幕上下去。不等一下，头几帧录进去的是那层黑蒙层。
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(250);

        RecordFrames? frames = null;
        try
        {
            frames = await RecordService.RunAsync(region, fps, maxSec,
                onProgress: hud.Report,
                cancelled: () => hud.Stopped,
                paused: () => hud.Paused,
                captureAudio: captureAudio,
                muted: () => hud.Muted,
                discarded: () => hud.Cancelled);

            // 取消要排在「没抓到帧」前面：刚开录就取消本来就是 0 帧，
            // 那不是失败，不能报「录制没成功」。
            if (frames.Stopped == RecordStop.Cancelled)
            {
                Toast("已取消录制，没有保存");
                return;
            }

            if (frames.Stopped == RecordStop.Failed || frames.Paths.Count == 0)
            {
                Toast("录制没成功，一帧都没抓到");
                return;
            }

            hud.ReportEncoding(frames.Paths.Count);
            var result = await AnimEncoder.SaveAsync(
                frames.Paths, UniqueRecordPath(), frames.EffectiveFps > 0
                    ? (int)Math.Round(frames.EffectiveFps) : fps,
                S.RecordFormat, frames.AudioPath, cancelEncode.Token);

            var note = result.FellBack
                ? $"（{result.FellBackWhy}，存成了 {result.Format.ToString().ToUpperInvariant()}）"
                : "";
            // 暂停过就说一声：文件里那 12 秒是刨掉暂停之后的，不说的话用户会觉得少了。
            if (frames.Pauses > 0)
                note += $"（暂停 {frames.Pauses} 次，共 {frames.PausedFor.TotalSeconds:0} 秒，没录进去）";
            if (frames.Stopped == RecordStop.PausedTooLong)
                note += $"（暂停超过 {RecordService.MaxPausedMinutes} 分钟，自己收了）";
            Toast($"已保存：{Path.GetFileName(result.Path)}"
                  + $"（{Mb(result.Bytes)} · {frames.Paths.Count} 帧 · "
                  + $"{frames.EffectiveFps:0.#} fps）{note}",
                () => RevealInExplorer(result.Path));
        }
        catch (OperationCanceledException)
        {
            // 编码到一半被取消的。半成品文件由各个编码器自己删掉了，
            // 临时帧交给下面的 finally。
            Toast("已取消录制，没有保存");
        }
        catch (Exception ex)
        {
            Log.Error("录制失败", ex);
            Toast("录制失败：" + ex.Message);
        }
        finally
        {
            hud.Close();
            frames?.Cleanup();
        }
    }

    static string Mb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{bytes / 1024.0:0} KB";

    /// <summary>
    /// 录制文件的路径，不带扩展名——真正存成什么后缀要等编码完才知道
    /// （要 WebP 但没有 img2webp 时会退回 GIF）。
    /// 两种后缀都占用了才算重名，免得 a.webp 存在时把 a.gif 也顶掉。
    /// </summary>
    static string UniqueRecordPath()
    {
        var dir = RecordDir();
        Directory.CreateDirectory(dir);
        var stem = $"闪译录制 {DateTime.Now:yyyy-MM-dd HHmmss}";
        var path = Path.Combine(dir, stem);
        // 三个后缀都要查：可能编 MP4 失败退回了 WebP，跟上一次的重名。
        for (var i = 2; Taken(path); i++) path = Path.Combine(dir, $"{stem}({i})");
        return path;

        static bool Taken(string p)
            => File.Exists(p + ".webp") || File.Exists(p + ".gif") || File.Exists(p + ".mp4");
    }

    /// <summary>弹长图预览。用户在这儿挑存哪儿、要不要识别。</summary>
    void ShowLongShotPreview(CapturedImage image, string note)
    {
        var win = new LongShotWindow(image, note);
        win.Action += action => _ = HandleAsync(action, image);
        win.Show();
        win.Activate();
    }

    /// <summary>弹「另存为」再存。用户取消返回 null。</summary>
    string? SaveShotAs(CapturedImage shot)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = ShotName(),
            InitialDirectory = Directory.Exists(CaptureDir()) ? CaptureDir() : null,
        };
        if (dlg.ShowDialog() != true) return null;

        try
        {
            shot.SavePng(dlg.FileName);
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
            Toast("保存截图失败：" + ex.Message);
            return null;
        }
    }

    static string ShotName() => $"闪译截图 {DateTime.Now:yyyy-MM-dd HHmmss}.png";

    /// <summary>图片存放目录。没设过就用「图片」文件夹下的 FlashTrans。</summary>
    public static string CaptureDir()
    {
        var dir = SettingsService.Instance.Current.CaptureSaveDir;
        if (!string.IsNullOrWhiteSpace(dir)) return dir;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FlashTrans");
    }

    /// <summary>录制文件存放目录。没单独设过就跟截图放一起。</summary>
    public static string RecordDir()
    {
        var dir = SettingsService.Instance.Current.RecordSaveDir;
        return string.IsNullOrWhiteSpace(dir) ? CaptureDir() : dir;
    }

    /// <summary>报告存到哪儿了。点一下能直接在资源管理器里定位到那张图。</summary>
    void ToastSaved(string path)
        => Toast($"已保存：{Path.GetFileName(path)}", () => RevealInExplorer(path));

    /// <summary>在资源管理器里选中这个文件。</summary>
    static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                // 路径里可能有空格，得带引号；/select, 后面那个逗号是它的写法要求
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Log.Warn("打开资源管理器失败：" + ex.Message); }
    }

    static bool TrySetClipboard(string text)
    {
        try
        {
            SelectionReader.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("写剪贴板失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>复制图片。成功返回 true——调用方要据此决定提示说什么。</summary>
    bool TrySetClipboardImage(CapturedImage shot)
    {
        try
        {
            SelectionReader.SetImage(shot);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("复制图片失败：" + ex.Message);
            Toast("复制图片失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>图片和文字一起放进剪贴板，粘到哪儿由对方挑格式。</summary>
    void TrySetClipboardBoth(CapturedImage shot, string text)
    {
        try
        {
            SelectionReader.SetImageAndText(shot, text);
        }
        catch (Exception ex)
        {
            Log.Warn("复制图片和文字失败：" + ex.Message);
            TrySetClipboard(text);   // 退一步，至少把文字放进去
        }
    }
}
