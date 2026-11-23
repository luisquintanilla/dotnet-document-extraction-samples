// Ported from LiteParse crates/liteparse/src/ocr/ocria/img.rs, Apache-2.0.
namespace OcriaNano.DocumentExtraction.Imaging;

/// <summary>
/// <c>cv2.resize</c> as OpenCV's portable scalar code computes it (pixel-centre mapping, fixed-point weights).
/// Deliberately scalar: the results feed hard thresholds downstream.
/// </summary>
internal static class Resize
{
    private const int ResizeBits = 11;
    private const int ResizeOne = 1 << ResizeBits;

    // Rust's f64::EPSILON (C#'s double.Epsilon is the smallest denormal).
    private const double Epsilon = 2.220446049250313e-16;

    /// <summary>cvRound: round half to even.</summary>
    internal static long CvRound(double v) => (long)Math.Round(v, MidpointRounding.ToEven);

    internal static int SatShort(float v) => (int)Math.Clamp(CvRound(v), short.MinValue, short.MaxValue);

    private static (int Sx, int A0, int A1)[] LinearTaps(int src, int dst, bool area)
    {
        double invScale = dst / (double)src;
        double scale = 1.0 / invScale;
        var taps = new (int, int, int)[dst];
        for (int d = 0; d < dst; d++)
        {
            long sx;
            float f;
            if (area)
            {
                sx = (long)Math.Floor(d * scale);
                f = (float)(((double)(d + 1)) - ((sx + 1) * invScale));
                f = f <= 0f ? 0f : f - MathF.Floor(f);
            }
            else
            {
                float fx = (float)(((d + 0.5) * scale) - 0.5);
                float floor = MathF.Floor(fx);
                sx = (long)floor;
                f = fx - floor;
            }

            if (sx < 0)
            {
                sx = 0;
                f = 0f;
            }

            if (sx >= src - 1)
            {
                sx = src - 1;
                f = 0f;
            }

            taps[d] = ((int)sx, SatShort((1f - f) * ResizeOne), SatShort(f * ResizeOne));
        }

        return taps;
    }

    private static Img ResizeLinearTaps(Img img, int w, int h, bool area)
    {
        var xt = LinearTaps(img.Width, w, area);
        var yt = LinearTaps(img.Height, h, area);
        int ch = img.Channels;

        // horizontal pass, one row of ints per source row
        int[][] rows = new int[img.Height][];
        for (int y = 0; y < img.Height; y++)
        {
            int[] r = new int[w * ch];
            for (int dx = 0; dx < w; dx++)
            {
                (int sx, int a0, int a1) = xt[dx];
                for (int c = 0; c < ch; c++)
                {
                    int s0 = img.At(sx, y, c);
                    r[(dx * ch) + c] = sx + 1 < img.Width ? (s0 * a0) + (img.At(sx + 1, y, c) * a1) : s0 * ResizeOne;
                }
            }

            rows[y] = r;
        }

        Img output = new(w, h, ch);
        for (int dy = 0; dy < h; dy++)
        {
            (int sy, int b0, int b1) = yt[dy];
            int[] r0 = rows[sy];
            int[] r1 = rows[Math.Min(sy + 1, img.Height - 1)];
            int o = dy * w * ch;
            for (int i = 0; i < w * ch; i++)
            {
                // FixedPtCast<int, uchar, 22>
                long v = (((long)r0[i] * b0) + ((long)r1[i] * b1) + (1L << 21)) >> 22;
                output.Data[o + i] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return output;
    }

    /// <summary><c>cv2.resize(.., interpolation=INTER_LINEAR)</c>.</summary>
    public static Img Linear(Img img, int w, int h)
        => w == img.Width && h == img.Height ? img.Clone() : ResizeLinearTaps(img, w, h, false);

    /// <summary>OpenCV <c>computeResizeAreaTab</c>: (source index, weight) runs per output index.</summary>
    private static List<(int Sx, float Weight)>[] AreaTab(int src, int dst, double scale)
    {
        var tab = new List<(int, float)>[dst];
        for (int d = 0; d < dst; d++)
        {
            double fsx1 = d * scale;
            double fsx2 = fsx1 + scale;
            double cell = Math.Min(scale, src - fsx1);
            long sx2 = (long)Math.Floor(fsx2);
            sx2 = Math.Min(sx2, src - 1);
            long sx1 = Math.Min((long)Math.Ceiling(fsx1), sx2);
            List<(int, float)> t = [];
            if (sx1 - fsx1 > 1e-3)
            {
                t.Add(((int)(sx1 - 1), (float)((sx1 - fsx1) / cell)));
            }

            for (long sx = sx1; sx < sx2; sx++)
            {
                t.Add(((int)sx, (float)(1.0 / cell)));
            }

            if (fsx2 - sx2 > 1e-3)
            {
                t.Add(((int)sx2, (float)(Math.Min(Math.Min(fsx2 - sx2, 1.0), cell) / cell)));
            }

            tab[d] = t;
        }

        return tab;
    }

    /// <summary>
    /// <c>cv2.resize(.., interpolation=INTER_AREA)</c>: box averaging when shrinking both axes, OpenCV's area-weighted
    /// linear taps otherwise.
    /// </summary>
    public static Img Area(Img img, int w, int h)
    {
        if (w == img.Width && h == img.Height)
        {
            return img.Clone();
        }

        double scaleX = img.Width / (double)w, scaleY = img.Height / (double)h;
        if (scaleX < 1.0 || scaleY < 1.0)
        {
            return ResizeLinearTaps(img, w, h, true);
        }

        int ch = img.Channels;
        double isx = Math.Round(scaleX, MidpointRounding.AwayFromZero), isy = Math.Round(scaleY, MidpointRounding.AwayFromZero);
        if (Math.Abs(scaleX - isx) < Epsilon && Math.Abs(scaleY - isy) < Epsilon)
        {
            // integer factors: plain block mean (resizeAreaFast)
            int kx = (int)isx, ky = (int)isy;
            uint area = (uint)(kx * ky);
            Img blocks = new(w, h, ch);
            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        uint s = 0;
                        for (int yy = 0; yy < ky; yy++)
                        {
                            for (int xx = 0; xx < kx; xx++)
                            {
                                s += img.At((dx * kx) + xx, (dy * ky) + yy, c);
                            }
                        }

                        uint v = area == 4 ? (s + 2) >> 2 : (uint)CvRound(s * (double)(1.0f / area));
                        blocks.Data[(((dy * w) + dx) * ch) + c] = (byte)Math.Min(v, 255u);
                    }
                }
            }

            return blocks;
        }

        // flat horizontal taps: (output offset, source offset, weight), in OpenCV's order
        List<(int D, int S, float Alpha)> xt = [];
        var xTab = AreaTab(img.Width, w, scaleX);
        for (int dx = 0; dx < xTab.Length; dx++)
        {
            foreach ((int sx, float a) in xTab[dx])
            {
                xt.Add((dx * ch, sx * ch, a));
            }
        }

        var yt = AreaTab(img.Height, h, scaleY);
        Img output = new(w, h, ch);
        int rowLen = img.Width * ch;

        // horizontal sums of one source row; adjacent output rows share boundary rows, so the last row's sums are kept
        // (the same values OpenCV computes, in the same order)
        float[] buf = new float[w * ch];
        int bufRow = -1;
        float[] sum = new float[w * ch];
        for (int dy = 0; dy < h; dy++)
        {
            Array.Clear(sum);
            foreach ((int sy, float beta) in yt[dy])
            {
                if (bufRow != sy)
                {
                    Array.Clear(buf);
                    int rowStart = sy * rowLen;
                    foreach ((int d, int s, float alpha) in xt)
                    {
                        for (int c = 0; c < ch; c++)
                        {
                            buf[d + c] += img.Data[rowStart + s + c] * alpha;
                        }
                    }

                    bufRow = sy;
                }

                for (int i = 0; i < sum.Length; i++)
                {
                    sum[i] += buf[i] * beta;
                }
            }

            int o = dy * w * ch;
            for (int i = 0; i < sum.Length; i++)
            {
                output.Data[o + i] = (byte)Math.Clamp(CvRound(sum[i]), 0, 255);
            }
        }

        return output;
    }
}
