#!/usr/bin/env dotnet
// 04: Tables and sources.
// Walks every table cell by cell (including merged cells) and shows where each element
// came from on the page, so you can cite it or highlight it later.
//   dotnet run 04-tables-and-sources.cs -- ollama
//   dotnet run 04-tables-and-sources.cs -- cu
#:include common/Providers.cs

using Microsoft.Extensions.Documents;

string provider = args.Length > 0 ? args[0] : "ollama";
string path = args.Length > 1 ? args[1] : Path.Combine("books", "pages", "tokens-explained-hero.pdf");

using var extractor = Providers.Create(provider, out string? skipped);
if (extractor is null)
{
    Console.WriteLine($"Skipping {provider}: {skipped}.");
    return;
}

ExtractionDocument document = await Samples.ExtractFileAsync(extractor, path);
Console.WriteLine($"{Providers.DisplayNames[provider]}: {document.Identifier}");

// Tables: a grid of cells. A merged cell reports its span and appears once, at its top-left position.
foreach (var table in document.EnumerateContent().OfType<ExtractionDocumentTableElement>())
{
    Console.WriteLine();
    Console.WriteLine($"Table: {table.Cells.RowCount} rows x {table.Cells.ColumnCount} columns");
    // Cells enumerates each cell once; a merged cell sits at its top-left position.
    foreach (var cell in table.Cells)
    {
        string span = (cell.RowSpan, cell.ColumnSpan) switch
        {
            (1, 1) => "",
            var (rows, cols) => $" spans {rows}x{cols}",
        };
        string role = cell.Role is null ? "" : $" ({cell.Role})";
        string text = Samples.Shorten(cell.GetText(), 40);
        Console.WriteLine($"  [{cell.RowIndex},{cell.ColumnIndex}]{span}{role} {text}");
    }
}

// Sources: every element can say which page it came from and, when the provider
// knows, the polygon around it on that page.
Console.WriteLine();
Console.WriteLine("Where the headings are:");
foreach (var heading in document.EnumerateContent().OfType<ExtractionDocumentTextElement>()
             .Where(e => e.HeadingLevel is not null || e.Role is "heading" or "title" or "sectionHeading"))
{
    Console.WriteLine($"  \"{Samples.Shorten(heading.GetText(), 40)}\"");
    foreach (var source in heading.Sources)
    {
        var page = source.Page;
        string box = source.Polygon.Count == 0
            ? "no polygon"
            : string.Join(" ", source.Polygon.Select(p => $"({p.X:0.##},{p.Y:0.##})"));
        Console.WriteLine($"    page {page.PageNumber}, {page.CoordinateUnit?.ToString() ?? "unknown units"}: {box}");
    }
}
