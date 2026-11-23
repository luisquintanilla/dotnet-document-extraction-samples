# Vendored prototype packages

The samples read documents through `ExtractionDocument.GetText()`, `ExtractionDocumentSection.EnumerateContent()` and the
other traversal APIs proposed in [dotnet/extensions#7963](https://github.com/dotnet/extensions/pull/7963) (for
[dotnet/extensions#7962](https://github.com/dotnet/extensions/issues/7962)). Until that ships, this folder carries a
build of the PR so the repo restores on any machine. `NuGet.config` maps only `Microsoft.Extensions.Documents.Abstractions`
to this folder; everything else comes from nuget.org.

| Package | Built from | SHA-256 |
|---|---|---|
| `Microsoft.Extensions.Documents.Abstractions.11.0.0-preview.1.26600.4.nupkg` | dotnet/extensions `luisquintanilla-extraction-document-traversal` @ `cfddcd62decda849c63f50d9e2bc400255251df0` | `b086fe798c3154bc31f09c3211f4bf8764c70561a87cf25ecd2736c0e626c5ae` |

It is MIT-licensed like the rest of dotnet/extensions. Delete this folder, the `local` source in `NuGet.config` and the
`#:package Microsoft.Extensions.Documents.Abstractions@…` overrides once a `Microsoft.Extensions.DocumentExtraction`
release includes the traversal APIs.
