using OcriaNano.DocumentExtraction.Document;

namespace OcriaNano.DocumentExtraction.Tests.Document;

public class MarkdownAndSelectionTests
{
    [Theory]
    [InlineData("title", "Tokens", 1, "# Tokens")]
    [InlineData("heading", " Why ", 3, "### Why")]
    [InlineData("listItem", "• one item", null, "- one item")]
    [InlineData("code", "x = 1", null, "```\nx = 1\n```")]
    [InlineData("paragraph", "plain", null, "plain")]
    public void TextUsesRoleMarkers(string role, string text, int? level, string expected)
        => Assert.Equal(expected, MarkdownWriter.Text(role, text, level));

    [Fact]
    public void TableWithoutSpansIsGfm()
    {
        MarkdownTable t = new(2, 2, [new(0, 0, 1, 1, "A", true), new(0, 1, 1, 1, "B|C", true), new(1, 0, 1, 1, "1", false), new(1, 1, 1, 1, "2", false)]);
        Assert.Equal("| A | B\\|C |\n| --- | --- |\n| 1 | 2 |", MarkdownWriter.Table(t));
    }

    [Fact]
    public void TableWithSpansIsHtml()
    {
        MarkdownTable t = new(2, 2, [new(0, 0, 1, 2, "Head & tail", true), new(1, 0, 1, 1, "1", false), new(1, 1, 1, 1, "2", false)]);
        Assert.Equal("<table>\n<tr><th colspan=\"2\">Head &amp; tail</th></tr>\n<tr><td>1</td><td>2</td></tr>\n</table>", MarkdownWriter.Table(t));
    }

    [Theory]
    [InlineData(null, 3, new[] { 1, 2, 3 })]
    [InlineData("2", 3, new[] { 2 })]
    [InlineData("3-1", 3, null)]
    [InlineData(" 1 , 2-9 ", 4, new[] { 1, 2, 3, 4 })]
    [InlineData("5,1", 3, new[] { 1 })]
    [InlineData("a", 3, null)]
    public void PageSelectionParses(string? spec, int count, int[]? expected)
    {
        if (expected is null)
        {
            Assert.Throws<ArgumentException>(() => PageSelection.Parse(spec, count));
        }
        else
        {
            Assert.Equal(expected, PageSelection.Parse(spec, count));
        }
    }
}
