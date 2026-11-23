// Ported from LiteParse crates/liteparse/src/ocr/ocria/img.rs, Apache-2.0.
using OcriaNano.DocumentExtraction.Ocr;

namespace OcriaNano.DocumentExtraction.Imaging;

/// <summary><c>cv2.getPerspectiveTransform</c> and <c>cv2.warpPerspective</c> (bicubic, replicated border), OpenCV-exact.</summary>
internal static class Perspective
{
    private const int InterBits = 5;
    private const int InterTab = 1 << InterBits;
    internal const int RemapScale = 1 << 15;

    private static readonly Lazy<int[][]> s_tab = new(BuildBicubicTab);

    /// <summary>The 32 x 32 sub-pixel fixed-point bicubic weights (16 each), every row summing to exactly 2^15.</summary>
    internal static int[][] BicubicTab => s_tab.Value;

    /// <summary><c>cv2.getPerspectiveTransform(src, dst)</c>: the 3x3 homography taking <paramref name="src"/> corners to <paramref name="dst"/>.</summary>
    public static double[] Transform(ReadOnlySpan<PointF> src, ReadOnlySpan<PointF> dst)
    {
        double[][] a = new double[8][];
        for (int i = 0; i < 8; i++)
        {
            a[i] = new double[9];
        }

        for (int i = 0; i < 4; i++)
        {
            double x = src[i].X, y = src[i].Y;
            double u = dst[i].X, v = dst[i].Y;
            a[i] = [x, y, 1.0, 0.0, 0.0, 0.0, -x * u, -y * u, u];
            a[i + 4] = [0.0, 0.0, 0.0, x, y, 1.0, -x * v, -y * v, v];
        }

        // Gaussian elimination with partial pivoting (OpenCV solves with LU)
        for (int col = 0; col < 8; col++)
        {
            // Rust's max_by keeps the last of equal maxima
            int piv = col;
            for (int p = col + 1; p < 8; p++)
            {
                if (Math.Abs(a[p][col]) >= Math.Abs(a[piv][col]))
                {
                    piv = p;
                }
            }

            (a[col], a[piv]) = (a[piv], a[col]);
            double d = a[col][col];
            if (Math.Abs(d) < 1e-300)
            {
                continue;
            }

            for (int r = 0; r < 8; r++)
            {
                if (r != col)
                {
                    double f = a[r][col] / d;
                    if (f != 0.0)
                    {
                        for (int k = col; k < 9; k++)
                        {
                            a[r][k] -= f * a[col][k];
                        }
                    }
                }
            }
        }

        double[] m = new double[9];
        for (int i = 0; i < 8; i++)
        {
            m[i] = a[i][i] != 0.0 ? a[i][8] / a[i][i] : 0.0;
        }

        m[8] = 1.0;
        return m;
    }

    private static double[] Invert3(double[] m)
    {
        double det = (m[0] * ((m[4] * m[8]) - (m[5] * m[7]))) - (m[1] * ((m[3] * m[8]) - (m[5] * m[6])))
            + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
        if (det == 0.0)
        {
            return new double[9];
        }

        double d = 1.0 / det;
        return
        [
            ((m[4] * m[8]) - (m[5] * m[7])) * d,
            ((m[2] * m[7]) - (m[1] * m[8])) * d,
            ((m[1] * m[5]) - (m[2] * m[4])) * d,
            ((m[5] * m[6]) - (m[3] * m[8])) * d,
            ((m[0] * m[8]) - (m[2] * m[6])) * d,
            ((m[2] * m[3]) - (m[0] * m[5])) * d,
            ((m[3] * m[7]) - (m[4] * m[6])) * d,
            ((m[1] * m[6]) - (m[0] * m[7])) * d,
            ((m[0] * m[4]) - (m[1] * m[3])) * d,
        ];
    }

    /// <summary>OpenCV <c>interpolateCubic</c> (A = -0.75).</summary>
    private static float[] CubicCoeffs(float x)
    {
        const float A = -0.75f;
        float c0 = ((((A * (x + 1f)) - (5f * A)) * (x + 1f)) + (8f * A)) * (x + 1f) - (4f * A);
        float c1 = ((((A + 2f) * x) - (A + 3f)) * x * x) + 1f;
        float c2 = ((((A + 2f) * (1f - x)) - (A + 3f)) * (1f - x) * (1f - x)) + 1f;
        return [c0, c1, c2, 1f - c0 - c1 - c2];
    }

    private static int[][] BuildBicubicTab()
    {
        float[][] oneD = new float[InterTab][];
        for (int i = 0; i < InterTab; i++)
        {
            oneD[i] = CubicCoeffs(i / (float)InterTab);
        }

        List<int[]> tab = new(InterTab * InterTab);
        for (int iy = 0; iy < InterTab; iy++)
        {
            for (int ix = 0; ix < InterTab; ix++)
            {
                float[] vy = oneD[iy], vx = oneD[ix];
                int[] t = new int[16];
                int isum = 0;
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        float v = vy[i] * vx[j];
                        t[(i * 4) + j] = Resize.SatShort(v * RemapScale);
                        isum += t[(i * 4) + j];
                    }
                }

                if (isum != RemapScale)
                {
                    int diff = isum - RemapScale;
                    (int, int) mk = (2, 2), big = (2, 2);
                    for (int k1 = 2; k1 < 4; k1++)
                    {
                        for (int k2 = 2; k2 < 4; k2++)
                        {
                            if (t[(k1 * 4) + k2] < t[(mk.Item1 * 4) + mk.Item2])
                            {
                                mk = (k1, k2);
                            }
                            else if (t[(k1 * 4) + k2] > t[(big.Item1 * 4) + big.Item2])
                            {
                                big = (k1, k2);
                            }
                        }
                    }

                    if (diff < 0)
                    {
                        t[(big.Item1 * 4) + big.Item2] -= diff;
                    }
                    else
                    {
                        t[(mk.Item1 * 4) + mk.Item2] -= diff;
                    }
                }

                tab.Add(t);
            }
        }

        return [.. tab];
    }

    /// <summary><c>cv2.warpPerspective(img, m, (w, h), flags=INTER_CUBIC, borderMode=BORDER_REPLICATE)</c>.</summary>
    public static Img WarpCubic(Img img, double[] m, int w, int h)
    {
        double[] inv = Invert3(m);
        int[][] tab = BicubicTab;
        int ch = img.Channels;
        Img output = new(w, h, ch);
        long sw = img.Width, sh = img.Height;
        Span<long> sum = stackalloc long[4];
        Span<int> cols = stackalloc int[4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double xf = x, yf = y;
                double x0 = (inv[0] * xf) + (inv[1] * yf) + inv[2];
                double y0 = (inv[3] * xf) + (inv[4] * yf) + inv[5];
                double ww = (inv[6] * xf) + (inv[7] * yf) + inv[8];
                ww = ww != 0.0 ? InterTab / ww : 0.0;
                double fx = Math.Clamp(x0 * ww, int.MinValue, int.MaxValue);
                double fy = Math.Clamp(y0 * ww, int.MinValue, int.MaxValue);
                long xi = Resize.CvRound(fx), yi = Resize.CvRound(fy);
                long sx = xi >> InterBits, sy = yi >> InterBits;
                int a = (int)((((yi & (InterTab - 1)) * InterTab) + (xi & (InterTab - 1))));
                int[] wt = tab[a];

                // the 4 x 4 source window, clamped to the image (BORDER_REPLICATE)
                bool inside = sx >= 1 && sy >= 1 && sx + 2 < sw && sy + 2 < sh;
                for (int j = 0; j < 4; j++)
                {
                    cols[j] = inside ? (int)(sx - 1) + j : (int)Math.Clamp(sx - 1 + j, 0, sw - 1);
                }

                sum.Clear();
                for (int i = 0; i < 4; i++)
                {
                    int yy = inside ? (int)(sy - 1) + i : (int)Math.Clamp(sy - 1 + i, 0, sh - 1);
                    int rowStart = yy * img.Width * ch;
                    for (int j = 0; j < 4; j++)
                    {
                        long wv = wt[(i * 4) + j];
                        int px = rowStart + (cols[j] * ch);
                        for (int c = 0; c < ch; c++)
                        {
                            sum[c] += img.Data[px + c] * wv;
                        }
                    }
                }

                int o = ((y * w) + x) * ch;
                for (int c = 0; c < ch; c++)
                {
                    output.Data[o + c] = (byte)Math.Clamp((sum[c] + (1 << 14)) >> 15, 0, 255);
                }
            }
        }

        return output;
    }
}
