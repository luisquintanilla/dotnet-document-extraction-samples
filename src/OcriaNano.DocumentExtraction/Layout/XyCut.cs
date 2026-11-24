// Ported from LiteParse crates/liteparse/src/projection.rs (xy_cut, xy_cut_rec, xy_find_best_cut,
// xy_find_column_cut, xy_find_crossing_line_cut, xy_find_banner_cut, isolated_numeric_footer, xy_walk_leaves), Apache-2.0.
// Display-math and margin-line-number signals are not available here and are treated as absent.
namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A text item for XY-cut: its page box (top-left origin, points), text, and whether it is an OCR line.</summary>
internal readonly record struct XyItem(Box4 Box, string Text, bool Ocr)
{
    public double X => Box.X0;

    public double Y => Box.Y0;

    public double W => Math.Max(0, Box.Width);

    public double H => Math.Max(0, Box.Height);
}

/// <summary>
/// LiteParse's recursive XY-cut reading order: split the page at whitespace valleys, column gutters, banners and
/// spanning lines into regions, and read the leaf regions depth-first (top band before bottom, left column before right).
/// </summary>
internal static class XyCut
{
    private const double BucketPt = 2.0;
    private const int MaxBuckets = 1 << 16;
    private const double TDens = 0.10;
    private const double MinVValleyPt = 8.0;
    private const double MinHValleyFactor = 1.4;
    private const int MaxDepth = 12;
    private const int MinItemsToCut = 4;
    private const int MinLinesPerSide = 2;
    private const double VPreferenceMargin = 1.1;
    private const double ColumnBucketPt = 5.0;
    private const int ColumnMinLinesPerPeak = 10;
    private const double ColumnMinGapFraction = 0.25;
    private const double ColumnPeakDominance = 0.55;
    private const double ColumnPeakBalanceRatio = 0.4;
    private const double ColumnMinFill = 0.55;
    private const double MinFillHardFloor = 0.38;
    private const double ColumnPhantomBandFraction = 0.25;
    private const double BannerWidthFraction = 0.6;
    private const double BannerClearanceFactor = 1.5;
    private const double StraddlePartitionFraction = 0.25;
    private const double HistogramScore = 1.0e9;

    private enum Axis
    {
        Horizontal,
        Vertical,
    }

    private readonly record struct Cut(Axis Axis, double Position, double Score)
    {
        public bool IsHistogramV => Axis == Axis.Vertical && Math.Abs(Score - HistogramScore) < 1;
    }

    private readonly record struct Rect(double X, double Y, double Width, double Height);

    /// <summary>
    /// Item indices grouped by leaf region, leaves in reading order, each leaf's items in line order (<see cref="ReadingOrder"/>).
    /// Items with empty text are left out; an isolated page-number footer comes last as its own group.
    /// </summary>
    public static List<List<int>> Leaves(IReadOnlyList<XyItem> items, double pageWidth, double pageHeight, IReadOnlyList<Box4>? obstacles = null)
    {
        Rect[] figures = [.. (obstacles ?? []).Select(o => new Rect(o.X0, o.Y0, Math.Max(0, o.Width), Math.Max(0, o.Height)))];
        List<int> all = [];
        List<int> footer = [];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Text.Length == 0)
            {
                continue;
            }

            (IsolatedNumericFooter(items, i, pageWidth, pageHeight) ? footer : all).Add(i);
        }

        List<List<int>> leaves = [];
        if (all.Count > 0)
        {
            Rect bbox = RootBox(items, all, pageWidth, pageHeight);
            double medianH = MedianLineHeight(items, all);
            Rec(items, all, bbox, 0, false, medianH, figures, leaves);
        }

        if (footer.Count > 0)
        {
            leaves.Add(footer);
        }

        return [.. leaves.Select(leaf => ReadingOrder.Order([.. leaf.Select(i => items[i].Box)]).Select(k => leaf[k]).ToList())];
    }

    private static void Rec(IReadOnlyList<XyItem> items, List<int> idxs, Rect bbox, int depth, bool inheritedTabular, double medianH, Rect[] figures, List<List<int>> leaves)
    {
        if (idxs.Count <= MinItemsToCut || depth >= MaxDepth)
        {
            leaves.Add(idxs);
            return;
        }

        Cut? banner = FindBannerCut(items, idxs, bbox, medianH);
        Cut? h = FindBestCut(items, idxs, bbox, Axis.Horizontal, medianH, figures);
        Cut? v = FindBestCut(items, idxs, bbox, Axis.Vertical, medianH, figures);

        // The column histogram always runs: its fill gate is also the region-level table detector.
        bool regionTabular = false;
        Cut? columnFull = FindColumnCut(items, idxs, bbox, medianH, ref regionTabular);
        bool effectiveTabular = regionTabular || (inheritedTabular && columnFull is null);
        Cut? column = v is null && h is null ? columnFull : null;
        bool preferH = h is not Cut hs || v is not Cut vs || vs.Score <= hs.Score * VPreferenceMargin;

        List<Cut> candidates = [];
        if (banner is Cut bc)
        {
            candidates.Add(bc);
        }

        // A line crossing the would-be gutter (a section heading, a spanning float) separates column runs.
        if (!effectiveTabular && (v ?? columnFull) is Cut gutter && FindCrossingLineCut(items, idxs, gutter.Position, medianH) is Cut cc)
        {
            candidates.Add(cc);
        }

        if (column is Cut col)
        {
            candidates.Add(col);
        }

        // In a tabular region a density V valley is a table gutter, not a column boundary.
        if (effectiveTabular)
        {
            v = null;
        }

        foreach (Cut? c in preferH ? [h, v] : new[] { v, h })
        {
            if (c is Cut cut)
            {
                candidates.Add(cut);
            }
        }

        if (!candidates.Any(c => c.IsHistogramV) && columnFull is Cut cf)
        {
            candidates.Add(cf);
        }

        foreach (Cut cut in candidates)
        {
            (List<int> left, List<int> right) = Partition(items, idxs, cut, medianH);
            if (left.Count == 0 || right.Count == 0)
            {
                continue;
            }

            (Rect leftBox, Rect rightBox) = Split(bbox, cut);

            // A banner cut at the root may isolate a single wide title line; elsewhere an H cut needs two lines a side,
            // unless the thin side is one line spanning the column gutter of the other side.
            bool allowSingleLine = cut.Axis == Axis.Horizontal && double.IsPositiveInfinity(cut.Score) && depth == 0;
            if (cut.Axis == Axis.Horizontal && !allowSingleLine)
            {
                int lc = DistinctLines(items, left, medianH), rc = DistinctLines(items, right, medianH);
                bool rescue = false;
                if (lc < MinLinesPerSide && rc >= MinLinesPerSide)
                {
                    rescue = SpanningRescue(items, left, lc, right, rightBox, medianH, figures);
                }
                else if (rc < MinLinesPerSide && lc >= MinLinesPerSide)
                {
                    rescue = SpanningRescue(items, right, rc, left, leftBox, medianH, figures);
                }

                if ((lc < MinLinesPerSide || rc < MinLinesPerSide) && !rescue)
                {
                    continue;
                }
            }

            // A real column gutter runs past several lines on both sides.
            if (cut.Axis == Axis.Vertical
                && (DistinctLines(items, left, medianH) < MinLinesPerSide || DistinctLines(items, right, medianH) < MinLinesPerSide))
            {
                continue;
            }

            bool childTabular = effectiveTabular && cut.Axis == Axis.Horizontal;
            Rec(items, left, leftBox, depth + 1, childTabular, medianH, figures, leaves);
            Rec(items, right, rightBox, depth + 1, childTabular, medianH, figures, leaves);
            return;
        }

        leaves.Add(idxs);
    }

    private static bool SpanningRescue(IReadOnlyList<XyItem> items, List<int> thin, int thinLines, List<int> wide, Rect wideBox, double medianH, Rect[] figures)
    {
        if (thinLines > 1 || ColumnCutX(items, wide, wideBox, medianH, figures) is not double gx)
        {
            return false;
        }

        (double tx0, double tx1) = XExtent(items, thin);
        return tx0 < gx - 1 && tx1 > gx + 1;
    }

    private static (List<int> Left, List<int> Right) Partition(IReadOnlyList<XyItem> items, List<int> idxs, Cut cut, double medianH)
    {
        List<int> left = [], right = [];
        if (!cut.IsHistogramV)
        {
            foreach (int i in idxs)
            {
                XyItem it = items[i];
                double at = cut.Axis == Axis.Horizontal ? it.Y + it.H * 0.5 : it.X + it.W * 0.5;
                (at < cut.Position ? left : right).Add(i);
            }

            return (left, right);
        }

        // A histogram V cut sits between column left edges: split item by item when it is a real column boundary
        // (a quarter of the rows have text on both sides), else keep each row whole so a wrapped line isn't torn.
        List<int> sorted = SortYX(items, idxs);
        double bandTol = Math.Max(medianH * 0.5, 2.0);
        List<(int K, int J)> bands = Bands(items, sorted, bandTol);
        int rowsTotal = 0, rowsStraddle = 0;
        foreach ((int k, int j) in bands)
        {
            bool hasLeft = false, hasRight = false;
            for (int q = k; q < j; q++)
            {
                double ix = items[sorted[q]].X;
                if (double.IsFinite(ix))
                {
                    if (ix < cut.Position)
                    {
                        hasLeft = true;
                    }
                    else
                    {
                        hasRight = true;
                    }
                }
            }

            if (hasLeft || hasRight)
            {
                rowsTotal++;
                if (hasLeft && hasRight)
                {
                    rowsStraddle++;
                }
            }
        }

        if (rowsTotal > 0 && (double)rowsStraddle / rowsTotal >= StraddlePartitionFraction)
        {
            foreach (int i in idxs)
            {
                (items[i].X < cut.Position ? left : right).Add(i);
            }
        }
        else
        {
            foreach ((int k, int j) in bands)
            {
                double minX = double.PositiveInfinity;
                for (int q = k; q < j; q++)
                {
                    double ix = items[sorted[q]].X;
                    if (double.IsFinite(ix) && ix < minX)
                    {
                        minX = ix;
                    }
                }

                List<int> side = minX < cut.Position ? left : right;
                for (int q = k; q < j; q++)
                {
                    side.Add(sorted[q]);
                }
            }
        }

        return (left, right);
    }

    private static Cut? FindBestCut(IReadOnlyList<XyItem> items, List<int> idxs, Rect bbox, Axis axis, double medianH, Rect[] figures)
    {
        (double origin, double length) = axis == Axis.Horizontal ? (bbox.Y, bbox.Height) : (bbox.X, bbox.Width);
        if (length <= 0)
        {
            return null;
        }

        double bucketPt = Math.Max(BucketPt, length / MaxBuckets);
        int n = Math.Max((int)Math.Ceiling(length / bucketPt), 1);
        double[] density = new double[n];

        void Stamp(double c0, double c1, double weight)
        {
            double b0f = Math.Floor((c0 - origin) / bucketPt);
            double b1f = Math.Ceiling((c1 - origin) / bucketPt);
            int b0 = (int)Math.Max(b0f, 0);
            int b1 = (int)Math.Min(Math.Max(b1f, b0f + 1), n);
            for (int b = b0; b < b1; b++)
            {
                density[b] += weight;
            }
        }

        foreach (int i in idxs)
        {
            XyItem it = items[i];
            (double c0, double c1) = axis == Axis.Horizontal ? (it.Y, it.Y + it.H) : (it.X, it.X + it.W);
            Stamp(c0, c1, Math.Max(it.Text.EnumerateRunes().Count(), 1));
        }

        // Obstacles stamp in as opaque blocks so cuts go around them rather than through them.
        foreach (Rect fig in figures)
        {
            double fx0 = Math.Max(fig.X, bbox.X), fy0 = Math.Max(fig.Y, bbox.Y);
            double fx1 = Math.Min(fig.X + fig.Width, bbox.X + bbox.Width), fy1 = Math.Min(fig.Y + fig.Height, bbox.Y + bbox.Height);
            if (fx1 <= fx0 || fy1 <= fy0)
            {
                continue;
            }

            if (axis == Axis.Horizontal)
            {
                Stamp(fy0, fy1, Math.Max(fx1 - fx0, 1));
            }
            else
            {
                Stamp(fx0, fx1, Math.Max(fy1 - fy0, 1));
            }
        }

        List<double> nonzero = [.. density.Where(d => d > 0)];
        if (nonzero.Count == 0)
        {
            return null;
        }

        nonzero.Sort();
        double median = nonzero[nonzero.Count / 2];
        if (median <= 0)
        {
            return null;
        }

        double threshold = median * TDens;
        int firstDense = Array.FindIndex(density, d => d > threshold);
        int lastDense = Array.FindLastIndex(density, d => d > threshold);
        if (firstDense < 0 || lastDense <= firstDense + 1)
        {
            return null;
        }

        double minValleyPt = axis == Axis.Vertical ? MinVValleyPt : medianH * MinHValleyFactor;
        int minValleyBuckets = Math.Max((int)Math.Ceiling(minValleyPt / bucketPt), 1);
        Cut? best = null;
        int idx = firstDense + 1;
        while (idx <= lastDense)
        {
            if (density[idx] > threshold)
            {
                idx++;
                continue;
            }

            int s = idx;
            while (idx <= lastDense && density[idx] <= threshold)
            {
                idx++;
            }

            int width = idx - s;
            if (width >= minValleyBuckets)
            {
                double mean = 0;
                for (int b = s; b < idx; b++)
                {
                    mean += density[b];
                }

                mean /= width;
                double score = width * Math.Max(1 - mean / median, 0);
                double position = origin + (s + idx) * 0.5 * bucketPt;
                if (!PassesThroughObstacle(axis, position, bbox, figures) && (best is not Cut prior || score > prior.Score))
                {
                    best = new Cut(axis, position, score);
                }
            }
        }

        return best;
    }

    private static bool PassesThroughObstacle(Axis axis, double position, Rect bbox, Rect[] obstacles)
    {
        const double EdgeTolerancePt = 1.0;
        foreach (Rect ob in obstacles)
        {
            double ox0 = Math.Max(ob.X, bbox.X), oy0 = Math.Max(ob.Y, bbox.Y);
            double ox1 = Math.Min(ob.X + ob.Width, bbox.X + bbox.Width), oy1 = Math.Min(ob.Y + ob.Height, bbox.Y + bbox.Height);
            if (ox1 <= ox0 || oy1 <= oy0)
            {
                continue;
            }

            bool through = axis == Axis.Vertical
                ? position > ox0 + EdgeTolerancePt && position < ox1 - EdgeTolerancePt
                : position > oy0 + EdgeTolerancePt && position < oy1 - EdgeTolerancePt;
            if (through)
            {
                return true;
            }
        }

        return false;
    }

    private static (Rect, Rect) Split(Rect bbox, Cut cut)
    {
        if (cut.Axis == Axis.Horizontal)
        {
            double topH = Math.Max(cut.Position - bbox.Y, 0);
            return (bbox with { Height = topH }, bbox with { Y = cut.Position, Height = Math.Max(bbox.Height - topH, 0) });
        }

        double leftW = Math.Max(cut.Position - bbox.X, 0);
        return (bbox with { Width = leftW }, bbox with { X = cut.Position, Width = Math.Max(bbox.Width - leftW, 0) });
    }

    /// <summary>Per-column fill (covered x-span over slot width per band): the least-filled column and the band-weighted mean.</summary>
    private static (double Min, double Avg, double[] Covered, int[] BandCount) FillStats(
        IReadOnlyList<XyItem> items, List<int> sorted, List<(double X, int Count)> peaks, double bandTol, double bucketPt, double maxX)
    {
        int n = peaks.Count;
        double[] covered = new double[n];
        int[] bandCount = new int[n];
        foreach ((int k, int j) in Bands(items, sorted, bandTol))
        {
            double[] lo = [.. Enumerable.Repeat(double.PositiveInfinity, n)];
            double[] hi = [.. Enumerable.Repeat(double.NegativeInfinity, n)];
            for (int q = k; q < j; q++)
            {
                XyItem it = items[sorted[q]];
                if (!double.IsFinite(it.X))
                {
                    continue;
                }

                // Peak centres are bucket-quantised, so a column's items may start up to a bucket left of its peak.
                int c = 0;
                while (c + 1 < n && it.X >= peaks[c + 1].X - bucketPt)
                {
                    c++;
                }

                lo[c] = Math.Min(lo[c], it.X);
                hi[c] = Math.Max(hi[c], it.X + it.W);
            }

            for (int c = 0; c < n; c++)
            {
                if (hi[c] > lo[c])
                {
                    double colW = c + 1 < n ? peaks[c + 1].X - peaks[c].X : maxX - peaks[c].X;
                    if (colW > 1)
                    {
                        covered[c] += Math.Min((hi[c] - lo[c]) / colW, 1);
                        bandCount[c]++;
                    }
                }
            }
        }

        (double min, double avg) = Summarize(covered, bandCount, Enumerable.Range(0, n));
        return (min, avg, covered, bandCount);
    }

    private static (double Min, double Avg) Summarize(double[] covered, int[] bandCount, IEnumerable<int> columns)
    {
        double min = double.PositiveInfinity, wsum = 0, wn = 0;
        foreach (int c in columns)
        {
            if (bandCount[c] > 0)
            {
                double f = covered[c] / bandCount[c];
                min = Math.Min(min, f);
                wsum += f * bandCount[c];
                wn += bandCount[c];
            }
        }

        return (min, wn > 0 ? wsum / wn : double.PositiveInfinity);
    }

    /// <summary>
    /// Column gutter from a histogram of line-start x positions, for when a spanning element hides the density valley.
    /// Its fill gate also flags tabular regions (sets <paramref name="tabular"/>), whose gutters must not be cut.
    /// </summary>
    private static Cut? FindColumnCut(IReadOnlyList<XyItem> items, List<int> idxs, Rect bbox, double medianH, ref bool tabular)
    {
        if (bbox.Width <= 1 || idxs.Count < 8)
        {
            return null;
        }

        double bandTol = Math.Max(medianH * 0.5, 2.0);

        // OCR items are whole lines, so two in one band are split at a gutter, never a word space.
        int ocrItems = idxs.Count(i => items[i].Ocr);
        double gutterGap = ocrItems * 2 > idxs.Count ? Math.Max(medianH * 0.5, 4.0) : Math.Max(medianH, 4.0);
        List<int> sorted = SortYX(items, idxs);
        List<double> lineStarts = [], nearStarts = [];
        double nearGapFloor = gutterGap * 0.6;
        foreach ((int k, int j) in Bands(items, sorted, bandTol))
        {
            List<int> band = sorted.GetRange(k, j - k);
            Units.StableSort(band, (a, b) => items[a].X.CompareTo(items[b].X));
            double prevRight = double.NegativeInfinity;
            foreach (int idx in band)
            {
                XyItem it = items[idx];
                if (double.IsFinite(it.X))
                {
                    double gap = it.X - prevRight;
                    if (gap > gutterGap)
                    {
                        lineStarts.Add(it.X);
                    }
                    else if (gap > nearGapFloor)
                    {
                        nearStarts.Add(it.X);
                    }
                }

                prevRight = Math.Max(prevRight, it.X + it.W);
            }
        }

        if (lineStarts.Count < 8)
        {
            return null;
        }

        (double minX, double maxX) = XExtent(items, idxs);
        if (!double.IsFinite(minX) || maxX <= minX + 1)
        {
            return null;
        }

        double totalW = maxX - minX;
        double bucketPt = ColumnBucketPt;
        int nBuckets = Math.Max((int)Math.Ceiling(totalW / bucketPt), 1);
        int[] hist = new int[nBuckets];
        foreach (double x in lineStarts)
        {
            int b = (int)Math.Max(Math.Floor((x - minX) / bucketPt), 0);
            if (b < nBuckets)
            {
                hist[b]++;
            }
        }

        // A near-gap start counts only when it aligns (to 1.5pt) with at least two strict starts: a tight gutter, not a river.
        int rescued = 0;
        List<double> countedStarts = [.. lineStarts];
        foreach (double x in nearStarts)
        {
            if (lineStarts.Count(s => Math.Abs(s - x) <= 1.5) >= 2)
            {
                int b = (int)Math.Max(Math.Floor((x - minX) / bucketPt), 0);
                if (b < nBuckets)
                {
                    hist[b]++;
                    rescued++;
                    countedStarts.Add(x);
                }
            }
        }

        int nStarts = lineStarts.Count + rescued;

        // Peaks: runs of occupied buckets, merged through up to two empty buckets.
        List<(double X, int Count)> peaks = [];
        int i = 0;
        while (i < nBuckets)
        {
            if (hist[i] == 0)
            {
                i++;
                continue;
            }

            int s = i, total = 0, lastNonzero = i, j = i;
            while (j < nBuckets)
            {
                if (hist[j] > 0)
                {
                    total += hist[j];
                    lastNonzero = j;
                    j++;
                }
                else if (j - lastNonzero <= 2)
                {
                    j++;
                }
                else
                {
                    break;
                }
            }

            peaks.Add((minX + bucketPt * (s + lastNonzero + 1.0) * 0.5, total));
            i = j;
        }

        List<(double X, int Count)> raw = [.. peaks];
        int adaptiveMin = Math.Clamp(nStarts / 6, 4, ColumnMinLinesPerPeak);
        peaks.RemoveAll(p => p.Count < adaptiveMin);
        if (peaks.Count < 2)
        {
            // 3+ balanced, dominant, sparsely-filled raw peaks are still tabular evidence: classify, never cut.
            if (raw.Count >= 3)
            {
                int rawSum = raw.Sum(p => p.Count), rawMin = raw.Min(p => p.Count), rawMax = raw.Max(p => p.Count);
                bool balanced = rawMax > 0 && rawMin >= 2 && (double)rawMin / rawMax >= ColumnPeakBalanceRatio;
                double dominance = (double)rawSum / Math.Max(nStarts, 1);
                if (balanced && dominance >= ColumnPeakDominance)
                {
                    List<(double X, int Count)> rawPeaks = [.. raw.OrderBy(p => p.X)];
                    (double minFill, double avgFill, _, _) = FillStats(items, sorted, rawPeaks, bandTol, bucketPt, maxX);
                    if ((double.IsFinite(minFill) && minFill < MinFillHardFloor) || (double.IsFinite(avgFill) && avgFill < ColumnMinFill))
                    {
                        tabular = true;
                    }
                }
            }

            return null;
        }

        peaks = [.. peaks.OrderBy(p => p.X)];
        if (peaks.Count > 2)
        {
            List<(double X, int Count)> byCount = [.. peaks.OrderByDescending(p => p.Count)];
            double third = byCount[2].Count, second = byCount[1].Count, largest = byCount[0].Count;
            if (second <= 0 || third / second >= ColumnPeakBalanceRatio)
            {
                // Comparable peaks: N-column prose or a table. Keep the ones balanced against the largest; the fill gate decides.
                peaks.RemoveAll(p => largest > 0 && p.Count / largest < ColumnPeakBalanceRatio);
                if (peaks.Count < 2)
                {
                    return null;
                }
            }
            else
            {
                peaks = [.. new[] { byCount[0], byCount[1] }.OrderBy(p => p.X)];
            }
        }

        // Fill gate: prose fills each column slot nearly edge to edge; table cells leave wide whitespace.
        {
            (double minFill, double avgFill, double[] covered, int[] bandCount) = FillStats(items, sorted, peaks, bandTol, bucketPt, maxX);
            int n = peaks.Count;
            if (n > 2)
            {
                // A sparsely-banded, low-fill peak is figure scatter (axis ticks), not a column: drop it when the rest read as prose.
                int maxBands = bandCount.Max();
                if (maxBands > 0)
                {
                    int bandFloor = (int)Math.Ceiling(maxBands * ColumnPhantomBandFraction);
                    double Fill(int c) => bandCount[c] > 0 ? covered[c] / bandCount[c] : 0;
                    List<int> survivors = [.. Enumerable.Range(0, n).Where(c => !(Fill(c) < MinFillHardFloor && bandCount[c] < bandFloor))];
                    if (survivors.Count >= 2 && survivors.Count < n && survivors.All(c => Fill(c) >= MinFillHardFloor))
                    {
                        (minFill, avgFill) = Summarize(covered, bandCount, survivors);
                        peaks = [.. survivors.Select(c => peaks[c])];
                        n = peaks.Count;
                    }
                }
            }

            if (n > 2 && double.IsFinite(minFill) && minFill < MinFillHardFloor)
            {
                tabular = true;
                return null;
            }

            if (double.IsFinite(avgFill) && avgFill < ColumnMinFill)
            {
                tabular = true;
                return null;
            }
        }

        if ((double)peaks.Sum(p => p.Count) / nStarts < ColumnPeakDominance)
        {
            return null;
        }

        double smallest = peaks.Min(p => p.Count), biggest = peaks.Max(p => p.Count);
        if ((biggest > 0 ? smallest / biggest : 0) < ColumnPeakBalanceRatio)
        {
            return null;
        }

        // Cut the leftmost gutter wide enough to be a column gap (not an indent); recursion peels the rest.
        double minGap = totalW * ColumnMinGapFraction;
        int li = -1;
        for (int w = 0; w < peaks.Count - 1; w++)
        {
            if (peaks[w + 1].X - peaks[w].X >= minGap)
            {
                li = w;
                break;
            }
        }

        if (li < 0)
        {
            return null;
        }

        // Place the cut in the actual gutter: between the left side's rightmost extent and the right column's first start.
        double midCenters = (peaks[li].X + peaks[li + 1].X) * 0.5;
        double rightMinStart = countedStarts.Where(x => x > midCenters).DefaultIfEmpty(double.PositiveInfinity).Min();
        double boundary = double.IsFinite(rightMinStart) ? rightMinStart : peaks[li + 1].X;
        double gutterLeft = double.NegativeInfinity;
        foreach (int idx in idxs)
        {
            XyItem it = items[idx];
            if (double.IsFinite(it.X))
            {
                double r = it.X + it.W;
                if (r <= boundary - 1 && r > gutterLeft)
                {
                    gutterLeft = r;
                }
            }
        }

        double cutX = double.IsFinite(gutterLeft) && gutterLeft > peaks[li].X ? (gutterLeft + boundary) * 0.5 : midCenters;
        if (cutX <= minX + 1 || cutX >= maxX - 1)
        {
            return null;
        }

        return new Cut(Axis.Vertical, cutX, HistogramScore);
    }

    private static double? ColumnCutX(IReadOnlyList<XyItem> items, List<int> idxs, Rect bbox, double medianH, Rect[] figures)
    {
        if (FindBestCut(items, idxs, bbox, Axis.Vertical, medianH, figures) is Cut v)
        {
            return v.Position;
        }

        bool tabular = false;
        return FindColumnCut(items, idxs, bbox, medianH, ref tabular)?.Position;
    }

    /// <summary>
    /// An H cut isolating lines that cross the gutter at <paramref name="gutterX"/> by three line heights on both sides
    /// (headings or floats spanning the columns), when at most a third of the bands cross.
    /// </summary>
    private static Cut? FindCrossingLineCut(IReadOnlyList<XyItem> items, List<int> idxs, double gutterX, double medianH)
    {
        List<int> sorted = [.. idxs];
        Units.StableSort(sorted, (a, b) => items[a].Y.CompareTo(items[b].Y));
        double bandTol = Math.Max(medianH * 0.5, 2.0);
        double reach = 3.0 * Math.Max(medianH, 4.0);
        List<(double Y0, double Y1, bool Crosses)> bands = [];
        foreach (int i in sorted)
        {
            XyItem it = items[i];
            double y0 = it.Y, y1 = it.Y + it.H;
            bool crosses = it.X < gutterX - reach && it.X + it.Box.Width > gutterX + reach;
            if (bands.Count > 0 && (y0 <= bands[^1].Y1 - bandTol || Math.Abs(y0 - (bands[^1].Y0 + bands[^1].Y1) * 0.5) <= bandTol))
            {
                (double ly0, double ly1, bool lc) = bands[^1];
                bands[^1] = (Math.Min(ly0, y0), Math.Max(ly1, y1), lc || crosses);
            }
            else
            {
                bands.Add((y0, y1, crosses));
            }
        }

        int crossing = bands.Count(b => b.Crosses);
        if (bands.Count < 3 || crossing == 0 || crossing * 3 > bands.Count)
        {
            return null;
        }

        Cut Between(int a) => new(Axis.Horizontal, (bands[a].Y1 + bands[a + 1].Y0) * 0.5, double.PositiveInfinity);
        int first = bands.FindIndex(b => b.Crosses);
        if (first > 0)
        {
            return Between(first - 1);
        }

        int firstPlain = bands.FindIndex(b => !b.Crosses);
        return firstPlain < 1 ? null : Between(firstPlain - 1);
    }

    /// <summary>An H cut below (or above) a stack of wide bands with clear space around it: a title or spanning heading.</summary>
    private static Cut? FindBannerCut(IReadOnlyList<XyItem> items, List<int> idxs, Rect bbox, double medianH)
    {
        if (bbox.Width <= 1 || idxs.Count < 2)
        {
            return null;
        }

        List<int> sorted = [.. idxs];
        Units.StableSort(sorted, (a, b) => items[a].Y.CompareTo(items[b].Y));
        double bandTol = Math.Max(medianH * 0.5, 2.0);
        double minClearance = Math.Max(medianH * BannerClearanceFactor, 8.0);
        double widthThreshold = BannerWidthFraction * bbox.Width;
        List<(double Y0, double Y1, double X0, double X1)> bands = [];
        foreach (int i in sorted)
        {
            XyItem it = items[i];
            double y0 = it.Y, y1 = it.Y + it.H, x0 = it.X, x1 = it.X + it.W;
            if (bands.Count > 0)
            {
                (double ly0, double ly1, double lx0, double lx1) = bands[^1];
                if (Math.Abs(y0 - (ly0 + ly1) * 0.5) <= bandTol || y0 <= ly1 + bandTol)
                {
                    bands[^1] = (Math.Min(ly0, y0), Math.Max(ly1, y1), Math.Min(lx0, x0), Math.Max(lx1, x1));
                    continue;
                }
            }

            bands.Add((y0, y1, x0, x1));
        }

        bool Wide(int b) => bands[b].X1 - bands[b].X0 >= widthThreshold;
        int k = 0;
        while (k < bands.Count)
        {
            if (!Wide(k))
            {
                k++;
                continue;
            }

            int start = k, end = k;
            while (end + 1 < bands.Count && Wide(end + 1) && bands[end + 1].Y0 - bands[end].Y1 < minClearance)
            {
                end++;
            }

            double top = bands[start].Y0, bot = bands[end].Y1;
            double? above = start > 0 ? bands[start - 1].Y1 : null;
            double? below = end + 1 < bands.Count ? bands[end + 1].Y0 : null;
            bool aboveClear = above is not double ay || top - ay >= minClearance;
            bool belowClear = below is not double by || by - bot >= minClearance;
            if (aboveClear && belowClear && (below ?? above) is not null)
            {
                // Prefer cutting below the banner so it becomes the top region.
                double cutY = below is double b ? (bot + b) * 0.5 : (above!.Value + top) * 0.5;
                if (cutY > bbox.Y + 1 && cutY < bbox.Y + bbox.Height - 1)
                {
                    return new Cut(Axis.Horizontal, cutY, double.PositiveInfinity);
                }
            }

            k = end + 1;
        }

        return null;
    }

    /// <summary>A centred one- to three-digit item in the lower half, below every other item: a page number, read last.</summary>
    private static bool IsolatedNumericFooter(IReadOnlyList<XyItem> items, int idx, double pageWidth, double pageHeight)
    {
        XyItem item = items[idx];
        string text = item.Text.Trim();
        double center = item.X + item.W * 0.5;
        if (text.Length is < 1 or > 3
            || !text.All(char.IsAsciiDigit)
            || item.Y < pageHeight * 0.5
            || center <= pageWidth * 0.5 - 5
            || center >= pageWidth * 0.5 + 20)
        {
            return false;
        }

        double gapRatio = item.Y < pageHeight * 0.9 ? 2.0 : 0.5;
        for (int o = 0; o < items.Count; o++)
        {
            XyItem other = items[o];
            if (o != idx && other.Text.Trim().Length > 0 && !(other.Y + Math.Min(item.H, other.H) * gapRatio < item.Y))
            {
                return false;
            }
        }

        return true;
    }

    private static Rect RootBox(IReadOnlyList<XyItem> items, List<int> idxs, double pageWidth, double pageHeight)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (int i in idxs)
        {
            XyItem it = items[i];
            minX = Math.Min(minX, it.X);
            minY = Math.Min(minY, it.Y);
            maxX = Math.Max(maxX, it.X + it.W);
            maxY = Math.Max(maxY, it.Y + it.H);
        }

        if (!double.IsFinite(minX))
        {
            return new Rect(0, 0, Math.Max(pageWidth, 1), Math.Max(pageHeight, 1));
        }

        double x = Math.Max(minX, 0), y = Math.Max(minY, 0);
        return new Rect(x, y, Math.Max(maxX - x, 1), Math.Max(maxY - y, 1));
    }

    private static double MedianLineHeight(IReadOnlyList<XyItem> items, List<int> idxs)
    {
        List<double> heights = [.. idxs.Select(i => items[i].Box.Height).Where(h => h > 0.5)];
        if (heights.Count == 0)
        {
            return 10.0;
        }

        heights.Sort();
        return heights[heights.Count / 2];
    }

    private static int DistinctLines(IReadOnlyList<XyItem> items, List<int> idxs, double medianH)
    {
        if (idxs.Count == 0)
        {
            return 0;
        }

        double band = Math.Max(medianH * 0.5, 2.0);
        List<double> ys = [.. idxs.Select(i => items[i].Y)];
        ys.Sort();
        int lines = 1;
        double last = ys[0];
        foreach (double y in ys.Skip(1))
        {
            if (Math.Abs(y - last) > band)
            {
                lines++;
                last = y;
            }
        }

        return lines;
    }

    private static (double Lo, double Hi) XExtent(IReadOnlyList<XyItem> items, IEnumerable<int> idxs)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (int i in idxs)
        {
            lo = Math.Min(lo, items[i].X);
            hi = Math.Max(hi, items[i].X + items[i].W);
        }

        return (lo, hi);
    }

    private static List<int> SortYX(IReadOnlyList<XyItem> items, List<int> idxs)
    {
        List<int> sorted = [.. idxs];
        Units.StableSort(sorted, (a, b) => items[a].Y != items[b].Y ? items[a].Y.CompareTo(items[b].Y) : items[a].X.CompareTo(items[b].X));
        return sorted;
    }

    /// <summary>Runs of y-sorted items whose top lies within <paramref name="bandTol"/> of the run's first item, as [K, J) ranges.</summary>
    private static List<(int K, int J)> Bands(IReadOnlyList<XyItem> items, List<int> sorted, double bandTol)
    {
        List<(int, int)> bands = [];
        int k = 0;
        while (k < sorted.Count)
        {
            double by = items[sorted[k]].Y;
            int j = k;
            while (j < sorted.Count && Math.Abs(items[sorted[j]].Y - by) <= bandTol)
            {
                j++;
            }

            bands.Add((k, j));
            k = j;
        }

        return bands;
    }
}
