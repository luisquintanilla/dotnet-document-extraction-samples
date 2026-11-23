using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;
using OcriaNano.DocumentExtraction.Layout;

namespace OcriaNano.DocumentExtraction.Document;

/// <summary>Where a unit came from on the page: its polygon in points (an OCR quad or a native line rect) and its OCR confidence.</summary>
internal readonly record struct UnitSource(IReadOnlyList<(double X, double Y)> Polygon, double? Confidence)
{
    public static UnitSource Rect(Box4 b, double? confidence = null)
        => new([(b.X0, b.Y0), (b.X1, b.Y0), (b.X1, b.Y1), (b.X0, b.Y1)], confidence);
}

/// <summary>Everything the builder needs for one page.</summary>
internal sealed class PageResult
{
    /// <summary>Gets the 1-based page number.</summary>
    public required int Number { get; init; }

    /// <summary>Gets the page width in points.</summary>
    public required double Width { get; init; }

    /// <summary>Gets the page height in points.</summary>
    public required double Height { get; init; }

    /// <summary>Gets source units per point: 1 for PDFs, pixels per point for images.</summary>
    public double SourceScale { get; init; } = 1;

    /// <summary>Gets the coordinate unit reported for the page.</summary>
    public ExtractionDocumentCoordinateUnit CoordinateUnit { get; init; } = ExtractionDocumentCoordinateUnit.Point;

    /// <summary>Gets the page's units (points, top-left).</summary>
    public IReadOnlyList<Unit> Units { get; init; } = [];

    /// <summary>Gets one source per unit.</summary>
    public IReadOnlyList<UnitSource> Sources { get; init; } = [];

    /// <summary>Gets what LayoutNet found, or <see cref="PageModel.Empty"/>.</summary>
    public PageModel Model { get; init; } = PageModel.Empty;

    /// <summary>Gets why LayoutNet didn't run on this page, or null when it ran.</summary>
    public string? LayoutSkipped { get; init; }

    /// <summary>Gets whether the page was OCR'd.</summary>
    public bool Ocred { get; init; }

    /// <summary>Gets the OCR scripts read on the page.</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];
}

/// <summary>Turns per-page units, blocks and tables into an <see cref="ExtractionDocument"/> with Markdown content (plan §5.7).</summary>
internal static class ExtractionDocumentBuilder
{
    public const string PagesOcredKey = "ocria.pagesOcred";
    public const string MeanConfidenceKey = "ocria.meanConfidence";
    public const string ScriptKey = "ocria.script";
    public const string LayoutKey = "ocria.layout";
    public const string ModelVersionKey = "ocria.modelVersion";
    public const string ColumnHeaderRole = "columnHeader";

    /// <summary>The element role and heading level for a LayoutNet class.</summary>
    public static (string Role, int? HeadingLevel) RoleOf(LayoutClass c) => c switch
    {
        LayoutClass.Title => ("title", 1),
        LayoutClass.Heading => ("heading", 2),
        LayoutClass.PageHeader => ("pageHeader", null),
        LayoutClass.PageFooter => ("pageFooter", null),
        LayoutClass.List => ("listItem", null),
        LayoutClass.Caption => ("caption", null),
        LayoutClass.Footnote => ("footnote", null),
        LayoutClass.Formula => ("formula", null),
        LayoutClass.Code => ("code", null),
        LayoutClass.KeyValue => ("keyValue", null),
        LayoutClass.FigureText => ("figureText", null),
        _ => ("paragraph", null),
    };

    public static ExtractionDocument Build(string identifier, IReadOnlyList<PageResult> pages)
    {
        List<(ExtractionDocumentSourcePage Page, ExtractionDocumentSection Section)> built = [];
        List<string> markdown = [];
        List<double> confidences = [];
        foreach (PageResult page in pages)
        {
            double s = page.SourceScale;
            ExtractionDocumentSourcePage source = new(
                page.Number.ToString(CultureInfo.InvariantCulture),
                page.Number,
                new ExtractionDocumentPageDimensions(page.Width * s, page.Height * s),
                page.CoordinateUnit,
                ExtractionDocumentCoordinateOrigin.TopLeft);
            (List<ExtractionDocumentElement> elements, string md) = BuildPage(page, source);
            ExtractionDocumentSection section = new(new TextContent(md));
            foreach (ExtractionDocumentElement e in elements)
            {
                section.Elements.Add(e);
            }

            built.Add((source, section));
            markdown.Add(md);
            confidences.AddRange(page.Sources.Where(u => u.Confidence.HasValue).Select(u => u.Confidence!.Value));
        }

        ExtractionDocument doc = new(identifier, new TextContent(string.Join("\n\n", markdown.Where(m => m.Length > 0))));
        foreach ((ExtractionDocumentSourcePage p, ExtractionDocumentSection sec) in built)
        {
            doc.SourcePages.Add(p);
            doc.Sections.Add(sec);
        }

        doc.Metadata[PagesOcredKey] = pages.Count(p => p.Ocred);
        if (confidences.Count > 0)
        {
            doc.Metadata[MeanConfidenceKey] = Math.Round(confidences.Average(), 4);
        }

        string[] scripts = [.. pages.SelectMany(p => p.Scripts).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (scripts.Length > 0)
        {
            doc.Metadata[ScriptKey] = string.Join(",", scripts);
        }

        doc.Metadata[LayoutKey] = LayoutSummary(pages);
        doc.Metadata[ModelVersionKey] = "v" + OcriaNanoModels.Version;
        return doc;
    }

    private static string LayoutSummary(IReadOnlyList<PageResult> pages)
    {
        List<PageResult> skipped = [.. pages.Where(p => p.LayoutSkipped is not null)];
        if (skipped.Count == 0)
        {
            return "ran";
        }

        string reasons = string.Join("; ", skipped
            .GroupBy(p => p.LayoutSkipped)
            .Select(g => $"{g.Key} (page {string.Join(",", g.Select(p => p.Number))})"));
        return skipped.Count == pages.Count ? $"skipped: {reasons}" : $"ran; skipped: {reasons}";
    }

    private abstract record Item(Box4 Box);

    private sealed record BlockItem(ModelBlock Block) : Item(Block.BBox);

    private sealed record TableItem(ModelTable Table) : Item(Table.BBox);

    private static (List<ExtractionDocumentElement> Elements, string Markdown) BuildPage(PageResult page, ExtractionDocumentSourcePage source)
    {
        List<Item> items = Compose(page);
        List<int> order = items.Count > 1 ? ReadingOrder.Order([.. items.Select(i => i.Box)]) : [.. Enumerable.Range(0, items.Count)];
        List<ExtractionDocumentElement> elements = [];
        List<string> md = [];
        foreach (int k in order)
        {
            switch (items[k])
            {
                case BlockItem { Block: var b }:
                    (ExtractionDocumentElement el, string m) = Block(page, source, b);
                    elements.Add(el);
                    md.Add(m);
                    break;
                case TableItem { Table: var t }:
                    (ExtractionDocumentElement tel, string tm) = Table(page, source, t);
                    elements.Add(tel);
                    md.Add(tm);
                    break;
            }
        }

        return (elements, string.Join("\n\n", md.Where(m => m.Length > 0)));
    }

    private static List<Item> Compose(PageResult page)
    {
        HashSet<int> tableUnits = [.. page.Model.Tables.SelectMany(t => t.Units.Concat(t.Grid.Cells.SelectMany(c => c.Units)))];
        List<Item> items = [.. page.Model.Tables.Select(t => new TableItem(t))];
        HashSet<int> covered = [.. tableUnits];
        foreach (ModelBlock b in page.Model.Blocks)
        {
            if (b.Units.Count(tableUnits.Contains) * 2 > b.Units.Count || string.IsNullOrWhiteSpace(b.Text))
            {
                continue;
            }

            items.Add(new BlockItem(b));
            covered.UnionWith(b.Units);
        }

        List<int> leftover = [.. Enumerable.Range(0, page.Units.Count).Where(i => !covered.Contains(i))];
        items.AddRange(HeuristicBlocks.Build(page.Units, leftover).Select(b => new BlockItem(b)));
        return items;
    }

    private static (ExtractionDocumentElement, string) Block(PageResult page, ExtractionDocumentSourcePage source, ModelBlock b)
    {
        string text = b.Text.Trim();
        ExtractionDocumentElement element;
        string md;
        if (b.Class == LayoutClass.FigureText)
        {
            element = new ExtractionDocumentImageElement(new TextContent(text), text);
            md = text;
        }
        else
        {
            (string role, int? level) = RoleOf(b.Class);
            element = new ExtractionDocumentTextElement(new TextContent(text), role) { HeadingLevel = level };
            md = MarkdownWriter.Text(role, text, level);
        }

        AddSources(element.Sources, page, source, b.Units);
        return (element, md);
    }

    private static (ExtractionDocumentElement, string) Table(PageResult page, ExtractionDocumentSourcePage source, ModelTable t)
    {
        Grid g = t.Grid;
        HashSet<int> head = [.. g.HeadRows];
        List<MarkdownCell> cells = [.. g.Cells.Select(c => new MarkdownCell(
            c.R0, c.C0, c.R1 - c.R0 + 1, c.C1 - c.C0 + 1, c.Text.Trim(), head.Contains(c.R0)))];
        string md = MarkdownWriter.Table(new MarkdownTable(g.NRows, g.NCols, cells));
        ExtractionDocumentTableElement table = new(new TextContent(md), g.NRows, g.NCols);
        foreach (GridCell c in g.Cells)
        {
            string text = c.Text.Trim();
            int rs = c.R1 - c.R0 + 1, cs = c.C1 - c.C0 + 1;
            string? role = head.Contains(c.R0) ? ColumnHeaderRole : null;
            ExtractionDocumentTableCell cell = text.Length > 0
                ? new(new ExtractionDocumentTextElement(new TextContent(text), "paragraph"), rs, cs, role)
                : new(rs, cs, role);
            AddSources(cell.Sources, page, source, c.Units);
            table.Cells[c.R0, c.C0] = cell;
        }

        table.Sources.Add(new ExtractionDocumentSourceReference(source, Polygon(UnitSource.Rect(t.BBox).Polygon, page.SourceScale), null));
        return (table, md);
    }

    private static void AddSources(ICollection<ExtractionDocumentSourceReference> into, PageResult page, ExtractionDocumentSourcePage source, IEnumerable<int> units)
    {
        foreach (int i in units)
        {
            if (i >= 0 && i < page.Sources.Count)
            {
                UnitSource u = page.Sources[i];
                into.Add(new ExtractionDocumentSourceReference(source, Polygon(u.Polygon, page.SourceScale), u.Confidence));
            }
        }
    }

    private static List<ExtractionDocumentPoint> Polygon(IReadOnlyList<(double X, double Y)> points, double scale)
        => [.. points.Select(p => new ExtractionDocumentPoint(Math.Round(p.X * scale, 2), Math.Round(p.Y * scale, 2)))];
}
