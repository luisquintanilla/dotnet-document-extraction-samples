// Ported from LiteParse crates/liteparse/src/table_model/mod.rs (run_page, run_page_framed, mask_minor), Apache-2.0.
using Microsoft.ML.OnnxRuntime;
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Onnx;

namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>Runs <c>tablenet.onnx</c> on a page and decodes tables and blocks (port of <c>run_page</c>).</summary>
internal sealed partial class LayoutNetRunner
{
    /// <summary>Pages with more units than this skip the model (attention and decoding grow with N^2).</summary>
    public const int MaxUnits = 1500;

    public LayoutNetRunner(InferenceSession tableNet)
    {
        TableNet = tableNet;
    }

    public InferenceSession TableNet { get; }

    /// <summary>Tables and blocks on the page; <see cref="PageModel.Empty"/> for pages with under 2 or over 1500 units, or under 20 characters.</summary>
    public PageModel Run(LayoutPageInput input, CancellationToken cancellationToken = default)
    {
        Rules? rules = null;
        LayoutImage image;
        if (input.Raster is { Width: > 0, Height: > 0 } raster)
        {
            GrayImage gray = RuleDetector.Grey(raster);
            rules = RuleDetector.Detect(gray, RuleDetector.Scale);
            image = LayoutImage.FromGray(gray);
        }
        else
        {
            image = LayoutImage.Blank(input.Width, input.Height);
        }

        if (MainRotation.Detect(input.Units, input.Ocr) is not int q)
        {
            return RunFramed(input.Units, input.Width, input.Height, rules, image, cancellationToken);
        }

        double w = input.Width, h = input.Height;
        List<Unit> upright = [.. input.Units.Select(u =>
        {
            Unit c = u.Clone();
            c.PBox = Units.ToFrame(u.PBox, q, w, h);
            return c;
        })];
        (double uw, double uh) = q % 2 == 1 ? (h, w) : (w, h);
        PageModel pm = RunFramed(upright, uw, uh, rules is null ? null : MainRotation.RotateRules(rules, q, w, h), image.Rotate(q), cancellationToken);
        Box4 Back(Box4 b) => Units.FromFrame(b, q, w, h);
        return new PageModel(
            [.. pm.Tables.Select(t => t with
            {
                BBox = Back(t.BBox),
                Grid = t.Grid with { Cells = [.. t.Grid.Cells.Select(c => c with { Bx = Back(c.Bx) })] },
            })],
            [.. pm.Blocks.Select(b => b with { BBox = Back(b.BBox) })]);
    }

    /// <summary>Sets the logits of units that run against the page's main direction to -9 (as tablelab.run_model).</summary>
    internal static void MaskMinor(Logits l, IReadOnlyList<bool> minor)
    {
        int n = l.N;
        for (int i = 0; i < minor.Count; i++)
        {
            if (!minor[i])
            {
                continue;
            }

            l.NodeData[i * 2] = -9f;
            l.NodeData[(i * 2) + 1] = -9f;
            for (int k = 0; k < 4; k++)
            {
                for (int j = 0; j < n; j++)
                {
                    l.PairData[(((k * n) + i) * n) + j] = -9f;
                    l.PairData[(((k * n) + j) * n) + i] = -9f;
                }
            }
        }
    }

    private PageModel RunFramed(IReadOnlyList<Unit> units, double width, double height, Rules? rules, LayoutImage image, CancellationToken ct)
    {
        int n = units.Count;
        if (n is < 2 or > MaxUnits || units.Sum(u => Units.CharCount(u.Text)) < 20)
        {
            return PageModel.Empty;
        }

        float[] feats = UnitFeatures.Compute(units, (float)width, (float)height, rules);
        float[] pairf = rules is null
            ? new float[n * n * 2]
            : UnitFeatures.PairRuleFeatures([.. units.Select(u => new[] { (float)u.PBox.X0, (float)u.PBox.Y0, (float)u.PBox.X1, (float)u.PBox.Y1 })], rules);
        ct.ThrowIfCancellationRequested();
        Logits logits = Forward(feats, pairf, n, units, image);
        MaskMinor(logits, [.. units.Select(u => u.Minor)]);
        List<DUnit> du = [.. units.Select(u => new DUnit(u.Bx, u.Text, u.H))];
        DecodeOptions opt = new();
        List<ModelTable> tables = [];
        foreach (List<int> g0 in TableDecoder.Decode(du, logits, opt))
        {
            List<int> g = [.. g0.Where(i => !units[i].Minor)];
            if (g.Count < 2)
            {
                continue;
            }

            DecodedGrid grid = GridDecoder.Decode(du, g, logits, opt);
            if (grid.NCols < 2 || grid.IsToc() || grid.IsChart() || grid.IsProse())
            {
                continue;
            }

            Box4 bbox = g.Aggregate(Box4.Empty, (acc, i) => acc.Union(units[i].PBox));
            tables.Add(new ModelTable(bbox, grid.ToGrid(), g));
        }

        return new PageModel(tables, BlockDecoder.Build(units, logits));
    }

    private Logits Forward(float[] feats, float[] pairf, int n, IReadOnlyList<Unit> units, LayoutImage image)
    {
        int fd = UnitFeatures.Dim;
        using OrtInput<float> featsIn = new(1, n, fd);
        using OrtInput<bool> mask = new(1, n);
        using OrtInput<float> pair = new(1, n, n, 2);
        using OrtInput<float> img = new(1, 1, LayoutImage.Side, LayoutImage.Side);
        using OrtInput<float> boxes = new(1, n, 4);
        using OrtInput<float> valid = new(1, 2);
        feats.CopyTo(featsIn.Span);
        mask.Span.Fill(true);
        pairf.CopyTo(pair.Span);
        image.FillCanvas(img.Span);
        image.FillBoxes(units, boxes.Span);
        valid.Span[0] = image.Width / (float)LayoutImage.Side;
        valid.Span[1] = image.Height / (float)LayoutImage.Side;
        using IDisposableReadOnlyCollection<OrtValue> outputs = TableNet.Run(
            [("feats", featsIn.Value), ("mask", mask.Value), ("pairf", pair.Value), ("image", img.Value), ("boxes", boxes.Value), ("valid", valid.Value)],
            "node",
            "pair",
            "cls");
        long[] pshape = outputs[1].GetShape();
        long[] cshape = outputs[2].GetShape();
        return new Logits(
            n,
            outputs[0].GetTensorDataAsSpan<float>().ToArray(),
            outputs[1].GetTensorDataAsSpan<float>().ToArray(),
            outputs[2].GetTensorDataAsSpan<float>().ToArray(),
            (int)cshape[2],
            (int)pshape[1]);
    }
}
