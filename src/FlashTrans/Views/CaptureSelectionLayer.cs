using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>选区八个方向的把手，加一个「在选区里面」。</summary>
enum Grip { None, Inside, TL, T, TR, R, BR, B, BL, L }

/// <summary>
/// 截图的画布：底图 + 选区 + 画上去的标注。全部自己 OnRender，不堆控件——
/// 拖动时每帧重画，控件树越薄越跟手。
/// 坐标一律用本元素的 DIP，只有导出成图片时才换算成图的像素。
/// </summary>
sealed class CaptureSelectionLayer : FrameworkElement
{
    const double GripSize = 8;      // 把手方块边长
    const double GripHit = 10;      // 把手的命中半径，比看起来的大一点才好抓
    const double AnnoHit = 4;       // 标注的命中容差，细线也要抓得住
    const double HandleSize = 7;    // 标注上改形状那些点的直径
    const double HandleHit = 8;     // 它们的命中半径

    static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)));
    static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x16, 0x1A)));
    static readonly Brush GripFill = Frozen(new SolidColorBrush(Colors.White));
    static readonly Color Accent = Color.FromRgb(0x4C, 0x8D, 0xFF);
    static readonly Pen Edge = Frozen(new Pen(Frozen(new SolidColorBrush(Accent)), 1.5));
    static readonly Pen GripEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Accent)), 1));
    static readonly Pen Guide = Frozen(new Pen(Frozen(new SolidColorBrush(
        Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF))), 1));

    /// <summary>
    /// 选中那一笔的虚线框，黑白两支笔叠出来。
    /// 先铺一条实的深色，再拿白虚线压上去，白的缝里露出深色——
    /// 这样白底黑底都看得见。单用白虚线在浅色照片上几乎看不出来。
    /// </summary>
    static readonly Pen ActiveEdgeBack = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x10, 0x10))), 1.4));

    static readonly Pen ActiveEdge = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF))), 1.4)
    {
        DashStyle = new DashStyle([3.5, 3.5], 0),
    });

    readonly CapturedImage _shot;
    readonly ImageSource _image;
    ImageSource? _mosaic;

    Rect _sel;
    bool _hasSel;
    Point _dragFrom;
    Rect _selAtDragStart;
    Grip _grip;
    bool _creating;
    Annotation? _drawing;

    /// <summary>已经落下的标注，按先后顺序。撤销就是砍最后一个。</summary>
    readonly List<Annotation> _items = [];

    /// <summary>撤销/删掉的那些，连它原来在第几个位置一起记着，等着被重做接回去。</summary>
    readonly List<(Annotation Item, int At)> _undone = [];

    public CaptureTool Tool { get; set; } = CaptureTool.None;
    public Color PenColor { get; set; } = Colors.Red;
    public double PenWidth { get; set; } = 3;

    /// <summary>文字标注的字号。自己一个值，跟 PenWidth 无关——细箭头配大字是常事。</summary>
    public double TextSize { get; set; } = 20;
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }

    /// <summary>
    /// 马赛克格子多大。改了要把打好格子的那张图扔掉重做，
    /// 不然调了格子屏幕上一点反应都没有。
    /// </summary>
    public int MosaicBlock
    {
        get => _mosaicBlock;
        set
        {
            if (_mosaicBlock == value) return;
            _mosaicBlock = value;
            _mosaic = null;
            InvalidateVisual();
        }
    }

    int _mosaicBlock = 12;

    /// <summary>
    /// 手头正在弄的那一笔：刚画完的，或者刚点中的。换颜色、改粗细、拖动、删除都冲它来。
    /// 不然只能「先选好颜色再画」，画完发现颜色不合适就得撤销重画一遍。
    /// 又画一笔就换成新的；撤销掉之后指回前一笔。
    /// </summary>
    Annotation? _active;

    /// <summary>手头那一笔是什么类型。工具条据此决定第二行摆字号还是摆粗细。</summary>
    public CaptureTool ActiveKind => _active switch
    {
        TextAnnotation => CaptureTool.Text,
        MosaicAnnotation => CaptureTool.Mosaic,
        RectAnnotation => CaptureTool.Rect,
        EllipseAnnotation => CaptureTool.Ellipse,
        ArrowAnnotation => CaptureTool.Arrow,
        PenAnnotation => CaptureTool.Pen,
        _ => CaptureTool.None,
    };

    /// <summary>换了手头这一笔。没选工具时点中一段文字，工具条要把字号那一排换出来。</summary>
    public event Action? ActiveChanged;

    /// <summary>改手头这一笔都走这儿，省得每处都记着发事件。</summary>
    void SetActive(Annotation? a)
    {
        if (ReferenceEquals(_active, a)) return;
        _active = a;
        ActiveChanged?.Invoke();
    }

    /// <summary>正在拖的那一笔，以及按下时的鼠标位置和它当时的位置。</summary>
    Annotation? _moving;
    Point _moveFrom;
    Rect _moveExtent;

    /// <summary>正在改形状的那一笔，和拖的是它第几个点。</summary>
    Annotation? _sizing;
    int _sizingHandle;

    static bool Shift => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    /// <summary>已经挪出去多少。夹边界要按「总位移」算，不能每帧累加，否则贴边时会漂。</summary>
    Vector _moved;

    public bool HasSelection => _hasSel && _sel.Width >= 4 && _sel.Height >= 4;
    public Rect Selection => _sel;
    public bool CanUndo => _items.Count > 0;
    public bool CanRedo => _undone.Count > 0;
    public int AnnotationCount => _items.Count;

    /// <summary>
    /// 图上这些笔的先后顺序。给自测用：重做要把删掉的那一笔放回原来的层，
    /// 只数个数验不出这件事——放到末尾个数也一样对，但它会盖住本来在它上面的东西。
    /// </summary>
    internal IReadOnlyList<Annotation> Items => _items;

    /// <summary>
    /// 给自测用：真实路径是拿鼠标点一下，控制台里点不了。
    /// 「删掉中间那一笔再重做」这条只有中间那一笔被选中时才走得到。
    /// </summary>
    internal void SelectForTest(Annotation? a) => SetActive(a);

    /// <summary>选区变了（新建、移动、缩放），工具条要跟着挪。</summary>
    public event Action? SelectionChanged;
    /// <summary>文字工具点了某处，宿主窗口该在这儿摆一个输入框。</summary>
    public event Action<Point>? TextRequested;
    public event Action? Cancelled;
    /// <summary>双击选区里面，等于「就用这块」。</summary>
    public event Action? Committed;

    public CaptureSelectionLayer(CapturedImage shot)
    {
        _shot = shot;
        _image = shot.ToBitmap();
        Focusable = true;
    }

    // ------------------------------------------------------------- 对外操作

    /// <summary>撤销最后一笔。</summary>
    public void Undo()
    {
        if (_items.Count == 0) return;
        Remove(_items.Count - 1);
        InvalidateVisual();
    }

    /// <summary>删掉手头选中的那一笔。撤销砍的是最后一笔，这个砍的是点中的那一笔。</summary>
    public bool DeleteActive()
    {
        if (_active is null) return false;
        Remove(_items.IndexOf(_active));
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 从图上拿掉第 i 笔，并记下它原来在第几个位置——重做要放回原处。
    /// 放回末尾的话，删掉中间一笔再重做，它会跑到最上层去，盖住本来在它上面的东西。
    /// </summary>
    void Remove(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        var gone = _items[i];
        _items.RemoveAt(i);
        _undone.Add((gone, i));
        // 手头那一笔正好被撤掉了的话得指回还在图上的那个，
        // 不然改颜色会改到一个已经被砍掉的对象上，屏幕上什么都不动。
        // 手头指着别的（点中了中间某一笔）就别动它。
        if (ReferenceEquals(_active, gone)) SetActive(_items.Count > 0 ? _items[^1] : null);
    }

    /// <summary>
    /// 把刚撤掉的那一笔接回来。撤销多按了一下是常事，
    /// 没有这个就只能照着原样再画一遍——手画的那一笔根本画不回来。
    /// </summary>
    public bool Redo()
    {
        if (_undone.Count == 0) return false;
        var (a, at) = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        _items.Insert(Math.Clamp(at, 0, _items.Count), a);
        SetActive(a);
        InvalidateVisual();
        return true;
    }

    public void AddAnnotation(Annotation a)
    {
        if (a.IsEmpty) return;
        _items.Add(a);
        // 又画了新的一笔，之前撤掉的就接不回来了——跟别处的撤销/重做一个规矩，
        // 留着的话「重做」会插进一笔跟当前画面毫无关系的东西。
        _undone.Clear();
        SetActive(a);
        InvalidateVisual();
    }

    /// <summary>
    /// 把当前的颜色、粗细、文字样式套到刚画完的那一笔上。
    /// 返回有没有真的改到东西——工具条据此决定要不要重画。
    /// </summary>
    public bool Restyle()
    {
        if (_active is null || !_active.Styleable) return false;

        _active.Color = PenColor;
        _active.Width = PenWidth;
        // 文字吃的是字号和加粗斜体，不吃线宽。写完再调大、再加粗都落到刚写的那一段上，
        // 跟改颜色一样——不然想换个字号就得删掉重打一遍。
        if (_active is TextAnnotation t)
        {
            t.FontSize = TextSize;
            t.Bold = TextBold;
            t.Italic = TextItalic;
        }

        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 把手头那一笔挪一段。方向键微调走这儿，拖鼠标也走这儿。
    /// 夹在选区里：挪到选区外面的部分导出时不在图上，等于挪没了。
    /// </summary>
    public bool Nudge(Vector d)
    {
        if (_active is null) return false;
        _active.Move(ClampMove(_active.Extent, d));
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// 想挪 d，实际能挪多少。整个 extent 都得留在选区里；
    /// 比选区还大的东西（一段长文字）就不夹了，否则它会被推得乱跑。
    /// </summary>
    Vector ClampMove(Rect extent, Vector d)
    {
        if (!HasSelection || extent.IsEmpty) return d;
        var x = extent.Width <= _sel.Width
            ? Math.Clamp(extent.X + d.X, _sel.Left, _sel.Right - extent.Width) - extent.X
            : d.X;
        var y = extent.Height <= _sel.Height
            ? Math.Clamp(extent.Y + d.Y, _sel.Top, _sel.Bottom - extent.Height) - extent.Y
            : d.Y;
        return new Vector(x, y);
    }

    /// <summary>点在哪一笔上。从上往下找——后画的盖在前面画的上头，先问它。</summary>
    Annotation? HitAnnotation(Point p)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
            if (_items[i].HitTest(p, AnnoHit)) return _items[i];
        return null;
    }

    /// <summary>全选：整屏都要。</summary>
    public void SelectAll()
    {
        _sel = new Rect(0, 0, ActualWidth, ActualHeight);
        _hasSel = true;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>摆一个现成的选区。给自测渲染用，等价于用户已经拖好了。</summary>
    internal void PresetSelection(Rect sel)
    {
        _sel = sel;
        _hasSel = true;
        InvalidateVisual();
    }

    /// <summary>
    /// 把选区那块连标注一起导出成图。
    /// 走 RenderTargetBitmap 而不是直接裁像素：标注是矢量的，按目标像素重画一遍
    /// 才不会出现放大后的锯齿。
    /// </summary>
    public CapturedImage? Export()
    {
        if (!HasSelection) return null;
        var (sx, sy) = Scale();

        var px = (int)Math.Round(_sel.X * sx);
        var py = (int)Math.Round(_sel.Y * sy);
        var pw = (int)Math.Round(_sel.Width * sx);
        var ph = (int)Math.Round(_sel.Height * sy);

        var cropped = CaptureOverlay.CropPixels(_shot, px, py, pw, ph);
        if (cropped is null) return null;
        if (_items.Count == 0) return cropped;   // 没画东西就不用多走一遍渲染

        var ctx = new AnnotationCtx
        {
            Scale = sx,
            Offset = new Vector(_sel.X, _sel.Y),
            Mosaic = MosaicImage(),
            // 底图整体的位置：把整张图的左上角平移到裁出来的小图坐标系里
            ImageBounds = new Rect(-_sel.X * sx, -_sel.Y * sy, _shot.Width, _shot.Height),
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped.ToBitmap(), new Rect(0, 0, cropped.Width, cropped.Height));
            foreach (var a in _items) a.Draw(dc, ctx);
        }

        var rtb = new RenderTargetBitmap(cropped.Width, cropped.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var buf = new byte[cropped.Width * 4 * cropped.Height];
        conv.CopyPixels(buf, cropped.Width * 4, 0);
        // 渲染出来的 alpha 是 255（底图不透明），但保险起见铺一遍：
        // OCR 收到半透明图会认不出字
        for (var i = 3; i < buf.Length; i += 4) buf[i] = 0xFF;
        return new CapturedImage(cropped.Width, cropped.Height, buf);
    }

    // ------------------------------------------------------------- 鼠标

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);

        // 双击选区里面 = 确认，跟按回车一样
        if (e.ClickCount == 2 && HasSelection && _sel.Contains(p) && Tool == CaptureTool.None)
        {
            Committed?.Invoke();
            return;
        }

        if (!HasSelection)
        {
            // 还没有选区：拖出来一个
            _creating = true;
            _dragFrom = p;
            _sel = new Rect(p, p);
            _hasSel = true;
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        if (Tool == CaptureTool.Text)
        {
            TextRequested?.Invoke(p);
            return;
        }

        if (Tool != CaptureTool.None)
        {
            // 画标注：起点必须在选区里，不然就是想重新框
            if (!_sel.Contains(p)) return;
            _drawing = StartAnnotation(p, Shift);
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        // 手头那一笔的改形状点排在最前面。它只在选中的那一笔上画出来，
        // 位置可能压在选区把手上——这时候优先让它改形状：用户刚点中这一笔，
        // 又去点它身上那个明显的圆点，要的显然是改这一笔。想拉选区就先点空处松开选中。
        if (_active is not null && HitHandle(_active, p) is { } h)
        {
            _sizing = _active;
            _sizingHandle = h;
            CaptureMouse();
            InvalidateVisual();
            return;
        }

        // 没选工具：先看八个把手（它们在选区边上，标注被裁在里面，抢不到），
        // 再看有没有点中某一笔标注，最后才是整块挪选区。
        // 顺序反过来的话，一个占了大半个选区的方框会把「整块挪」堵死。
        _grip = HitGrip(p);
        if (_grip is Grip.None or Grip.Inside)
        {
            var hit = HitAnnotation(p);
            if (hit is not null)
            {
                _grip = Grip.None;
                SetActive(hit);
                _moving = hit;
                _moveFrom = p;
                _moveExtent = hit.Extent;
                _moved = default;
                CaptureMouse();
                InvalidateVisual();
                return;
            }
            // 点在空处：手头那一笔不再选中，虚线框收掉
            if (_active is not null) { SetActive(null); InvalidateVisual(); }
        }

        if (_grip == Grip.None) return;
        _dragFrom = p;
        _selAtDragStart = _sel;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_creating)
        {
            _sel = Normalize(_dragFrom, p);
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        if (_drawing is not null)
        {
            Extend(_drawing, p, Shift);
            InvalidateVisual();
            return;
        }

        if (_sizing is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            // 夹在选区里，跟画的时候一个道理：拖到选区外面的部分导出时不在图上
            _sizing.DragHandle(_sizingHandle, Clamp(p, _sel));
            InvalidateVisual();
            return;
        }

        if (_moving is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            // 按「从按下那一刻算起的总位移」夹边界，再减掉已经挪掉的部分。
            // 每帧只夹当帧那一小段的话，贴住边之后鼠标往回走，位置会跟鼠标脱开。
            var want = ClampMove(_moveExtent, p - _moveFrom);
            var step = want - _moved;
            if (step.LengthSquared > 0)
            {
                _moving.Move(step);
                _moved = want;
                InvalidateVisual();
            }
            return;
        }

        if (_grip != Grip.None && e.LeftButton == MouseButtonState.Pressed)
        {
            _sel = Resize(_selAtDragStart, _grip, p - _dragFrom);
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        // 没在拖：光标跟着底下是什么变，让人知道能拉还是能挪。
        // 顺序跟按下时的判断一致，否则光标显的是一回事、点下去做的是另一回事。
        Cursor = Tool != CaptureTool.None ? Cursors.Pen
            : !HasSelection ? Cursors.Cross
            : _active is not null && HitHandle(_active, p) is { } hh ? HandleCursor(_active, hh)
            : HitAnnotation(p) is not null ? Cursors.SizeAll
            : CursorFor(HitGrip(p));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        ReleaseMouseCapture();

        if (_creating)
        {
            _creating = false;
            _sel = Normalize(_dragFrom, p);
            // 手抖点一下不算框选：清掉，让用户重新拖
            if (_sel.Width < 4 || _sel.Height < 4) { _hasSel = false; _sel = default; }
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        if (_drawing is not null)
        {
            Extend(_drawing, p, Shift);
            AddAnnotation(_drawing);
            _drawing = null;
            InvalidateVisual();
            return;
        }

        _moving = null;
        _sizing = null;
        _grip = Grip.None;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // 有标注先撤一笔，有选区就退回没选区，都没有才退出——右键当「后退一步」
        if (_items.Count > 0) Undo();
        else if (HasSelection) { _hasSel = false; _sel = default; SelectionChanged?.Invoke(); InvalidateVisual(); }
        else Cancelled?.Invoke();
    }

    /// <summary>形状类标注的起点，拖动时一直拿它当对角。</summary>
    Point _shapeStart;

    /// <summary>
    /// 这一笔画笔是不是直线模式。按下笔那一刻的 Shift 决定，中途改不了：
    /// 画到一半按下 Shift 就把已经画的手写笔迹清成一条直线，那是把人画的东西弄没了。
    /// </summary>
    bool _penStraight;

    Annotation? StartAnnotation(Point p, bool square)
    {
        _shapeStart = p;
        _penStraight = square;
        return Tool switch
        {
            CaptureTool.Rect => new RectAnnotation { Bounds = new Rect(p, p), Color = PenColor, Width = PenWidth },
            CaptureTool.Ellipse => new EllipseAnnotation { Bounds = new Rect(p, p), Color = PenColor, Width = PenWidth },
            CaptureTool.Arrow => new ArrowAnnotation { From = p, To = p, Color = PenColor, Width = PenWidth },
            CaptureTool.Pen => Pen0(p),
            CaptureTool.Mosaic => new MosaicAnnotation { Bounds = new Rect(p, p) },
            _ => null,
        };
    }

    Annotation Pen0(Point p)
    {
        var a = new PenAnnotation { Color = PenColor, Width = PenWidth };
        a.Points.Add(p);
        return a;
    }

    /// <summary>
    /// 把正在画的那一笔延伸到当前鼠标位置。夹在选区内，别画到选区外面去。
    /// square 是「按住 Shift」：形状取正方形/正圆，箭头吸到 15° 的整数倍上。
    /// 拖的过程中松开或按下 Shift 都马上生效，除了画笔——它按下笔那一刻就定了，见 _penStraight。
    /// </summary>
    void Extend(Annotation a, Point p, bool square)
    {
        p = Clamp(p, _sel);
        switch (a)
        {
            case BoundsAnnotation b:
                b.Bounds = square ? SquareFrom(_shapeStart, p, _sel) : Normalize(_shapeStart, p);
                break;
            case ArrowAnnotation ar:
                ar.To = square ? SnapAngle(_shapeStart, p, _sel) : p;
                break;
            case PenAnnotation pen when _penStraight:
                // 直线模式：整条只留起点和当前点。工具条上没有单独的直线工具，
                // 画个下划线、连一条引线全靠这个。
                pen.Points.RemoveRange(1, pen.Points.Count - 1);
                pen.Points.Add(p);
                break;
            case PenAnnotation pen:
                pen.Points.Add(p);
                break;
        }
    }

    static Point Clamp(Point p, Rect box) => new(
        Math.Clamp(p.X, box.Left, box.Right), Math.Clamp(p.Y, box.Top, box.Bottom));

    static Rect Normalize(Point a, Point b) => new(
        new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
        new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));

    // --------------------------------------------------- 按住 Shift 时的约束
    // 都是纯函数，方便单独验：正方形是不是真的等边、贴着选区边时会不会溢出、
    // 箭头的角度吸完还是不是那个角度。

    /// <summary>
    /// 从 start 拖到 cur，取成正方形。边长按拖得多的那个方向算——
    /// 框住的范围不小于鼠标划过的地方，比取小的那边更符合「我要框住这些」。
    /// 会被 box 夹住：贴着选区边拖时不许溢出去，溢出的部分导出时也不在图上。
    /// </summary>
    internal static Rect SquareFrom(Point start, Point cur, Rect box)
    {
        var dx = cur.X - start.X;
        var dy = cur.Y - start.Y;
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));

        // 往哪边长就看那边还剩多少地方，两个方向取小的，这样两条边还是一样长
        var room = Math.Min(
            dx >= 0 ? box.Right - start.X : start.X - box.Left,
            dy >= 0 ? box.Bottom - start.Y : start.Y - box.Top);
        side = Math.Max(0, Math.Min(side, room));

        return Normalize(start, new Point(
            start.X + (dx >= 0 ? side : -side),
            start.Y + (dy >= 0 ? side : -side)));
    }

    /// <summary>
    /// 把 from→to 这一段吸到 stepDeg 的整数倍角度上。15° 一档，
    /// 水平、竖直、45° 这几个常用的角都在档上。
    /// 吸完可能戳到选区外面，那就沿着这个方向缩短——先夹点再吸角的话角度就歪了。
    /// </summary>
    internal static Point SnapAngle(Point from, Point to, Rect box, double stepDeg = 15)
    {
        var v = to - from;
        var len = v.Length;
        if (len < 1e-6) return to;

        var step = stepDeg * Math.PI / 180;
        var ang = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
        var dir = new Vector(Math.Cos(ang), Math.Sin(ang));
        return from + dir * Math.Min(len, ReachInBox(from, dir, box));
    }

    /// <summary>从 from 沿 dir 走，最远能走多少还留在 box 里。</summary>
    static double ReachInBox(Point from, Vector dir, Rect box)
    {
        var reach = double.PositiveInfinity;
        // 每个方向分别算到那一侧边界还有多远。分量接近 0 就是不往那边走，不设限。
        if (Math.Abs(dir.X) > 1e-9)
            reach = Math.Min(reach, ((dir.X > 0 ? box.Right : box.Left) - from.X) / dir.X);
        if (Math.Abs(dir.Y) > 1e-9)
            reach = Math.Min(reach, ((dir.Y > 0 ? box.Bottom : box.Top) - from.Y) / dir.Y);
        return Math.Max(0, reach);
    }

    // ------------------------------------------------------------- 把手

    /// <summary>鼠标落在哪个把手上。都不沾但在选区里就是 Inside（整块拖）。</summary>
    Grip HitGrip(Point p)
    {
        if (!HasSelection) return Grip.None;
        var (l, t, r, b) = (_sel.Left, _sel.Top, _sel.Right, _sel.Bottom);
        var (cx, cy) = (l + _sel.Width / 2, t + _sel.Height / 2);

        if (Near(p, l, t)) return Grip.TL;
        if (Near(p, cx, t)) return Grip.T;
        if (Near(p, r, t)) return Grip.TR;
        if (Near(p, r, cy)) return Grip.R;
        if (Near(p, r, b)) return Grip.BR;
        if (Near(p, cx, b)) return Grip.B;
        if (Near(p, l, b)) return Grip.BL;
        if (Near(p, l, cy)) return Grip.L;
        return _sel.Contains(p) ? Grip.Inside : Grip.None;
    }

    static bool Near(Point p, double x, double y) =>
        Math.Abs(p.X - x) <= GripHit && Math.Abs(p.Y - y) <= GripHit;

    /// <summary>点在这一笔哪个改形状的点上，都不沾就是 null。</summary>
    static int? HitHandle(Annotation a, Point p)
    {
        var hs = a.Handles;
        for (var i = 0; i < hs.Count; i++)
            if (Math.Abs(p.X - hs[i].X) <= HandleHit && Math.Abs(p.Y - hs[i].Y) <= HandleHit)
                return i;
        return null;
    }

    /// <summary>BoundsAnnotation.Handles 的顺序对应的八个方向。别写成枚举加下标——那样重排枚举就悄悄错位。</summary>
    static readonly Grip[] HandleGrips =
        [Grip.TL, Grip.T, Grip.TR, Grip.R, Grip.BR, Grip.B, Grip.BL, Grip.L];

    /// <summary>改形状的点该配什么光标。矩形那八个按方向给，箭头两头就是个十字。</summary>
    static Cursor HandleCursor(Annotation a, int i) =>
        a is BoundsAnnotation && i < HandleGrips.Length ? CursorFor(HandleGrips[i]) : Cursors.SizeAll;

    static Cursor CursorFor(Grip g) => g switch
    {
        Grip.TL or Grip.BR => Cursors.SizeNWSE,
        Grip.TR or Grip.BL => Cursors.SizeNESW,
        Grip.T or Grip.B => Cursors.SizeNS,
        Grip.L or Grip.R => Cursors.SizeWE,
        Grip.Inside => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    /// <summary>按住把手拖出来的新选区。整块移动时不许拖出屏幕。</summary>
    Rect Resize(Rect start, Grip g, Vector d)
    {
        if (g == Grip.Inside)
        {
            var x = Math.Clamp(start.X + d.X, 0, Math.Max(0, ActualWidth - start.Width));
            var y = Math.Clamp(start.Y + d.Y, 0, Math.Max(0, ActualHeight - start.Height));
            return new Rect(x, y, start.Width, start.Height);
        }

        var (l, t, r, b) = (start.Left, start.Top, start.Right, start.Bottom);
        if (g is Grip.TL or Grip.L or Grip.BL) l += d.X;
        if (g is Grip.TR or Grip.R or Grip.BR) r += d.X;
        if (g is Grip.TL or Grip.T or Grip.TR) t += d.Y;
        if (g is Grip.BL or Grip.B or Grip.BR) b += d.Y;

        // 拉过头会翻面，Normalize 之后仍然是个正的矩形
        return Normalize(
            new Point(Math.Clamp(l, 0, ActualWidth), Math.Clamp(t, 0, ActualHeight)),
            new Point(Math.Clamp(r, 0, ActualWidth), Math.Clamp(b, 0, ActualHeight)));
    }

    /// <summary>图的像素 / 本层 DIP。铺满时两个方向一般一样，但不假设。</summary>
    (double X, double Y) Scale() =>
        (ActualWidth > 0 ? _shot.Width / ActualWidth : 1,
         ActualHeight > 0 ? _shot.Height / ActualHeight : 1);

    // ------------------------------------------------------------- 画

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(_image, full);

        if (!_hasSel || _sel.Width < 1 || _sel.Height < 1)
        {
            dc.DrawRectangle(Dim, null, full);
            DrawHint(dc, full);
            return;
        }

        // 挖洞：选区里保持原亮度，外面压暗
        var hole = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(_sel));
        hole.Freeze();
        dc.DrawGeometry(Dim, null, hole);

        // 标注只画在选区里面：溢出去的部分导出时也不在图上，屏幕上就不该看见
        dc.PushClip(new RectangleGeometry(_sel));
        var ctx = new AnnotationCtx { Scale = 1, Mosaic = _mosaicNeeded() ? MosaicImage() : null, ImageBounds = full };
        foreach (var a in _items) a.Draw(dc, ctx);
        _drawing?.Draw(dc, ctx);
        dc.Pop();

        // 选中框画在裁剪外面：贴着选区边的那一笔，框会有一半在选区外，
        // 裁掉的话就只剩两条边，看着像画坏了。
        if (Tool == CaptureTool.None && _drawing is null) DrawActiveFrame(dc);

        dc.DrawRectangle(null, Edge, _sel);
        if (_creating) DrawGuides(dc, _sel, full);
        // 画标注的时候把手会挡住笔，只在「没选工具」时显示
        if (Tool == CaptureTool.None && !_creating) DrawGrips(dc, _sel);
        DrawSize(dc, _sel, full);
    }

    /// <summary>有没有马赛克要画。没有就别去做那张全屏的马赛克图，白花时间。</summary>
    bool _mosaicNeeded() =>
        _drawing is MosaicAnnotation || _items.Exists(a => a is MosaicAnnotation);

    void DrawHint(DrawingContext dc, Rect full)
    {
        var t = Text("拖动选择区域　·　Esc 取消　·　空格截当前窗口", 15);
        const double pad = 14;
        var w = t.Width + pad * 2;
        var h = t.Height + pad;
        var x = full.Left + (full.Width - w) / 2;
        var y = full.Top + full.Height * 0.42;
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(x, y, w, h), 8, 8);
        dc.DrawText(t, new Point(x + pad, y + pad / 2));
    }

    /// <summary>选区边上引四条线到屏幕边缘，方便对齐。只在拖的时候画，画完就收。</summary>
    static void DrawGuides(DrawingContext dc, Rect sel, Rect full)
    {
        dc.DrawLine(Guide, new Point(full.Left, sel.Top), new Point(full.Right, sel.Top));
        dc.DrawLine(Guide, new Point(full.Left, sel.Bottom), new Point(full.Right, sel.Bottom));
        dc.DrawLine(Guide, new Point(sel.Left, full.Top), new Point(sel.Left, full.Bottom));
        dc.DrawLine(Guide, new Point(sel.Right, full.Top), new Point(sel.Right, full.Bottom));
    }

    /// <summary>
    /// 给手头那一笔套个虚线框，告诉用户「拖的就是这个、改颜色改的也是这个」。
    /// 不画实线：实线框跟选区那条边、跟用户自己画的矩形都容易看混。
    /// </summary>
    void DrawActiveFrame(DrawingContext dc)
    {
        if (_active is null) return;
        var r = _active.Extent;
        if (r.IsEmpty) return;

        r = Rect.Inflate(r, 3, 3);
        if (r.Width <= 0 || r.Height <= 0) return;
        dc.DrawRectangle(null, ActiveEdgeBack, r);
        dc.DrawRectangle(null, ActiveEdge, r);

        // 改形状的点画成圆的，跟选区那八个方块分开——一眼能看出拖的是这一笔还是整个选区
        foreach (var h in _active.Handles)
            dc.DrawEllipse(GripFill, GripEdge, h, HandleSize / 2, HandleSize / 2);
    }

    static void DrawGrips(DrawingContext dc, Rect sel)
    {
        var (cx, cy) = (sel.Left + sel.Width / 2, sel.Top + sel.Height / 2);
        foreach (var (x, y) in new[]
                 {
                     (sel.Left, sel.Top), (cx, sel.Top), (sel.Right, sel.Top),
                     (sel.Right, cy), (sel.Right, sel.Bottom), (cx, sel.Bottom),
                     (sel.Left, sel.Bottom), (sel.Left, cy),
                 })
            dc.DrawRectangle(GripFill, GripEdge,
                new Rect(x - GripSize / 2, y - GripSize / 2, GripSize, GripSize));
    }

    void DrawSize(DrawingContext dc, Rect sel, Rect full)
    {
        var (sx, sy) = Scale();
        // 报的是导出后的像素数，不是 DIP：用户关心存下来的图多大
        var t = Text($"{(int)Math.Round(sel.Width * sx)} × {(int)Math.Round(sel.Height * sy)}", 12);
        const double pad = 6;
        var w = t.Width + pad * 2;
        var h = t.Height + pad;
        var x = Math.Clamp(sel.Left, full.Left, Math.Max(full.Left, full.Right - w));
        // 摆在选区外面，别挡住内容：先试上边，上边贴屏顶就挪到下边，
        // 上下都放不下（选区几乎占满屏）才压进选区里
        var y = sel.Top - h - 5;
        if (y < full.Top) y = sel.Bottom + 5;
        if (y + h > full.Bottom) y = Math.Max(full.Top, Math.Min(sel.Top + 5, full.Bottom - h));
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(t, new Point(x + pad, y + pad / 2));
    }

    FormattedText Text(string s, double size) => new(s, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.White,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>
    /// 整张底图打好格子的版本，第一次用马赛克时才做（全屏一遍要几十毫秒，
    /// 没画马赛克就别做）。做好了存下来，之后每帧重画都是直接铺这张图。
    /// </summary>
    ImageSource? MosaicImage()
    {
        if (_mosaic is not null) return _mosaic;
        _mosaic = _shot.Mosaic(_mosaicBlock).ToBitmap();
        return _mosaic;
    }
}
