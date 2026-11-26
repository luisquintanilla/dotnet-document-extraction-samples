#!/usr/bin/env dotnet
// 07: Any format, no service (X-Ray).
// X-Ray.DocumentExtraction is a provider backed by X-Ray.Content, which reads the file itself,
// in process: no keys and no network. Unlike PdfPig it is not limited to PDFs. This sample
// extracts the same book from its Markdown source and from its PDF, and an HTML page, with
// one client and the same code.
//   dotnet run 07-xray-formats.cs
//   dotnet run 07-xray-formats.cs -- path/to/any.docx
#:include common/Providers.cs

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

string[] files = args.Length > 0
    ? args
    :
    [
        Path.Combine("books", "src", "tokens-explained", "book.md"),
        Path.Combine("books", "pdf", "tokens-explained.pdf"),
    ];

using DocumentExtractionClient extractor = Providers.Create("xray", out _)!;

foreach (string path in files)
{
    ExtractionDocument document = await Samples.ExtractFileAsync(extractor, path);
    Console.WriteLine($"== {Path.GetFileName(path)} ({Samples.MediaType(path)})");
    Console.WriteLine($"title: {document.Metadata.GetValueOrDefault("title") ?? "(none)"}");
    Samples.PrintOutline(document, maxElements: 12);

    foreach (var table in document.EnumerateContent().OfType<ExtractionDocumentTableElement>())
    {
        Console.WriteLine();
        Console.WriteLine(Samples.TableToMarkdown(table));
    }
    Console.WriteLine();
}

// A document does not have to come from a file. Anything that is a stream and a media type works.
const string html = """
    <h1>Release notes</h1>
    <p>Shipped this week.</p>
    <h2>Fixes</h2>
    <table><tr><th>Area</th><th>Count</th></tr><tr><td>Parser</td><td>4</td></tr></table>
    """;
using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html));
ExtractionDocument page = await extractor.ExtractAsync(stream, "notes.html", "text/html");
Console.WriteLine("== notes.html (in memory)");
Samples.PrintOutline(page);

// Formats the provider cannot read say so, instead of returning an empty document.
try
{
    await extractor.ExtractAsync(new MemoryStream([1, 2, 3]), "mystery.bin", "application/x-unknown");
}
catch (NotSupportedException ex)
{
    Console.WriteLine();
    Console.WriteLine($"Unsupported: {ex.Message}");
}
