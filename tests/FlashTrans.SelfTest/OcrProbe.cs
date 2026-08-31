using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>抓屏 + 文字识别。不联网，但依赖系统装了 OCR 语言包。</summary>
static class OcrProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("抓屏：虚拟桌面范围合理", () =>
        {
            var vs = ScreenCapture.VirtualScreen();
            var w = vs.Right - vs.Left;
            var h = vs.Bottom - vs.Top;
            Console.WriteLine($"       虚拟桌面 {w}x{h} @ ({vs.Left},{vs.Top})");
            if (w < 320 || h < 240) throw new InvalidOperationException("虚拟桌面尺寸不合理");
        });

        step("抓屏：取一块像素且 alpha 已铺满", () =>
        {
            var img = ScreenCapture.Grab(0, 0, 200, 120)
                      ?? throw new InvalidOperationException("抓屏返回 null");
            if (img.Width != 200 || img.Height != 120)
                throw new InvalidOperationException($"尺寸不对：{img.Width}x{img.Height}");
            if (img.Pixels.Length != 200 * 120 * 4)
                throw new InvalidOperationException("缓冲区大小不对");
            // BitBlt 不管 alpha 通道，忘了铺 255 的话 OCR 会收到一张全透明的图
            for (var i = 3; i < img.Pixels.Length; i += 4)
                if (img.Pixels[i] != 0xFF)
                    throw new InvalidOperationException($"第 {i / 4} 个像素的 alpha 是 {img.Pixels[i]}");
            Console.WriteLine($"       {img.Width}x{img.Height}，{img.Pixels.Length / 1024}KB");
        });

        step("抓屏：宽高为 0 时安全返回", () =>
        {
            if (ScreenCapture.Grab(0, 0, 0, 50) is not null)
                throw new InvalidOperationException("宽度 0 应该返回 null");
            if (ScreenCapture.Grab(0, 0, 50, -3) is not null)
                throw new InvalidOperationException("负高度应该返回 null");
        });

        step("抓屏：小图放大后交给 OCR", () =>
        {
            var img = ScreenCapture.Grab(0, 0, 300, 40)
                      ?? throw new InvalidOperationException("抓屏返回 null");
            var up = img.ScaleUpTo(360);
            Console.WriteLine($"       {img.Width}x{img.Height} → {up.Width}x{up.Height}");
            // 放大倍数有 4 倍上限（细长条按 360 算要 9 倍，放到那么大只是白烧内存），
            // 所以这里要的是「按上限放大了」，不是「一定够到 360」
            if (up.Height != img.Height * 4 || up.Width != img.Width * 4)
                throw new InvalidOperationException($"没按 4 倍上限放大：{up.Width}x{up.Height}");
            if (up.Pixels.Length != up.Width * up.Height * 4)
                throw new InvalidOperationException("放大后缓冲区大小不对");

            // 差一点点的图按需要的倍数放，不要一律顶到上限
            var mid = new CapturedImage(100, 200, new byte[100 * 200 * 4]).ScaleUpTo(360);
            if (mid.Height != 400) throw new InvalidOperationException($"200px 该放 2 倍，实际 {mid.Height}");

            // 够大的图原样返回，不该白拷一遍
            var big = new CapturedImage(100, 400, new byte[100 * 400 * 4]);
            if (!ReferenceEquals(big.ScaleUpTo(360), big))
                throw new InvalidOperationException("已经够高了还在放大");
        });

        step("抓屏：裁一块出来，位置对且越界能收住", CropProbe);
        step("抓屏：存成 PNG 能读回来，尺寸和像素都对", SavePngProbe);
        step("抓屏：保存目录留空时落到「图片」文件夹", SaveDirProbe);

        step("OCR：系统语言包可用性", () =>
        {
            var langs = OcrService.AvailableLanguages;
            Console.WriteLine($"       可用语言：{(langs.Length == 0 ? "（无）" : string.Join(", ", langs))}");
            if (!OcrService.IsAvailable)
                Console.WriteLine("       " + OcrService.NoEngineHint());
        });

        step("OCR：语言解析走前缀匹配", () =>
        {
            if (!OcrService.IsAvailable) { Console.WriteLine("       （无语言包，跳过）"); return; }
            // zh-CN 在系统里叫 zh-Hans-CN，不做前缀匹配会解析失败
            foreach (var want in new[] { "zh-CN", "en", "ja", "auto" })
                Console.WriteLine($"       {want,-6} → {OcrService.ResolveLanguage(want) ?? "（无）"}");
            if (OcrService.ResolveLanguage("zh-CN") is null)
                throw new InvalidOperationException("有语言包却解析不出中文");
            // 没装的语言也要给个能用的兜底，不能返回 null 让调用方崩
            if (OcrService.ResolveLanguage("xx-YY") is null)
                throw new InvalidOperationException("未知语言没有兜底");
        });

        step("OCR：认出自己画上去的字", RoundTrip);

        step("马赛克：格子是硬边且取的是块内均值", MosaicProbe);
        step("马赛克：改格子大小会重做那张图", MosaicBlockProbe);
        step("标注：画完再换颜色粗细，改的是刚画的那一笔", RestyleProbe);
        step("文字：字号独立于粗细，加粗斜体真落到字面上", TextStyleProbe);
        step("文字：加粗改了宽度，量出来的框跟着变", TextMetricProbe);
        step("参数：工具条能调到的范围和配置里夹的范围一致", LimitsProbe);
        step("标注：空心形状只有边框能抓，中间不算", HitTestProbe);
        step("标注：挪位置、夹在选区里、删掉选中那一笔", MoveProbe);
        step("标注：按住 Shift 出正方形、正圆，箭头吸到 15° 档上", ShiftConstraintProbe);
        step("标注：撤销之后能重做，接回原来那一层", RedoProbe);
        step("标注：拖圆点改形状，翻面和夹边都对", HandleProbe);
    }

    /// <summary>
    /// 点在哪一笔上。空心的形状（矩形、圆、箭头、画笔）只认线附近——
    /// 把内部也算上的话，一个大方框会盖住整块选区，选区本身就再也拖不动了。
    /// 实心的（马赛克、文字）整块都算。
    /// </summary>
    static void HitTestProbe()
    {
        var rect = new RectAnnotation { Bounds = new Rect(100, 100, 200, 120), Width = 3 };
        if (!rect.HitTest(new Point(100, 160), 4)) throw new InvalidOperationException("矩形左边线上没抓住");
        if (!rect.HitTest(new Point(200, 220), 4)) throw new InvalidOperationException("矩形下边线上没抓住");
        if (rect.HitTest(new Point(200, 160), 4)) throw new InvalidOperationException("矩形正中间不该算抓住");
        if (rect.HitTest(new Point(60, 160), 4)) throw new InvalidOperationException("矩形外面不该算抓住");

        // 细长条整个都算边框，不然它压根抓不住
        var thin = new RectAnnotation { Bounds = new Rect(0, 0, 100, 3), Width = 3 };
        if (!thin.HitTest(new Point(50, 1.5), 4)) throw new InvalidOperationException("细长矩形抓不住");

        var el = new EllipseAnnotation { Bounds = new Rect(0, 0, 200, 100), Width = 3 };
        if (!el.HitTest(new Point(0, 50), 4)) throw new InvalidOperationException("椭圆最左点没抓住");
        if (!el.HitTest(new Point(100, 0), 4)) throw new InvalidOperationException("椭圆最上点没抓住");
        if (el.HitTest(new Point(100, 50), 4)) throw new InvalidOperationException("椭圆圆心不该算抓住");
        // 外接矩形的角在椭圆外面，按矩形判断就会误中
        if (el.HitTest(new Point(2, 2), 4)) throw new InvalidOperationException("椭圆的角落不该算抓住");

        var arrow = new ArrowAnnotation { From = new Point(0, 0), To = new Point(100, 100), Width = 3 };
        if (!arrow.HitTest(new Point(50, 50), 4)) throw new InvalidOperationException("箭头线上没抓住");
        if (arrow.HitTest(new Point(20, 80), 4)) throw new InvalidOperationException("离箭头线很远不该算抓住");

        var pen = new PenAnnotation { Width = 3 };
        pen.Points.AddRange([new Point(0, 0), new Point(50, 0), new Point(50, 50)]);
        if (!pen.HitTest(new Point(25, 1), 4)) throw new InvalidOperationException("画笔第一段上没抓住");
        if (!pen.HitTest(new Point(50, 25), 4)) throw new InvalidOperationException("画笔第二段上没抓住");
        if (pen.HitTest(new Point(10, 40), 4)) throw new InvalidOperationException("画笔拐角空处不该算抓住");

        // 马赛克是实心一块，整块都能抓
        var m = new MosaicAnnotation { Bounds = new Rect(10, 10, 80, 40) };
        if (!m.HitTest(new Point(50, 30), 4)) throw new InvalidOperationException("马赛克中间该能抓住");
        if (m.HitTest(new Point(200, 30), 4)) throw new InvalidOperationException("马赛克外面不该算抓住");

        // 文字也是实心一块。框有多大得真去量，不能瞎给个数
        var t = new TextAnnotation { At = new Point(20, 30), Text = "标注文字 Annotation", FontSize = 20 };
        var ext = t.Extent;
        if (ext.Width < 20 || ext.Height < 10)
            throw new InvalidOperationException($"文字没量出尺寸：{ext.Width}x{ext.Height}");
        if (Math.Abs(ext.X - 20) > 0.01 || Math.Abs(ext.Y - 30) > 0.01)
            throw new InvalidOperationException($"文字框的左上角该在落笔处，实际 {ext.TopLeft}");
        if (!t.HitTest(new Point(ext.X + ext.Width / 2, ext.Y + ext.Height / 2), 4))
            throw new InvalidOperationException("文字中间该能抓住");
        if (t.HitTest(new Point(ext.Right + 40, ext.Y), 4))
            throw new InvalidOperationException("文字右边老远不该算抓住");

        // 换了字号要重新量，不能拿着上次的缓存不放
        var before = t.Extent.Width;
        t.FontSize = 40;
        if (t.Extent.Width <= before + 1)
            throw new InvalidOperationException($"字号翻倍了框还是 {t.Extent.Width}（原来 {before}）");
        Console.WriteLine("       空心形状只认线附近，实心的整块算，文字框按字号实测");
    }

    /// <summary>
    /// 画歪了要能拖回来。挪出选区的部分导出时不在图上，所以得夹在选区里。
    /// </summary>
    static void MoveProbe()
    {
        var layer = new CaptureSelectionLayer(new CapturedImage(400, 300, new byte[400 * 300 * 4]));
        layer.PresetSelection(new Rect(0, 0, 400, 300));

        // 没选中东西时挪不动，也不能崩
        if (layer.Nudge(new Vector(5, 0))) throw new InvalidOperationException("没选中东西却说挪动了");
        if (layer.DeleteActive()) throw new InvalidOperationException("没选中东西却说删掉了");

        var rect = new RectAnnotation { Bounds = new Rect(100, 100, 60, 40), Width = 3 };
        layer.AddAnnotation(rect);
        if (!layer.Nudge(new Vector(20, -10))) throw new InvalidOperationException("画完了却挪不动");
        if (rect.Bounds.X != 120 || rect.Bounds.Y != 90)
            throw new InvalidOperationException($"挪到了 {rect.Bounds.TopLeft}，该是 (120,90)");

        // 往选区外面推：贴住边就不再动，整个形状（连线宽）都得留在里面
        layer.Nudge(new Vector(-9999, -9999));
        var ext = rect.Extent;
        if (ext.Left < -0.01 || ext.Top < -0.01)
            throw new InvalidOperationException($"被推出选区了：{ext}");
        layer.Nudge(new Vector(9999, 9999));
        ext = rect.Extent;
        if (ext.Right > 400.01 || ext.Bottom > 300.01)
            throw new InvalidOperationException($"被推出选区了：{ext}");

        // 上面把它顶到右下角了，挪回中间来——贴着边的东西是挪不动的（那正是上面在验的）
        layer.Nudge(new Vector(-150, -120));

        // 挪的是「手头那一笔」。又画一笔之后，挪的就是新的那个
        var arrow = new ArrowAnnotation { From = new Point(10, 10), To = new Point(50, 50), Width = 3 };
        layer.AddAnnotation(arrow);
        var rectAt = rect.Bounds.TopLeft;
        layer.Nudge(new Vector(7, 0));
        if (arrow.From.X != 17) throw new InvalidOperationException($"箭头没挪：{arrow.From}");
        if (rect.Bounds.TopLeft != rectAt) throw new InvalidOperationException("挪错了对象，矩形也动了");

        // 删掉手头那一笔，剩下的那个接手；矩形还在
        if (!layer.DeleteActive()) throw new InvalidOperationException("删不掉");
        if (layer.AnnotationCount != 1) throw new InvalidOperationException($"删完还剩 {layer.AnnotationCount} 笔");
        layer.Nudge(new Vector(3, 0));
        if (Math.Abs(rect.Bounds.X - (rectAt.X + 3)) > 0.01)
            throw new InvalidOperationException("删掉之后没接手到剩下那一笔");
        Console.WriteLine("       拖得动、夹在选区里、删的是选中那一笔");
    }

    /// <summary>
    /// 按住 Shift 的那几条约束。都是纯函数，直接喂坐标验——
    /// 正方形要真等边（差一个像素就是没约束住），角度吸完还得是那个角度：
    /// 贴着选区边时要沿原方向缩短，不能把点夹回来把角度掰歪。
    /// </summary>
    static void ShiftConstraintProbe()
    {
        var box = new Rect(0, 0, 400, 300);
        var start = new Point(100, 100);

        // 往右下拖，横向拖得多：边长取大的那个，方向跟着拖的方向
        var sq = CaptureSelectionLayer.SquareFrom(start, new Point(220, 160), box);
        if (Math.Abs(sq.Width - sq.Height) > 0.01)
            throw new InvalidOperationException($"不等边：{sq.Width}x{sq.Height}");
        if (Math.Abs(sq.Width - 120) > 0.01)
            throw new InvalidOperationException($"边长该取大的那个 120，实际 {sq.Width}");
        if (Math.Abs(sq.Left - 100) > 0.01 || Math.Abs(sq.Top - 100) > 0.01)
            throw new InvalidOperationException($"起点该钉在 (100,100)：{sq}");

        // 往左上拖：方框应该落在起点的左上方，仍然等边
        var up = CaptureSelectionLayer.SquareFrom(start, new Point(40, 70), box);
        if (Math.Abs(up.Width - up.Height) > 0.01)
            throw new InvalidOperationException($"左上方向不等边：{up.Width}x{up.Height}");
        if (Math.Abs(up.Right - 100) > 0.01 || Math.Abs(up.Bottom - 100) > 0.01)
            throw new InvalidOperationException($"该钉在起点左上：{up}");

        // 贴着选区右边拖出去：夹住之后还得是正方形，且不许溢出
        var clipped = CaptureSelectionLayer.SquareFrom(new Point(350, 100), new Point(900, 280), box);
        if (Math.Abs(clipped.Width - clipped.Height) > 0.01)
            throw new InvalidOperationException($"夹完不等边了：{clipped.Width}x{clipped.Height}");
        if (clipped.Right > 400.01)
            throw new InvalidOperationException($"溢出选区：{clipped}");
        if (Math.Abs(clipped.Width - 50) > 0.01)
            throw new InvalidOperationException($"该缩到右边剩下的 50，实际 {clipped.Width}");

        // 40° 吸到 45°：两个分量的绝对值相等就是 45
        var snapped = CaptureSelectionLayer.SnapAngle(start, new Point(200, 184), box);
        var dx = snapped.X - start.X;
        var dy = snapped.Y - start.Y;
        if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) > 0.01)
            throw new InvalidOperationException($"没吸到 45°：Δ({dx:F2},{dy:F2})");

        // 差一点点水平的，要吸成完全水平
        var flat = CaptureSelectionLayer.SnapAngle(start, new Point(300, 105), box);
        if (Math.Abs(flat.Y - start.Y) > 0.01)
            throw new InvalidOperationException($"该吸成水平，实际 y={flat.Y}");

        // 吸完戳到选区外：沿原方向缩短，角度不能变
        var far = CaptureSelectionLayer.SnapAngle(new Point(200, 200), new Point(1000, 1000), box);
        if (far.X > 400.01 || far.Y > 300.01)
            throw new InvalidOperationException($"缩短后仍在选区外：{far}");
        var fdx = far.X - 200;
        var fdy = far.Y - 200;
        if (Math.Abs(Math.Abs(fdx) - Math.Abs(fdy)) > 0.01)
            throw new InvalidOperationException($"缩短把 45° 掰歪了：Δ({fdx:F2},{fdy:F2})");

        Console.WriteLine($"       正方形 120、夹到 50 还等边；40°→45°、5°→水平；"
                          + $"越界缩到 ({far.X:F0},{far.Y:F0}) 角度不变");
    }

    /// <summary>
    /// 撤销之后能接回来，而且要接回原来那一层。
    /// 只数个数是验不出层次的——放到最上面个数一样对，但它会盖住本来在它上面的东西。
    /// </summary>
    static void RedoProbe()
    {
        var layer = new CaptureSelectionLayer(new CapturedImage(400, 300, new byte[400 * 300 * 4]));
        layer.PresetSelection(new Rect(0, 0, 400, 300));

        if (layer.CanRedo) throw new InvalidOperationException("什么都没撤销就说能重做");
        if (layer.Redo()) throw new InvalidOperationException("空着也能重做");

        var a = new RectAnnotation { Bounds = new Rect(10, 10, 50, 50), Width = 3 };
        var b = new EllipseAnnotation { Bounds = new Rect(80, 10, 50, 50), Width = 3 };
        var c = new ArrowAnnotation { From = new Point(200, 10), To = new Point(260, 60), Width = 3 };
        layer.AddAnnotation(a);
        layer.AddAnnotation(b);
        layer.AddAnnotation(c);

        // 撤两笔再重做两笔，顺序要跟原来一样
        layer.Undo();
        layer.Undo();
        if (layer.AnnotationCount != 1) throw new InvalidOperationException($"撤了两笔还剩 {layer.AnnotationCount}");
        if (!layer.CanRedo) throw new InvalidOperationException("撤销过了却说不能重做");
        layer.Redo();
        layer.Redo();
        if (layer.Items is not [var i0, var i1, var i2]
            || !ReferenceEquals(i0, a) || !ReferenceEquals(i1, b) || !ReferenceEquals(i2, c))
            throw new InvalidOperationException("重做之后顺序不对");
        if (layer.CanRedo) throw new InvalidOperationException("都重做完了还说能重做");

        // 删掉中间那一笔，重做要放回中间去。放到末尾的话个数一样对，
        // 但它会盖住本来压在它上面的东西——这是只数个数验不出来的那一条。
        layer.SelectForTest(b);
        if (!layer.DeleteActive()) throw new InvalidOperationException("删不掉中间那一笔");
        if (layer.Items.Contains(b)) throw new InvalidOperationException("说删了其实还在");
        layer.Redo();
        if (layer.Items is not [var k0, var k1, var k2]
            || !ReferenceEquals(k0, a) || !ReferenceEquals(k1, b) || !ReferenceEquals(k2, c))
            throw new InvalidOperationException(
                $"删中间一笔再重做没回原层，现在第二笔是 {layer.Items[1].GetType().Name}");

        // 又画了新的一笔，之前撤掉的就接不回来了
        layer.Undo();
        layer.AddAnnotation(new RectAnnotation { Bounds = new Rect(300, 200, 40, 40), Width = 3 });
        if (layer.CanRedo) throw new InvalidOperationException("又画了一笔，撤掉的那些不该还能重做");
        if (layer.Redo()) throw new InvalidOperationException("清空之后还能重做");

        Console.WriteLine("       撤两笔重做两笔顺序不变；删中间一笔重做回原层；又画一笔就清空重做");
    }

    /// <summary>
    /// 拖那几个圆点改形状。矩形八个点、箭头两头；拉过对边要翻面而不是出负宽高。
    /// </summary>
    static void HandleProbe()
    {
        var rect = new RectAnnotation { Bounds = new Rect(100, 100, 200, 100), Width = 3 };
        if (rect.Handles.Count != 8)
            throw new InvalidOperationException($"矩形该有 8 个点，实际 {rect.Handles.Count}");

        // 点的位置：0 左上、2 右上、4 右下、6 左下，奇数是四条边的中点
        var h = rect.Handles;
        if (h[0] != new Point(100, 100) || h[4] != new Point(300, 200))
            throw new InvalidOperationException($"角点不对：{h[0]} {h[4]}");
        if (h[1] != new Point(200, 100) || h[7] != new Point(100, 150))
            throw new InvalidOperationException($"边中点不对：{h[1]} {h[7]}");

        // 拖右下角：只改右下，左上不动
        rect.DragHandle(4, new Point(350, 260));
        if (rect.Bounds != new Rect(100, 100, 250, 160))
            throw new InvalidOperationException($"拖右下角后是 {rect.Bounds}");

        // 拖上边中点：只改上边，左右不动
        rect.DragHandle(1, new Point(999, 60));
        if (rect.Bounds != new Rect(100, 60, 250, 200))
            throw new InvalidOperationException($"拖上边后是 {rect.Bounds}，横向不该变");

        // 拉过对边：翻面，宽高不能是负的
        rect.DragHandle(0, new Point(400, 300));
        if (rect.Bounds.Width < 0 || rect.Bounds.Height < 0)
            throw new InvalidOperationException($"翻面出了负的宽高：{rect.Bounds}");
        if (rect.Bounds != new Rect(350, 260, 50, 40))
            throw new InvalidOperationException($"翻面后是 {rect.Bounds}");

        // 圆和马赛克是同一套（都从 BoundsAnnotation 来），随便验一个别的
        var mos = new MosaicAnnotation { Bounds = new Rect(0, 0, 50, 50) };
        mos.DragHandle(2, new Point(80, 10));
        if (mos.Bounds != new Rect(0, 10, 80, 40))
            throw new InvalidOperationException($"马赛克拖右上角后是 {mos.Bounds}");

        // 箭头两头，拖尖不动尾
        var arrow = new ArrowAnnotation { From = new Point(10, 10), To = new Point(60, 60), Width = 3 };
        if (arrow.Handles.Count != 2)
            throw new InvalidOperationException($"箭头该有 2 个点，实际 {arrow.Handles.Count}");
        arrow.DragHandle(1, new Point(200, 30));
        if (arrow.To != new Point(200, 30) || arrow.From != new Point(10, 10))
            throw new InvalidOperationException($"拖箭尖后 From={arrow.From} To={arrow.To}");

        // 画笔和文字没有改形状的点：手画的一串点套框缩放没意义，文字归字号管
        var pen = new PenAnnotation { Width = 3 };
        pen.Points.Add(new Point(0, 0));
        pen.Points.Add(new Point(10, 10));
        if (pen.Handles.Count != 0) throw new InvalidOperationException("画笔不该有改形状的点");
        if (new TextAnnotation { Text = "x" }.Handles.Count != 0)
            throw new InvalidOperationException("文字不该有改形状的点");

        Console.WriteLine("       矩形 8 点、箭头 2 点，拖边只动一条、拉过对边会翻面");
    }

    /// <summary>
    /// 马赛克得「碎」，不能只是「糊」。糊而不碎意味着字还认得出来，等于没遮。
    /// 所以这里验两件事：同一格里所有像素完全相同（硬边），且那个值是原块的均值。
    /// </summary>
    static void MosaicProbe()
    {
        // 每个像素的蓝色通道 = 列号，红色通道 = 行号，块均值可以手算出来
        const int w = 20, h = 12, block = 8;
        var src = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w * 4 + x * 4;
                src[i] = (byte)(x * 10);       // B
                src[i + 2] = (byte)(y * 20);   // R
                src[i + 3] = 0xFF;
            }

        var m = new CapturedImage(w, h, src).Mosaic(block);
        if (m.Width != w || m.Height != h)
            throw new InvalidOperationException($"尺寸变了：{m.Width}x{m.Height}");

        for (var by = 0; by < h; by += block)
            for (var bx = 0; bx < w; bx += block)
            {
                // 右边和下边那一列格子不满一格，得按实际范围算，不能按 block 算
                var xEnd = Math.Min(bx + block, w);
                var yEnd = Math.Min(by + block, h);
                long sumB = 0, sumR = 0;
                var n = (xEnd - bx) * (yEnd - by);
                for (var y = by; y < yEnd; y++)
                    for (var x = bx; x < xEnd; x++)
                    {
                        sumB += src[y * w * 4 + x * 4];
                        sumR += src[y * w * 4 + x * 4 + 2];
                    }
                var wantB = (byte)(sumB / n);
                var wantR = (byte)(sumR / n);

                for (var y = by; y < yEnd; y++)
                    for (var x = bx; x < xEnd; x++)
                    {
                        var i = y * m.Stride + x * 4;
                        if (m.Pixels[i] != wantB || m.Pixels[i + 2] != wantR)
                            throw new InvalidOperationException(
                                $"({x},{y}) 是 B{m.Pixels[i]}/R{m.Pixels[i + 2]}，该是 B{wantB}/R{wantR}");
                        if (m.Pixels[i + 3] != 0xFF)
                            throw new InvalidOperationException($"({x},{y}) alpha 丢了：{m.Pixels[i + 3]}");
                    }
            }
        Console.WriteLine($"       {w}x{h} 按 {block} 分块，边角不满一格也按实际范围取均值");
    }

    /// <summary>
    /// 马赛克格子是在工具条上现调的，调完必须重做那张打好格子的图。
    /// 忘了扔缓存的话，调格子屏幕上一点反应都没有——看着就像这个按钮坏了。
    /// </summary>
    static void MosaicBlockProbe()
    {
        // 明暗分界故意放在 24——它是 8 的整数倍但不是 32 的，
        // 所以 8px 的格子压不到界（左上角还是纯黑），32px 的格子跨过去会被抹成中间值。
        // 分界放在 32 的话两种格子算出来的左上角都是纯黑，这条探针就什么也验不到了。
        const int w = 64, h = 32, edge = 24;
        var src = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w * 4 + x * 4;
                src[i] = (byte)(x < edge ? 0 : 255);
                src[i + 3] = 0xFF;
            }
        var shot = new CapturedImage(w, h, src);

        var layer = new CaptureSelectionLayer(shot) { MosaicBlock = 8 };
        layer.PresetSelection(new Rect(0, 0, w, h));
        layer.AddAnnotation(new MosaicAnnotation { Bounds = new Rect(0, 0, w, h) });

        var small = layer.Export() ?? throw new InvalidOperationException("导出返回 null");
        layer.MosaicBlock = 32;
        var big = layer.Export() ?? throw new InvalidOperationException("导出返回 null");

        if (small.Pixels.SequenceEqual(big.Pixels))
            throw new InvalidOperationException("换了格子大小，导出的图一模一样——那张图没重做");

        // 8px 的格子压不到分界，左上角还是纯黑；32px 的跨过去，被抹成 (24×0 + 8×255)/32 ≈ 64
        if (small.Pixels[0] != 0)
            throw new InvalidOperationException($"8px 格子不该跨过分界，左上角是 {small.Pixels[0]}");
        if (big.Pixels[0] is < 40 or > 90)
            throw new InvalidOperationException($"32px 格子该跨过分界抹成 ~64，实际 {big.Pixels[0]}");
        Console.WriteLine($"       格子 8 → 左上角 {small.Pixels[0]}，格子 32 → {big.Pixels[0]}");
    }

    /// <summary>
    /// 字号跟画笔粗细是两回事。绑在一起的时候，想给一条细箭头配一行大字
    /// 就得先把线调粗——这里验它们各走各的，加粗和斜体也真落到了字面上。
    /// </summary>
    static void TextStyleProbe()
    {
        var layer = new CaptureSelectionLayer(new CapturedImage(40, 40, new byte[40 * 40 * 4]))
        {
            TextSize = 20,
            PenWidth = 3,
        };

        var t = new TextAnnotation { At = new Point(2, 2), Text = "Bold 加粗", FontSize = 20 };
        layer.AddAnnotation(t);

        // 调粗细：线宽变了，字号一动不动
        layer.PenWidth = 12;
        layer.Restyle();
        if (Math.Abs(t.FontSize - 20) > 0.01)
            throw new InvalidOperationException($"粗细带着字号一起变了：{t.FontSize}");

        // 加粗和斜体要落到刚写的那一段上，跟改颜色一个道理
        layer.TextBold = true;
        layer.TextItalic = true;
        layer.Restyle();
        if (!t.Bold || !t.Italic)
            throw new InvalidOperationException($"加粗/斜体没落到那段字上：Bold={t.Bold} Italic={t.Italic}");

        // 真正画出来的字要比不加粗时多墨。只看属性不够——属性对了但
        // Typeface 里没接上去的话，画出来还是那个细字。
        var plain = InkOf("加粗 Bold", bold: false, italic: false);
        var bold = InkOf("加粗 Bold", bold: true, italic: false);
        if (bold <= plain)
            throw new InvalidOperationException($"加粗没多出墨来：普通 {plain}，加粗 {bold}");

        var italic = InkOf("加粗 Bold", bold: false, italic: true);
        if (italic == plain)
            throw new InvalidOperationException("斜体画出来和普通体一模一样");

        Console.WriteLine($"       字号不跟粗细走；墨量 普通 {plain} → 加粗 {bold}，斜体 {italic}");
    }

    /// <summary>
    /// 一段字画出来占了多少个不透明像素。加粗前后比这个数，
    /// 比「属性设上了没有」实在——属性对而 Typeface 没接上时它一点不变。
    /// </summary>
    static int InkOf(string text, bool bold, bool italic)
    {
        var a = new TextAnnotation
        {
            At = new Point(4, 4),
            Text = text,
            FontSize = 28,
            Bold = bold,
            Italic = italic,
            Color = Colors.Black,
        };

        const int w = 300, h = 60;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
            a.Draw(dc, new AnnotationCtx { Scale = 1, ImageBounds = new Rect(0, 0, w, h) });

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);

        var buf = new byte[w * 4 * h];
        rtb.CopyPixels(buf, w * 4, 0);
        var ink = 0;
        for (var i = 3; i < buf.Length; i += 4)
            if (buf[i] > 40) ink++;   // 半透明的边缘像素也算，但滤掉几乎看不见的那层
        return ink;
    }

    /// <summary>
    /// 量出来的框跟着加粗变。缓存的键漏了 Bold 的话，加粗之后框还是原来那么短，
    /// 右边那几个字就再也点不中了。
    /// </summary>
    static void TextMetricProbe()
    {
        var t = new TextAnnotation { At = new Point(0, 0), Text = "宽度 measured width", FontSize = 24 };
        var plain = t.Extent.Width;

        t.Bold = true;
        var bold = t.Extent.Width;
        if (bold <= plain)
            throw new InvalidOperationException($"加粗之后框没变宽：{plain} → {bold}（缓存没认加粗）");

        // 点到最右边那个字上还得抓得住
        if (!t.HitTest(new Point(bold - 2, t.Extent.Height / 2), 2))
            throw new InvalidOperationException("加粗后右端抓不住，框短了");

        t.Bold = false;
        if (Math.Abs(t.Extent.Width - plain) > 0.01)
            throw new InvalidOperationException($"取消加粗没退回原宽度：{t.Extent.Width}，原来 {plain}");

        t.Italic = true;
        if (t.Extent.Width <= 0) throw new InvalidOperationException("斜体量不出宽度");
        Console.WriteLine($"       宽度 普通 {plain:F1} → 加粗 {bold:F1}，取消后退回");
    }

    /// <summary>
    /// 工具条能调到的范围、配置里夹的范围、设置页滑块的范围必须是同一个。
    /// 不一致的话用户在工具条上调到 20 的粗细，下次启动被悄悄改回 12。
    /// </summary>
    static void LimitsProbe()
    {
        var s = new AppSettings
        {
            Version = AppSettings.CurrentVersion,
            CapturePenWidth = 999,
            CaptureFontSize = 999,
            CaptureMosaicBlock = 999,
        };
        SettingsService.Normalize(s);
        if (s.CapturePenWidth != CaptureLimits.MaxPenWidth)
            throw new InvalidOperationException($"粗细夹到了 {s.CapturePenWidth}，上限该是 {CaptureLimits.MaxPenWidth}");
        if (s.CaptureFontSize != CaptureLimits.MaxFontSize)
            throw new InvalidOperationException($"字号夹到了 {s.CaptureFontSize}，上限该是 {CaptureLimits.MaxFontSize}");
        if (s.CaptureMosaicBlock != CaptureLimits.MaxMosaicBlock)
            throw new InvalidOperationException($"格子夹到了 {s.CaptureMosaicBlock}");

        // 工具条上摆的那几档都得落在范围里，不然点一下就被夹走，看着像没反应
        foreach (var v in CaptureLimits.PenWidths)
            if (v < CaptureLimits.MinPenWidth || v > CaptureLimits.MaxPenWidth)
                throw new InvalidOperationException($"粗细预设 {v} 在范围外");
        foreach (var v in CaptureLimits.FontSizes)
            if (v < CaptureLimits.MinFontSize || v > CaptureLimits.MaxFontSize)
                throw new InvalidOperationException($"字号预设 {v} 在范围外");
        foreach (var v in CaptureLimits.MosaicBlocks)
            if (v < CaptureLimits.MinMosaicBlock || v > CaptureLimits.MaxMosaicBlock)
                throw new InvalidOperationException($"格子预设 {v} 在范围外");

        // 老配置升上来时，字号要接住他原来看到的那个大小，不能忽然换一号
        var old = new AppSettings { Version = 2, CapturePenWidth = 4 };
        if (!SettingsService.Migrate(old)) throw new InvalidOperationException("v2 配置没走迁移");
        var want = CaptureLimits.FontSizeForWidth(4);
        if (Math.Abs(old.CaptureFontSize - want) > 0.01)
            throw new InvalidOperationException($"迁移后的字号是 {old.CaptureFontSize}，该是 {want}");
        if (old.Version != AppSettings.CurrentVersion)
            throw new InvalidOperationException($"迁移完版本是 {old.Version}");

        Console.WriteLine($"       粗细 ≤{CaptureLimits.MaxPenWidth}、字号 ≤{CaptureLimits.MaxFontSize}、"
                          + $"格子 ≤{CaptureLimits.MaxMosaicBlock}；v2 升上来字号接成 {want}");
    }

    /// <summary>
    /// 「先选颜色再画」是个别扭的顺序：画完发现颜色不对就得撤销重画。
    /// 换颜色或粗细要落到刚画的那一笔上。马赛克两个都不吃，撤销之后也没有「刚画的」可改。
    /// </summary>
    static void RestyleProbe()
    {
        var layer = new CaptureSelectionLayer(new CapturedImage(40, 40, new byte[40 * 40 * 4]));

        // 一笔都没画：不能崩，也不能说自己改了
        if (layer.Restyle()) throw new InvalidOperationException("没画过东西却说改了");

        var rect = new RectAnnotation { Bounds = new Rect(1, 1, 10, 10), Color = Colors.Red, Width = 3 };
        layer.AddAnnotation(rect);
        layer.PenColor = Colors.Blue;
        layer.PenWidth = 6;
        if (!layer.Restyle()) throw new InvalidOperationException("画完换颜色却没改到");
        if (rect.Color != Colors.Blue || rect.Width != 6)
            throw new InvalidOperationException($"改的不是刚画那一笔：{rect.Color} / {rect.Width}");

        // 文字吃的是字号，不是线宽。调粗细不该动到已经写好的字号——
        // 这两个值绑在一起过，想要大字就得先把线调粗，那是这一版要拆开的东西。
        var text = new TextAnnotation { At = new Point(2, 2), Text = "字", Color = Colors.Red, FontSize = 15 };
        layer.AddAnnotation(text);
        layer.TextSize = 15;
        layer.PenWidth = 8;
        layer.Restyle();
        if (Math.Abs(text.FontSize - 15) > 0.01)
            throw new InvalidOperationException($"调粗细动到了字号：{text.FontSize}，该还是 15");

        // 调字号才该改到它，而且是落到刚写的那一段上
        layer.TextSize = 36;
        layer.Restyle();
        if (Math.Abs(text.FontSize - 36) > 0.01)
            throw new InvalidOperationException($"调字号没落到刚写的那段字上：{text.FontSize}");

        // 马赛克没有颜色粗细可言，改它等于什么都没发生
        layer.AddAnnotation(new MosaicAnnotation { Bounds = new Rect(1, 1, 8, 8) });
        if (layer.Restyle()) throw new InvalidOperationException("马赛克不该吃颜色粗细");

        // 撤销之后「刚画的」要退回上一笔，不能还指着已经被删掉的那个
        layer.Undo();
        layer.PenColor = Colors.Green;
        if (!layer.Restyle()) throw new InvalidOperationException("撤销后没退回上一笔");
        if (text.Color != Colors.Green)
            throw new InvalidOperationException($"撤销后改错了对象：文字还是 {text.Color}");
        Console.WriteLine("       改色/改粗细落到最后一笔，撤销后退回上一笔");
    }

    /// <summary>
    /// 自己渲染一张已知内容的图交给 OCR，再比对认出来的字。
    /// 不抓真实屏幕：屏幕上有什么无法预期，断言就没法写死。
    /// 用哪种语言的样本取决于系统装了哪个包——拿中文引擎去认英文，
    /// 「Hello」会被读成「HeIIo」（小写 l 认成大写 I），那是样本选错了，不是代码坏了。
    /// </summary>
    /// <summary>
    /// 裁图。选区坐标是算出来的，差一行就会把要识别的字切掉半截，
    /// 所以这里逐像素比对位置，不只看尺寸对不对。
    /// </summary>
    static void CropProbe()
    {
        // 每个像素的蓝色通道写上它的序号，裁出来就能认出原来在哪
        const int w = 16, h = 10;
        var src = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w * 4 + x * 4;
                src[i] = (byte)x;          // B = 列号
                src[i + 1] = (byte)y;      // G = 行号
                src[i + 3] = 0xFF;
            }
        var img = new CapturedImage(w, h, src);

        var cut = CaptureOverlay.CropPixels(img, 5, 3, 6, 4)
                  ?? throw new InvalidOperationException("裁图返回 null");
        if (cut.Width != 6 || cut.Height != 4)
            throw new InvalidOperationException($"尺寸不对：{cut.Width}x{cut.Height}");
        for (var y = 0; y < cut.Height; y++)
            for (var x = 0; x < cut.Width; x++)
            {
                var i = y * cut.Stride + x * 4;
                if (cut.Pixels[i] != 5 + x || cut.Pixels[i + 1] != 3 + y)
                    throw new InvalidOperationException(
                        $"({x},{y}) 取到的是原图 ({cut.Pixels[i]},{cut.Pixels[i + 1]})，该是 ({5 + x},{3 + y})");
            }

        // 超出右下边界要收进去，不能读到缓冲区外面
        var clamped = CaptureOverlay.CropPixels(img, 12, 8, 999, 999)
                      ?? throw new InvalidOperationException("越界裁图返回 null");
        if (clamped.Width != 4 || clamped.Height != 2)
            throw new InvalidOperationException($"没收进边界：{clamped.Width}x{clamped.Height}");

        // 负坐标当 0 处理，宽高为 0 直接没得裁
        var neg = CaptureOverlay.CropPixels(img, -20, -20, 3, 3)
                  ?? throw new InvalidOperationException("负坐标返回 null");
        if (neg.Pixels[0] != 0 || neg.Pixels[1] != 0)
            throw new InvalidOperationException("负坐标没有归到左上角");
        if (CaptureOverlay.CropPixels(img, 2, 2, 0, 5) is not null)
            throw new InvalidOperationException("宽度 0 该返回 null");

        Console.WriteLine($"       16x10 裁 (5,3,6x4) 位置正确；越界收成 {clamped.Width}x{clamped.Height}");
    }

    /// <summary>
    /// 保存目录。留空要落到「图片」文件夹，填了就用填的那个——
    /// 这里错了截图会存到用户找不到的地方。
    /// </summary>
    static void SaveDirProbe()
    {
        var s = SettingsService.Instance.Current;
        var backup = s.CaptureSaveDir;
        try
        {
            s.CaptureSaveDir = "";
            var fallback = AppHost.CaptureDir();
            var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!fallback.StartsWith(pics, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"留空时落到了 {fallback}，不在「图片」文件夹下");
            if (!fallback.EndsWith("FlashTrans", StringComparison.Ordinal))
                throw new InvalidOperationException($"留空时目录名不对：{fallback}");

            s.CaptureSaveDir = @"D:\截图";
            if (AppHost.CaptureDir() != @"D:\截图")
                throw new InvalidOperationException("填了目录却没用上：" + AppHost.CaptureDir());

            // 只填空格等于没填
            s.CaptureSaveDir = "   ";
            if (AppHost.CaptureDir() != fallback)
                throw new InvalidOperationException("全空格没当成留空：" + AppHost.CaptureDir());

            Console.WriteLine($"       留空 → {fallback}");
        }
        finally { s.CaptureSaveDir = backup; }
    }

    /// <summary>
    /// 存 PNG。只截图不识别是一条独立的路，存坏了用户拿不到东西，
    /// 所以存完再读回来逐像素比一遍，顺带确认目录能自己建出来。
    /// </summary>
    static void SavePngProbe()
    {
        const int w = 7, h = 5;
        var src = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w * 4 + x * 4;
                src[i] = (byte)(x * 30);       // B
                src[i + 1] = (byte)(y * 50);   // G
                src[i + 2] = 0x80;             // R
                src[i + 3] = 0xFF;
            }

        // 故意多套一层不存在的子目录：真实场景里用户填的目录可能还没建
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "FlashTransSelfTest", Guid.NewGuid().ToString("N")[..8]);
        var path = System.IO.Path.Combine(dir, "shot.png");
        try
        {
            new CapturedImage(w, h, src).SavePng(path);
            if (!System.IO.File.Exists(path)) throw new InvalidOperationException("文件没生成");

            var back = new System.Windows.Media.Imaging.PngBitmapDecoder(
                new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (back.PixelWidth != w || back.PixelHeight != h)
                throw new InvalidOperationException($"读回来尺寸不对：{back.PixelWidth}x{back.PixelHeight}");

            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                back, PixelFormats.Bgra32, null, 0);
            var buf = new byte[w * 4 * h];
            conv.CopyPixels(buf, w * 4, 0);
            for (var i = 0; i < buf.Length; i++)
                if (buf[i] != src[i])
                    throw new InvalidOperationException(
                        $"第 {i} 字节存回来是 {buf[i]}，原本是 {src[i]}");

            Console.WriteLine($"       {w}x{h} 存 PNG {new System.IO.FileInfo(path).Length} 字节，像素一致");
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { /* 临时目录，删不掉就算了 */ }
        }
    }

    static void RoundTrip()
    {
        if (!OcrService.IsAvailable) { Console.WriteLine("       （无语言包，跳过）"); return; }

        var tag = OcrService.ResolveLanguage(null)!;
        var zh = tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var ja = tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        // 片段都挑不容易混的：避开 l/I、O/0 这类字形几乎一样的组合
        var (sample, pieces, font) = zh ? ("闪译 OCR 1234", new[] { "闪译", "OCR", "1234" }, "Microsoft YaHei")
                                  : ja ? ("翻訳 OCR 1234", new[] { "翻訳", "OCR", "1234" }, "Yu Gothic")
                                       : ("Trans OCR 1234", new[] { "Trans", "OCR", "1234" }, "Segoe UI");

        var text = OcrService.RecognizeAsync(Render(sample, 640, 120, 44, font), null)
                             .GetAwaiter().GetResult();
        Console.WriteLine($"       引擎 {tag}，样本「{sample}」→ 识别「{text}」");

        var flat = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        foreach (var piece in pieces)
            if (!flat.Contains(piece, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"没认出「{piece}」，实际是「{text}」");

        // 汉字之间不能夹空格：引擎把每个字当一个词，原样拼出来就是「闪 译」，
        // 送去翻译质量会明显变差。这里要求识别结果里出现的是连着的。
        if ((zh || ja) && !text.Contains(pieces[0], StringComparison.Ordinal))
            throw new InvalidOperationException($"汉字被空格拆开了：「{text}」");

        // 西文那部分的空格要留着，不能一起吃掉
        if (!text.Contains("OCR 1234", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"西文之间的空格丢了：「{text}」");
    }

    /// <summary>白底黑字画一行文本，转成和抓屏一样的 BGRA 像素。</summary>
    static CapturedImage Render(string text, int width, int height, double fontSize, string font)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(font), fontSize, Brushes.Black, 96);
            dc.DrawText(ft, new Point(20, (height - ft.Height) / 2));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var stride = width * 4;
        var buf = new byte[stride * height];
        rtb.CopyPixels(buf, stride, 0);
        return new CapturedImage(width, height, buf);
    }
}
