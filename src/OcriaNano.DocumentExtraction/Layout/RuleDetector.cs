// Ported from LiteParse crates/liteparse/src/table_model/features.rs (detect_rules, InkBits, rules_from_runs, grey), Apache-2.0.
using System.Numerics;
using OcriaNano.DocumentExtraction.Imaging;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>Ruling-line detection on the 150 dpi page render.</summary>
internal static class RuleDetector
{
    /// <summary>Grey level below which a pixel is ink.</summary>
    public const byte Dark = 170;

    /// <summary>Pixels per point of the rules render.</summary>
    public const float Scale = Units.RulesDpi / 72f;

    /// <summary>
    /// The render in grey: OpenCV's integer RGB2GRAY weights applied to PDFium's BGR(x) channel order, so the
    /// blue channel gets the red weight, as the training renders were made.
    /// </summary>
    public static GrayImage Grey(Bgra32Image raster)
    {
        GrayImage g = new(raster.Width, raster.Height);
        byte[] src = raster.Data;
        byte[] dst = g.Data;
        for (int i = 0, j = 0; i < dst.Length; i++, j += 4)
        {
            dst[i] = (byte)(((4899 * src[j]) + (9617 * src[j + 1]) + (1868 * src[j + 2]) + (1 << 13)) >> 14);
        }

        return g;
    }

    /// <summary>Ruling lines of a page from its 150 dpi render.</summary>
    public static Rules Detect(Bgra32Image raster)
    {
        if (raster.Width == 0 || raster.Height == 0)
        {
            return new Rules();
        }

        return Detect(Grey(raster), Scale);
    }

    /// <summary>
    /// Ruling lines from a grey page render at <paramref name="scale"/> pixels per point: dark pixels opened with a long
    /// thin kernel, so only runs longer than any glyph survive; each 8-connected surviving component is one rule,
    /// unless it is thicker than 2.5 pt.
    /// </summary>
    public static Rules Detect(GrayImage gray, float scale)
    {
        InkBits ink = new(gray);
        (List<(int, int)>[] hruns, List<(int, int)>[] vruns) = ink.Runs();
        return RulesFromRuns(hruns, vruns, scale);
    }

    private sealed class InkBits
    {
        private readonly int _w;
        private readonly int _h;
        private readonly int _words;
        private readonly ulong[] _bits;

        public InkBits(GrayImage g)
        {
            _w = g.Width;
            _h = g.Height;
            _words = (_w + 63) / 64;
            _bits = new ulong[_words * _h];
            for (int y = 0; y < _h; y++)
            {
                ReadOnlySpan<byte> row = g.Data.AsSpan(y * _w, _w);
                for (int k = 0; k * 64 < _w; k++)
                {
                    ReadOnlySpan<byte> px = row.Slice(k * 64, Math.Min(64, _w - (k * 64)));
                    ulong word = 0;
                    for (int b = 0; b < px.Length; b++)
                    {
                        word |= (px[b] < Dark ? 1UL : 0UL) << b;
                    }

                    _bits[(y * _words) + k] = word;
                }
            }
        }

        private ReadOnlySpan<ulong> Row(int y) => _bits.AsSpan(y * _words, _words);

        /// <summary>Horizontal ink runs per row and vertical runs per column, (start, end inclusive), at least 3% of the page side (15 px floor).</summary>
        public (List<(int, int)>[] H, List<(int, int)>[] V) Runs()
        {
            int w = _w, h = _h, words = _words;
            int lh = Math.Max(15, (int)(w * 0.03f));
            int lv = Math.Max(15, (int)(h * 0.03f));
            List<(int, int)>[] hruns = new List<(int, int)>[h];
            List<(int, int)>[] vruns = new List<(int, int)>[w];
            for (int i = 0; i < h; i++)
            {
                hruns[i] = [];
            }

            for (int i = 0; i < w; i++)
            {
                vruns[i] = [];
            }

            int[] open = new int[w];
            ulong[] zero = new ulong[words];
            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<ulong> cur = Row(y);
                ReadOnlySpan<ulong> prev = y == 0 ? zero : Row(y - 1);
                for (int k = 0; k < words; k++)
                {
                    ulong changed = cur[k] ^ prev[k];
                    while (changed != 0)
                    {
                        int x = (k * 64) + BitOperations.TrailingZeroCount(changed);
                        changed &= changed - 1;
                        if (((cur[k] >> (x % 64)) & 1) == 1)
                        {
                            open[x] = y;
                        }
                        else if (y - open[x] >= lv)
                        {
                            vruns[x].Add((open[x], y - 1));
                        }
                    }
                }

                int px = NextBit(cur, 0, w, true);
                while (px < w)
                {
                    int end = NextBit(cur, px, w, false);
                    if (end - px >= lh)
                    {
                        hruns[y].Add((px, end - 1));
                    }

                    px = NextBit(cur, end, w, true);
                }
            }

            if (h > 0)
            {
                ReadOnlySpan<ulong> last = Row(h - 1);
                for (int x = 0; x < w; x++)
                {
                    if (((last[x / 64] >> (x % 64)) & 1) == 1 && h - open[x] >= lv)
                    {
                        vruns[x].Add((open[x], h - 1));
                    }
                }
            }

            return (hruns, vruns);
        }

        /// <summary>The first position at or after <paramref name="from"/> (below <paramref name="w"/>) whose bit equals <paramref name="set"/>; <paramref name="w"/> when there is none.</summary>
        private static int NextBit(ReadOnlySpan<ulong> row, int from, int w, bool set)
        {
            int k = from / 64;
            if (k >= row.Length)
            {
                return w;
            }

            ulong flip = set ? 0UL : ulong.MaxValue;
            ulong word = (row[k] ^ flip) & (ulong.MaxValue << (from % 64));
            while (true)
            {
                if (word != 0)
                {
                    return Math.Min((k * 64) + BitOperations.TrailingZeroCount(word), w);
                }

                k++;
                if (k >= row.Length)
                {
                    return w;
                }

                word = row[k] ^ flip;
            }
        }
    }

    private static int Root(int[] p, int k)
    {
        while (p[k] != k)
        {
            p[k] = p[p[k]];
            k = p[k];
        }

        return k;
    }

    private static float Round1(float v) => MathF.Round(v * 10f, MidpointRounding.AwayFromZero) / 10f;

    /// <summary>Each 8-connected component of runs on adjacent lines is one rule, unless it is thicker than 2.5 pt.</summary>
    private static Rules RulesFromRuns(List<(int, int)>[] hruns, List<(int, int)>[] vruns, float scale)
    {
        const float thickMaxPt = 2.5f;
        Rules output = new();
        for (int axis = 0; axis < 2; axis++)
        {
            List<(int, int)>[] runs = axis == 0 ? hruns : vruns;
            int linesN = runs.Length;
            List<(int Line, int S, int E)> ids = [];
            int[] first = new int[linesN + 1];
            for (int line = 0; line < linesN; line++)
            {
                first[line] = ids.Count;
                foreach ((int s, int e) in runs[line])
                {
                    ids.Add((line, s, e));
                }
            }

            first[linesN] = ids.Count;
            int[] parent = Enumerable.Range(0, ids.Count).ToArray();
            for (int line = 1; line < linesN; line++)
            {
                for (int a = first[line - 1]; a < first[line]; a++)
                {
                    for (int b = first[line]; b < first[line + 1]; b++)
                    {
                        (_, int s0, int e0) = ids[a];
                        (_, int s1, int e1) = ids[b];
                        if (s1 <= e0 + 1 && e1 + 1 >= s0)
                        {
                            int ra = Root(parent, a), rb = Root(parent, b);
                            if (ra != rb)
                            {
                                parent[ra] = rb;
                            }
                        }
                    }
                }
            }

            (int L0, int L1, int S, int E)?[] comp = new (int, int, int, int)?[ids.Count];
            for (int k = 0; k < ids.Count; k++)
            {
                int r = Root(parent, k);
                (int line, int s, int e) = ids[k];
                var c = comp[r] ?? (int.MaxValue, 0, int.MaxValue, 0);
                comp[r] = (Math.Min(c.L0, line), Math.Max(c.L1, line), Math.Min(c.S, s), Math.Max(c.E, e));
            }

            foreach ((int l0, int l1, int s, int e)? cc in comp)
            {
                if (cc is null)
                {
                    continue;
                }

                (int l0, int l1, int s, int e) = cc.Value;
                float thick = l1 - l0 + 1;
                if (thick > thickMaxPt * scale)
                {
                    continue;
                }

                float along = e - s + 1;
                float mid = (l0 + (thick / 2f)) / scale;
                float a0 = s / scale, a1 = (s + along) / scale;
                RuleSegment seg = new(Round1(mid), Round1(a0), Round1(a1));
                (axis == 0 ? output.H : output.V).Add(seg);
            }
        }

        return output;
    }
}
