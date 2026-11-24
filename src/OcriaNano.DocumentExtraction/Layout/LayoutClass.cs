// Port of LiteParse crates/liteparse/src/table_model/mod.rs (BLOCK_CLASSES), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>The 13 block classes LayoutNet's <c>cls</c> head predicts, in output order.</summary>
internal enum LayoutClass
{
    Text,
    Heading,
    Title,
    PageHeader,
    PageFooter,
    List,
    Table,
    Caption,
    Footnote,
    Formula,
    Code,
    FigureText,
    KeyValue,
}

internal static class LayoutClasses
{
    public const int Count = 13;
}
