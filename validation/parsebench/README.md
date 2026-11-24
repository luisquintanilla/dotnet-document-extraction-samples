# Reading an `ExtractionDocument`: how much text does each approach see?

This harness backs [dotnet/extensions#7962](https://github.com/dotnet/extensions/issues/7962). It extracts ten hard
pages from [ParseBench-2](https://huggingface.co/datasets/llamaindex/ParseBench-2) with each provider in this repo, then
reads every result five ways and measures how much of the page's text each way returns.

The point is the gap *within* a provider between the first strategy and the last. The numbers are not a ranking of
providers: the metric is a simple token recall, the sample is ten pages, and the VLM and LLM-based providers are
nondeterministic.

## Strategies

| Id | Strategy | What it misses on nested providers |
|---|---|---|
| A | `foreach section → foreach element → if (element is ExtractionDocumentTextElement)` | Nested sections, table cells, image text |
| B | `document.EnumerateContent()`, keeping only `ExtractionDocumentTextElement` | Table cells, image text |
| C | Recursive walker over sections and table cells that prints every `TextContent` (the CommunityToolkit README's `PrintElement`) | Image text, such as chart labels |
| D | `document.EnumerateContent()` + `element.GetText()` | Nothing |
| E | `document.GetText()` | Nothing |

D and E use the APIs proposed in #7962.

## Results

See [results/summary.md](results/summary.md) for per-provider means and per-page numbers. Committed:

| File | Contents |
|---|---|
| `results/summary.md` | Per-provider means, failures and per-page recall |
| `results/results.json` | Recall, character counts, repeated-line counts and structure counts for every provider, page and strategy. Numbers only, no extracted text. |
| `results/manifest.json` | Run time, runtime, dataset revision, model or analyzer IDs and package versions. No endpoints or credentials. |

Each run also writes what the providers returned to `results/<provider>/`. Those files are git-ignored; run the
harness to inspect them:

| File | Contents |
|---|---|
| `<page>.document.json` | The document's structure: sections, elements, roles, text, table cells with spans, image text, page numbers. Metadata, raw provider responses, image bytes, URIs and bounding boxes are left out. |
| `<page>.<A-E>.txt` | The text each strategy read |

## Run it

1. Configure the providers you want, the same way as for the samples (`azd up`, or the environment variables listed in
   [common/Providers.cs](../../common/Providers.cs)). Providers that aren't configured are skipped.
2. Until #7962 ships, `common/Providers.cs` on this branch references a prototype build of
   `Microsoft.Extensions.Documents.Abstractions`. Pack it from the #7962 pull request and add the folder as a package
   source.
3. Run:

   ```bash
   dotnet run probe.cs                 # every configured provider
   dotnet run probe.cs -- cu,docintel  # just these
   ```

The first run downloads the ten pages and their ground truth from a pinned dataset revision into `.corpus/` and checks
them against the SHA-256 hashes in [corpus.json](corpus.json). Running some providers replaces only their results and
keeps the rest. Pages that a hosted service throttles (HTTP 429) are retried with backoff.

## Reading the numbers

- **Flat providers** (PdfPig, the vision language model, Mistral) return paragraphs and tables as top-level elements,
  so every strategy reads the same text. Their A and E columns match.
- **Nested providers** (Document Intelligence, Content Understanding) put text inside nested sections and table cells,
  and chart labels in image text. The two-level loop (A) misses almost all of it; that gap is #7962.
- **OCRia-nano-1** (local ONNX) keeps sections flat but returns detected tables as table elements with cells, so the
  text-only strategies (A, B) miss the table text and its A column sits below E. It reads the text layer where one
  exists, and OCRs pages without one: it recovers `text_text_dense__baoutou` (0.96 against 0 for PdfPig). On
  `dense_tables_dense_table_002` (a table of VINs and short all-caps codes) its garble check ignores digit-bearing and
  short all-caps tokens when it measures vowels, so it keeps the text layer and scores 0.99 against 0.93 for PdfPig.
- A low number in *both* A and E is about extraction, not reading. Examples:
  - `dense_tables_dense_table_002` is a rotated, low-resolution scan. Document Intelligence returned it as flat lines
    with no table.
  - On the same page Mistral returned a single, repetitive header row.
  - PdfPig reads the PDF's text layer, so it finds nothing on `text_text_dense__baoutou`, which has none.
- Failures recorded in `summary.md`:
  - The local vision model timed out on `dense_tables_dense_table_002`.
  - Document Intelligence fails on `complex_table_headers_complex_header_006` because the provider rejects a table
    whose reported cells overlap. That's a CommunityToolkit provider issue, separate from #7962.

## How recall is measured

Each ParseBench-2 page comes with a `.test.json` that holds the expected Markdown and test rules. The harness takes the
lowercased words and numbers from both (one token per ideograph for CJK text, HTML tags stripped) and reports the share
of those tokens that appear in what a strategy read. It doesn't check order, structure or duplicates, so it measures
only whether text was *seen*.

## Data

The pages come from [llamaindex/ParseBench-2](https://huggingface.co/datasets/llamaindex/ParseBench-2) (Apache-2.0),
revision `c300dcf475574f027dbaad0e15f64f6c9b73b830`. They aren't committed here; `probe.cs` downloads them.
