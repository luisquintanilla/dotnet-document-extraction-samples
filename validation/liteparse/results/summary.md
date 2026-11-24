# C# port vs LiteParse

LiteParse (lit 2.21.1) with `--ocr-backend ocria-nano --format markdown`, against `OcriaNanoDocumentExtractionClient` `GetText()` with default options.
Words are lowercased runs of letters and digits (CJK: one per ideograph); Markdown image links and HTML tags are dropped.

- **Recall**: share of LiteParse's distinct words the port also produced.
- **Precision**: share of the port's distinct words LiteParse also produced.
- **Order**: 2·LCS / (LiteParse words + port words) over the word sequences; 1.0 when every word appears in the same order.
- **Truth**: ParseBench ground-truth token recall (the probe's measure), for LiteParse and for the port.

| Input | Recall | Precision | Order | Truth (LiteParse) | Truth (port) |
|---|---|---|---|---|---|
| books__agents-propose-tools-decide-hero | 1.000 | 0.937 | 0.977 | | |
| books__agents-propose-tools-decide-hero.png | 1.000 | 0.937 | 0.977 | | |
| books__agents-propose-tools-decide-scanned | 1.000 | 0.992 | 0.967 | | |
| books__agents-propose-tools-decide | 0.964 | 0.991 | 0.938 | | |
| books__tensors-in-ten-pages-hero | 0.971 | 0.925 | 0.853 | | |
| books__tensors-in-ten-pages-hero.png | 0.990 | 0.920 | 0.940 | | |
| books__tensors-in-ten-pages-scanned | 1.000 | 0.992 | 0.967 | | |
| books__tensors-in-ten-pages | 0.987 | 0.991 | 0.928 | | |
| books__tokens-explained-hero | 0.833 | 0.926 | 0.843 | | |
| books__tokens-explained-hero.png | 0.992 | 0.930 | 0.927 | | |
| books__tokens-explained-scanned | 1.000 | 0.989 | 0.971 | | |
| books__tokens-explained | 0.891 | 0.988 | 0.899 | | |
| parsebench__charts__Web_version__E-Government_Survey_2024_1392024_p101 | 0.993 | 0.889 | 0.902 | 0.941 | 0.941 |
| parsebench__complex_table_headers_complex_header_003 | 0.998 | 0.993 | 0.774 | 0.986 | 0.993 |
| parsebench__complex_table_headers_complex_header_006 | 0.983 | 0.893 | 0.941 | 0.897 | 0.979 |
| parsebench__dense_tables_dense_table_002 | 0.218 | 0.231 | 0.453 | 0.228 | 0.986 |
| parsebench__financial_tables_10k-2026-08-13_page1 | 1.000 | 1.000 | 0.757 | 0.994 | 0.994 |
| parsebench__forms_blank__australia--02_WA-DLA1--p2__blank | 0.997 | 0.993 | 0.973 | 0.868 | 0.868 |
| parsebench__headings_case-0001-6bba8f14b5f6 | 1.000 | 1.000 | 0.999 | 1.000 | 1.000 |
| parsebench__layout_annotated__02775267_p3 | 0.990 | 0.974 | 0.813 | 0.602 | 0.602 |
| parsebench__tables_1_timetable__1__page2 | 1.000 | 0.676 | 0.975 | 0.710 | 0.900 |
| parsebench__text_text_dense__baoutou | 1.000 | 1.000 | 0.881 | 0.957 | 0.957 |
| **Mean** | **0.946** | **0.917** | **0.893** | | |
