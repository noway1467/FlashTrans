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
    /// 两个坑叠在一起：_userResized 只认高度变化，拖宽存不下来；
    /// 而高度自适应时 WPF 每轮布局又拿 Width 反压窗口，把拖出来的宽度弹回旧值。
    /// </summary>
    static void PopupWidthProbe(AppHost host)
    {
        var s = SettingsService.Instance.Current;
        var (w0, l0, t0, blur0) = (s.PopupWidth, s.PopupLeft, s.PopupTop, s.PopupCloseOnBlur);
        try
        {
            // 离屏窗口拿不到焦点，一 pump 就 Deactivated 自己关了，
            // 那时 _widthPinned 还是 false，后面的宽度也就落在隐藏窗口上白设一场。
            s.PopupCloseOnBlur = false;
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

            w.HidePopup();
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
            w.HidePopup();
            Pump();
            Close(w);
        }
        finally
        {
            s.PopupWidth = w0; s.PopupLeft = l0; s.PopupTop = t0; s.PopupCloseOnBlur = blur0;
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
