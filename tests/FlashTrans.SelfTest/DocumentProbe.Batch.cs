using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static partial class DocumentProbe
{
    static void RunBatchProbes(Action<string, Action> step)
    {
        step("文件：SRT/VTT 字幕只译对白，序号、时间轴、NOTE/STYLE 原样保留", () => InTemp(SubtitleRoundtrip));
        step("文件：剩余时间按本次速度估算，续译不虚报", RemainingTime);
        step("文件：原文/目标语言互换、自动检测时禁用并落盘", () => InTemp(SwapLanguages));
        step("文件：批量队列逐个保存、跳过坏文件、失败暂停保留剩余", () => InTemp(BatchQueue));
    }

    static void SubtitleRoundtrip(string root)
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,500\r\n<i>Hello</i>\r\nworld again\r\n\r\n"
            + "3\r\n00:00:05,000 --> 00:00:06,000\r\n♪ ♪\r\n\r\n10\r\n00:01:00,000 --> 00:01:02,000\r\n42\r\n";
        var translator = new TestTranslator();
        var doc = Translate(PutText(root, "movie.srt", srt), translator);
        Check(translator.Inputs.SequenceEqual(new[] { "Hello world", "<i>Hello</i> world again" }), "字幕送翻内容错误：" + string.Join(" | ", translator.Inputs));
        Check(doc.Warnings.Any(w => w.Contains("合并为一行")), "多行字幕没有提示合并");
        var output = Path.Combine(root, "movie_zh.srt");
        doc.SaveAs(output);
        var bytes = File.ReadAllBytes(output);
        Check(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "字幕输出缺少 UTF-8 BOM");
        var expected = "1\r\n00:00:01,000 --> 00:00:02,000\r\n你好 世界\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,500\r\n<i>你好</i> 世界 again\r\n\r\n"
            + "3\r\n00:00:05,000 --> 00:00:06,000\r\n♪ ♪\r\n\r\n10\r\n00:01:00,000 --> 00:01:02,000\r\n42\r\n";
        Check(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == expected, "SRT 序号/时间轴/换行没有原样保留");

        const string vtt = "WEBVTT - Hello header\n\nNOTE Hello note\nworld note\n\nSTYLE\n::cue { color: yellow }\n\n"
            + "intro\n00:00:01.000 --> 00:00:02.000 align:start\nHello world\n\n00:00:03.000 --> 00:00:04.000\nworld";
        translator = new TestTranslator();
        doc = Translate(PutText(root, "talk.vtt", vtt), translator);
        Check(translator.Inputs.SequenceEqual(new[] { "Hello world", "world" }), "VTT 把头部、NOTE、STYLE 或 cue 标识送去翻译了");
        output = Path.Combine(root, "talk_zh.vtt");
        doc.SaveAs(output);
        var text = File.ReadAllText(output);
        Check(text == vtt.Replace("align:start\nHello world", "align:start\n你好 世界").Replace("04.000\nworld", "04.000\n世界"), "VTT 结构没有保留：" + text);
        Throws<InvalidDataException>(() => DocumentTranslation.Load(PutText(root, "plain.srt", "Hello world\n")));
        Check(DocumentTranslation.Supports("a.SRT") && DocumentTranslation.Supports("a.vtt") && DocumentTranslation.FileFilter.Contains("*.srt"), "字幕未加入支持列表");
    }

    static void RemainingTime()
    {
        var eta = new DocumentTranslationWindow.RemainingTimeEstimator();
        Check(eta.Describe(0, 100, 0) == "" && eta.Describe(1, 100, 10) == "", "样本太少时不应估算");
        Check(eta.Describe(10, 100, 30) == " · 预计还需 5 分钟", "估算错误：" + eta.Describe(10, 100, 30));
        Check(eta.Describe(99, 100, 50) == " · 预计不到 1 分钟" && eta.Describe(100, 100, 60) == "", "收尾估算错误");
        var resumed = new DocumentTranslationWindow.RemainingTimeEstimator();
        Check(resumed.Describe(50, 100, 0) == "" && resumed.Describe(52, 100, 4) == " · 预计还需 2 分钟", "续译把已完成段算进速度");
        var slow = new DocumentTranslationWindow.RemainingTimeEstimator();
        slow.Describe(0, 5000, 0);
        // 剩 4990 段 × 60 秒 ÷ 10 段 = 29940 秒 = 8 小时 19 分。
        Check(slow.Describe(10, 5000, 60) == " · 预计还需 8 小时 19 分钟", "小时格式错误：" + slow.Describe(10, 5000, 60));
    }

    static void SwapLanguages(string root)
    {
        var settings = SettingsService.Instance.Current;
        var (oldSource, oldTarget) = (settings.DocumentSourceLang, settings.DocumentTargetLang);
        var window = new DocumentTranslationWindow(new DocumentHistoryService(root)) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var from = (LangPicker)window.FromHost.Content; var to = (LangPicker)window.ToHost.Content;
            var swap = (Button)window.SwapHost.Content;
            from.SelectedCode = Languages.Auto;
            typeof(DocumentTranslationWindow).GetMethod("UpdateSwap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            Check(!swap.IsEnabled, "自动检测时仍可互换");
            from.SelectedCode = "en"; to.SelectedCode = "ja";
            typeof(DocumentTranslationWindow).GetMethod("UpdateSwap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            Check(swap.IsEnabled, "选定语言后不能互换");
            swap.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(from.SelectedCode == "ja" && to.SelectedCode == "en", "互换没有生效");
            var disk = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(SettingsService.Instance.ConfigPath), SettingsJson.Default.AppSettings)!;
            Check(disk.DocumentSourceLang == "ja" && disk.DocumentTargetLang == "en", "互换后没有落盘");
        }
        finally
        {
            window.Close();
            settings.DocumentSourceLang = oldSource; settings.DocumentTargetLang = oldTarget;
            SettingsService.Instance.Save();
        }
    }

    static void BatchQueue(string root)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var requests = 0;
        // 本机假接口：正文含 FAIL 时返回 400，其余一律回「你好世界」。每个连接只处理一个请求。
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    using var data = new MemoryStream();
                    var buffer = new byte[4096];
                    string received;
                    while (true)
                    {
                        var count = await stream.ReadAsync(buffer, stop.Token);
                        if (count == 0) throw new IOException("请求提前断开");
                        data.Write(buffer, 0, count);
                        received = Encoding.UTF8.GetString(data.ToArray());
                        var headerEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd < 0) continue;
                        var match = Regex.Match(received[..headerEnd], @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (match.Success && data.Length >= headerEnd + 4 + int.Parse(match.Groups[1].Value)) break;
                    }
                    Interlocked.Increment(ref requests);
                    var fail = received.Contains("FAIL", StringComparison.Ordinal);
                    var body = Encoding.UTF8.GetBytes(fail ? "{\"error\":{\"message\":\"self-test failure\"}}" : "{\"choices\":[{\"message\":{\"content\":\"你好世界\"}}]}");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(fail ? "400 Bad Request" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(body, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var settings = SettingsService.Instance.Current;
        var (oldSource, oldTarget, oldBilingual, oldDelay) = (settings.DocumentSourceLang, settings.DocumentTargetLang, settings.DocumentBilingualOutput, settings.DocumentRequestDelayMs);
        settings.DocumentBilingualOutput = false;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var cfg = ProviderConfig.Create(ProviderKind.OpenAiCompat); cfg.Options["baseUrl"] = $"http://127.0.0.1:{port}/v1"; cfg.Options["model"] = "test-local"; cfg.Options["apiKey"] = "";
            var first = PutText(root, "a.txt", "Hello world");
            var empty = PutText(root, "empty.txt", "123\n");
            var broken = PutText(root, "broken.docx", "not zip");
            var subtitle = PutText(root, "c.srt", "1\n00:00:01,000 --> 00:00:02,000\nHello world\n");
            var history = new DocumentHistoryService(Path.Combine(root, "history"));
            var window = new DocumentTranslationWindow(history) { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); window.SetOutputDirectory("");
                window.RequestDelayBox.Text = "0";
                window.Providers.Items.Clear();
                window.Providers.Items.Add(new ComboBoxItem { Content = cfg.DisplayName, Tag = cfg });
                window.Providers.SelectedIndex = 0;
                ((LangPicker)window.FromHost.Content).SelectedCode = "en";
                ((LangPicker)window.ToHost.Content).SelectedCode = "zh-CN";
                Check(window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { "bad.exe", first })), "混有不支持类型的多文件拖入被整体拒绝");
                Check(!window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { "bad.exe", "x.zip" })), "全是不支持类型却接受了拖入");
                PumpUntil(window.OpenFilesAsync([first, first.ToUpperInvariant(), empty, Path.Combine(root, "bad.exe"), broken, subtitle]));
                Check(window.QueuePanel.Visibility == Visibility.Visible && window.QueueTitle.Text.Contains("3 个"), "队列没有显示或没去重：" + window.QueueTitle.Text);
                Check(window.Status.Text.Contains("略过 1 个"), "没有提示略过不支持的文件：" + window.Status.Text);
                Check((string)window.StartButton.Content == "翻译全部 4 个", "开始按钮没有显示批量数量：" + window.StartButton.Content);
                window.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUi(() => window.Status.Text.Contains("批量完成") || window.Status.Text.Contains("暂停"));
                WaitUi(() => window.StartButton.IsEnabled);
                Check(window.Status.Text.Contains("已保存 2 个") && window.Status.Text.Contains("跳过 2 个"), "批量汇总错误：" + window.Status.Text);
                Check(File.ReadAllText(Path.Combine(root, "a_译文_zh-CN.txt")) == "你好世界", "第一个文件没有保存");
                Check(File.ReadAllText(Path.Combine(root, "c_译文_zh-CN.srt")).Contains("00:00:01,000 --> 00:00:02,000\n你好世界"), "队列中的字幕没有保存");
                Check(window.QueuePanel.Visibility == Visibility.Collapsed && history.Load().Count(e => e.Status == "Completed") == 2, "完成后队列未清空或历史不对");
                Check(window.OpenResult.Visibility == Visibility.Visible && window.OpenResultFolder.Visibility == Visibility.Visible, "完成后没有显示打开译文和文件夹");

                var failing = PutText(root, "f1.txt", "FAIL here");
                var rest = PutText(root, "f2.txt", "Hello again");
                PumpUntil(window.OpenFilesAsync([failing, rest]));
                var before = requests;
                window.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUi(() => window.Status.Text.Contains("暂停"));
                WaitUi(() => window.StartButton.IsEnabled);
                Check(window.Status.Text.Contains("翻译失败") && window.Status.Text.Contains("剩余 1 个"), "失败后没有暂停队列：" + window.Status.Text);
                Check(window.QueueTitle.Text.Contains("1 个") && !File.Exists(Path.Combine(root, "f2_译文_zh-CN.txt")) && requests - before == 3,
                    "失败后仍继续翻译了后面的文件或重试次数不对");
            }
            finally { window.Close(); }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            settings.DocumentSourceLang = oldSource; settings.DocumentTargetLang = oldTarget;
            settings.DocumentBilingualOutput = oldBilingual; settings.DocumentRequestDelayMs = oldDelay;
            SettingsService.Instance.Save();
            stop.Cancel(); listener.Stop();
            try { server.GetAwaiter().GetResult(); } catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException) { }
        }
    }
}
