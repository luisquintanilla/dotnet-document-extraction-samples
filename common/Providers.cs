#!/usr/bin/env dotnet
// Shared helpers for every sample. Each sample pulls this file in with #:include,
// so all package references live here in one place.
#:package Microsoft.Extensions.DocumentExtraction@11.0.1-preview.1.26570.2
#:package CommunityToolkit.DocumentExtraction.PdfPig@1.0.0-preview
#:package CommunityToolkit.DocumentExtraction.VisionLanguageModel@1.0.0-preview
#:package CommunityToolkit.DocumentExtraction.Mistral@1.0.0-preview
#:package CommunityToolkit.DocumentExtraction.AzureDocumentIntelligence@1.0.0-preview
#:package CommunityToolkit.DocumentExtraction.AzureContentUnderstanding@1.0.0-preview
#:package X-Ray.DocumentExtraction@26.11.8565-preview
#:package Azure.Identity@1.21.0
#:package OllamaSharp@5.5.2
#:package PDFtoImage@5.4.0
#:property PublishAot=false
#:property NoWarn=$(NoWarn);MEDE0001;EXTEXP0019;CTDE0001;CA1416

using Azure.AI.ContentUnderstanding;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using CommunityToolkit.DocumentExtraction.AzureContentUnderstanding;
using CommunityToolkit.DocumentExtraction.AzureDocumentIntelligence;
using CommunityToolkit.DocumentExtraction.Mistral;
using CommunityToolkit.DocumentExtraction.PdfPig;
using CommunityToolkit.DocumentExtraction.VisionLanguageModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using OllamaSharp;
using XRay.DocumentExtraction;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

/// <summary>Creates any of the six providers by name, using environment variables for settings.</summary>
static class Providers
{
    public static readonly string[] All = ["pdfpig", "xray", "ollama", "mistral", "docintel", "cu"];

    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["pdfpig"] = "PdfPig (local, text layer)",
        ["xray"] = "X-Ray.Content (local, 90+ formats)",
        ["ollama"] = "Vision language model (Ollama)",
        ["mistral"] = "Mistral Document AI",
        ["docintel"] = "Azure Document Intelligence",
        ["cu"] = "Azure Content Understanding",
    };

    /// <summary>Returns a client, or null with a reason when the provider isn't configured.</summary>
    public static DocumentExtractionClient? Create(string name, out string? skipped)
    {
        LoadAzdEnvironment();
        skipped = null;
        switch (name)
        {
            case "pdfpig":
                return new PdfPigDocumentExtractionClient(
                    pageSegmenter: DocstrumBoundingBoxes.Instance,
                    roleResolver: block => block.TextLines[0].Words[0].Letters[0].PointSize >= 13 ? "heading" : null);

            case "xray":
                // X-Ray.Content reads the file itself, in process: PDFs with a text layer and
                // also Word, Excel, PowerPoint, HTML, Markdown, e-mail and 90+ other formats.
                // No keys, no network, no native libraries (OCR is opt-in and off here).
                return new XRayDocumentExtractionClient();

            case "ollama":
                string endpoint = Env("OLLAMA_ENDPOINT") ?? "http://localhost:11434";
                string model = Env("OLLAMA_VISION_MODEL") ?? "qwen3.5:4b";
                IChatClient chat = new OllamaApiClient(
                    new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(5) }, model);
                // Small Qwen models "think" by default. Turning it off keeps a page under a few seconds.
                chat = chat.AsBuilder()
                    .ConfigureOptions(o =>
                    {
                        o.MaxOutputTokens ??= 8192;
                        o.Temperature ??= 0;
                        o.AdditionalProperties ??= [];
                        o.AdditionalProperties["think"] = false;
                        o.AdditionalProperties["num_ctx"] = 16384;
                    })
                    .Build();
                // Ollama reads images, not PDFs, so rasterize each PDF page first.
                // Structured output asks the model for headings, tables, and figures as JSON.
                // Set OLLAMA_STRUCTURED=0 for plain markdown, which is faster.
                bool structured = Env("OLLAMA_STRUCTURED") is not ("0" or "false");
                var plain = new VisionLanguageModelDocumentExtractionClient(chat, new() { UseStructuredOutput = false });
                return new PdfPageRasterizer(structured
                    ? new PlainMarkdownRetry(new VisionLanguageModelDocumentExtractionClient(chat, new() { UseStructuredOutput = true }), plain)
                    : plain);

            case "mistral":
                if (Env("AZURE_MISTRAL_OCR_URI") is { } mistralUri)
                {
                    return new MistralDocumentExtractionClient(
                        new MistralDocumentExtractionClientOptions
                        {
                            OcrEndpoint = new Uri(mistralUri),
                            DefaultModel = Env("AZURE_MISTRAL_OCR_MODEL") ?? "mistral-ocr-4-0",
                            TokenScope = "https://cognitiveservices.azure.com/.default",
                        },
                        new DefaultAzureCredential());
                }
                if (Env("MISTRAL_API_KEY") is { } key)
                {
                    return new MistralDocumentExtractionClient(
                        new MistralDocumentExtractionClientOptions
                        {
                            OcrEndpoint = new Uri("https://api.mistral.ai/v1/ocr"),
                            DefaultModel = Env("MISTRAL_OCR_MODEL") ?? "mistral-ocr-latest",
                        },
                        key);
                }
                skipped = "set AZURE_MISTRAL_OCR_URI (run azd up) or MISTRAL_API_KEY";
                return null;

            case "docintel":
                if (Env("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT") is not { } diEndpoint)
                {
                    skipped = "set AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT (run azd up)";
                    return null;
                }
                return new DocumentIntelligenceClient(new Uri(diEndpoint), new DefaultAzureCredential())
                    .AsDocumentExtractionClient(new() { DefaultModel = "prebuilt-layout" });

            case "cu":
                if (Env("AZURE_CONTENT_UNDERSTANDING_ENDPOINT") is not { } cuEndpoint)
                {
                    skipped = "set AZURE_CONTENT_UNDERSTANDING_ENDPOINT (run azd up)";
                    return null;
                }
                return new ContentUnderstandingClient(new Uri(cuEndpoint), new DefaultAzureCredential())
                    .AsDocumentExtractionClient(new()
                    {
                        AnalyzerId = Env("AZURE_CONTENT_UNDERSTANDING_ANALYZER") ?? "prebuilt-layout",
                    });

            default:
                throw new ArgumentException($"Unknown provider '{name}'. Use one of: {string.Join(", ", All)}.");
        }
    }

    static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    static bool azdLoaded;

    /// <summary>After azd up, read the endpoints from .azure/&lt;env&gt;/.env so you don't have to export them.</summary>
    static void LoadAzdEnvironment()
    {
        if (azdLoaded) return;
        azdLoaded = true;
        string config = Path.Combine(Samples.Root, ".azure", "config.json");
        if (!File.Exists(config)) return;
        string? environment = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config))
            .RootElement.TryGetProperty("defaultEnvironment", out var e) ? e.GetString() : null;
        string dotenv = Path.Combine(Samples.Root, ".azure", environment ?? "", ".env");
        if (environment is null || !File.Exists(dotenv)) return;
        foreach (string line in File.ReadAllLines(dotenv))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line[..equals].Trim();
            if (key.StartsWith("AZURE_") && Env(key) is null)
            {
                Environment.SetEnvironmentVariable(key, line[(equals + 1)..].Trim().Trim('"'));
            }
        }
    }
}

/// <summary>
/// Middleware for small local models. If the model returns JSON that doesn't match the
/// schema (for example, a table with no cells), read the page again as plain markdown.
/// </summary>
sealed class PlainMarkdownRetry(DocumentExtractionClient structured, DocumentExtractionClient plain) : DelegatingDocumentExtractionClient(structured)
{
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        try
        {
            return await base.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"   Structured output failed for {identifier} ({ex.Message}). Retrying as plain markdown.");
            buffer.Position = 0;
            return await plain.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        }
    }
}

/// <summary>
/// Middleware that turns a PDF into one PNG per page, extracts each page with the inner
/// client, and stitches the results back into a single document with page numbers.
/// </summary>
sealed class PdfPageRasterizer(DocumentExtractionClient inner) : DelegatingDocumentExtractionClient(inner)
{
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (mediaType != "application/pdf")
        {
            return await base.ExtractAsync(stream, identifier, mediaType, options, cancellationToken);
        }

        using var pdf = new MemoryStream();
        await stream.CopyToAsync(pdf, cancellationToken);
        byte[] bytes = pdf.ToArray();
        int pageCount = PDFtoImage.Conversion.GetPageCount(bytes);

        var merged = new List<ExtractionDocumentSection>();
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < pageCount; i++)
        {
            using var png = new MemoryStream();
            PDFtoImage.Conversion.SavePng(png, bytes, i, options: new(Dpi: 150));
            png.Position = 0;

            var page = await base.ExtractAsync(png, $"{identifier}#page={i + 1}", "image/png", options, cancellationToken);
            var sourcePage = new ExtractionDocumentSourcePage($"page-{i + 1}", i + 1, null, null, null);
            foreach (var section in page.Sections)
            {
                if (section.Sources.Count == 0)
                {
                    section.Sources.Add(new ExtractionDocumentSourceReference(sourcePage, [], null));
                }
                merged.Add(section);
            }
            if (page.Content is TextContent pageText)
            {
                text.AppendLine(pageText.Text).AppendLine();
            }
        }

        var document = new ExtractionDocument(identifier, new TextContent(text.ToString().TrimEnd()));
        document.Sections.AddRange(merged);
        return document;
    }
}

/// <summary>Small helpers for finding sample files and printing documents.</summary>
static class Samples
{
    /// <summary>The repository root, found by walking up to global.json.</summary>
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "global.json"))) return d.FullName;
            }
            return Directory.GetCurrentDirectory();
        }
    }

    public static string Book(string fileName) => Path.Combine(Root, "books", fileName);

    public static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };

    public static async Task<ExtractionDocument> ExtractFileAsync(DocumentExtractionClient client, string path,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await client.ExtractAsync(stream, Path.GetFileName(path), MediaType(path), options, cancellationToken);
    }

    public static string TextOf(ExtractionDocumentElement element) => element switch
    {
        // Sections hold other elements (table cells hold a section too), so join their text.
        ExtractionDocumentSection section => string.Join(" ", Leaves(section).Select(TextOf).Where(t => t.Length > 0)),
        { Content: TextContent t } => t.Text,
        { Content: null } => "",
        _ => element.Content.ToString() ?? "",
    };

    /// <summary>
    /// Walks nested sections depth-first and yields the content elements (text, tables, images).
    /// Layout-aware providers such as Document Intelligence nest sections inside sections.
    /// </summary>
    public static IEnumerable<ExtractionDocumentElement> Leaves(ExtractionDocumentElement element)
    {
        if (element is not ExtractionDocumentSection section)
        {
            yield return element;
            yield break;
        }
        foreach (var child in section.Elements)
            foreach (var leaf in Leaves(child))
                yield return leaf;
    }

    /// <summary>Page furniture repeats on every page and adds noise to search results.</summary>
    public static bool IsPageFurniture(ExtractionDocumentElement element) =>
        element is ExtractionDocumentTextElement { Role: "pageHeader" or "pageFooter" or "pageNumber" };

    public static int? PageOf(ExtractionDocumentElement element) =>
        element.Sources.FirstOrDefault()?.Page.PageNumber;

    /// <summary>Prints one line per element: its kind, page, and a preview of its text.</summary>
    public static void PrintOutline(ExtractionDocument document, int maxElements = int.MaxValue)
    {
        Console.WriteLine($"{document.Identifier}: {document.Sections.Count} sections");
        int shown = 0;
        foreach (var section in document.Sections)
        {
            foreach (var element in Leaves(section))
            {
                if (shown++ >= maxElements) { Console.WriteLine("  ..."); return; }
                Console.WriteLine($"  p{PageOf(element) ?? PageOf(section)} [{KindOf(element)}] {Shorten(TextOf(element))}");
            }
        }
    }

    public static string KindOf(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentTextElement { HeadingLevel: { } level } => $"h{level}",
        ExtractionDocumentTextElement e => e.Role ?? "text",
        ExtractionDocumentTableElement table => $"table {table.Cells.RowCount}x{table.Cells.ColumnCount}",
        ExtractionDocumentImageElement => "image",
        ExtractionDocumentSection => "section",
        _ => element.GetType().Name,
    };

    /// <summary>Renders a table grid as Markdown. Merged cells show their span, like "(spans 2 cols)".</summary>
    public static string TableToMarkdown(ExtractionDocumentTableElement table)
    {
        var cells = table.Cells;
        if (cells.RowCount == 0 || cells.ColumnCount == 0) return "(empty table)";
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < cells.RowCount; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cells.ColumnCount; c++)
            {
                var cell = cells[r, c];
                string text = "";
                if (cell is not null && cell.RowIndex == r && cell.ColumnIndex == c)
                {
                    text = cell.Content is null ? "" : TextOf(cell.Content).ReplaceLineEndings(" ").Replace("|", "\\|");
                    if (cell.ColumnSpan > 1) text += $" (spans {cell.ColumnSpan} cols)";
                    if (cell.RowSpan > 1) text += $" (spans {cell.RowSpan} rows)";
                }
                else if (cell is not null)
                {
                    text = "^";
                }
                sb.Append($" {text} |");
            }
            sb.AppendLine();
            if (r == 0) sb.Append('|').AppendJoin("", Enumerable.Repeat(" --- |", cells.ColumnCount)).AppendLine();
        }
        return sb.ToString();
    }

    public static string Shorten(string text, int max = 70)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..(max - 3)] + "...";
    }
}
