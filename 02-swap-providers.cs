#!/usr/bin/env dotnet
// 02: Swap providers without changing your code.
// The provider is picked by name. Everything after Providers.Create is the same for all six.
//   dotnet run 02-swap-providers.cs -- ollama
//   dotnet run 02-swap-providers.cs -- docintel books/pdf/tensors-in-ten-pages.pdf
#:include common/Providers.cs

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

string provider = args.Length > 0 ? args[0] : "pdfpig";
string path = args.Length > 1 ? args[1] : Path.Combine("books", "pages", "tokens-explained-hero.pdf");

using DocumentExtractionClient? extractor = Providers.Create(provider, out string? skipped);
if (extractor is null)
{
    Console.WriteLine($"Skipping {provider}: {skipped}.");
    return;
}

// From here down, nothing knows or cares which provider is running.
var started = System.Diagnostics.Stopwatch.StartNew();
ExtractionDocument document = await Samples.ExtractFileAsync(extractor, path);

Console.WriteLine($"{Providers.DisplayNames[provider]} took {started.Elapsed.TotalSeconds:F1}s");
Samples.PrintOutline(document);
