using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FlashTrans.Views;

/// <summary>工具条上选中的是哪个工具。</summary>
public enum CaptureTool
{
    /// <summary>不画东西：拖动改选区、拖边角缩放。</summary>
    None,
    Rect,
    Ellipse,
    Arrow,
    Pen,
    Mosaic,
    Text,
}

/// <summary>
/// 画在截图上的一笔。坐标都在「选区层的 DIP」空间里，导出时统一乘一个比例换成图的像素，
/// 这样界面上多粗的线，导出的图上就是按同样比例的粗细，不会因为屏幕缩放而变。
/// </summary>
abstract class Annotation
{
    // 可写：画完再换颜色/粗细也要能改到刚画的那一笔上，
    // 不是只能「先选好再画」。见 CaptureSelectionLayer.Restyle。
    public Color Color { get; set; } = Colors.Red;
    public double Width { get; set; } = 3;

    /// <summary>颜色和粗细对这一笔有没有意义。马赛克两个都不吃。</summary>
    public virtual bool Styleable => true;

    public abstract void Draw(DrawingContext dc, AnnotationCtx ctx);

    /// <summary>画完还有没有内容：拖出来是个点的形状直接丢掉，别在图上留个看不见的东西。</summary>
    public virtual bool IsEmpty => false;

    /// <summary>这一笔占的地方。选中框画在这儿，拖动时也靠它夹在选区里。</summary>
    public abstract Rect Extent { get; }

    /// <summary>整体挪一段。画歪了不用撤销重画，拖着走就行。</summary>
    public abstract void Move(Vector d);

    /// <summary>
    /// 拖着能改形状的点。选中这一笔时画成小圆点，拖一个就改一处。
    /// 空的表示这一笔改不了形状：画笔是一串手画的点，套个框缩放没什么意义；
    /// 文字的大小归字号那一栏管，不归这儿。
    /// </summary>
    public virtual IReadOnlyList<Point> Handles => [];

    /// <summary>把第 i 个点拖到 to。i 是 Handles 里的下标。</summary>
    public virtual void DragHandle(int i, Point to) { }

    /// <summary>
    /// 点在这一笔上吗。tol 是容差，比看起来的线宽再宽一点才好抓。
    /// 线条类只认线附近：它们中间是空的，把内部也算上的话，一个大方框会盖住
    /// 整块选区，选区本身就再也拖不动了。实心的（马赛克、文字）整块都算。
    /// </summary>
    public abstract bool HitTest(Point p, double tol);

    /// <summary>点到线段的距离。箭头和画笔的命中判断都用它。</summary>
    protected static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        var t = Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>点在矩形边框附近吗。外扩 tol 之内、内缩 tol 之外，就是压在框线上。</summary>
    protected static bool NearEdge(Rect r, Point p, double tol)
    {
        if (!Rect.Inflate(r, tol, tol).Contains(p)) return false;
        // 内缩到负数时 Inflate 给出 Rect.Empty，Contains 恒为 false——
        // 也就是「细长条整个都算边框」，正是想要的
        var inner = Rect.Inflate(r, -tol, -tol);
        return !inner.Contains(p);
    }

    /// <summary>点在椭圆那条线附近吗。</summary>
    protected static bool NearEllipse(Rect r, Point p, double tol)
    {
        var rx = r.Width / 2;
        var ry = r.Height / 2;
        // 扁到没有短轴时按矩形算，下面那个除法会炸
        if (rx < 0.5 || ry < 0.5) return NearEdge(r, p, tol);

        var c = new Point(r.Left + rx, r.Top + ry);
        // 椭圆的隐式方程，在线上时正好等于 1。外扩一个 tol 算一次、内缩一个 tol 再算一次，
        // 落在两者之间就是压在线上。
        double F(double ax, double ay)
        {
            var dx = (p.X - c.X) / ax;
            var dy = (p.Y - c.Y) / ay;
            return dx * dx + dy * dy;
        }
        return F(rx + tol, ry + tol) <= 1
               && F(Math.Max(0.5, rx - tol), Math.Max(0.5, ry - tol)) >= 1;
    }

    protected Pen MakePen(AnnotationCtx ctx)
    {
        var pen = new Pen(new SolidColorBrush(Color), Width * ctx.Scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}

/// <summary>
/// 画的时候需要知道的外部信息。
/// Scale：DIP → 目标画布的倍数。屏幕上预览时是 1，导出到图的像素时是图宽/层宽。
/// Offset：选区左上角，导出时要把坐标平移到裁出来的小图里。
/// Mosaic：整张底图打好格子的版本，马赛克靠裁它的对应区域来画。
/// </summary>
sealed class AnnotationCtx
{
    public double Scale { get; init; } = 1;
    public Vector Offset { get; init; }
    public ImageSource? Mosaic { get; init; }
    /// <summary>底图在本画布坐标系里的整体位置，马赛克要按它对齐。</summary>
    public Rect ImageBounds { get; init; }

    public Point Map(Point p) => new((p.X - Offset.X) * Scale, (p.Y - Offset.Y) * Scale);

    public Rect Map(Rect r) => new(Map(r.TopLeft), new Size(r.Width * Scale, r.Height * Scale));
}

/// <summary>
/// 拿一个矩形定形状的那几种（矩形、圆、马赛克）。它们的 Bounds、挪动、
/// 八个改形状的点都是同一套，只有画法和命中判断不一样。
/// </summary>
abstract class BoundsAnnotation : Annotation
{
    public Rect Bounds { get; set; }
    public override bool IsEmpty => Bounds.Width < 2 || Bounds.Height < 2;
    public override void Move(Vector d) => Bounds = Rect.Offset(Bounds, d);

    /// <summary>八个点：四角加四条边的中点，顺序跟选区把手一致（左上起，顺时针）。</summary>
    public override IReadOnlyList<Point> Handles
    {
        get
        {
            var (l, t, r, b) = (Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
            var (cx, cy) = (l + Bounds.Width / 2, t + Bounds.Height / 2);
            return [new(l, t), new(cx, t), new(r, t), new(r, cy),
                    new(r, b), new(cx, b), new(l, b), new(l, cy)];
        }
    }

    public override void DragHandle(int i, Point to)
    {
        var (l, t, r, b) = (Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
        // 角上的点两个方向都改，边上的只改自己那一条
        if (i is 0 or 6 or 7) l = to.X;
        if (i is 2 or 3 or 4) r = to.X;
        if (i is 0 or 1 or 2) t = to.Y;
        if (i is 4 or 5 or 6) b = to.Y;
        // 拉过对边会翻面，翻过去仍然是个正的矩形
        Bounds = new Rect(new Point(Math.Min(l, r), Math.Min(t, b)),
                          new Point(Math.Max(l, r), Math.Max(t, b)));
    }
}

sealed class RectAnnotation : BoundsAnnotation
{
    // 框线跨在边界两边，各占半个线宽，选中框得把它整个圈进去
    public override Rect Extent => Rect.Inflate(Bounds, Width / 2, Width / 2);
    public override bool HitTest(Point p, double tol) => NearEdge(Bounds, p, tol + Width / 2);

    public override void Draw(DrawingContext dc, AnnotationCtx ctx) =>
        dc.DrawRectangle(null, MakePen(ctx), ctx.Map(Bounds));
}

sealed class EllipseAnnotation : BoundsAnnotation
{
    public override Rect Extent => Rect.Inflate(Bounds, Width / 2, Width / 2);
    public override bool HitTest(Point p, double tol) => NearEllipse(Bounds, p, tol + Width / 2);

    public override void Draw(DrawingContext dc, AnnotationCtx ctx)
    {
        var r = ctx.Map(Bounds);
        dc.DrawEllipse(null, MakePen(ctx), new Point(r.Left + r.Width / 2, r.Top + r.Height / 2),
            r.Width / 2, r.Height / 2);
    }
}

sealed class ArrowAnnotation : Annotation
{
    public Point From { get; set; }
    public Point To { get; set; }
    public override bool IsEmpty => (To - From).Length < 4;

    // 箭头那个头比线粗不少（下面按 Width*3.5 画），外扩要按它算，不然选中框会切掉头
    public override Rect Extent => Rect.Inflate(new Rect(From, To), Width * 1.8, Width * 1.8);
    public override void Move(Vector d) { From += d; To += d; }
    public override bool HitTest(Point p, double tol) =>
        DistToSegment(p, From, To) <= tol + Width / 2 + Width * 1.2;

    /// <summary>两头各一个点：指错了地方就拖箭尖，不用整条重画。</summary>
    public override IReadOnlyList<Point> Handles => [From, To];

    public override void DragHandle(int i, Point to)
    {
        if (i == 0) From = to;
        else To = to;
    }

    public override void Draw(DrawingContext dc, AnnotationCtx ctx)
    {
        var a = ctx.Map(From);
        var b = ctx.Map(To);
        var pen = MakePen(ctx);
        dc.DrawLine(pen, a, b);

        // 箭头大小跟着线宽走，细线配小箭头才好看
        var len = Math.Max(8, Width * ctx.Scale * 3.5);
        var dir = b - a;
        if (dir.Length < 0.01) return;
        dir.Normalize();
        var back = -dir * len;
        var side = new Vector(-dir.Y, dir.X) * (len * 0.45);

        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(b, isFilled: true, isClosed: true);
            g.LineTo(b + back + side, true, false);
            g.LineTo(b + back - side, true, false);
        }
        head.Freeze();
        var fill = new SolidColorBrush(Color);
        fill.Freeze();
        dc.DrawGeometry(fill, null, head);
    }
}

sealed class PenAnnotation : Annotation
{
    public List<Point> Points { get; } = [];
    public override bool IsEmpty => Points.Count < 2;

    public override Rect Extent
    {
        get
        {
            if (Points.Count == 0) return Rect.Empty;
            var r = new Rect(Points[0], Points[0]);
            foreach (var p in Points) r.Union(p);
            return Rect.Inflate(r, Width / 2, Width / 2);
        }
    }

    public override void Move(Vector d)
    {
        for (var i = 0; i < Points.Count; i++) Points[i] += d;
    }

    public override bool HitTest(Point p, double tol)
    {
        var reach = tol + Width / 2;
        for (var i = 1; i < Points.Count; i++)
            if (DistToSegment(p, Points[i - 1], Points[i]) <= reach) return true;
        return Points.Count == 1 && (p - Points[0]).Length <= reach;
    }

    public override void Draw(DrawingContext dc, AnnotationCtx ctx)
    {
        if (Points.Count < 2) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ctx.Map(Points[0]), isFilled: false, isClosed: false);
            for (var i = 1; i < Points.Count; i++) g.LineTo(ctx.Map(Points[i]), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, MakePen(ctx), geo);
    }
}

/// <summary>
/// 马赛克。不自己算像素：把整张底图预先打好格子存成一张图，这里只是把那张图
/// 在本矩形范围内画出来。拖的时候每帧都重画，逐块求平均会卡。
/// </summary>
sealed class MosaicAnnotation : BoundsAnnotation
{
    /// <summary>马赛克没有颜色也没有线宽，改样式时要跳过它。</summary>
    public override bool Styleable => false;

    public override Rect Extent => Bounds;
    // 实心一块，整块都能抓——挪走之后露出来的是那块位置本来的马赛克，正常
    public override bool HitTest(Point p, double tol) => Rect.Inflate(Bounds, tol, tol).Contains(p);

    public override void Draw(DrawingContext dc, AnnotationCtx ctx)
    {
        if (ctx.Mosaic is null) return;
        var area = ctx.Map(Bounds);
        if (area.Width <= 0 || area.Height <= 0) return;

        dc.PushClip(new RectangleGeometry(area));
        // 整张打好格子的图按底图的位置铺上去，只有 clip 里那块看得见，
        // 所以马赛克块和底图的网格是对齐的，拖动时不会跳。
        dc.DrawImage(ctx.Mosaic, ctx.ImageBounds);
        dc.Pop();
    }
}

sealed class TextAnnotation : Annotation
{
    public Point At { get; set; }
    public string Text { get; set; } = "";
    /// <summary>字号按 DIP 存，跟线宽一样在导出时乘比例。自己一个值，不跟线宽绑。</summary>
    public double FontSize { get; set; } = 18;
    public bool Bold { get; set; }
    public bool Italic { get; set; }

    public override bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// 量出来的字有多大，连量的时候用的那几个参数一起存着。
    /// 鼠标一动就要问一次「点在这段字上吗」，每次都重新排版一遍太浪费；
    /// 参数或内容变了才重量。加粗和斜体也算参数——加粗的字更宽，
    /// 漏掉它的话框会短一截，右边那几个字就抓不住了。
    /// </summary>
    (string Text, double Size, bool Bold, bool Italic, Size Measured)? _cache;

    Size Measured()
    {
        if (_cache is { } c && c.Size == FontSize && c.Text == Text
            && c.Bold == Bold && c.Italic == Italic) return c.Measured;
        var ft = MakeText(1);
        var m = new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        _cache = (Text, FontSize, Bold, Italic, m);
        return m;
    }

    // 一整块字，中间不是空的，所以整块都能抓
    public override Rect Extent => new(At, Measured());
    public override void Move(Vector d) => At += d;
    public override bool HitTest(Point p, double tol) => Rect.Inflate(Extent, tol, tol).Contains(p);

    public override void Draw(DrawingContext dc, AnnotationCtx ctx)
    {
        if (IsEmpty) return;
        var brush = new SolidColorBrush(Color);
        brush.Freeze();
        var ft = MakeText(ctx.Scale, brush);
        var at = ctx.Map(At);

        // 沿着字的轮廓描一圈深色，像字幕那样。
        // 不垫底板：底板在浅色背景上就是一块灰，红字压在灰上比不垫还难认，
        // 还会挡住原图内容——标注本来是要指点原图的。
        //
        // 描边固定用黑：底图是什么样这里并不知道，而画面里极少有纯黑的大片区域，
        // 黑边能在绝大多数底色上把字托出来。只有用户挑了接近黑的颜色时才反过来用白，
        // 否则字和描边一起糊在深色底上。
        var outline = new SolidColorBrush(Luminance(Color) < 0.22
            ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xCC, 0, 0, 0));
        outline.Freeze();
        var pen = new Pen(outline, Math.Max(1.2, FontSize * ctx.Scale * 0.09))
        {
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        // 描边和填色分两次画。一次 DrawGeometry(brush, pen, ...) 是不行的：
        // 笔画跨在轮廓线两边，一半压进字里，笔杆本来就只有两三个像素粗，
        // 被吃掉一半就只剩一丝原色，看上去整个字都是描边的颜色。
        // 先描后填，描进去的那一半被填色盖掉，只剩外面那一圈。
        var geo = ft.BuildGeometry(at);
        dc.DrawGeometry(null, pen, geo);
        dc.DrawGeometry(brush, null, geo);
    }

    /// <summary>排版一次。量尺寸和真正画都走这儿，否则量出来的框和画出来的字会对不上。</summary>
    FormattedText MakeText(double scale, Brush? brush = null) => new(
        Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        Face(), FontSize * scale, brush ?? Brushes.Black, 1.0);

    /// <summary>
    /// 字体。雅黑有真正的粗体字面，加粗不是把笔画描一圈糊出来的；
    /// 斜体它没有，WPF 会自己倾斜一份（Oblique），效果够用。
    /// </summary>
    Typeface Face() => new(
        new FontFamily("Microsoft YaHei UI"),
        Italic ? FontStyles.Italic : FontStyles.Normal,
        Bold ? FontWeights.Bold : FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>感知亮度（Rec.709）。用来决定描边该用黑的还是白的。</summary>
    static double Luminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
