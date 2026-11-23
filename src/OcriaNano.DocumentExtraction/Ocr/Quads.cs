// Ported from LiteParse crates/liteparse/src/ocr/ocria/det.rs, Apache-2.0.
namespace OcriaNano.DocumentExtraction.Ocr;

/// <summary>Line-quad geometry shared by the detector, merger and page steps. A quad is [top-left, top-right, bottom-right, bottom-left] along the text.</summary>
internal static class Quads
{
    public static float Norm(float x, float y) => MathF.Sqrt((x * x) + (y * y));

    public static float MinY(PointF[] q) => MathF.Min(MathF.Min(q[0].Y, q[1].Y), MathF.Min(q[2].Y, q[3].Y));

    public static float MinX(PointF[] q) => MathF.Min(MathF.Min(q[0].X, q[1].X), MathF.Min(q[2].X, q[3].X));

    /// <summary>Corners as [top-left, top-right, bottom-right, bottom-left] along the line's long axis.</summary>
    public static PointF[] Order(PointF[] q)
    {
        float cx = (q[0].X + q[1].X + q[2].X + q[3].X) / 4f;
        float cy = (q[0].Y + q[1].Y + q[2].Y + q[3].Y) / 4f;
        float e1x = q[1].X - q[0].X, e1y = q[1].Y - q[0].Y;
        float e2x = q[2].X - q[1].X, e2y = q[2].Y - q[1].Y;
        float ux, uy;
        if (Norm(e1x, e1y) >= Norm(e2x, e2y))
        {
            ux = e1x;
            uy = e1y;
        }
        else
        {
            ux = e2x;
            uy = e2y;
        }

        float n = Norm(ux, uy) + 1e-6f;
        ux /= n;
        uy /= n;
        if (ux < 0f)
        {
            ux = -ux;
            uy = -uy;
        }

        float vx = -uy, vy = ux;
        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            a[i] = ((q[i].X - cx) * ux) + ((q[i].Y - cy) * uy);
            b[i] = ((q[i].X - cx) * vx) + ((q[i].Y - cy) * vy);
        }

        // np.argmin / argmax: the first extreme index
        int tl = 0, tr = 0, br = 0, bl = 0;
        for (int i = 1; i < 4; i++)
        {
            if (a[i] + b[i] < a[tl] + b[tl])
            {
                tl = i;
            }

            if (a[i] - b[i] > a[tr] - b[tr])
            {
                tr = i;
            }

            if (a[i] + b[i] > a[br] + b[br])
            {
                br = i;
            }

            if (a[i] - b[i] < a[bl] - b[bl])
            {
                bl = i;
            }
        }

        return [q[tl], q[tr], q[br], q[bl]];
    }
}
