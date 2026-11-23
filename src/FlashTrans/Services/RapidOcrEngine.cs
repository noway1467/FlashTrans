// PP-OCR 流程参考 RapidAI/RapidOCR 与 BobLd/RapidOcrNet 4.1.0（Apache-2.0）。
// 本文件将图像处理改为紧排 BGRA32 的纯 C# 实现；来源与许可见 Assets/licenses/RapidOcrNet.txt。
using System.IO;
using System.Text;
using Clipper2Lib;
using FlashTrans.Interop;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FlashTrans.Services;

/// <summary>只使用随包的 v6 模型，检测、方向分类和识别全程在本机执行。</summary>
internal sealed class RapidOcrEngine : IDisposable
{
    internal readonly record struct PixelPoint(float X, float Y);
    internal sealed record TextBlock(string Text, PixelPoint[] BoxPoints);

    readonly InferenceSession _detector;
    readonly InferenceSession _classifier;
    readonly InferenceSession _recognizer;
    readonly string[] _keys;
    readonly int _clsWidth;
    readonly int _clsHeight;

    public RapidOcrEngine(string directory)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            InterOpNumThreads = 0,
            IntraOpNumThreads = 0,
        };
        _detector = new InferenceSession(Path.Combine(directory, "PP-OCRv6_det_tiny.onnx"), options);
        try
        {
            _classifier = new InferenceSession(Path.Combine(directory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"), options);
            try
            {
                _recognizer = new InferenceSession(Path.Combine(directory, "PP-OCRv6_rec_small.onnx"), options);
                try
                {
                    _keys = ["#", .. File.ReadAllLines(Path.Combine(directory, "ppocrv6_small_dict.txt"), Encoding.UTF8), " "];
                    var dimensions = _classifier.InputMetadata.Values.First().Dimensions;
                    _clsHeight = dimensions[2] > 0 ? dimensions[2] : 48;
                    _clsWidth = dimensions[3] > 0 ? dimensions[3] : 192;
                }
                catch { _recognizer.Dispose(); throw; }
            }
            catch { _classifier.Dispose(); throw; }
        }
        catch { _detector.Dispose(); throw; }
    }

    public IReadOnlyList<TextBlock> Detect(CapturedImage original, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // PPOCRv6 = PythonCompat：无外围 padding、无固定 ImgResize；先约束 30..2000，
        // 再对超宽文字补上下白边（比例 8），最后将检测短边提升到 736 并做 32 对齐。
        var width = original.Width;
        var height = original.Height;
        var longest = Math.Max(width, height);
        if (longest > 2000)
        {
            var ratio = 2000f / longest;
            width = (int)(width * ratio);
            height = (int)(height * ratio);
        }
        var shortest = Math.Min(width, height);
        if (shortest < 30)
        {
            var ratio = 30f / Math.Max(1, shortest);
            width = (int)(width * ratio);
            height = (int)(height * ratio);
        }
        width = Align32(width);
        height = Align32(height);
        var bounded = Resize(original, width, height, ct);
        var paddingTop = 0;
        var image = bounded;
        if ((float)width / height > 8 || height <= 30)
        {
            paddingTop = Math.Abs(Math.Max(width / 8, 30) * 2 - height) / 2;
            image = VerticalPadding(bounded, paddingTop);
        }
        var scale = Math.Max(1f, 736f / Math.Min(image.Width, image.Height));
        var detWidth = Align32((int)(image.Width * scale));
        var detHeight = Align32((int)(image.Height * scale));
        using var detection = Run(_detector, Normalize(Resize(image, detWidth, detHeight, ct)), ct);
        var prediction = detection.First().AsTensor<float>().ToArray();
        if (prediction.Length != checked(detWidth * detHeight))
            throw new InvalidDataException("检测模型返回了不匹配的像素尺寸。");

        var boxes = DetectBoxes(prediction, detWidth, detHeight, image.Width, image.Height, ct);
        var result = new List<TextBlock>(boxes.Count);
        foreach (var box in boxes)
        {
            ct.ThrowIfCancellationRequested();
            var crop = PerspectiveCrop(image, box, ct);
            if (crop.Height >= crop.Width * 1.5) crop = crop.RotateForOcr(true);
            var clsWidth = Math.Max(1, Math.Min(_clsWidth, (int)Math.Ceiling((float)_clsHeight * crop.Width / crop.Height)));
            var clsImage = Resize(crop, clsWidth, _clsHeight, ct);
            var clsTensor = Normalize(clsImage, _clsWidth, 128);
            using (var classification = Run(_classifier, clsTensor, ct))
            {
                var scores = classification.First().AsTensor<float>().ToArray();
                if (scores[1] > scores[0] && scores[1] >= 0.9f) crop = Rotate180(crop);
            }

            // 识别模型使用动态宽度；4.1.0 并没有补到固定 320 宽，不能挤压长公式。
            var recWidth = Math.Max(1, (int)(crop.Width * (48f / crop.Height)));
            using var recognition = Run(_recognizer, Normalize(Resize(crop, recWidth, 48, ct)), ct);
            var tensor = recognition.First().AsTensor<float>();
            var steps = tensor.Dimensions[1];
            var columns = tensor.Dimensions[2];
            if (columns != _keys.Length)
                throw new InvalidDataException("识别模型与词表的字符数不一致。");
            var probabilities = tensor.ToArray();
            var text = new StringBuilder();
            var lastIndex = 0;
            var scoreSum = 0f;
            var characters = 0;
            for (var step = 0; step < steps; step++)
            {
                var index = 0;
                var score = float.NegativeInfinity;
                var offset = step * columns;
                for (var column = 0; column < columns; column++)
                {
                    if (probabilities[offset + column] <= score) continue;
                    score = probabilities[offset + column];
                    index = column;
                }
                if (index > 0 && index != lastIndex)
                {
                    text.Append(_keys[index]);
                    scoreSum += score;
                    characters++;
                }
                lastIndex = index;
            }
            if (characters == 0 || scoreSum / characters < 0.5f) continue;
            var boundRatioW = (float)original.Width / bounded.Width;
            var boundRatioH = (float)original.Height / bounded.Height;
            var mapped = box.Select(p => new PixelPoint(
                Math.Clamp(MathF.Round(p.X * boundRatioW), 0, original.Width),
                Math.Clamp(MathF.Round((p.Y - paddingTop) * boundRatioH), 0, original.Height))).ToArray();
            result.Add(new TextBlock(text.ToString(), mapped));
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session,
        DenseTensor<float> tensor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var cancel = ct.Register(() => options.Terminate = true);
        try
        {
            return session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)], session.OutputNames, options);
        }
        catch (OnnxRuntimeException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("本地 OCR 已取消。", ex, ct);
        }
    }

    static DenseTensor<float> Normalize(CapturedImage image, int paddedWidth = 0, byte padding = 0)
    {
        var width = paddedWidth > 0 ? paddedWidth : image.Width;
        var tensor = new DenseTensor<float>(new[] { 1, 3, image.Height, width });
        var data = tensor.Buffer.Span;
        if (width > image.Width) data.Fill((padding - 127.5f) * (1f / 127.5f));
        // RapidOcrNet 对 Bgra8888 直接读取前三个字节，三个模型实际输入都是 BGR。
        var plane = width * image.Height;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var source = y * image.Stride + x * 4;
                var destination = y * width + x;
                for (var channel = 0; channel < 3; channel++)
                    data[channel * plane + destination] = (image.Pixels[source + channel] - 127.5f) * (1f / 127.5f);
            }
        }
        return tensor;
    }

    static int Align32(int value) => Math.Max(32, (int)Math.Round(value / 32d) * 32);

    static CapturedImage VerticalPadding(CapturedImage image, int padding)
    {
        if (padding == 0) return image;
        var pixels = new byte[checked(image.Stride * (image.Height + padding * 2))];
        Array.Fill(pixels, (byte)255);
        Buffer.BlockCopy(image.Pixels, 0, pixels, padding * image.Stride, image.Pixels.Length);
        return new CapturedImage(image.Width, image.Height + padding * 2, pixels);
    }

    static CapturedImage Rotate180(CapturedImage image)
    {
        var pixels = new byte[image.Pixels.Length];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            Buffer.BlockCopy(image.Pixels, offset, pixels, pixels.Length - offset - 4, 4);
        return new CapturedImage(image.Width, image.Height, pixels);
    }

    static float Mitchell(float distance)
    {
        var x = MathF.Abs(distance);
        if (x < 1) return (7 * x * x * x - 12 * x * x + 16f / 3) / 6;
        if (x < 2) return (-7f / 3 * x * x * x + 12 * x * x - 20 * x + 32f / 3) / 6;
        return 0;
    }

    static CapturedImage Resize(CapturedImage image, int width, int height, CancellationToken ct)
    {
        if (width == image.Width && height == image.Height) return image;
        var pixels = new byte[checked(width * height * 4)];
        // 两次可分离卷积保留浮点中间结果，最后才量化，匹配 Skia 的 Mitchell 采样。
        var horizontal = new float[checked(width * image.Height * 3)];
        for (var x = 0; x < width; x++)
        {
            ct.ThrowIfCancellationRequested();
            var sourceX = (x + 0.5f) * image.Width / width - 0.5f;
            var start = (int)MathF.Floor(sourceX) - 1;
            for (var y = 0; y < image.Height; y++)
            {
                for (var sample = 0; sample < 4; sample++)
                {
                    var sx = Math.Clamp(start + sample, 0, image.Width - 1);
                    var weight = Mitchell(sourceX - (start + sample));
                    var source = y * image.Stride + sx * 4;
                    var destination = (y * width + x) * 3;
                    for (var channel = 0; channel < 3; channel++)
                        horizontal[destination + channel] += image.Pixels[source + channel] * weight;
                }
            }
        }
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            var sourceY = (y + 0.5f) * image.Height / height - 0.5f;
            var start = (int)MathF.Floor(sourceY) - 1;
            for (var x = 0; x < width; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = 0f;
                    for (var sample = 0; sample < 4; sample++)
                    {
                        var sy = Math.Clamp(start + sample, 0, image.Height - 1);
                        value += horizontal[(sy * width + x) * 3 + channel] * Mitchell(sourceY - (start + sample));
                    }
                    pixels[(y * width + x) * 4 + channel] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
                }
                pixels[(y * width + x) * 4 + 3] = 255;
            }
        }
        return new CapturedImage(width, height, pixels);
    }

    static List<PixelPoint[]> DetectBoxes(float[] prediction, int width, int height,
        int sourceWidth, int sourceHeight, CancellationToken ct)
    {
        var mask = new int[prediction.Length];
        // DBNet 的 3×3 膨胀必须在独立结果上进行，不能边写边把邻域无限扩散。
        for (var y = 1; y < height - 1; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 1; x < width - 1; x++)
            {
                for (var dy = -1; dy <= 1 && mask[y * width + x] == 0; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (prediction[(y + dy) * width + x + dx] > 0.3f) { mask[y * width + x] = 1; break; }
            }
        }
        var boxes = new List<PixelPoint[]>();
        foreach (var contour in FindContours(mask, width, height))
        {
            ct.ThrowIfCancellationRequested();
            if (contour.Length < 3) continue;
            var box = MiniBox(contour, out var minEdge);
            if (minEdge < 3 || box.Length != 4) continue;
            if (BoxScore(contour, prediction, width, height) < 0.5) continue;
            var path = new Path64(box.Select(p => new Point64(p.X, p.Y)));
            var perimeter = 0d;
            var signedArea = 0f;
            for (var i = 0; i < 4; i++)
            {
                var next = box[(i + 1) % 4];
                var dx = (double)next.X - box[i].X;
                var dy = (double)next.Y - box[i].Y;
                perimeter += Math.Sqrt(dx * dx + dy * dy);
                signedArea += (next.X - box[i].X) * (next.Y + box[i].Y) / 2f;
            }
            var offset = new ClipperOffset();
            offset.AddPath(path, JoinType.Round, EndType.Polygon);
            var expanded = new Paths64();
            offset.Execute(MathF.Abs(signedArea) * 1.6f / perimeter, expanded);
            if (expanded.Count == 0) continue;
            box = MiniBox(expanded[0].Select(p => new PixelPoint(p.X, p.Y)).ToArray(), out minEdge);
            if (minEdge < 5 || box.Length != 4) continue;
            boxes.Add(box.Select(p => new PixelPoint(
                Math.Clamp((int)(p.X / ((float)width / sourceWidth)), 0, sourceWidth),
                Math.Clamp((int)(p.Y / ((float)height / sourceHeight)), 0, sourceHeight))).ToArray());
        }
        var ordered = boxes.OrderBy(b => b[0].Y).ToArray();
        var line = 0;
        var tagged = new List<(PixelPoint[] Box, int Line)>();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (i > 0 && ordered[i][0].Y - ordered[i - 1][0].Y >= 10) line++;
            tagged.Add((ordered[i], line));
        }
        return tagged.OrderBy(b => b.Line).ThenBy(b => b.Box[0].X).Select(b => b.Box).ToList();
    }

    static double BoxScore(PixelPoint[] contour, float[] prediction, int width, int height)
    {
        var left = Math.Clamp((int)contour.Min(p => p.X), 1, width - 1);
        var right = Math.Clamp((int)contour.Max(p => p.X), 1, width - 1);
        var top = Math.Clamp((int)contour.Min(p => p.Y), 1, height - 1);
        var bottom = Math.Clamp((int)contour.Max(p => p.Y), 1, height - 1);
        double sum = 0;
        var count = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var inside = false;
                for (var i = 0; i < contour.Length; i++)
                {
                    var a = contour[i];
                    var b = contour[(i + 1) % contour.Length];
                    if ((a.Y > y + 0.5f) != (b.Y > y + 0.5f)
                        && x + 0.5f < (b.X - a.X) * (y + 0.5f - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                if (!inside) continue;
                // 4.1.0 的分数图子集以 min-1 为起点；保留这一采样偏移和 8 位量化。
                sum += MathF.Round(prediction[(y - 1) * width + x - 1] * 255f) / 255d;
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    static float Distance(PixelPoint a, PixelPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static float Cross(PixelPoint a, PixelPoint b, PixelPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static PixelPoint[] MiniBox(PixelPoint[] points, out float minEdge)
    {
        minEdge = 0;
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return [];
        var hull = new List<PixelPoint>();
        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lowerCount = hull.Count;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            while (hull.Count > lowerCount && Cross(hull[^2], hull[^1], sorted[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        var bestArea = float.PositiveInfinity;
        PixelPoint[] best = [];
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var length = Distance(a, b);
            var ux = (b.X - a.X) / length;
            var uy = (b.Y - a.Y) / length;
            var minU = float.PositiveInfinity;
            var maxU = float.NegativeInfinity;
            var minV = float.PositiveInfinity;
            var maxV = float.NegativeInfinity;
            foreach (var p in hull)
            {
                var u = p.X * ux + p.Y * uy;
                var v = -p.X * uy + p.Y * ux;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            var area = (maxU - minU) * (maxV - minV);
            if (area >= bestArea) continue;
            bestArea = area;
            minEdge = Math.Min(maxU - minU, maxV - minV);
            best = [new(minU * ux - minV * uy, minU * uy + minV * ux),
                    new(maxU * ux - minV * uy, maxU * uy + minV * ux),
                    new(maxU * ux - maxV * uy, maxU * uy + maxV * ux),
                    new(minU * ux - maxV * uy, minU * uy + maxV * ux)];
        }
        if (best.Length != 4) return [];
        Array.Sort(best, (a, b) => a.X.CompareTo(b.X));
        var left = best.Take(2).OrderBy(p => p.Y).ToArray();
        var right = best.Skip(2).OrderBy(p => p.Y).ToArray();
        return [left[0], right[0], right[1], left[1]];
    }

    static CapturedImage PerspectiveCrop(CapturedImage image, PixelPoint[] box, CancellationToken ct)
    {
        var width = Math.Max(1, (int)Distance(box[0], box[1]));
        var height = Math.Max(1, (int)Distance(box[0], box[3]));
        var dx1 = box[1].X - box[2].X;
        var dx2 = box[3].X - box[2].X;
        var dx3 = box[0].X - box[1].X + box[2].X - box[3].X;
        var dy1 = box[1].Y - box[2].Y;
        var dy2 = box[3].Y - box[2].Y;
        var dy3 = box[0].Y - box[1].Y + box[2].Y - box[3].Y;
        var denominator = dx1 * dy2 - dx2 * dy1;
        var g = denominator == 0 ? 0 : (dx3 * dy2 - dx2 * dy3) / denominator;
        var h = denominator == 0 ? 0 : (dx1 * dy3 - dx3 * dy1) / denominator;
        var a = box[1].X - box[0].X + g * box[1].X;
        var b = box[3].X - box[0].X + h * box[3].X;
        var d = box[1].Y - box[0].Y + g * box[1].Y;
        var e = box[3].Y - box[0].Y + h * box[3].Y;
        var pixels = new byte[checked(width * height * 4)];
        var left = (int)box.Min(p => p.X);
        var right = Math.Min(image.Width - 1, (int)box.Max(p => p.X) - 1);
        var top = (int)box.Min(p => p.Y);
        var bottom = Math.Min(image.Height - 1, (int)box.Max(p => p.Y) - 1);
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            var v = (y + 0.5f) / height;
            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5f) / width;
                var divisor = g * u + h * v + 1;
                var sx = (a * u + b * v + box[0].X) / divisor - 0.5f;
                var sy = (d * u + e * v + box[0].Y) / divisor - 0.5f;
                var startX = (int)MathF.Floor(sx) - 1;
                var startY = (int)MathF.Floor(sy) - 1;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = 0f;
                    for (var row = 0; row < 4; row++)
                    {
                        var sourceY = Math.Clamp(startY + row, top, bottom);
                        var wy = Mitchell(sy - startY - row);
                        for (var column = 0; column < 4; column++)
                        {
                            var sourceX = Math.Clamp(startX + column, left, right);
                            value += image.Pixels[sourceY * image.Stride + sourceX * 4 + channel]
                                * wy * Mitchell(sx - startX - column);
                        }
                    }
                    pixels[(y * width + x) * 4 + channel] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
                }
                pixels[(y * width + x) * 4 + 3] = 255;
            }
        }
        return new CapturedImage(width, height, pixels);
    }

    // Suzuki 邻域追踪保留外轮廓，不用连通域的轴对齐框替代旋转检测框。
    static readonly (int X, int Y)[] Neighbors = [(1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1)];

    static int FindNeighbor(int[] mask, int width, int x, int y, int previousX, int previousY, bool clockwise, int offset)
    {
        var first = Array.IndexOf(Neighbors, (previousX - x, previousY - y));
        for (var step = 0; step < 8; step++)
        {
            var index = clockwise ? (first - step - offset + 16) % 8 : (first + step + offset + 16) % 8;
            var (dx, dy) = Neighbors[index];
            var position = (y + dy) * width + x + dx;
            if (mask[position] != 0) return position;
        }
        return -1;
    }

    static List<PixelPoint[]> FindContours(int[] mask, int width, int height)
    {
        var contours = new List<PixelPoint[]>();
        var border = 1;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var position = y * width + x;
                var isOuter = mask[position] == 1 && mask[position - 1] == 0;
                var isHole = !isOuter && mask[position] >= 1 && mask[position + 1] == 0;
                if (!isOuter && !isHole) continue;
                border++;
                var first = FindNeighbor(mask, width, x, y, x + (isHole ? 1 : -1), y, true, 0);
                if (first < 0) { mask[position] = -border; continue; }
                var points = new List<PixelPoint> { new(x, y) };
                var previous = first;
                var current = position;
                while (true)
                {
                    var cx = current % width;
                    var cy = current / width;
                    var next = FindNeighbor(mask, width, cx, cy, previous % width, previous / width, false, 1);
                    if (next < 0) throw new InvalidDataException("检测轮廓邻域不连续。");
                    points.Add(new PixelPoint(next % width, next / width));
                    if (mask[current + 1] == 0) mask[current] = -border;
                    else if (mask[current] == 1) mask[current] = border;
                    if (next == position && current == first) break;
                    previous = current;
                    current = next;
                }
                if (!isHole) contours.Add(Approximate(points.ToArray(), 1));
            }
        }
        return contours;
    }

    static PixelPoint[] Approximate(ReadOnlySpan<PixelPoint> points, float epsilon)
    {
        if (points.Length <= 2) return points.ToArray();
        var a = points[0];
        var b = points[^1];
        var lengthSquared = (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        var farthest = 0f;
        var split = -1;
        for (var i = 1; i < points.Length - 1; i++)
        {
            var p = points[i];
            var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / lengthSquared, 0, 1);
            var distance = Distance(p, new PixelPoint(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
            if (distance <= farthest) continue;
            farthest = distance;
            split = i;
        }
        if (farthest <= epsilon) return [a, b];
        var first = Approximate(points[..(split + 1)], epsilon);
        var second = Approximate(points[split..], epsilon);
        return [.. first[..^1], .. second];
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _classifier.Dispose();
        _detector.Dispose();
    }
}
