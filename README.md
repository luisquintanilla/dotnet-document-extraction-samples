# Document extraction in .NET: samples

[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/luisquintanilla/dotnet-document-extraction-samples)

Small, runnable C# file-based apps that show `DocumentExtractionClient`
(`Microsoft.Extensions.DocumentExtraction`) and the five
[CommunityToolkit document extraction providers](https://github.com/CommunityToolkit/AI/tree/main/DocumentExtraction).

You write your document code once against `DocumentExtractionClient`. Then you pick the
provider: a local PDF parser, a vision model, or a cloud OCR service.

![Where document extraction fits in the .NET AI stack](docs/images/document-extraction-ai-stack.png)

> [!NOTE]
> These APIs are previews and marked experimental (`MEDE0001`, `EXTEXP0019`, `CTDE0001`).
> Expect them to change.

## What you need

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- Optional: [Ollama](https://ollama.com) for the vision model and embeddings samples
- Optional: an Azure subscription and the [Azure Developer CLI](https://aka.ms/azd) for the
  Azure Document Intelligence, Azure Content Understanding, and Mistral OCR providers

Or skip all of that and open the repo in GitHub Codespaces. The dev container has .NET 11 and an
Ollama sidecar with the models already pulled. Pick a 4-core or larger machine; vision models on
CPU are slow.

## Samples

Run each one from the repo root with `dotnet run <file>`.

| Sample | What it shows | Needs |
|---|---|---|
| [`01-extract.cs`](01-extract.cs) | Extract a PDF and print its outline | Nothing |
| [`02-swap-providers.cs`](02-swap-providers.cs) | Same code, any provider: `dotnet run 02-swap-providers.cs -- ollama` | The provider you pick |
| [`03-compare.cs`](03-compare.cs) | Run every configured provider on one page and write a side-by-side report | Any providers |
| [`04-tables-and-sources.cs`](04-tables-and-sources.cs) | Walk tables cell by cell and see where each element came from on the page | A provider that returns tables |
| [`05-pipeline.cs`](05-pipeline.cs) | Dependency injection, logging, and custom middleware, including an OCR fallback for scanned PDFs | OCRia-nano-1 models, Ollama, or an Azure provider |
| [`06-ingest.cs`](06-ingest.cs) | Feed extraction into `Microsoft.Extensions.DataIngestion`: chunk, embed, search | Ollama (embeddings) |

Captured runs for every sample are in [`outputs/`](outputs/), so you can see what each provider
returns without setting anything up.

All samples share [`common/Providers.cs`](common/Providers.cs), which creates a provider by name
and reads its settings from environment variables. Providers you haven't configured are skipped
with a message instead of failing.

## Providers

| Name | Provider | Runs | Good for | Settings |
|---|---|---|---|---|
| `pdfpig` | PdfPig | Locally, in process | PDFs with a text layer | None |
| `ocria` | [OCRia-nano-1](https://huggingface.co/llamaindex/OCRia-nano-1) (ONNX, local) | Locally, in process, on CPU | Scans, images, and layout (headings, tables) with no service or GPU | `OCRIA_MODEL_DIR` (optional); models from [`tools/ocria`](#try-ocria-nano-1-locally) |
| `ollama` | Vision language model (any `IChatClient`) | Locally with Ollama, or any hosted model | Scans and images, when you can review the output | `OLLAMA_ENDPOINT`, `OLLAMA_VISION_MODEL`, `OLLAMA_STRUCTURED` |
| `docintel` | Azure Document Intelligence | Azure | Layout, tables, and page coordinates | `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` |
| `cu` | Azure Content Understanding | Azure | Layout plus analyzers you can customize | `AZURE_CONTENT_UNDERSTANDING_ENDPOINT`, `AZURE_CONTENT_UNDERSTANDING_ANALYZER` |
| `mistral` | Mistral OCR | Microsoft Foundry or the Mistral API | Fast OCR to Markdown | `AZURE_MISTRAL_OCR_URI` and `AZURE_MISTRAL_OCR_MODEL`, or `MISTRAL_API_KEY` |

The Azure providers sign in with `DefaultAzureCredential` (for example, `az login`). No keys.

## Try it with Ollama

```bash
ollama pull qwen3.5:4b
ollama pull embeddinggemma
dotnet run 02-swap-providers.cs -- ollama
dotnet run 06-ingest.cs
```

The defaults are `http://localhost:11434`, `qwen3.5:4b`, and `embeddinggemma`. Override them with
`OLLAMA_ENDPOINT`, `OLLAMA_VISION_MODEL`, and `OLLAMA_EMBEDDING_MODEL`.

## Try OCRia-nano-1 locally

[`src/OcriaNano.DocumentExtraction`](src/OcriaNano.DocumentExtraction) is a
`DocumentExtractionClient` that runs LlamaIndex's OCRia-nano-1 models with ONNX Runtime, in
process. It keeps a PDF's text layer where there is one, OCRs the pages that need it, and runs the
layout model to find headings, headers and footers, lists, and tables (with merged cells). Each
element carries its page polygon. The C# code is a port of the pre- and post-processing in
[LiteParse](https://github.com/run-llama/liteparse) (Apache-2.0; see
[`NOTICE.md`](src/OcriaNano.DocumentExtraction/NOTICE.md)).

Download the models once (about 18 MB for Latin scripts), then run any sample with `ocria`:

```bash
pwsh tools/ocria/Get-OcriaModels.ps1 -AcceptLicense    # add -Scripts cjk,rtl,world for other scripts
dotnet run 02-swap-providers.cs -- ocria
```

The models go to the same folder LiteParse uses (`lit models pull ocria-nano` works too), or to
`OCRIA_MODEL_DIR`. The script checks every file against a pinned SHA-256. Without the models, the
samples skip `ocria` with a message.

> [!IMPORTANT]
> The OCRia-nano-1 weights aren't in this repo and aren't MIT. LlamaIndex publishes them under a
> [modified Open RAIL-M license](https://huggingface.co/llamaindex/OCRia-nano-1/blob/v1.0.0/LICENSE):
> free for research, personal use, and organizations under $2M in revenue or funding; above that,
> or for products that compete with LlamaParse, you need a commercial license from LlamaIndex.
> Read it before you use the models. The download script asks you to accept it.

**Why not just export to ONNX and call the model?** The models already are ONNX, and the client
loads them unchanged. What the graphs can't do is everything around them: turning the detection
map into rotated line boxes, cropping and warping each line, picking which recognizer to run,
reading the PDF text layer, building the layout model's per-word features from fonts and ruling
lines, and decoding its pair scores into paragraphs and table grids. That glue is the C# in
`src/OcriaNano.DocumentExtraction`.

## Try the Azure providers (optional)

`azd up` creates one Microsoft Foundry (Azure AI Services) resource with local auth turned off,
deploys `mistral-ocr-4-0`, and gives your account the roles it needs. The samples read the
endpoints from your azd environment automatically.

```bash
az login
azd auth login
azd provision        # pick a region with Content Understanding and Mistral OCR, such as eastus2
dotnet run 03-compare.cs
```

When you're done, delete everything:

```bash
azd down --purge
```

Cloud providers send your document to the service and bill per page. Check the pricing for
[Document Intelligence](https://azure.microsoft.com/pricing/details/ai-document-intelligence/),
[Content Understanding](https://azure.microsoft.com/pricing/details/content-understanding/), and
Mistral OCR in Foundry before you run large batches.

## The sample books

The input documents in [`books/`](books/) are three short intro books written for this repo:

- *Tokens, Explained*: what tokens are and how to count them in .NET
- *Tensors in Ten Pages*: shapes, math, and `TensorPrimitives`
- *Agents Propose, Tools Decide*: checking AI-written code with tools you already have

Each has headings, running headers and footers, a table with a merged header cell, a figure,
and code, so you can see how each provider handles real structure. Every book also comes as a
`-scanned.pdf` with no text layer, which is where PdfPig stops and OCR takes over. See
[`books/AUTHORING.md`](books/AUTHORING.md) to rebuild them or add your own.

## Learn more

- [`DocumentExtractionClient` API reference](https://learn.microsoft.com/dotnet/api/microsoft.extensions.documentextraction.documentextractionclient?view=net-11.0-pp)
- [`Microsoft.Extensions.DocumentExtraction` API proposal](https://github.com/dotnet/extensions/issues/7587)
- [CommunityToolkit document extraction providers](https://github.com/CommunityToolkit/AI/tree/main/DocumentExtraction)
- [Data ingestion building blocks for .NET](https://devblogs.microsoft.com/dotnet/introducing-data-ingestion-building-blocks-preview/)

## License

[MIT](LICENSE). The OCRia port in `src/OcriaNano.DocumentExtraction` includes code adapted from
LiteParse under Apache-2.0 ([`NOTICE.md`](src/OcriaNano.DocumentExtraction/NOTICE.md)). The
OCRia-nano-1 models are licensed separately, as described [above](#try-ocria-nano-1-locally).
