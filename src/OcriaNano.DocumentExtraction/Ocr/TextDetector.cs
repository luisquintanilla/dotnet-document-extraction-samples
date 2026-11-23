// Ported from LiteParse crates/liteparse/src/ocr/ocria/det.rs, Apache-2.0.
using System.Numerics.Tensors;
using System.Text.Json;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>
/// A second detection pass for small print: when the median line height at the first pass's scale is under
/// <see cref="MinHeight"/> input pixels (and the page has at least 8 lines), detect again with the long side scaled to
/// bring lines to <see cref="TargetHeight"/>, at most <see cref="MaxSide"/>.
/// </summary>
internal readonly record struct AdaptiveDetection(float MinHeight, float TargetHeight, int MaxSide);

/// <summary>
/// Text-line detector: a segmentation map from the ONNX model, then connected components expanded to line quads
/// (<c>lpocr_rec.Detector</c>).
/// </summary>
internal sealed class TextDetector
{
    // Kernel shrink ratio the detector targets were built with.
    private const float Shrink = 0.3f;

    // Detector thresholds for a model without det.meta.json.
    internal const float DefaultThreshold = 0.5f;
    internal const float DefaultMerge = 0.6f;
    internal const int DefaultLongSide = 1600;

    private readonly SessionPool _sessions;

    public TextDetector(SessionPool sessions, int longSide, float threshold, float merge)
    {
        _sessions = sessions;
        LongSide = longSide;
        Threshold = threshold;
        Merge = merge;
    }

    public int LongSide { get; }

    public float Threshold { get; }

    public float MinScore { get; init; } = 0.6f;

    /// <summary>Join collinear boxes with gaps under this many line heights (0 = off).</summary>
    public float Merge { get; }

    public AdaptiveDetection? Adaptive { get; init; }

    /// <summary>Reads the thresholds the detector shipped with (<c>det.meta.json</c>: <c>thresh</c>, <c>merge</c>).</summary>
    public static TextDetector Create(SessionPool sessions)
    {
        float? thresh = null, merge = null;
        if (sessions.Exists(OcriaNanoModels.DetectorMeta))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sessions.Directory, OcriaNanoModels.DetectorMeta)));
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    thresh = ReadFloat(json.RootElement, "thresh");
                    merge = ReadFloat(json.RootElement, "merge");
                }
            }
            catch (JsonException)
            {
            }
        }

        return new TextDetector(
            sessions,
            DefaultLongSide,
            thresh ?? DefaultThreshold,
            merge ?? DefaultMerge);

        static float? ReadFloat(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : null;
    }

    /// <summary>Line quads (TL, TR, BR, BL along the line) in image pixels, in reading-ish order.</summary>
    public List<PointF[]> Detect(Img img, CancellationToken cancellationToken = default)
    {
        (List<PointF[]> boxes, double scale) = DetectAt(img, LongSide, cancellationToken);
        if (Adaptive is not { } a || boxes.Count < 8)
        {
            return boxes;
        }

        float[] hs = new float[boxes.Count];
        for (int i = 0; i < hs.Length; i++)
        {
            PointF[] q = boxes[i];
            hs[i] = Quads.Norm(q[3].X - q[0].X, q[3].Y - q[0].Y) * (float)scale;
        }

        Array.Sort(hs);
        float med = MathF.Max(hs[hs.Length / 2], 1f);
        if (med >= a.MinHeight)
        {
            return boxes;
        }

        int side = (int)Math.Min((uint)(LongSide * a.TargetHeight / med), (uint)a.MaxSide) / 32 * 32;
        return side <= LongSide ? boxes : DetectAt(img, side, cancellationToken).Boxes;
    }

    /// <summary>One detection pass with the page's long side resized to <paramref name="longSide"/> (never enlarged); also returns the resize scale.</summary>
    private (List<PointF[]> Boxes, double Scale) DetectAt(Img img, int longSide, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double bigH = img.Height, bigW = img.Width;
        double s = Math.Min(longSide / Math.Max(bigH, bigW), 1.0);
        int h = Math.Max(32, (int)Math.Round(bigH * s / 32.0, MidpointRounding.ToEven) * 32);
        int w = Math.Max(32, (int)Math.Round(bigW * s / 32.0, MidpointRounding.ToEven) * 32);
        double sx = w / bigW, sy = h / bigH;
        Img small = Resize.Area(img, w, h);

        // v / 127.5 - 1 per channel plane: elementwise IEEE ops, bit-exact with the scalar reference
        using OrtInput<float> x = new(1, 3, h, w);
        Span<float> xs = x.Span;
        byte[] plane = new byte[h * w];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = small.Data[(i * 3) + c];
            }

            Span<float> dst = xs.Slice(c * h * w, h * w);
            TensorPrimitives.ConvertTruncating<byte, float>(plane, dst);
            TensorPrimitives.Divide<float>(dst, 127.5f, dst);
            TensorPrimitives.Subtract<float>(dst, 1f, dst);
        }

        using var outputs = _sessions.Get(OcriaNanoModels.Detector).Run([("x", x.Value)], "prob");
        var probValue = outputs.First();
        long[] shape = probValue.GetShape();
        int ph = (int)shape[2], pw = (int)shape[3];
        ReadOnlySpan<float> prob = probValue.GetTensorDataAsSpan<float>();
        float stride = h / ph;
        bool[] mask = new bool[prob.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = prob[i] > Threshold;
        }

        List<PointF[]> boxes = [];
        foreach (Component comp in Geometry.Components4(mask, pw, ph))
        {
            if (comp.Pixels.Count < 2)
            {
                continue;
            }

            // hard threshold: a sequential double sum, in discovery order
            double sum = 0;
            foreach ((int px, int py) in comp.Pixels)
            {
                sum += prob[(py * pw) + px];
            }

            float mean = (float)(sum / comp.Pixels.Count);
            if (mean < MinScore)
            {
                continue;
            }

            // the hull of the component's pixel cells: each row's leftmost and rightmost cell
            int rows = comp.Y1 - comp.Y0 + 1;
            (int Lo, int Hi)[] span = new (int, int)[rows];
            Array.Fill(span, (int.MaxValue, 0));
            foreach ((int px, int py) in comp.Pixels)
            {
                ref (int Lo, int Hi) cell = ref span[py - comp.Y0];
                cell.Lo = Math.Min(cell.Lo, px);
                cell.Hi = Math.Max(cell.Hi, px);
            }

            List<(float X, float Y)> pts = new(rows * 4);
            for (int i = 0; i < rows; i++)
            {
                (int x0, int x1) = span[i];
                if (x0 == int.MaxValue)
                {
                    continue;
                }

                float y = comp.Y0 + i;
                pts.Add((x0 * stride, y * stride));
                pts.Add((x0 * stride, (y + 1f) * stride));
                pts.Add(((x1 + 1f) * stride, y * stride));
                pts.Add(((x1 + 1f) * stride, (y + 1f) * stride));
            }

            RotRect r0 = Geometry.MinAreaRect(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pts));

            // line height is the side nearer vertical, not the shorter side: a single digit's kernel is taller than it is wide
            float rem = (r0.Angle + 90f) % 180f;
            if (rem < 0f)
            {
                rem += 180f;
            }

            bool wIsHorizontal = MathF.Abs(rem - 90f) < 45f;
            float kernelH = wIsHorizontal ? r0.H : r0.W;
            float lineH = kernelH / (1f - (2f * Shrink));
            float d = Shrink * lineH;
            RotRect r = r0 with { W = r0.W + (2f * d), H = r0.H + (2f * d) };
            (float X, float Y)[] corners = Geometry.BoxPoints(r);
            PointF[] quad = new PointF[4];
            for (int i = 0; i < 4; i++)
            {
                quad[i] = new PointF(
                    Math.Clamp((float)(corners[i].X / sx), 0f, img.Width),
                    Math.Clamp((float)(corners[i].Y / sy), 0f, img.Height));
            }

            boxes.Add(Quads.Order(quad));
        }

        if (Merge > 0f)
        {
            boxes = LineMerger.Merge(boxes, Merge);
        }

        // (row band of 8 px, then left edge); a stable sort, as Python's
        List<PointF[]> sorted = [.. boxes
            .OrderBy(q => Math.Round(Quads.MinY(q) / 8.0, MidpointRounding.ToEven))
            .ThenBy(q => Quads.MinX(q))];
        return (sorted, s);
    }
}
