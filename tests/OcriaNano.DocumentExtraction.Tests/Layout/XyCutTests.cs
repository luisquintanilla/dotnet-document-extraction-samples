using OcriaNano.DocumentExtraction.Layout;

namespace OcriaNano.DocumentExtraction.Tests.Layout;

public class XyCutTests
{
    private const double PageW = 612, PageH = 792;

    private static XyItem Line(double x0, double y0, double x1, string text) => new(new Box4(x0, y0, x1, y0 + 9), text, false);

    private static List<XyItem> Column(double x0, double x1, double y0, int lines, string tag) =>
        [.. Enumerable.Range(0, lines).Select(i => Line(x0, y0 + (i * 12), x1, $"{tag}{i}"))];

    private static List<string> Read(List<XyItem> items) =>
        [.. XyCut.Leaves(items, PageW, PageH).SelectMany(l => l).Select(i => items[i].Text)];

    [Fact]
    public void TwoColumnsReadColumnByColumn()
    {
        List<XyItem> items = [.. Column(50, 290, 100, 30, "L"), .. Column(322, 562, 100, 30, "R")];

        List<string> order = Read(items);

        Assert.Equal([.. Enumerable.Range(0, 30).Select(i => $"L{i}"), .. Enumerable.Range(0, 30).Select(i => $"R{i}")], order);
    }

    [Fact]
    public void FullWidthTitleComesBeforeColumns()
    {
        List<XyItem> items = [.. Column(50, 290, 120, 25, "L"), .. Column(322, 562, 120, 25, "R"), Line(50, 80, 562, "TITLE")];

        List<string> order = Read(items);

        Assert.Equal("TITLE", order[0]);
        Assert.True(order.IndexOf("L24") < order.IndexOf("R0"));
    }

    [Fact]
    public void SingleColumnProseKeepsTopToBottomOrder()
    {
        List<XyItem> items = Column(72, 540, 72, 40, "P");

        Assert.Equal([.. items.Select(i => i.Text)], Read(items));
    }

    [Fact]
    public void TableGridIsNotReadColumnMajor()
    {
        List<XyItem> items = [];
        for (int r = 0; r < 20; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                // Short cells leave most of each column slot empty, unlike prose.
                items.Add(Line(60 + (c * 130), 100 + (r * 12), 90 + (c * 130), $"r{r}c{c}"));
            }
        }

        List<string> order = Read(items);

        Assert.True(order.IndexOf("r0c3") < order.IndexOf("r1c0"));
        Assert.True(order.IndexOf("r18c0") < order.IndexOf("r19c3"));
    }

    [Fact]
    public void IsolatedPageNumberFooterReadsLast()
    {
        List<XyItem> items = [.. Column(50, 290, 100, 30, "L"), .. Column(322, 562, 100, 30, "R"), Line(300, 750, 312, "7")];

        Assert.Equal("7", Read(items)[^1]);
    }
}
