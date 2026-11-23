# Results

Mean recall of each page's ground-truth tokens. Compare columns within a row: the numbers measure how
much of what a provider returned each reading strategy sees, not how good the providers are.

| Provider | Pages | Nested pages | A | B | C | D | E |
|---|---:|---:|---:|---:|---:|---:|---:|
| PdfPig (local, text layer) | 10/10 | 0 | 0.81 | 0.81 | 0.81 | 0.81 | 0.81 |
| OCRia-nano-1 (ONNX, local) | 10/10 | 0 | 0.67 | 0.67 | 0.85 | 0.85 | 0.85 |
| Vision language model (Ollama) | 9/10 | 0 | 0.83 | 0.83 | 0.83 | 0.83 | 0.83 |
| Mistral Document AI | 10/10 | 0 | 0.76 | 0.76 | 0.76 | 0.76 | 0.76 |
| Azure Document Intelligence | 9/10 | 8 | 0.06 | 0.50 | 0.77 | 0.85 | 0.85 |
| Azure Content Understanding | 10/10 | 10 | 0.05 | 0.48 | 0.84 | 0.92 | 0.92 |

A: two-level loop over text elements. B: `EnumerateContent()` keeping only text elements. C: recursive walker that
skips image text. D: `EnumerateContent()` + `GetText()` per element. E: `document.GetText()`.

## Failures

- ollama on `dense_tables_dense_table_002`: TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.
- docintel on `complex_table_headers_complex_header_006`: InvalidOperationException: A covered position must be edited through its anchor cell.

## Per page (recall A → E)

| Page | pdfpig | ocria | ollama | mistral | docintel | cu |
|---|---|---|---|---|---|---|
| `charts__Web_version__E-Government_Survey_2024_1392024_p101` | 1 → 1 | 0.471 → 0.941 | 1 → 1 | 0.294 → 0.294 | 0.059 → 1 | 0.059 → 1 |
| `complex_table_headers_complex_header_003` | 0.989 → 0.989 | 0.605 → 0.993 | 0.811 → 0.811 | 0.982 → 0.982 | 0.018 → 0.987 | 0.018 → 0.991 |
| `complex_table_headers_complex_header_006` | 0.979 → 0.979 | 0.256 → 0.979 | 0.533 → 0.533 | 0.964 → 0.964 | failed | 0.108 → 0.974 |
| `dense_tables_dense_table_002` | 0.93 → 0.93 | 0.253 → 0.253 | failed | 0 → 0 | 0.263 → 0.263 | 0 → 0.744 |
| `financial_tables_10k-2026-08-13_page1` | 0.994 → 0.994 | 0.994 → 0.994 | 0.988 → 0.988 | 0.988 → 0.988 | 0 → 0.994 | 0 → 0.994 |
| `forms_blank__australia--02_WA-DLA1--p2__blank` | 0.868 → 0.868 | 0.868 → 0.868 | 0.868 → 0.868 | 0.868 → 0.868 | 0 → 0.868 | 0 → 0.868 |
| `headings_case-0001-6bba8f14b5f6` | 1 → 1 | 1 → 1 | 1 → 1 | 1 → 1 | 0.043 → 1 | 0.217 → 1 |
| `layout_annotated__02775267_p3` | 0.602 → 0.602 | 0.602 → 0.602 | 0.593 → 0.593 | 0.598 → 0.598 | 0.045 → 0.608 | 0.045 → 0.608 |
| `tables_1_timetable__1__page2` | 0.75 → 0.75 | 0.9 → 0.9 | 0.69 → 0.69 | 1 → 1 | 0.02 → 1 | 0.02 → 1 |
| `text_text_dense__baoutou` | 0 → 0 | 0.782 → 0.957 | 0.946 → 0.946 | 0.914 → 0.914 | 0.071 → 0.97 | 0.045 → 0.971 |
