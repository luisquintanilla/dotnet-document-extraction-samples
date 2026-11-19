#!/usr/bin/env dotnet
// 06: Feed a RAG pipeline.
// Extracts the three books with any provider, turns the result into IngestionDocuments,
// chunks at headings, embeds with a local model, and searches the result.
//   dotnet run 06-ingest.cs
//   dotnet run 06-ingest.cs -- cu "How do I count tokens before calling a model?"
#:include common/Providers.cs
#:package Microsoft.Extensions.DataIngestion@11.0.0-preview.1.26560.3
#:package Microsoft.ML.Tokenizers.Data.O200kBase@3.0.0
#:package Microsoft.SemanticKernel.Connectors.InMemory@1.74.0-preview

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Connectors.InMemory;
using OllamaSharp;

string providerName = args.Length > 0 ? args[0] : "pdfpig";
string question = args.Length > 1 ? args[1] : "How do I count tokens before calling a model?";

using var extractor = Providers.Create(providerName, out string? skipped)
    ?? throw new InvalidOperationException($"{providerName} isn't configured: {skipped}");

// 1. Read: any DocumentExtractionClient becomes an IngestionDocumentReader.
IngestionDocumentReader reader = new ExtractionReader(extractor);

// 2. Chunk: split at the headings extraction found, and keep each chunk under a token budget.
var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
IngestionChunker<string> chunker = new HeaderChunker(new IngestionChunkerOptions(tokenizer)
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 32,
});

// 3. Write: embed each chunk and store it in an in-memory vector store.
IEmbeddingGenerator<string, Embedding<float>> embeddings = new OllamaApiClient(
    new Uri(Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434"),
    Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL") ?? "embeddinggemma");
using var store = new InMemoryVectorStore(new() { EmbeddingGenerator = embeddings });
using var writer = new VectorStoreWriter<string>(store, dimensionCount: 768);

using var pipeline = new IngestionPipeline<string>(reader, chunker, writer);
// The scanned copies hold the same text, so skip them.
var books = new DirectoryInfo(Path.Combine(Samples.Root, "books", "pdf"))
    .EnumerateFiles("*.pdf")
    .Where(f => !f.Name.Contains("-scanned"));
await foreach (var result in pipeline.ProcessAsync(books))
{
    Console.WriteLine($"Ingested {Path.GetFileName(result.DocumentId)}: {(result.Succeeded ? "ok" : result.Exception?.Message)}");
}

// 4. Search.
Console.WriteLine();
Console.WriteLine($"Q: {question}");
await foreach (var hit in writer.VectorStoreCollection.SearchAsync(question, top: 3))
{
    Console.WriteLine();
    Console.WriteLine($"[{hit.Score:F2}] {Path.GetFileName(hit.Record["documentid"]?.ToString())} | {hit.Record["context"]}");
    Console.WriteLine($"   {Samples.Shorten(hit.Record["content"]?.ToString() ?? "", 300)}");
}

/// <summary>
/// The bridge. Maps an ExtractionDocument (what extraction produced) to an IngestionDocument
/// (what the ingestion pipeline chunks), keeping headings, tables, and page numbers.
/// </summary>
sealed class ExtractionReader(DocumentExtractionClient client) : IngestionDocumentReader
{
    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType,
        CancellationToken cancellationToken = default)
    {
        var extracted = await client.ExtractAsync(source, identifier, mediaType, cancellationToken: cancellationToken);
        var document = new IngestionDocument(identifier);
        foreach (var section in extracted.Sections)
        {
            var mapped = new IngestionDocumentSection();
            // Layout-aware providers nest sections, so walk down to the content elements.
            // Skip repeated page headers, footers, and page numbers; they only add noise to chunks.
            foreach (var element in section.EnumerateContent().Where(e => !Samples.IsPageFurniture(e)))
            {
                if (Map(element) is { } item)
                {
                    item.PageNumber = Samples.PageOf(element) ?? Samples.PageOf(section);
                    mapped.Elements.Add(item);
                }
            }
            document.Sections.Add(mapped);
        }
        return document;
    }

    static IngestionDocumentElement? Map(ExtractionDocumentElement element) => element switch
    {
        ExtractionDocumentTextElement { Content: TextContent t } text when text.HeadingLevel is not null || text.Role is "heading" or "title" or "sectionHeading"
            => new IngestionDocumentHeader(t.Text) { Level = text.HeadingLevel ?? 2 },
        ExtractionDocumentTableElement table
            => new IngestionDocumentParagraph(Samples.TableToMarkdown(table) + "\n"),
        ExtractionDocumentImageElement image when (image.AlternativeText ?? image.Text) is { Length: > 0 } alt
            => new IngestionDocumentParagraph($"Figure: {alt}\n"),
        { Content: TextContent { Text.Length: > 0 } t }
            => new IngestionDocumentParagraph(t.Text + "\n"),
        _ => null,
    };
}
