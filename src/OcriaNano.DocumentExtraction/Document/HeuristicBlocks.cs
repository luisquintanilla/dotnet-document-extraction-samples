using OcriaNano.DocumentExtraction.Layout;

namespace OcriaNano.DocumentExtraction.Document;

/// <summary>
/// Paragraphs from spacing alone, for pages LayoutNet skipped (disabled, too few or too many units) and for units no
/// layout block claimed: lines in reading order, with a new paragraph where the gap exceeds 1.5x the median line height.
/// </summary>
internal static class HeuristicBlocks
{
    /// <summary>Gap, in median line heights, that starts a new paragraph.</summary>
    public const double ParagraphGap = 1.5;

    /// <summary>Groups <paramref name="indices"/> (into <paramref name="units"/>) into paragraph blocks.</summary>
    public static List<ModelBlock> Build(IReadOnlyList<Unit> units, IReadOnlyList<int> indices)
    {
        List<ModelBlock> blocks = [];
        if (indices.Count == 0)
        {
            return blocks;
        }

        List<int> order = [.. ReadingOrder.Order([.. indices.Select(i => units[i].PBox)]).Select(k => indices[k])];
        List<List<int>> lines = [];
        foreach (int i in order)
        {
            if (lines.Count > 0 && SameLine(units[lines[^1][^1]].PBox, units[i].PBox))
            {
                lines[^1].Add(i);
            }
            else
            {
                lines.Add([i]);
            }
        }

        List<double> heights = [.. order.Select(i => units[i].PBox.Height).Where(h => h > 0)];
        double median = heights.Count > 0 ? Units.Median(heights) : 0;
        List<List<int>> paragraphs = [[.. lines[0]]];
        Box4 prev = Bounds(units, lines[0]);
        for (int k = 1; k < lines.Count; k++)
        {
            Box4 cur = Bounds(units, lines[k]);
            double gap = cur.Y0 - prev.Y1;
            if (gap > ParagraphGap * median || cur.Y1 < prev.Y0)
            {
                paragraphs.Add([]);
            }

            paragraphs[^1].AddRange(lines[k]);
            prev = cur;
        }

        foreach (List<int> p in paragraphs)
        {
            string text = string.Join(" ", p.Select(i => units[i].Text.Trim()).Where(t => t.Length > 0));
            if (text.Length > 0)
            {
                blocks.Add(new ModelBlock(Bounds(units, p), LayoutClass.Text, text, p));
            }
        }

        return blocks;
    }

    private static bool SameLine(Box4 a, Box4 b)
    {
        double overlap = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
        return overlap > 0.5 * Math.Min(a.Height, b.Height) && b.X0 >= a.X0;
    }

    private static Box4 Bounds(IReadOnlyList<Unit> units, IEnumerable<int> idx) => idx.Aggregate(Box4.Empty, (acc, i) => acc.Union(units[i].PBox));
}
