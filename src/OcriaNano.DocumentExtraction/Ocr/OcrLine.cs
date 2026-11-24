namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>A point in image pixels (OCR) or PDF points (layout), top-left origin.</summary>
internal readonly record struct PointF(float X, float Y);

/// <summary>An axis-aligned box: left, top, right, bottom.</summary>
internal readonly record struct BoxF(float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;

    public float Height => Y2 - Y1;

    public float CenterX => (X1 + X2) / 2;

    public float CenterY => (Y1 + Y2) / 2;

    public static BoxF Bounds(ReadOnlySpan<PointF> points)
    {
        float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
        foreach (PointF p in points)
        {
            x1 = MathF.Min(x1, p.X);
            y1 = MathF.Min(y1, p.Y);
            x2 = MathF.Max(x2, p.X);
            y2 = MathF.Max(y2, p.Y);
        }

        return new BoxF(x1, y1, x2, y2);
    }

    public BoxF Scale(float s) => new(X1 * s, Y1 * s, X2 * s, Y2 * s);

    public BoxF Union(BoxF o) => new(MathF.Min(X1, o.X1), MathF.Min(Y1, o.Y1), MathF.Max(X2, o.X2), MathF.Max(Y2, o.Y2));

    public float IntersectionArea(BoxF o)
    {
        float w = MathF.Min(X2, o.X2) - MathF.Max(X1, o.X1);
        float h = MathF.Min(Y2, o.Y2) - MathF.Max(Y1, o.Y1);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public float Area => MathF.Max(0, Width) * MathF.Max(0, Height);
}

/// <summary>A word inside an <see cref="OcrLine"/>.</summary>
internal sealed record OcrWord(string Text, BoxF Box);

/// <summary>
/// One recognized line, matching LiteParse's <c>OcrResult</c>: the text, its box in page pixels, its quad
/// (top-left, top-right, bottom-right, bottom-left in the upright reading frame), a 0-1 confidence and its rotation
/// in degrees counter-clockwise.
/// </summary>
internal sealed record OcrLine(string Text, BoxF Box, PointF[] Quad, float Confidence, int Rotation, string Script)
{
    public IReadOnlyList<OcrWord> Words { get; init; } = [];
}

/// <summary>The OCR of one page image.</summary>
internal sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines);
