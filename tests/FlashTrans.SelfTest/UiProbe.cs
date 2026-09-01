using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>逐个构造窗口并强制走一遍布局/模板，触发所有资源查找。</summary>
static class UiProbe
{
    public static void RunAll(AppHost host, Action<string, Action> step)
    {
        var s = SettingsService.Instance.Current;

        step("托盘图标：句柄非空", TrayIconProbe);
        step("剪贴板：原生读写往返", ClipboardProbe);
        step("弹窗：只拖宽度也能记住", () => PopupWidthProbe(host));
        step("弹窗：焦点被抢走也不关闭", () => PopupBlurProbe(host));
        step("弹窗：收起能原样叫回，关掉的叫不回", () => PopupStashProbe(host));
        step("弹窗：最大高度按屏幕工作区收口", () => PopupMaxHeightProbe(host));
        step("弹窗：焦点一走就撤置顶，让别的窗口盖上来", () => PopupTopmostProbe(host));
        step("托盘菜单：弹出建锚点、收干净、重复收不炸", () => TrayMenuProbe(host));

        step("主窗口：构造 + 布局", () => Probe(new MainWindow(host)));

        step("主窗口：多语言 + 双语对照开启后重排", () =>
        {
            s.MultiTargetEnabled = true;
            s.Bilingual = true;
            var w = new MainWindow(host);
            Probe(w, close: false);
            w.SetInput("Hello world\nSecond line", translate: false);
            w.OnSettingsChanged();
            Close(w);
            s.MultiTargetEnabled = false;
            s.Bilingual = false;
        });

        step("主窗口：浅色主题下重建标签", () =>
        {
            ThemeService.ApplyTheme(AppTheme.Light);
            var w = new MainWindow(host);
            Probe(w);
            ThemeService.ApplyTheme(AppTheme.Dark);
            ThemeService.ApplyAccent(s.AccentColor);
        });

        step("弹窗：构造 + 布局", () =>
        {
            var p = new PopupWindow(host);
            Probe(p, close: false);
            p.OnSettingsChanged();
            Close(p);
        });

        step("划词图标：构造 + 定位", () =>
        {
            var icon = new SelectionIcon();
            icon.ShowAt(new Point(-4000, -4000), "hello");
            icon.UpdateLayout();
            icon.HideIcon();
            icon.Close();
        });

        step("结果区：渲染批次（聚合 + 双语 + 词典）", ResultRender);
        step("结果区：聚合边到边（占位 → 乱序填充 → 收尾）", ProgressiveRender);

        // 设置窗口的每个分类都单独展开一次
        foreach (var tab in new[] { "general", "sources", "languages", "capture", "hotkeys", "appearance", "about" })
        {
            var key = tab;
            step($"设置窗口：{key} 页", () =>
            {
                var w = new SettingsWindow(host);
                if (!w.Topmost) throw new InvalidOperationException("设置窗必须保持在翻译窗之上");
                Probe(w, close: false);
                w.SelectTab(key);
                w.UpdateLayout();
                Close(w);
            });
        }

        step("设置窗口：展开每一个翻译源的编辑区", ExpandEverySource);
        // 数量从枚举里取，加了源不用回来改这行字
        step($"设置窗口：添加菜单（含全部 {ProviderMeta.All.Length} 种源）", AddMenuProbe);
        step("翻译源：每种类型都有元信息且能创建实例", ProviderCoverageProbe);
        step("配置迁移：老配置补上新增的免费源", MigrateProbe);
        step("语言选择器：展开下拉并搜索", LangPickerProbe);

        step("对话框：危险语气 + 取消返回 false", () => DialogProbe(accept: false));
        step("对话框：危险语气 + 确认返回 true", () => DialogProbe(accept: true));
        step("对话框：危险语气默认焦点在取消上", DialogFocusProbe);
        step("对话框：Esc 等于取消", DialogEscapeProbe);
        step("对话框：单按钮提示（无父窗口）", DialogNoOwnerProbe);
        step("对话框：蒙层跟着父窗口盖并在关闭后收走", ScrimProbe);
        step("轻提示：弹出 + 复用 + 位置在工作区内", ToastProbe);
        step("浮条：躲开选区，占满工作区时压到任务栏上", HudPlacementProbe);
        step("对截屏隐身：真摆一块纯色再抓回来验", CaptureHidingProbe);
        step("长截图浮条：躲不开选区时进度文字就不再变", LongShotHudProbe);
        step("识别结果：改完再复制，拿到的是改后的字", () => OcrResultProbe(copy: true));
        step("识别结果：翻译按钮走的也是框里的字", () => OcrResultProbe(copy: false));
        step("识别结果：清空后按复制不关窗", OcrResultEmptyProbe);
    }

    // ------------------------------------------------------------- 具体探测

    /// <summary>托盘图标以前用 LoadImage 读 exe，必然返回 NULL，通知区域就是个透明空位。</summary>
    static void TrayIconProbe()
    {
        var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (!System.IO.File.Exists(ico))
            throw new InvalidOperationException("app.ico 没随生成输出，托盘图标会退回抽 exe 资源：" + ico);

        var cx = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        var h = Win32.LoadImage(IntPtr.Zero, ico, Win32.IMAGE_ICON, cx, cx, Win32.LR_LOADFROMFILE);
        Console.WriteLine($"       ico={cx}px 句柄={h}");
        if (h == IntPtr.Zero) throw new InvalidOperationException("从 ico 加载图标失败");
        Win32.DestroyIcon(h);

        // 退路也要能用：单文件发布时 Assets 可能不在磁盘上。
        // 注意不能拿自测 exe 去探——它没嵌图标，探了必然是 0。
        var exe = FindShippedExe();
        if (exe is null) { Console.WriteLine("       （没找到已发布的 FlashTrans.exe，跳过退路检查）"); return; }

        var small = new IntPtr[1];
        var n = Win32.ExtractIconEx(exe, 0, null, small, 1);
        Console.WriteLine($"       ExtractIconEx({System.IO.Path.GetFileName(exe)})={n} 句柄={small[0]}");
        if (n == 0 || small[0] == IntPtr.Zero)
            throw new InvalidOperationException("从 exe 抽图标失败，单文件发布时托盘会是空白");
        Win32.DestroyIcon(small[0]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    /// <summary>照系统 resize 的方向改 HWND，让 WPF 自己回填 Width。</summary>
    static void Drag(Window w, IntPtr hwnd, double dpi, double target)
    {
        if (!Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                (int)Math.Round(target * dpi), (int)Math.Round(w.ActualHeight * dpi),
                Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE))
            throw new InvalidOperationException(
                "SetWindowPos 失败：" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    /// <summary>找一个真正带图标的 FlashTrans.exe（自测 exe 自己没有）。</summary>
    static string? FindShippedExe()
    {
        var root = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && root is not null; up++)
        {
            foreach (var rel in (string[])[
                "FlashTrans.exe",
                @"dist\FlashTrans-win-x64-fast\FlashTrans.exe",
                @"dist\FlashTrans-win-x64-small\FlashTrans.exe",
                @"src\FlashTrans\bin\Release\net9.0-windows\FlashTrans.exe"])
            {
                var p = System.IO.Path.Combine(root, rel);
                if (System.IO.File.Exists(p)) return p;
            }
            root = System.IO.Directory.GetParent(root)?.FullName;
        }
        return null;
    }

    /// <summary>剪贴板改走原生 API：OpenClipboard 是全局锁，OLE 版会把浏览器一起拖住。</summary>
    static void ClipboardProbe()
    {
        var original = SelectionReader.ReadText();
        try
        {
            var probe = "闪译剪贴板探针 FlashTrans probe 123";
            SelectionReader.SetText(probe);
            var back = SelectionReader.ReadText();
            Console.WriteLine($"       写入 {probe.Length} 字，读回 {back?.Length ?? -1} 字");
            if (back != probe) throw new InvalidOperationException($"往返不一致，读回：{back ?? "<null>"}");

            // 空串不该清掉剪贴板
            SelectionReader.SetText("");
            if (SelectionReader.ReadText() != probe)
                throw new InvalidOperationException("空串把剪贴板冲掉了");
        }
        finally
        {
            if (!string.IsNullOrEmpty(original)) SelectionReader.SetText(original);
        }
    }

    /// <summary>
    /// 托盘菜单以前挂在那个不可见的 0×0 消息窗口上，点外面关不掉——SetForegroundWindow
    /// 对不可见窗口无效，WPF 的鼠标捕获就收不到别处的点击。现在弹之前先放一个 1×1 的
    /// 锚点窗口把线程顶到前台。
    ///
    /// 这里验得到的是：弹得出来、锚点只建一个、收的时候两个一起收干净、重复收不炸
    /// （收的过程里菜单自己的 Closed 还会再回调一次 CloseTrayMenu，就是这条路）。
    /// 真正「点外面自动关」要人动鼠标，断言测不到。
    /// </summary>
    static void TrayMenuProbe(AppHost host)
    {
        try
        {
            host.ShowTrayMenu();
            Pump();
            if (AnchorCount() != 1)
                throw new InvalidOperationException($"锚点窗口应有 1 个，实际 {AnchorCount()} 个");

            // 连着右键两次别叠出第二个锚点
            host.ShowTrayMenu();
            Pump();
            if (AnchorCount() != 1)
                throw new InvalidOperationException($"右键两次叠出了 {AnchorCount()} 个锚点");

            host.CloseTrayMenu();
            Pump();
            if (AnchorCount() != 0)
                throw new InvalidOperationException($"收完还剩 {AnchorCount()} 个锚点窗口");

            // 重复收：CloseTrayMenu 里那道只收一次的闸
            host.CloseTrayMenu();
            host.CloseTrayMenu();
            Pump();
            Console.WriteLine("       弹出→锚点 1 个→重复右键不叠→收干净→重复收不炸");
        }
        finally
        {
            host.CloseTrayMenu();
            Pump();
        }
    }

    static int AnchorCount() => Application.Current.Windows.OfType<Window>()
        .Count(w => w.Title == AppHost.TrayAnchorTitle);

    /// <summary>
    /// 以前弹窗挂着 Deactivated → HidePopup：切一下别的软件窗口，正在看的译文就没了。
    /// 离屏窗口本来就拿不到焦点，pump 几轮就会走一遍 Deactivated，正好拿来验。
    /// </summary>
    static void PopupBlurProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (l0, t0) = (s.PopupLeft, s.PopupTop);
        var w = new PopupWindow(host);
        try
        {
            w.Left = -4000; w.Top = -4000;
            w.ShowFor("blur probe", new Point(-4000, -4000));
            Pump();
            if (!w.IsVisible) throw new InvalidOperationException("刚弹出来就不见了");

            w.Activate();
            Pump();
            Pump();
            Pump();
            if (!w.IsVisible) throw new InvalidOperationException("失去焦点后自己关了");
        }
        finally
        {
            Close(w);
            s.PopupLeft = l0; s.PopupTop = t0;
        }
    }

    /// <summary>
    /// 「不失去焦点就不关」加上原来那个永久 Topmost，弹窗就成了一块甩不掉的浮层：
    /// 从任务栏点别的程序，那个窗口起不到弹窗上面来。
    ///
    /// 弹出时仍要置顶（划词、剪贴板这些路径抢不到前台，不置顶可能看不见），
    /// 但拿到焦点又丢掉之后必须撤下来。离屏窗口拿不到真焦点，所以走 force。
    /// </summary>
    static void PopupTopmostProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (top0, l0, t0) = (s.PopupTopmost, s.PopupLeft, s.PopupTop);
        var w = new PopupWindow(host);
        try
        {
            s.PopupTopmost = false;
            w.Left = -4000; w.Top = -4000;
            w.ShowFor("topmost probe", new Point(-4000, -4000));
            Pump();
            if (!w.Topmost) throw new InvalidOperationException("弹出时没置顶，可能压在原窗口底下看不见");

            w.LowerBelowForeground(force: true);
            Pump();
            if (w.Topmost) throw new InvalidOperationException("焦点走了还赖在置顶层，别的窗口盖不上来");

            // 被盖住时按快捷键要能重新抬上来
            w.RaiseToFront();
            Pump();
            if (!w.Topmost) throw new InvalidOperationException("叫不上来");

            // 用户明确要求一直置顶时，让位那步不该动它
            s.PopupTopmost = true;
            w.LowerBelowForeground(force: true);
            Pump();
            if (!w.Topmost) throw new InvalidOperationException("开了「一直置顶」还是被放下去了");

            // 再开一次也得回到置顶
            s.PopupTopmost = false;
            w.LowerBelowForeground(force: true);
            w.ShowFor("topmost probe 2", new Point(-4000, -4000));
            Pump();
            if (!w.Topmost) throw new InvalidOperationException("重开没有回到置顶");
            Console.WriteLine("       弹出置顶→让位撤顶→叫得回来→「一直置顶」不让位");
            w.ClosePopup();
            Pump();
        }
        finally
        {
            s.PopupTopmost = top0; s.PopupLeft = l0; s.PopupTop = t0;
            Close(w);
        }
    }

    /// <summary>收起要留住内容能原样叫回；关掉的要作废；新翻译要把收起的顶掉。</summary>
    static void PopupStashProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (l0, t0) = (s.PopupLeft, s.PopupTop);
        var w = new PopupWindow(host);
        try
        {
            w.Left = -4000; w.Top = -4000;
            w.ShowFor("stash probe", new Point(-4000, -4000));
            Pump();

            w.StashPopup();
            Pump();
            if (w.IsVisible) throw new InvalidOperationException("收起了还看得见");
            if (!w.CanRestore) throw new InvalidOperationException("收起后却说没东西可叫回");

            if (!w.RestorePopup()) throw new InvalidOperationException("叫不回来");
            Pump();
            if (!w.IsVisible) throw new InvalidOperationException("叫回来了却没显示");
            if (w.CanRestore) throw new InvalidOperationException("已经回来了还说能叫回");

            // 关闭是「不要了」，快捷键不该再把它捞回来
            w.ClosePopup();
            Pump();
            if (w.CanRestore) throw new InvalidOperationException("关掉的还说能叫回");
            if (w.RestorePopup()) throw new InvalidOperationException("关掉的居然叫回来了");
            if (w.IsVisible) throw new InvalidOperationException("关掉的又显示出来了");

            // 收起期间来了新翻译：直接摆出新结果，不留着旧的等人叫
            w.ShowFor("stash probe 2", new Point(-4000, -4000));
            Pump();
            w.StashPopup();
            Pump();
            if (!w.CanRestore) throw new InvalidOperationException("收起后该能叫回");
            w.ShowFor("stash probe 3", new Point(-4000, -4000));
            Pump();
            if (!w.IsVisible) throw new InvalidOperationException("新翻译没把弹窗摆出来");
            if (w.CanRestore) throw new InvalidOperationException("新翻译来了还留着收起的那个");
            w.ClosePopup();
            Pump();
        }
        finally
        {
            Close(w);
            s.PopupLeft = l0; s.PopupTop = t0;
        }
    }

    /// <summary>
    /// 源多的时候要的是更高的弹窗，所以上限放宽到能超过屏幕；
    /// 但真显示时必须按当前屏幕的工作区收口，否则窗口伸到任务栏底下去。
    /// </summary>
    static void PopupMaxHeightProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (mh0, l0, t0) = (s.PopupMaxHeight, s.PopupLeft, s.PopupTop);
        var w = new PopupWindow(host);
        try
        {
            s.PopupMaxHeight = 2400;   // 比任何一块屏都高
            w.Left = -4000; w.Top = -4000;
            w.ShowFor("max height probe", new Point(-4000, -4000));
            Pump();

            // 离屏窗口和光标可能落在不同显示器上，取两者里高的那块，免得多屏下误报
            var tallest = Math.Max(ScreenHelper.WorkAreaOf(w).Height,
                                   ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), w).Height);
            Console.WriteLine($"       上限设 2400，工作区高 {tallest:F0}，实际收到 {w.MaxHeight:F0}");
            if (w.MaxHeight >= 2400) throw new InvalidOperationException("上限完全没收口");
            if (w.MaxHeight > tallest)
                throw new InvalidOperationException($"上限超出工作区：{w.MaxHeight:F0} > {tallest:F0}");

            // 反过来，设置里给的比屏幕小就该照用户的来
            s.PopupMaxHeight = 300;
            w.OnSettingsChanged();
            Pump();
            if (Math.Abs(w.MaxHeight - 300) > 1)
                throw new InvalidOperationException($"没照设置的上限来：期望 300，实际 {w.MaxHeight:F0}");
            w.ClosePopup();
            Pump();
        }
        finally
        {
            s.PopupMaxHeight = mh0; s.PopupLeft = l0; s.PopupTop = t0;
            Close(w);
        }
    }

    /// <summary>
    /// 两个坑叠在一起：_userResized 只认高度变化，拖宽存不下来；
    /// 而高度自适应时 WPF 每轮布局又拿 Width 反压窗口，把拖出来的宽度弹回旧值。
    /// </summary>
    static void PopupWidthProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (w0, l0, t0) = (s.PopupWidth, s.PopupLeft, s.PopupTop);
        try
        {
            // 弹窗不再有「失去焦点自动关闭」，离屏窗口 pump 一下也还在，
            // 这个探针才量得到宽度。以前要先把 PopupCloseOnBlur 关掉。
            var w = new PopupWindow(host);
            w.Left = -4000; w.Top = -4000;
            w.ShowFor("width probe", new Point(-4000, -4000));
            Pump();

            // 改 HWND 让系统发 WM_SIZE、WPF 再回填 Width，这是拖动的方向。
            // 反过来直接写 Width 测不出真实行为：WindowChrome 会嵌套发一轮 WM_SIZE 把旧值盖回来。
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            var dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
            var target = Math.Round(w.Width + 66);

            // 拖宽。STC=Height 时 WPF 会把外部改宽整个吞掉（HWND 纹丝不动），
            // 真实拖动走的是系统 resize 循环，能改到；这里先切 Manual 才模拟得出来。
            w.SizeToContent = SizeToContent.Manual;
            Pump();
            Drag(w, hwnd, dpi, target);
            Pump();

            w.ClosePopup();
            Pump();
            if (Math.Abs(s.PopupWidth - target) > 1.5)
                throw new InvalidOperationException($"宽度没存下：期望 {target}，实际 {s.PopupWidth}");

            // 再开一次：这才是用户抱怨的"每次都要自己调整"
            w.ShowFor("width probe 2", new Point(-4000, -4000));
            Pump();
            GetWindowRect(hwnd, out var r);
            var reopened = (r.Right - r.Left) / dpi;
            Console.WriteLine($"       拖到 {target}，存下 {s.PopupWidth}，重开量到 {Math.Round(reopened)}");
            if (Math.Abs(reopened - target) > 2)
                throw new InvalidOperationException($"重开又变回去了：期望 {target}，实际 {Math.Round(reopened)}");
            w.ClosePopup();
            Pump();
            Close(w);
        }
        finally
        {
            s.PopupWidth = w0; s.PopupLeft = l0; s.PopupTop = t0;
        }
    }

    static void ResultRender()
    {
        var view = new ResultView();
        var holder = new Window
        {
            Content = view, Width = 500, Height = 400,
            Left = -4000, Top = -4000, ShowInTaskbar = false, ShowActivated = false,
        };
        holder.Show();

        view.ShowMessage("空状态");
        view.ShowLoading("测试源");
        view.BeginStream("Hello", "AI");
        view.AppendStream("你好");
        view.AppendStream("，世界");

        var batch = new TranslateBatch
        {
            SourceText = "Hello world\nSecond line",
            From = "en",
            Targets = ["zh-CN", "ja"],
            TotalMs = 123,
            Notes = ["已自动切换到「必应」"],
        };
        var ok = new TranslateResult
        {
            ProviderId = "a", ProviderName = "谷歌", ElapsedMs = 88,
            Phonetic = "həˈləʊ",
            Dict = [new DictEntry { Pos = "int.", Terms = ["你好", "喂"] }],
        };
        ok.Texts["zh-CN"] = "你好世界\n第二行";
        ok.Texts["ja"] = "こんにちは世界\n二行目";
        batch.Results.Add(ok);
        batch.Results.Add(new TranslateResult
        {
            ProviderId = "b", ProviderName = "DeepL", Error = "缺少 API Key",
        });
        var cached = new TranslateResult { ProviderId = "c", ProviderName = "必应", FromCache = true };
        cached.Texts["zh-CN"] = "你好，世界";
        batch.Results.Add(cached);

        view.ShowBatch(batch, aggregate: true);
        holder.UpdateLayout();
        view.ShowBatch(batch, aggregate: false, onlyProviderId: "a");
        holder.UpdateLayout();
        _ = ResultView.AllText(batch);

        Close(holder);
    }

    /// <summary>
    /// 聚合边到边显示：占位卡 → 逐个换成真结果 → 收尾。
    /// 换卡按 Id 找槽位，所以要盯住越界和找不到 Id 这两种情况——
    /// 结果乱序回来、源在翻译途中被删掉，都会走到那里。
    /// </summary>
    static void ProgressiveRender()
    {
        var view = new ResultView();
        var holder = new Window
        {
            Content = view, Width = 500, Height = 400,
            Left = -4000, Top = -4000, ShowInTaskbar = false, ShowActivated = false,
        };
        holder.Show();

        var batch = new TranslateBatch
        {
            SourceText = "Hello world", From = "en", Targets = ["zh-CN"],
            Notes = ["「彩云」已跳过：缺少 token"],
        };
        List<ProviderConfig> cfgs =
        [
            new() { Id = "p1", Kind = ProviderKind.GoogleFree, Name = "谷歌" },
            new() { Id = "p2", Kind = ProviderKind.BingFree, Name = "必应" },
            new() { Id = "p3", Kind = ProviderKind.Tencent, Name = "腾讯" },
        ];

        view.BeginAggregate(batch, cfgs);
        holder.UpdateLayout();

        // 乱序回来：先第 3 个，再第 1 个，第 2 个报错
        var third = new TranslateResult { ProviderId = "p3", ProviderName = "腾讯", ElapsedMs = 61 };
        third.Texts["zh-CN"] = "你好，世界";
        view.UpdateOne(third);
        holder.UpdateLayout();

        var first = new TranslateResult { ProviderId = "p1", ProviderName = "谷歌", ElapsedMs = 88 };
        first.Texts["zh-CN"] = "你好世界";
        view.UpdateOne(first);
        holder.UpdateLayout();

        view.UpdateOne(new TranslateResult
        {
            ProviderId = "p2", ProviderName = "必应", Error = "请求超时",
        });
        holder.UpdateLayout();

        // 不存在的 Id：源在途中被删掉就是这样，必须安静忽略而不是抛
        view.UpdateOne(new TranslateResult { ProviderId = "没这个源", ProviderName = "幽灵" });
        holder.UpdateLayout();

        batch.Results.AddRange([first, third]);
        view.EndAggregate(batch);
        holder.UpdateLayout();

        // 收尾后再来一个迟到的结果：槽位已清空，同样不能炸
        view.UpdateOne(third);
        holder.UpdateLayout();

        // 一个源都没有：EndAggregate 该给出空状态提示而不是留白
        var empty = new ResultView();
        holder.Content = empty;
        empty.EndAggregate(new TranslateBatch { SourceText = "x" });
        holder.UpdateLayout();

        Close(holder);
    }

    static void ExpandEverySource()
    {
        var s = SettingsService.Instance.Current;
        var backup = s.Providers.Select(p => p.Clone()).ToList();
        try
        {
            // 每种源都放一个，确保所有字段编辑器（含密钥、多行、布尔）都被构造
            s.Providers.Clear();
            foreach (var meta in ProviderMeta.All)
                s.Providers.Add(ProviderConfig.Create(meta.Kind));

            var w = new SettingsWindow(new AppHost());
            Probe(w, close: false);
            w.SelectTab("sources");
            w.UpdateLayout();

            foreach (var btn in Descendants<Button>(w).Where(b => (b.ToolTip as string) == "展开设置").ToList())
            {
                btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                w.UpdateLayout();
            }
            Close(w);
        }
        finally
        {
            s.Providers.Clear();
            s.Providers.AddRange(backup);
        }
    }

    /// <summary>
    /// 装了旧版的人升级后，settings.json 已经存在，CreateDefault 不会再跑，
    /// 新加的免费源就永远不出现。这里模拟一份 v1 配置走一遍迁移。
    /// </summary>
    static void MigrateProbe()
    {
        var old = new AppSettings { Version = 1 };
        old.Providers.Add(ProviderConfig.Create(ProviderKind.GoogleFree));
        var mine = ProviderConfig.Create(ProviderKind.OpenAiCompat, "我自己加的");
        old.Providers.Add(mine);
        old.PrimaryProviderId = old.Providers[0].Id;

        if (!SettingsService.Migrate(old)) throw new InvalidOperationException("v1 配置没被迁移");
        if (old.Version != AppSettings.CurrentVersion)
            throw new InvalidOperationException("迁移后版本号没跟上：" + old.Version);
        if (old.Providers.All(p => p.Kind != ProviderKind.TranSmart))
            throw new InvalidOperationException("迁移没补上腾讯交互翻译");

        // 用户自己加的源要留在原地，主用源不能被顶掉
        if (old.Providers.Last().Id != mine.Id)
            throw new InvalidOperationException("新源插错位置，把用户自己的源挤到后面去了");
        if (old.PrimaryProviderId != old.Providers[0].Id)
            throw new InvalidOperationException("主用源被改了");

        // 再跑一次不该重复添加
        if (SettingsService.Migrate(old)) throw new InvalidOperationException("已是最新版还在迁移");
        if (old.Providers.Count(p => p.Kind == ProviderKind.TranSmart) != 1)
            throw new InvalidOperationException("重复添加了");

        Console.WriteLine($"       v1 → v{old.Version}，源 {old.Providers.Count} 个，顺序：" +
                          string.Join(" ", old.Providers.Select(p => p.Kind)));
    }

    /// <summary>
    /// 新增一种 ProviderKind 要动四处：枚举、ProviderMeta.All、Registry.Create、LangCodes。
    /// 漏掉任何一处这里都会响——不然只有用户点到那个源才会炸。
    /// </summary>
    static void ProviderCoverageProbe()
    {
        var kinds = Enum.GetValues<ProviderKind>();
        var registry = new ProviderRegistry();

        foreach (var kind in kinds)
        {
            if (ProviderMeta.All.All(m => m.Kind != kind))
                throw new InvalidOperationException($"{kind} 没有在 ProviderMeta.All 里登记，添加菜单里看不到它");

            // Get 找不到会兜底返回 All[0]，光看返回值发现不了漏登记，所以上面单独查了一遍
            var impl = registry.Get(ProviderConfig.Create(kind));
            if (impl.Kind != kind)
                throw new InvalidOperationException($"{kind} 创建出来的实例类型不对：{impl.Kind}");
        }
        Console.WriteLine($"       {kinds.Length} 种源全部登记且可创建");
    }

    static void AddMenuProbe()
    {
        var w = new SettingsWindow(new AppHost());
        Probe(w, close: false);
        w.SelectTab("sources");
        w.UpdateLayout();

        var add = Descendants<Button>(w).FirstOrDefault(b => b.Content is "＋ 添加源")
                  ?? throw new InvalidOperationException("没找到「添加源」按钮");
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        w.UpdateLayout();

        var menus = Descendants<ContextMenu>(w).ToList();
        Close(w);
    }

    static void LangPickerProbe()
    {
        var picker = new LangPicker(includeAuto: true) { SelectedCode = "auto" };
        var holder = new Window
        {
            Content = picker, Width = 320, Height = 200,
            Left = -4000, Top = -4000, ShowInTaskbar = false, ShowActivated = false,
        };
        holder.Show();
        holder.UpdateLayout();

        picker.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        holder.UpdateLayout();
        picker.SelectedCode = "ja";
        holder.UpdateLayout();
        Close(holder);
    }

    // ------------------------------------------------------------- 对话框 / 轻提示

    /// <summary>
    /// 对话框是模态的，ShowDialog 会一直卡住。这里往队列里塞一个回调，
    /// 等模态帧跑起来之后找到窗口再点按钮，走的是和真实使用完全相同的路径。
    /// </summary>
    static void DialogProbe(bool accept)
    {
        var owner = OffscreenOwner();
        var okText = accept ? "删除" : "取消";
        WhenDialogUp(dlg =>
        {
            var btn = Descendants<Button>(dlg).FirstOrDefault(b => (b.Content as string) == okText)
                      ?? throw new InvalidOperationException($"对话框上找不到「{okText}」按钮");
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });

        var got = AppDialog.Confirm(owner, "删除翻译源", "确定要删除「有道翻译」吗？",
            okText: "删除", tone: DialogTone.Danger, detail: "密钥会一起清掉。");

        Close(owner);
        if (got != accept) throw new InvalidOperationException($"返回值应为 {accept}，实际 {got}");
    }

    /// <summary>删除、重置这类操作，焦点要落在「取消」上，顺手一个回车不能把事办了。</summary>
    static void DialogFocusProbe()
    {
        var owner = OffscreenOwner();
        string? focused = null;

        WhenDialogUp(dlg =>
        {
            focused = Descendants<Button>(dlg).FirstOrDefault(b => b.IsKeyboardFocused || b.IsFocused)
                          ?.Content as string;
            dlg.Close();
        });

        var got = AppDialog.Confirm(owner, "重置为默认设置", "所有设置都会回到初始状态，确定吗？",
            okText: "重置", tone: DialogTone.Danger);

        Close(owner);
        if (focused != "取消") throw new InvalidOperationException($"焦点应在「取消」，实际在「{focused ?? "无"}」");
        if (got) throw new InvalidOperationException("直接关闭窗口不该算确认");
    }

    static void DialogEscapeProbe()
    {
        var owner = OffscreenOwner();
        WhenDialogUp(dlg =>
        {
            var src = System.Windows.Interop.HwndSource.FromVisual(dlg) as System.Windows.Interop.HwndSource
                      ?? throw new InvalidOperationException("拿不到对话框的 HwndSource");
            dlg.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            if (dlg.IsVisible) throw new InvalidOperationException("按下 Esc 之后对话框应该已经关了");
        });

        var got = AppDialog.Confirm(owner, "删除翻译源", "确定要删除吗？",
            okText: "删除", tone: DialogTone.Danger);

        Close(owner);
        if (got) throw new InvalidOperationException("Esc 应该等于取消，不能返回 true");
    }

    static void DialogNoOwnerProbe()
    {
        WhenDialogUp(dlg => dlg.Close());
        AppDialog.Info(null, "还没有日志", "这一次运行还没写过日志文件。");
    }

    /// <summary>蒙层要盖在父窗口上，并且对话框关掉之后不能留在屏幕上。</summary>
    static void ScrimProbe()
    {
        var owner = OffscreenOwner();
        owner.Topmost = true;               // 设置窗就是置顶的，蒙层必须跟上
        var scrimSeen = 0;

        WhenDialogUp(dlg =>
        {
            // AppDialog 之外多出来的那个无边框透明窗口就是蒙层
            var scrims = Application.Current.Windows.OfType<Window>()
                .Where(w => w != dlg && w != owner && w.AllowsTransparency
                            && w.WindowStyle == WindowStyle.None && w.IsVisible
                            && w is not ToastWindow and not SelectionIcon)
                .ToList();
            scrimSeen = scrims.Count;

            foreach (var s in scrims)
            {
                if (!s.Topmost)
                    throw new InvalidOperationException("父窗口置顶时蒙层也要置顶，否则会被压在下面");
                // 盖住的范围要对上父窗口，差几个像素算正常（取整）
                if (Math.Abs(s.Width - owner.ActualWidth) > 2 || Math.Abs(s.Height - owner.ActualHeight) > 2)
                    throw new InvalidOperationException(
                        $"蒙层尺寸 {s.Width:F0}x{s.Height:F0} 与父窗口 {owner.ActualWidth:F0}x{owner.ActualHeight:F0} 不符");
            }
            dlg.Close();
        });

        AppDialog.Info(owner, "提示", "蒙层探针");

        if (scrimSeen != 1) throw new InvalidOperationException($"对话框弹出时应有 1 层蒙层，实际 {scrimSeen}");

        var left = Application.Current.Windows.OfType<Window>()
            .Count(w => w != owner && w.AllowsTransparency && w.IsVisible
                        && w is not ToastWindow and not SelectionIcon);
        Close(owner);
        if (left != 0) throw new InvalidOperationException($"关闭后还剩 {left} 个蒙层没收走");
    }

    /// <summary>
    /// 识别难免有错字，所以结果得能改。这里改一遍框里的字再按按钮，
    /// 验事件收到的是改后的内容——要是直接把识别原文传出去，改了也白改。
    /// </summary>
    static void OcrResultProbe(bool copy)
    {
        var w = new OcrResultWindow("Hello wor1d");
        string? got = null;
        var hits = 0;
        w.Copy += t => { got = t; hits++; };
        w.Translate += t => { got = t; hits++; };
        Probe(w, close: false);

        var box = Descendants<TextBox>(w).FirstOrDefault()
                  ?? throw new InvalidOperationException("识别结果窗里没有可编辑的框");
        if (box.Text != "Hello wor1d")
            throw new InvalidOperationException($"框里装的不是识别结果：{box.Text}");
        if (!box.AcceptsReturn) throw new InvalidOperationException("多行识别结果要能换行");
        box.Text = "Hello world";

        var label = copy ? "复制" : "翻译";
        var btn = Descendants<Button>(w).FirstOrDefault(b => (b.Content as string)?.StartsWith(label) == true)
                  ?? throw new InvalidOperationException($"找不到「{label}」按钮");
        btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        if (got != "Hello world")
            throw new InvalidOperationException($"{label}拿到的是「{got}」，该是改后的「Hello world」");
        if (w.IsVisible) throw new InvalidOperationException($"按了{label}窗口该关掉");

        // 关窗过程中还在派发消息，这中间再按一次不能把动作干第二遍
        btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (hits != 1) throw new InvalidOperationException($"动作干了 {hits} 遍");
        Close(w);
    }

    /// <summary>全删空了没什么可复制的，这时候关窗只会让用户白识别一次。</summary>
    static void OcrResultEmptyProbe()
    {
        var w = new OcrResultWindow("some text");
        var fired = false;
        w.Copy += _ => fired = true;
        Probe(w, close: false);

        var box = Descendants<TextBox>(w).First();
        box.Text = "   ";
        Descendants<Button>(w).First(b => (b.Content as string)?.StartsWith("复制") == true)
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

        if (fired) throw new InvalidOperationException("空内容不该复制出去");
        if (!w.IsVisible) throw new InvalidOperationException("空内容时窗口要留着让人接着改");
        Close(w);
    }

    static Window OffscreenOwner()
    {
        var owner = new Window
        {
            Width = 420, Height = 300, Left = -4000, Top = -4000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        owner.Show();
        owner.UpdateLayout();
        return owner;
    }

    /// <summary>模态帧起来之后回调。窗口还没出现就把自己重新排一次队。</summary>
    static void WhenDialogUp(Action<AppDialog> act)
    {
        var tries = 0;
        void Poll()
        {
            var dlg = Application.Current.Windows.OfType<AppDialog>().FirstOrDefault(w => w.IsVisible);
            if (dlg is not null) { act(dlg); return; }
            if (++tries > 40) throw new InvalidOperationException("对话框没有出现");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(Poll));
        }
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(Poll));
    }

    static void ToastProbe()
    {
        ToastWindow.Show("划词翻译已开启");
        Pump();

        var toast = Application.Current.Windows.OfType<ToastWindow>().FirstOrDefault()
                    ?? throw new InvalidOperationException("轻提示没有创建（可能被内部 catch 吃了，看日志）");
        if (!toast.IsVisible) throw new InvalidOperationException("轻提示没有显示");
        if (toast.ActualHeight <= 0) throw new InvalidOperationException("轻提示高度为 0，自适应没生效");

        // 摆位要落在某块屏幕的工作区里，不能还留在丢出去的 -20000
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), toast);
        if (toast.Left < work.Left - 1 || toast.Left > work.Right)
            throw new InvalidOperationException($"轻提示 Left={toast.Left:F0} 不在工作区内");
        if (toast.Top < work.Top - 1 || toast.Top > work.Bottom)
            throw new InvalidOperationException($"轻提示 Top={toast.Top:F0} 不在工作区内");

        // 第二条要复用同一个窗口，不能越弹越多
        ToastWindow.Show("剪贴板里没有文本");
        Pump();
        var count = Application.Current.Windows.OfType<ToastWindow>().Count();
        if (count != 1) throw new InvalidOperationException($"轻提示应复用一个窗口，实际 {count} 个");

        ToastWindow.Shutdown();
        Pump();
        if (Application.Current.Windows.OfType<ToastWindow>().Any())
            throw new InvalidOperationException("Shutdown 之后轻提示没有关掉");
    }

    /// <summary>
    /// 长截图和录制的浮条必须摆在选区外面。摆进去的代价不只是难看：长图那边，
    /// 每帧变一次的进度文字会让「已经滚到底」判不出来——两帧本该一模一样，就因为
    /// 那几个数字不同被当成画面还在动，于是拿重复的页脚去对齐，同一屏底部反复接十几遍。
    ///
    /// 最要命的一种是照着最大化窗口选：选区正好占满工作区，上下左右都没缝，
    /// 只有任务栏那条能站人。以前按工作区摆，这种情况必然摆进选区里。
    /// </summary>
    static void HudPlacementProbe()
    {
        var mon = new Rect(0, 0, 1920, 1080);
        var work = new Rect(0, 0, 1920, 1040);          // 底下 40 是横着的任务栏
        const double w = 240, h = 36;

        void Check(string what, Rect sel, bool mustAvoid = true)
        {
            var (left, top) = ScreenHelper.PlaceOutside(sel, mon, w, h);
            var box = new Rect(left, top, w, h);
            if (!mon.Contains(box))
                throw new InvalidOperationException($"{what}：浮条 {box} 跑出屏幕了，看不见");

            var over = Rect.Intersect(box, sel);
            var area = over.IsEmpty ? 0 : over.Width * over.Height;
            if (mustAvoid && area > 0)
                throw new InvalidOperationException(
                    $"{what}：浮条 ({left:0},{top:0}) 压在选区 {sel} 上 {area:0} 平方点");
            Console.WriteLine($"       {what} → ({left:0},{top:0}){(area > 0 ? " 躲不开" : "")}");
        }

        Check("选区在屏幕中间", new Rect(300, 200, 800, 400));
        Check("选区贴着屏幕顶", new Rect(300, 0, 800, 900));
        Check("选区贴着工作区底", new Rect(300, 200, 800, 840));
        Check("最大化窗口：占满整个工作区", work);
        // 连任务栏都盖住了，那就是躲不开——只要求别摆到屏幕外面去
        Check("选区占满整块屏", mon, mustAvoid: false);
    }

    /// <summary>
    /// 拿真显示器的尺寸建一个长截图浮条，验两件事：照着最大化窗口选（选区正好占满
    /// 工作区）时它得挪到任务栏上去；实在躲不开、系统又不支持对截屏隐身时，
    /// 那行进度文字必须冻住——它每帧一变，长图那边就判不出「已经滚到底」了。
    /// </summary>
    static void LongShotHudProbe()
    {
        var handle = Win32.MonitorFromPoint(ScreenHelper.CursorPos(), Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>(),
        };
        if (handle == IntPtr.Zero || !Win32.GetMonitorInfo(handle, ref mi))
            throw new InvalidOperationException("取不到显示器信息，没法造出真实尺寸的选区");

        Check("照着最大化窗口选", mi.rcWork);
        Check("选区占满整块屏", mi.rcMonitor);

        void Check(string what, RECT region)
        {
            var hud = new LongShotHud(region);
            try
            {
                hud.Show();
                hud.UpdateLayout();
                Pump();

                var before = AllText(hud);
                hud.Report(1289, 2);
                hud.UpdateLayout();
                Pump();
                var after = AllText(hud);

                var tl = ScreenHelper.ToDip(new POINT { X = region.Left, Y = region.Top }, hud);
                var br = ScreenHelper.ToDip(new POINT { X = region.Right, Y = region.Bottom }, hud);
                var over = Rect.Intersect(new Rect(tl, br),
                                          new Rect(hud.Left, hud.Top, hud.ActualWidth, hud.ActualHeight));
                var inside = !over.IsEmpty && over.Width * over.Height > 0;

                if (inside)
                {
                    if (after != before)
                        throw new InvalidOperationException(
                            $"{what}：浮条压在选区里，进度文字却还在变（{after}）");
                }
                else if (after == before)
                {
                    throw new InvalidOperationException($"{what}：浮条在选区外，却没报进度（{after}）");
                }

                Console.WriteLine($"       {what} → ({hud.Left:0},{hud.Top:0})"
                                  + (inside ? " 压在选区里" : " 在选区外"));
            }
            finally { hud.Close(); }   // 顺带把轮询 Esc 的定时器停掉
        }
    }

    /// <summary>丢在屏幕外的一小块纯色窗口，用来试各种窗口属性。</summary>
    static Window NewChip(Brush fill, bool transparent) => new()
    {
        Width = 200,
        Height = 80,
        Left = -30000,
        Top = -30000,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = false,
        Topmost = true,
        AllowsTransparency = transparent,
        Background = fill,
    };

    /// <summary>
    /// 钉住「WDA_EXCLUDEFROMCAPTURE 这条路走不通」这个结论，免得下次又有人去试。
    /// 真在屏幕上摆一块纯色，设上隐身，再 BitBlt 回来数颜色，量到两件事：
    ///
    /// 一、对带 WS_EX_LAYERED 的窗口这个调用直接返回 false，而 WPF 只要
    /// AllowsTransparency=true 就是层窗口——两条浮条正是那么建的，从来没设上过。
    /// 二、就算设上了（普通窗口），底下垫的绿色一点都透不出来，那块是<b>纯黑</b>。
    /// 长截图里就成了一条黑杠，比拍到浮条还难看。所以浮条只能靠摆位置躲开选区。
    /// </summary>
    static void CaptureHidingProbe()
    {
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos());
        var magenta = Color.FromRgb(0xFF, 0x00, 0xFF);
        var green = Color.FromRgb(0x00, 0xC0, 0x40);

        var back = NewChip(new SolidColorBrush(green), transparent: false);
        back.Width = 320;
        back.Height = 160;
        back.Topmost = false;
        back.Left = work.Left + 40;
        back.Top = work.Top + 40;
        back.Show();
        try
        {
            foreach (var transparent in new[] { false, true })
            {
                var w = NewChip(new SolidColorBrush(magenta), transparent);
                w.Left = back.Left + 60;
                w.Top = back.Top + 40;
                try
                {
                    w.Show();
                    Settle();

                    var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice
                            ?? throw new InvalidOperationException("取不到 DPI 变换");
                    var box = ScreenCapture.ToPixels(new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight),
                                                     m.M11, m.M22);

                    var shown = Ratio(ScreenCapture.Grab(box), magenta);
                    if (shown < 0.9)
                        throw new InvalidOperationException(
                            $"AllowsTransparency={transparent}：窗口摆上去却只拍到 {shown:P0} 的纯色，这条探测本身不成立");

                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    var set = Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
                    Settle();
                    var after = ScreenCapture.Grab(box);
                    var left = Ratio(after, magenta);
                    var behind = Ratio(after, green);

                    Console.WriteLine($"       AllowsTransparency={transparent}：设上={set} "
                                      + $"隐身后还剩 {left:P0}，透出后面的绿 {behind:P0}，中心色={Center(after)}");

                    // 层窗口：压根设不上
                    if (transparent)
                    {
                        if (set) throw new InvalidOperationException("层窗口居然设上了，结论要重写");
                        if (left < 0.9) throw new InvalidOperationException("没设上却拍不到了，说明返回值不能信");
                        continue;
                    }
                    // 普通窗口：设得上，但抓回来是个黑洞，不是后面的绿
                    if (!set) throw new InvalidOperationException("普通窗口都设不上，系统比预期还老");
                    if (left > 0.02) throw new InvalidOperationException($"说是设上了，却还是拍到 {left:P0}");
                    if (behind > 0.1)
                        throw new InvalidOperationException(
                            $"居然透出了 {behind:P0} 的后景——那这条路能走，浮条的躲法可以重新考虑");
                }
                finally { w.Close(); }
            }
        }
        finally { back.Close(); }
    }

    static string Center(CapturedImage? img)
    {
        if (img is null) return "无";
        var i = (img.Height / 2) * img.Stride + (img.Width / 2) * 4;
        return $"#{img.Pixels[i + 2]:X2}{img.Pixels[i + 1]:X2}{img.Pixels[i]:X2}";
    }

    /// <summary>抓到的图里这个颜色占了多少（容一点色差，DWM 合成会差几个数）。</summary>
    static double Ratio(CapturedImage? img, Color c)
    {
        if (img is null) throw new InvalidOperationException("抓屏返回 null");
        var hit = 0;
        var total = 0;
        for (var y = 0; y < img.Height; y += 3)
            for (var x = 0; x < img.Width; x += 3)
            {
                var i = y * img.Stride + x * 4;
                total++;
                if (Math.Abs(img.Pixels[i] - c.B) <= 8
                    && Math.Abs(img.Pixels[i + 1] - c.G) <= 8
                    && Math.Abs(img.Pixels[i + 2] - c.R) <= 8) hit++;
            }
        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>等 WPF 画完、再等 DWM 合成上屏，然后才谈得上抓屏。</summary>
    static void Settle()
    {
        Pump();
        System.Threading.Thread.Sleep(220);
        Pump();
    }

    static string AllText(DependencyObject root)
        => string.Join(" ", Descendants<TextBlock>(root).Select(t => t.Text));

    // ------------------------------------------------------------- 工具

    static void Probe(Window w, bool close = true)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -4000;
        w.Top = -4000;
        w.Show();
        w.UpdateLayout();
        w.Measure(new Size(w.Width, w.Height));
        w.Arrange(new Rect(0, 0, w.Width, w.Height));
        if (close) Close(w);
    }

    static void Close(Window w)
    {
        w.Hide();
        w.Close();
    }

    /// <summary>跑一轮消息泵，让 SizeChanged 这类事件真正派发出去。</summary>
    static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) yield return hit;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (var d in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return d;

        if (root is ContentControl { Content: DependencyObject c } && VisualTreeHelper.GetChildrenCount(root) == 0)
            foreach (var d in Descendants<T>(c)) yield return d;
    }
}
