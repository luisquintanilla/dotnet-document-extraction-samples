#!/usr/bin/env dotnet
// 01: Extract your first document.
// Reads a PDF with PdfPig (runs locally, no keys) and prints the document outline.
//   dotnet run 01-extract.cs
//   dotnet run 01-extract.cs -- path/to/your.pdf
#:package Microsoft.Extensions.DocumentExtraction@11.0.0-preview.1.26560.3
// TODO: drop this line once Microsoft.Extensions.DocumentExtraction ships with GetText (dotnet/extensions#7962).
#:package Microsoft.Extensions.Documents.Abstractions@11.0.0-preview.1.26600.4
#:package CommunityToolkit.DocumentExtraction.PdfPig@1.0.0-preview
#:property PublishAot=false
#:property NoWarn=$(NoWarn);MEDE0001;EXTEXP0019;CTDE0001

using CommunityToolkit.DocumentExtraction.PdfPig;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

string path = args.Length > 0 ? args[0] : Path.Combine("books", "pdf", "tokens-explained.pdf");

// PdfPig reads the PDF text layer. Docstrum groups words into paragraph-sized blocks,
// and the role callback marks large text as a heading. Both are local heuristics.
using DocumentExtractionClient extractor = new PdfPigDocumentExtractionClient(
    pageSegmenter: DocstrumBoundingBoxes.Instance,
    roleResolver: block => block.TextLines[0].Words[0].Letters[0].PointSize >= 13 ? "heading" : null);

await using var input = File.OpenRead(path);
ExtractionDocument document = await extractor.ExtractAsync(input, Path.GetFileName(path), "application/pdf");

Console.WriteLine($"{document.Identifier}: {document.Sections.Count} sections");
Console.WriteLine();

foreach (var section in document.Sections)
{
    int? page = section.Sources.FirstOrDefault()?.Page.PageNumber;
    Console.WriteLine($"Section (page {page?.ToString() ?? "?"}), {section.Elements.Count} elements");

    // PdfPig returns flat sections. Layout-aware providers (Document Intelligence, Content Understanding)
    // nest sections inside sections and put text in table cells, so a two-level loop like this one
    // only sees part of their output. To read everything, use document.GetText(), or
    // section.EnumerateContent() with element.GetText() as samples 02-06 do.
    foreach (var element in section.Elements)
    {
        string text = element.GetText().ReplaceLineEndings(" ");
        if (text.Length > 70) text = text[..67] + "...";

        string kind = element switch
        {
            ExtractionDocumentTextElement { HeadingLevel: { } level } => $"heading {level}",
            ExtractionDocumentTextElement e => e.Role ?? "text",
            ExtractionDocumentTableElement table => $"table {table.Cells.RowCount}x{table.Cells.ColumnCount}",
            ExtractionDocumentImageElement => "image",
            ExtractionDocumentSection nested => $"nested section, {nested.Elements.Count} elements",
            _ => element.GetType().Name,
        };
        Console.WriteLine($"  [{kind}] {text}");
    }
}
