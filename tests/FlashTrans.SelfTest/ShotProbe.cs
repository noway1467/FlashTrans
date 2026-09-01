using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>
/// --shot：把对话框和轻提示渲染成 PNG，用来肉眼确认外观。
/// 断言能保证不崩、返回值对，但看不出好不好看。
/// </summary>
static class ShotProbe
{
    public static void Run(string outDir, AppHost host)
    {
        Directory.CreateDirectory(outDir);
        var s = SettingsService.Instance.Current;

        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.ApplyTheme(theme);
            ThemeService.ApplyAccent(s.AccentColor);
            var tag = theme == AppTheme.Dark ? "dark" : "light";

            // 参数要和 SettingsWindow.Sources.Remove 里的真实调用一致，
            // 否则截出来的不是用户会看到的那个对话框。
            ShotDialog(outDir, $"dialog-delete-{tag}.png",
                () => AppDialog.Confirm(null, "删除翻译源", "确定要删除「有道翻译」吗？",
                    okText: "删除", tone: DialogTone.Danger, icon: UiKit.IconTrash,
                    detail: "这个源填过的 API 密钥会一起清掉，之后要重新添加并再填一次。"));

            ShotDialog(outDir, $"dialog-reset-{tag}.png",
                () => AppDialog.Confirm(null, "重置为默认设置", "所有设置都会回到初始状态，确定吗？",
                    okText: "重置", tone: DialogTone.Danger,
                    detail: "已配置的翻译源、API 密钥、快捷键、外观偏好全部清空，无法撤销。"));

            ShotDialog(outDir, $"dialog-info-{tag}.png",
                () => AppDialog.Info(null, "还没有日志", "这一次运行还没有写过日志文件。"));

            ShotToast(outDir, $"toast-{tag}.png");
            ShotOcrResult(outDir, $"ocr-result-{tag}.png");
            ShotPopup(outDir, $"popup-{tag}.png", host);
            ShotSettings(outDir, $"settings-general-{tag}.png", host, "general");
            ShotSettings(outDir, $"settings-hotkeys-{tag}.png", host, "hotkeys");
            ShotSettings(outDir, $"settings-about-{tag}.png", host, "about");
            ShotSettings(outDir, $"settings-capture-{tag}.png", host, "capture");
            // 整页图缩到能看的尺寸后中文就糊了，十二行录键框单独来一张
            ShotSettings(outDir, $"settings-capture-keys-{tag}.png", host, "capture", card: "截图工具的键");
        }

        // 框选蒙层自己一套固定配色，不跟主题走，所以只截一遍
        ShotOverlay(outDir, "overlay-idle.png", null);
        ShotOverlay(outDir, "overlay-drag.png", new Rect(232, 118, 300, 96));
        // 贴顶边：尺寸标签要翻到选区下面去，不能盖住要识别的内容
        ShotOverlay(outDir, "overlay-topedge.png", new Rect(60, 6, 200, 60));
        // 几乎占满：上下都没地方，只能压进选区里
        ShotOverlay(outDir, "overlay-fullscreen.png", new Rect(4, 4, 752, 372));

        // 工具条：十几个按钮挤一条，字号和间距只能看图。
        // 后面几张是第二行那排参数——三种上下文摆的东西不一样多，
        // 对齐、间距、当前档位的高亮全都是断言看不见的。
        Save(CaptureOverlay.ToolbarForShot(), Path.Combine(outDir, "capture-toolbar.png"), "Bg");
        Save(CaptureOverlay.ToolbarForShot(CaptureTool.Rect),
             Path.Combine(outDir, "capture-toolbar-width.png"), "Bg");
        Save(CaptureOverlay.ToolbarForShot(CaptureTool.Text),
             Path.Combine(outDir, "capture-toolbar-text.png"), "Bg");
        // B / I 都按下的样子：亮底上那两个字面还得认得出来
        Save(CaptureOverlay.ToolbarForShot(CaptureTool.Text, bold: true, italic: true),
             Path.Combine(outDir, "capture-toolbar-text-on.png"), "Bg");
        Save(CaptureOverlay.ToolbarForShot(CaptureTool.Mosaic),
             Path.Combine(outDir, "capture-toolbar-mosaic.png"), "Bg");

        // 六个标注工具各画一笔，看线条粗细、箭头大小、文字底板、马赛克格子
        ShotAnnotations(outDir, "capture-annotations.png");
        // 选中框：横跨深蓝块和浅色底，两种底色上都得看得见才算合格
        ShotPicked(outDir, "capture-picked.png");
        // 改形状的圆点：箭头两头那两个，跟选区的方块把手能不能分开
        ShotHandles(outDir, "capture-handles.png");

        ThemeService.ApplyTheme(s.Theme);
        ThemeService.ApplyAccent(s.AccentColor);
        Console.WriteLine($"  截图写到 {Path.GetFullPath(outDir)}");
    }

    static void ShotDialog(string dir, string file, Action show)
    {
        Window? captured = null;
        Poll(() =>
        {
            captured = Application.Current.Windows.OfType<AppDialog>().FirstOrDefault(w => w.IsVisible);
            if (captured is null) return false;
            captured.UpdateLayout();
            Save(captured, Path.Combine(dir, file));
            captured.Close();
            return true;
        });
        show();
    }

    /// <summary>
    /// 识别结果窗。三个按钮挤在右下角，中英文混排的那一行字容易撑破框，
    /// 这些只能看图。文字里故意留个错字（wor1d），那正是要「能改」的理由。
    /// </summary>
    static void ShotOcrResult(string dir, string file)
    {
        var w = new OcrResultWindow(
            "Screen capture OCR\n框选之后识别这段文字\nrecognized text can be edited before you copy it, wor1d")
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };
        w.Show();
        w.UpdateLayout();
        Pump();
        Save(w, Path.Combine(dir, file));
        w.Hide();
        w.Close();
    }

    /// <summary>
    /// 翻译弹窗。顶栏那排图标按钮（复制、重译、展开、收起、关闭）挤在一起，
    /// 大小和间距只能看图；「钉住」那颗药丸按钮撤掉之后这一排的观感也得核一眼。
    /// 参数照真实调用走 ShowFor，不然截出来不是用户看到的那个。
    /// </summary>
    static void ShotPopup(string dir, string file, AppHost host)
    {
        var w = new PopupWindow(host)
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };
        w.ShowFor("The quick brown fox jumps over the lazy dog", new Point(-4000, -4000));
        w.UpdateLayout();
        Pump();
        Save(w, Path.Combine(dir, file));
        w.ClosePopup();
        w.Hide();
        w.Close();
    }

    static void ShotToast(string dir, string file)
    {
        ToastWindow.Show("划词翻译已开启");
        Pump();
        var toast = Application.Current.Windows.OfType<ToastWindow>().FirstOrDefault();
        if (toast is null) { Console.WriteLine("  轻提示没创建，跳过截图"); return; }
        toast.UpdateLayout();
        Save(toast, Path.Combine(dir, file));
        ToastWindow.Shutdown();
        Pump();
    }

    /// <summary>
    /// 设置页整页渲染。不截窗口可见区，而是把滚动区里的内容按完整高度铺开，
    /// 这样一张图能看完所有分组，不用来回滚。
    /// 底色用「Bg」——说明文字是直接落在窗口背景上的，铺错底色对比度就看不准了。
    /// </summary>
    static void ShotSettings(string dir, string file, AppHost host, string tab, string? card = null)
    {
        var w = new SettingsWindow(host)
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = -4000,
        };
        w.Show();
        w.SelectTab(tab);
        w.UpdateLayout();
        Pump();

        // 两个 ScrollViewer：窄的是左边分类栏，宽的是内容区
        var page = Descendants<ScrollViewer>(w)
            .OrderByDescending(v => v.ActualWidth)
            .FirstOrDefault()?.Content as FrameworkElement;

        // 页面默认竖向拉满滚动区，内容不够高时会被撑出一大片空白（关于页最明显）。
        // 截图里那片空白纯属干扰，真实窗口里内容本来就贴顶。
        if (page is not null)
        {
            page.VerticalAlignment = VerticalAlignment.Top;
            w.UpdateLayout();
        }

        var target = card is null ? page : page is null ? null : FindCard(page, card);
        if (target is null) Console.WriteLine($"  {file} 找不到{(card is null ? "设置页内容" : $"含「{card}」的分组")}，跳过");
        else Save(target, Path.Combine(dir, file), "Bg");

        w.Hide();
        w.Close();
    }

    /// <summary>
    /// 框选蒙层。底图是自己画的假桌面——真抓屏幕的话每次截出来都不一样，
    /// 而且压暗多少、洞里亮不亮要有确定的参照物才看得出来。
    /// </summary>
    static void ShotOverlay(string dir, string file, Rect? selection)
    {
        var size = new Size(760, 380);
        var desktop = FakeDesktop((int)size.Width, (int)size.Height);
        var layer = CaptureOverlay.LayerForShot(desktop, size, selection);
        Save(layer, Path.Combine(dir, file), "Bg");
    }

    /// <summary>
    /// 六个标注工具各来一笔。断言只能测到「加进去了几条」，
    /// 线粗不粗、箭头是不是个箭头、文字有没有底板、马赛克格子对不对齐，全靠这张图。
    /// </summary>
    static void ShotAnnotations(string dir, string file)
    {
        var size = new Size(760, 380);
        var desktop = FakeDesktop((int)size.Width, (int)size.Height);
        var layer = CaptureOverlay.LayerForShot(desktop, size, new Rect(30, 30, 700, 320),
            draw: l =>
            {
                l.PenColor = Color.FromRgb(0xFF, 0x3B, 0x30);
                l.PenWidth = 3;
                l.AddAnnotation(new RectAnnotation { Bounds = new Rect(50, 50, 150, 70), Color = l.PenColor, Width = 3 });
                l.AddAnnotation(new EllipseAnnotation { Bounds = new Rect(220, 50, 150, 70), Color = l.PenColor, Width = 3 });
                l.AddAnnotation(new ArrowAnnotation { From = new Point(400, 120), To = new Point(520, 55), Color = l.PenColor, Width = 3 });
                l.AddAnnotation(new PenAnnotation
                {
                    Points = { new Point(560, 60), new Point(600, 100), new Point(640, 55), new Point(690, 105) },
                    Color = l.PenColor,
                    Width = 3,
                });
                // 马赛克整块盖住那两行字，正好看出格子是硬边还是糊成一团
                l.AddAnnotation(new MosaicAnnotation { Bounds = new Rect(232, 120, 320, 62) });
                l.AddAnnotation(new TextAnnotation
                {
                    At = new Point(60, 232),
                    Text = "标注文字 Annotation",
                    Color = l.PenColor,
                    FontSize = 20,
                });
                // 加粗和斜体：雅黑有真的粗体字面，斜体是 WPF 自己倾的。
                // 中文倾过去会不会糊、粗体底板有没有被字撑破，都得看图。
                l.AddAnnotation(new TextAnnotation
                {
                    At = new Point(60, 272),
                    Text = "加粗 Bold",
                    Color = l.PenColor,
                    FontSize = 20,
                    Bold = true,
                });
                l.AddAnnotation(new TextAnnotation
                {
                    At = new Point(215, 272),
                    Text = "斜体 Italic",
                    Color = l.PenColor,
                    FontSize = 20,
                    Italic = true,
                });
                // 大字号单独来一个：字号现在跟粗细脱钩了，这一行的线还是 3px
                l.AddAnnotation(new TextAnnotation
                {
                    At = new Point(400, 250),
                    Text = "字号 48",
                    Color = l.PenColor,
                    FontSize = 48,
                    Bold = true,
                });
            });
        Save(layer, Path.Combine(dir, file), "Bg");
    }

    /// <summary>
    /// 选中那一笔的虚线框。最后画进去的那个就是「手头这一笔」，
    /// 所以把矩形放在最后，让框横跨深蓝色块和浅色渐变底——
    /// 单色的虚线只在其中一种底色上看得见，那就是没做对。
    /// </summary>
    static void ShotPicked(string dir, string file)
    {
        var size = new Size(760, 380);
        var desktop = FakeDesktop((int)size.Width, (int)size.Height);
        var layer = CaptureOverlay.LayerForShot(desktop, size, new Rect(20, 20, 720, 340),
            draw: l =>
            {
                l.PenColor = Color.FromRgb(0xFF, 0x3B, 0x30);
                l.PenWidth = 3;
                l.AddAnnotation(new TextAnnotation
                {
                    At = new Point(60, 250),
                    Text = "没选中的那一笔",
                    Color = l.PenColor,
                    FontSize = 20,
                });
                // 压在蓝块上，框会同时经过深蓝、白和浅色渐变
                l.AddAnnotation(new RectAnnotation
                {
                    Bounds = new Rect(30, 30, 220, 90),
                    Color = l.PenColor,
                    Width = 3,
                });
            });
        Save(layer, Path.Combine(dir, file), "Bg");
    }

    /// <summary>
    /// 改形状的那几个圆点。选中的是箭头，只有两头两个点——
    /// 要看的是它们跟选区那八个方块把手分不分得开：都在图上，
    /// 一个是圆的一个是方的，光靠断言看不出来到底看不看得出区别。
    /// 圆点故意压在选区右边缘附近，那儿正是两种把手会挨上的地方。
    /// </summary>
    static void ShotHandles(string dir, string file)
    {
        var size = new Size(760, 380);
        var desktop = FakeDesktop((int)size.Width, (int)size.Height);
        var layer = CaptureOverlay.LayerForShot(desktop, size, new Rect(40, 40, 680, 300),
            draw: l =>
            {
                l.PenColor = Color.FromRgb(0xFF, 0x3B, 0x30);
                l.PenWidth = 3;
                l.AddAnnotation(new RectAnnotation
                {
                    Bounds = new Rect(60, 60, 200, 90),
                    Color = l.PenColor,
                    Width = 3,
                });
                // 最后画的那个是手头这一笔。箭尖伸到选区右边缘，圆点和方块把手挤一块
                l.AddAnnotation(new ArrowAnnotation
                {
                    From = new Point(320, 240),
                    To = new Point(700, 90),
                    Color = l.PenColor,
                    Width = 3,
                });
            });
        Save(layer, Path.Combine(dir, file), "Bg");
    }

    /// <summary>一张有明暗、有色块、有文字的假桌面，用来看蒙层的压暗和挖洞效果。</summary>
    static CapturedImage FakeDesktop(int width, int height)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(
                Color.FromRgb(0xEC, 0xF1, 0xF8), Color.FromRgb(0xC9, 0xD6, 0xE8), 45),
                null, new Rect(0, 0, width, height));

            // 几个色块 + 一段文字，压暗前后一眼能比出来
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x62, 0xD9)), null,
                new Rect(40, 40, 180, 60));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0x54, 0x3F)), null,
                new Rect(560, 250, 150, 90));
            dc.DrawRectangle(Brushes.White, null, new Rect(220, 110, 330, 110));

            var text = new FormattedText(
                "Screen capture OCR\n框选之后识别这段文字", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 20,
                Brushes.Black, 96.0 / 96);
            dc.DrawText(text, new Point(238, 126));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var stride = width * 4;
        var buf = new byte[stride * height];
        rtb.CopyPixels(buf, stride, 0);
        // Pbgra32 的 alpha 已经是 255（画满了底），直接当 Bgra32 用
        return new CapturedImage(width, height, buf);
    }

    /// <summary>
    /// 窗口是透明的，直接渲染只有内容没有底。这里先铺一层主题底色再叠上去，
    /// 不然投影落在透明背景上看不出效果。
    /// </summary>
    static void Save(FrameworkElement w, string path, string backKey = "BgAlt")
    {
        var width = w.ActualWidth;
        var height = w.ActualHeight;
        if (width <= 0 || height <= 0) { Console.WriteLine($"  {Path.GetFileName(path)} 尺寸为 0，跳过"); return; }

        const double scale = 2;   // 2 倍渲染，中文小字才看得清
        var back = Application.Current.TryFindResource(backKey) as SolidColorBrush ?? Brushes.Gray;
        // 整页渲染时四周留点白，卡片边框贴着图边不好看
        var pad = w is Window ? 0 : 14;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(back, null, new Rect(0, 0, width + pad * 2, height + pad * 2));
            dc.DrawRectangle(new VisualBrush(w) { Stretch = Stretch.None }, null,
                new Rect(pad, pad, width, height));
        }
        width += pad * 2;
        height += pad * 2;

        var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        png.Save(fs);
        Console.WriteLine($"  {Path.GetFileName(path)}  {(int)(width * scale)}x{(int)(height * scale)}");
    }

    /// <summary>
    /// 找到含指定文字的那张分组卡片。分组卡是通栏的，先按宽度筛掉里面的小卡片，
    /// 再取最矮的一张——不然嵌套时会框进相邻分组。
    /// </summary>
    /// <summary>
    /// 按分组标题找那一组的卡片。标题不在卡片里面（Section 是「标题 + 卡片」两个兄弟节点），
    /// 所以不能拿标题文字去卡片里搜——得先找到标题，再取它后面紧挨着的那个卡片。
    /// </summary>
    static FrameworkElement? FindCard(FrameworkElement page, string title)
    {
        foreach (var head in Descendants<TextBlock>(page))
        {
            if (!head.Text.Equals(title, StringComparison.Ordinal)) continue;
            if (VisualTreeHelper.GetParent(head) is not Panel host) continue;

            for (var i = host.Children.IndexOf(head) + 1; i < host.Children.Count; i++)
                if (host.Children[i] is System.Windows.Controls.Border card && card.ActualHeight > 0)
                    return card;
        }
        return null;
    }

    static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        if (root is T hit) yield return hit;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (var d in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    static void Poll(Func<bool> act)
    {
        var tries = 0;
        void Step()
        {
            if (act()) return;
            if (++tries > 40) throw new InvalidOperationException("窗口没有出现");
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(Step));
        }
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, new Action(Step));
    }

    static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
