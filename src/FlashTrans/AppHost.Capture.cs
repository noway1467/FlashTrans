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
            await HandleAsync(picked.Action, picked.Image);
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
    async Task HandleAsync(CaptureAction action, CapturedImage? image)
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

            case CaptureAction.Ocr:
            case CaptureAction.OcrTranslate:
                await OcrAsync(image, translate: action == CaptureAction.OcrTranslate);
                break;
        }
    }

    /// <summary>识别这块图里的文字，送进主窗口或者直接弹翻译。</summary>
    async Task OcrAsync(CapturedImage shot, bool translate)
    {
        if (!OcrService.IsAvailable)
        {
            // 图还在手上，别让用户白截一次——先塞进剪贴板再报错
            var copied = TrySetClipboardImage(shot);
            AppDialog.Info(_main is { IsVisible: true } ? _main : null, "缺少文字识别语言包",
                "系统里没装可用的 OCR 语言包，暂时不能识别截图里的文字。",
                tone: DialogTone.Warning,
                detail: OcrService.NoEngineHint()
                        + (copied ? "\n刚才那张图已经复制到剪贴板了，可以先粘出去。" : ""));
            return;
        }

        var text = await RecognizeAsync(shot, null);
        if (text is null) return;

        if (translate)
        {
            if (S.OcrCopyText) TrySetClipboardBoth(shot, text);
            Point? anchor = S.PopupPlace == PopupPlace.NearMouse
                ? ScreenHelper.ToDip(ScreenHelper.CursorPos(), _popup)
                : null;
            ShowPopupFor(text, anchor);
        }
        else
        {
            // 「识别文字」：把字摆在一个能改的框里。识别难免有错字，
            // 直接进剪贴板的话用户得粘出去才发现错了。
            // 不往主窗口的输入框里塞——要翻译有「识别并翻译」，
            // 这个按钮是把图上的字拿去用在别处。
            ShowOcrResult(text);
        }
    }

    /// <summary>摆出识别结果，让用户改完再挑复制还是翻译。</summary>
    void ShowOcrResult(string text)
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
    async Task<string?> RecognizeAsync(CapturedImage shot, string? saved)
    {
        var lang = string.IsNullOrWhiteSpace(S.OcrLang) ? S.SourceLang : S.OcrLang;
        var kept = saved is null ? "" : $"（图片已存：{Path.GetFileName(saved)}）";
        string text;
        try
        {
            // 识别是 CPU 活儿，别占着界面线程
            text = await Task.Run(() => OcrService.RecognizeAsync(shot, lang));
        }
        catch (InvalidOperationException ex)
        {
            Toast(ex.Message + kept);
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Toast($"没识别出文字（{shot.Width}×{shot.Height}）。试试选大一点，或者换个识别语言{kept}");
            return null;
        }
        return text.Trim();
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
            LongShotStop.Diverged => "后面的画面接不上，停在这里。",
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

        var hud = new RecordHud(region, maxSec);
        hud.Show();

        // 蒙层刚关，还没真从屏幕上下去。不等一下，头几帧录进去的是那层黑蒙层。
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(250);

        RecordFrames? frames = null;
        try
        {
            frames = await RecordService.RunAsync(region, fps, maxSec,
                onProgress: hud.Report,
                cancelled: () => hud.Stopped);

            if (frames.Stopped == RecordStop.Failed || frames.Paths.Count == 0)
            {
                Toast("录制没成功，一帧都没抓到");
                return;
            }

            hud.ReportEncoding(frames.Paths.Count);
            var result = await AnimEncoder.SaveAsync(
                frames.Paths, UniqueRecordPath(), frames.EffectiveFps > 0
                    ? (int)Math.Round(frames.EffectiveFps) : fps,
                S.RecordFormat);

            var note = result.FellBackToGif ? "（没找到 img2webp，存成了 GIF）" : "";
            Toast($"已保存：{Path.GetFileName(result.Path)}"
                  + $"（{Mb(result.Bytes)} · {frames.Paths.Count} 帧 · "
                  + $"{frames.EffectiveFps:0.#} fps）{note}",
                () => RevealInExplorer(result.Path));
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
        var dir = CaptureDir();
        Directory.CreateDirectory(dir);
        var stem = $"闪译录制 {DateTime.Now:yyyy-MM-dd HHmmss}";
        var path = Path.Combine(dir, stem);
        for (var i = 2; File.Exists(path + ".webp") || File.Exists(path + ".gif"); i++)
            path = Path.Combine(dir, $"{stem}({i})");
        return path;
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

    static void TrySetClipboard(string text)
    {
        try { SelectionReader.SetText(text); }
        catch (Exception ex) { Log.Warn("写剪贴板失败：" + ex.Message); }
    }

    /// <summary>复制图片。成功返回 true——调用方要据此决定提示说什么。</summary>
    bool TrySetClipboardImage(CapturedImage shot)
    {
        try
        {
            Clipboard.SetImage(shot.ToBitmap());
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
            var data = new DataObject();
            data.SetImage(shot.ToBitmap());
            data.SetText(text);
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex)
        {
            Log.Warn("复制图片和文字失败：" + ex.Message);
            TrySetClipboard(text);   // 退一步，至少把文字放进去
        }
    }
}
