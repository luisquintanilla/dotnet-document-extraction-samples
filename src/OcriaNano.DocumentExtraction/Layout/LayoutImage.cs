// Ported from LiteParse crates/liteparse/src/table_model/features.rs (PageImage, resample_area, process_raster), Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A page rendered for the image branch: grey (0 = black), longest side <see cref="Side"/>, with its pixels per PDF point.</summary>
internal sealed class LayoutImage
{
    /// <summary>The image branch's square canvas side.</summary>
    public const int Side = 1024;

    public LayoutImage(int width, int height, byte[] px, float k)
    {
        Width = width;
        Height = height;
        Px = px;
        K = k;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Px { get; }

    /// <summary>Pixels per PDF point.</summary>
    public float K { get; }

    private static (int Tw, int Th, float S) Target(int w, int h)
    {
        float s = Side / (float)Math.Max(w, h);
        return (Math.Max((int)MathF.Round(w * s, MidpointRounding.AwayFromZero), 8), Math.Max((int)MathF.Round(h * s, MidpointRounding.AwayFromZero), 8), s);
    }

    /// <summary>The model's page image from the grey 150 dpi render (area-resampled to the longest side 1024).</summary>
    public static LayoutImage FromGray(GrayImage gray)
    {
        (int tw, int th, float s) = Target(gray.Width, gray.Height);
        return new LayoutImage(tw, th, ResampleArea(gray.Data, gray.Width, gray.Height, tw, th), RuleDetector.Scale * s);
    }

    /// <summary>A blank page of the given size in points, for running without a render.</summary>
    public static LayoutImage Blank(double widthPt, double heightPt)
    {
        int w = Math.Max((int)Math.Round(widthPt * RuleDetector.Scale), 1);
        int h = Math.Max((int)Math.Round(heightPt * RuleDetector.Scale), 1);
        (int tw, int th, float s) = Target(w, h);
        byte[] px = new byte[tw * th];
        Array.Fill(px, (byte)255);
        return new LayoutImage(tw, th, px, RuleDetector.Scale * s);
    }

    /// <summary>The page turned into reading direction <paramref name="q"/>'s frame (the pixel form of <c>to_frame</c>).</summary>
    public LayoutImage Rotate(int q)
    {
        int w = Width, h = Height;
        (int nw, int nh) = q % 2 == 1 ? (h, w) : (w, h);
        byte[] px = new byte[nw * nh];
        Array.Fill(px, (byte)255);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (int nx, int ny) = q switch
                {
                    1 => (h - 1 - y, x),
                    2 => (w - 1 - x, h - 1 - y),
                    _ => (y, w - 1 - x),
                };
                px[(ny * nw) + nx] = Px[(y * w) + x];
            }
        }

        return new LayoutImage(nw, nh, px, K);
    }

    /// <summary>Writes ink (<c>(255 - px) / 255</c>) on the zeroed square canvas, top-left aligned.</summary>
    public void FillCanvas(Span<float> canvas)
    {
        for (int y = 0; y < Math.Min(Height, Side); y++)
        {
            for (int x = 0; x < Math.Min(Width, Side); x++)
            {
                canvas[(y * Side) + x] = (255f - Px[(y * Width) + x]) / 255f;
            }
        }
    }

    /// <summary>Unit boxes (<c>PBox</c>, points) as clamped fractions of the canvas into <paramref name="boxes"/> ([n, 4]).</summary>
    public void FillBoxes(IReadOnlyList<Unit> units, Span<float> boxes)
    {
        float f = K / Side;
        for (int i = 0; i < units.Count; i++)
        {
            Box4 b = units[i].PBox;
            for (int j = 0; j < 4; j++)
            {
                boxes[(i * 4) + j] = Math.Clamp((float)b[j] * f, 0f, 1f);
            }
        }
    }

    /// <summary>Area-average resample (OpenCV INTER_AREA when shrinking): a separable box filter with fractional coverage.</summary>
    internal static byte[] ResampleArea(byte[] g, int w, int h, int tw, int th)
    {
        static (int[] At, List<(int I, float C)> Taps) Weights(int nIn, int nOut)
        {
            float f = nIn / (float)nOut;
            int[] at = new int[nOut + 1];
            List<(int, float)> taps = [];
            for (int o = 0; o < nOut; o++)
            {
                float a = o * f, b = (o + 1f) * f;
                int i = (int)MathF.Floor(a);
                while (i < b && i < nIn)
                {
                    float cov = MathF.Max(MathF.Min(b, i + 1f) - MathF.Max(a, i), 0f);
                    if (cov > 0f)
                    {
                        taps.Add((i, cov / f));
                    }

                    i++;
                }

                at[o + 1] = taps.Count;
            }

            return (at, taps);
        }

        (int[] ax, List<(int I, float C)> wx) = Weights(w, tw);
        (int[] ay, List<(int I, float C)> wy) = Weights(h, th);
        float[] tmp = new float[h * tw];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                float s = 0f;
                for (int t = ax[x]; t < ax[x + 1]; t++)
                {
                    s += g[(y * w) + wx[t].I] * wx[t].C;
                }

                tmp[(y * tw) + x] = s;
            }
        }

        byte[] output = new byte[th * tw];
        float[] acc = new float[tw];
        for (int y = 0; y < th; y++)
        {
            Array.Clear(acc);
            for (int t = ay[y]; t < ay[y + 1]; t++)
            {
                (int i, float c) = wy[t];
                for (int x = 0; x < tw; x++)
                {
                    acc[x] += tmp[(i * tw) + x] * c;
                }
            }

            for (int x = 0; x < tw; x++)
            {
                output[(y * tw) + x] = (byte)Math.Clamp(MathF.Round(acc[x], MidpointRounding.AwayFromZero), 0f, 255f);
            }
        }

        return output;
    }
}
