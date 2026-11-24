# OcriaNano.DocumentExtraction notices

## LiteParse (Apache-2.0)

This library is a C# port of the OCRia-nano pipeline glue in
[LiteParse](https://github.com/run-llama/liteparse) (`crates/liteparse/src/ocr/ocria/*`,
`crates/liteparse/src/table_model/*` and parts of `crates/liteparse/src/ocr_merge.rs`),
Copyright LlamaIndex, licensed under the Apache License, Version 2.0
(http://www.apache.org/licenses/LICENSE-2.0).

Ported files carry a header naming the LiteParse source they follow. Constants, thresholds and the
homoglyph tables are copied from LiteParse. The C# code was changed from the original: it is a
different language, it runs the models through ONNX Runtime for .NET, it reads PDFs with PdfPig and
renders them with PDFium (via PDFtoImage). From LiteParse's projection features (`projection.rs`) it
ports only the XY-cut reading-order stage, and it does not port the pure-Rust network kernels
(`table_model/net.rs`).

## OCRia-nano-1 models (not included)

The ONNX models this library runs are **not** part of the library or this repository. They are
published by LlamaIndex at https://huggingface.co/llamaindex/OCRia-nano-1 under a modified
Open RAIL-M license:
https://huggingface.co/llamaindex/OCRia-nano-1/blob/v1.0.0/LICENSE

In short (read the license itself): free for research, personal use, and organizations under
USD 2M in annual revenue or funding; larger organizations, and products that compete with
LlamaParse, need a commercial license from LlamaIndex. `tools/ocria/Get-OcriaModels.ps1` shows the
license and asks you to accept it before it downloads anything. The library never downloads models.

## Numerics

The port keeps OpenCV/Rust bit parity where a value feeds a hard threshold (integer grey
conversion, INTER_AREA resize, bicubic warp, connected components, minAreaRect, Otsu, CTC, the
component and line confidence means): that code is scalar and follows the original operation
order. `System.Numerics.Tensors` is used for elementwise operations (which are bit-exact; fused
multiply-add is never used) and for reductions and transcendentals (softmax, sigmoid, sin/cos)
only where the result feeds a network input or a soft ranking.
