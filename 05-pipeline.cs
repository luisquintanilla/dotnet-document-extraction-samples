#!/usr/bin/env dotnet
// 05: Build a pipeline.
// Registers clients in dependency injection, adds logging and two custom middleware,
// and falls back to an OCR provider when a PDF has no text layer (a scanned page).
//   dotnet run 05-pipeline.cs
//   dotnet run 05-pipeline.cs -- cu
#:include common/Providers.cs
#:package Microsoft.Extensions.DependencyInjection@11.0.0
#:package Microsoft.Extensions.Logging.Console@11.0.0

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;

// Use the provider you name, or local OCRia-nano if its models are installed,
// or the first cloud provider you've configured, or local Ollama.
string ocrName = args.Length > 0
    ? args[0]
    : new[] { "ocria", "cu", "docintel", "mistral" }.FirstOrDefault(n => Providers.Create(n, out _) is { } c && Dispose(c)) ?? "ollama";

var services = new ServiceCollection();
services.AddLogging(logging => logging
    .AddSimpleConsole(o => { o.SingleLine = true; o.IncludeScopes = false; })
    .AddFilter("Microsoft.Extensions.DocumentExtraction", LogLevel.Debug)
    .SetMinimumLevel(LogLevel.Warning));

// An OCR-capable client, used only when we need it.
services.AddKeyedDocumentExtractionClient("ocr", sp => Providers.Create(ocrName, out _)
        ?? throw new InvalidOperationException($"{ocrName} isn't configured."))
    .UseLogging()
    .Use(inner => new TimingClient(inner, ocrName));

// The client the app uses: fast and local first, OCR as a fallback.
// The first middleware you add is the outermost: logging, then fallback, then timing, then PdfPig.
services.AddKeyedDocumentExtractionClient("documents", sp => Providers.Create("pdfpig", out _)!)
    .UseLogging()
    .Use((inner, sp) => new ScannedPdfFallback(inner, sp.GetRequiredKeyedService<DocumentExtractionClient>("ocr")))
    .Use(inner => new TimingClient(inner, "pdfpig"));

using var provider = services.BuildServiceProvider();
var documents = provider.GetRequiredKeyedService<DocumentExtractionClient>("documents");

foreach (string file in new[] { "tokens-explained.pdf", "tokens-explained-scanned.pdf" })
{
    Console.WriteLine();
    Console.WriteLine($"== {file}");
    var document = await Samples.ExtractFileAsync(documents, Path.Combine("books", "pdf", file));
    int words = document.GetText().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    Console.WriteLine($"   {document.Sections.Count} sections, about {words} words");
}

static bool Dispose(IDisposable d) { d.Dispose(); return true; }

/// <summary>Middleware that times each call. Derive from DelegatingDocumentExtractionClient and override ExtractAsync.</summary>
sealed class TimingClient(DocumentExtractionClient inner, string label) : DelegatingDocumentExtractionClient(inner)
{
    public override async Task<ExtractionDocument> ExtractAsync(Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var document = await base.ExtractAsync(stream, identifier, mediaType, options, cancellationToken);
        Console.WriteLine($"   {label} read {identifier} in {timer.Elapsed.TotalSeconds:F1}s");
        return document;
    }
}

/// <summary>
/// Middleware that checks the result. If a PDF came back with almost no text, it's probably
/// a scan, so the same bytes go to the OCR client instead.
/// </summary>
sealed class ScannedPdfFallback(DocumentExtractionClient inner, DocumentExtractionClient ocr) : DelegatingDocumentExtractionClient(inner)
{
    public override async Task<ExtractionDocument> ExtractAsync(Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Buffer the input so it can be read twice.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var document = await base.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        int characters = document.GetText().Count(c => !char.IsWhiteSpace(c));
        if (characters > 100)
        {
            return document;
        }

        Console.WriteLine($"   No text layer in {identifier} ({characters} characters). Falling back to OCR.");
        buffer.Position = 0;
        return await ocr.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
    }
}
