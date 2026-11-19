#!/usr/bin/env dotnet
// 03: Compare providers on the same page.
// Runs every provider you have configured and writes a side-by-side report to outputs/compare.md.
// Providers without settings are skipped, so this works with only PdfPig and Ollama.
//   dotnet run 03-compare.cs
//   dotnet run 03-compare.cs -- books/pages/tensors-in-ten-pages-hero.pdf
#:include common/Providers.cs

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Documents;

string path = args.Length > 0 ? args[0] : Path.Combine("books", "pages", "tokens-explained-hero.pdf");
var summary = new StringBuilder();
var details = new StringBuilder();

summary.AppendLine($"# Provider comparison: `{Path.GetFileName(path)}`").AppendLine();
summary.AppendLine("| Provider | Time | Headings | Text blocks | Tables | Images |");
summary.AppendLine("| --- | --- | --- | --- | --- | --- |");

foreach (string name in Providers.All)
{
    using var extractor = Providers.Create(name, out string? skipped);
    if (extractor is null)
    {
        Console.WriteLine($"{name,-9} skipped: {skipped}");
        summary.AppendLine($"| {Providers.DisplayNames[name]} | skipped | | | | |");
        continue;
    }

    Console.Write($"{name,-9} ");
    var timer = Stopwatch.StartNew();
    ExtractionDocument document;
    try
    {
        document = await Samples.ExtractFileAsync(extractor, path);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"failed: {ex.Message}");
        summary.AppendLine($"| {Providers.DisplayNames[name]} | failed | | | | |");
        continue;
    }
    timer.Stop();

    // The same few lines of code read every provider's output.
    var elements = document.EnumerateContent().ToList();
    int headings = elements.OfType<ExtractionDocumentTextElement>().Count(e => e.HeadingLevel is not null || e.Role == "heading");
    int text = elements.OfType<ExtractionDocumentTextElement>().Count() - headings;
    var tables = elements.OfType<ExtractionDocumentTableElement>().ToList();
    int images = elements.OfType<ExtractionDocumentImageElement>().Count();

    Console.WriteLine($"{timer.Elapsed.TotalSeconds,5:F1}s  {headings} headings, {text} text, {tables.Count} tables, {images} images");
    summary.AppendLine($"| {Providers.DisplayNames[name]} | {timer.Elapsed.TotalSeconds:F1}s | {headings} | {text} | {tables.Count} | {images} |");

    details.AppendLine($"## {Providers.DisplayNames[name]}").AppendLine();
    details.AppendLine("Headings found:").AppendLine();
    foreach (var h in elements.OfType<ExtractionDocumentTextElement>().Where(e => e.HeadingLevel is not null || e.Role == "heading"))
    {
        details.AppendLine($"- {Samples.Shorten(h.GetText(), 80)}");
    }
    if (headings == 0) details.AppendLine("- (none)");
    details.AppendLine();

    foreach (var table in tables)
    {
        details.AppendLine($"Table, {table.Cells.RowCount} rows x {table.Cells.ColumnCount} columns:").AppendLine();
        details.AppendLine(Samples.TableToMarkdown(table));
    }
    foreach (var image in elements.OfType<ExtractionDocumentImageElement>())
    {
        string description = image.AlternativeText ?? image.Text ?? "(no description)";
        details.AppendLine($"Image: {Samples.Shorten(description, 160)}").AppendLine();
    }
}

Directory.CreateDirectory("outputs");
string report = Path.Combine("outputs", $"compare-{Path.GetFileNameWithoutExtension(path)}.md");
await File.WriteAllTextAsync(report, summary.AppendLine().Append(details).ToString());
Console.WriteLine($"Wrote {report}");
