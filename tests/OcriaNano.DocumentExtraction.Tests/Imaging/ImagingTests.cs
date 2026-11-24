using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Ocr;

namespace OcriaNano.DocumentExtraction.Tests.Imaging;

// Ported from the #[cfg(test)] tests of LiteParse crates/liteparse/src/ocr/ocria/img.rs, Apache-2.0, plus checks of the other primitives.
public class ImagingTests
{
    [Fact]
    public void Rot90MatchesNumpy()
    {
        Img img = new(3, 2, 1, [1, 2, 3, 4, 5, 6]);
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, img.Rot90(1).Data);
        Assert.Equal(new byte[] { 6, 5, 4, 3, 2, 1 }, img.Rot90(2).Data);
        Assert.Equal(2, img.Rot90(1).Width);
        Assert.Equal(3, img.Rot90(1).Height);
    }

    [Fact]
    public void BicubicWeightsSumToOne()
    {
        Assert.Equal(1024, Perspective.BicubicTab.Length);
        Assert.All(Perspective.BicubicTab, row => Assert.Equal(32768, row.Sum()));
    }

    [Fact]
    public void MinAreaRectOfAxisAlignedBox()
    {
        RotRect r = Geometry.MinAreaRect([(0f, 0f), (10f, 0f), (10f, 4f), (0f, 4f)]);
        Assert.Equal(5f, r.Cx, 1e-4f);
        Assert.Equal(2f, r.Cy, 1e-4f);
        Assert.Equal(10f, MathF.Max(r.W, r.H), 1e-4f);
        Assert.Equal(4f, MathF.Min(r.W, r.H), 1e-4f);
    }

    [Fact]
    public void AreaResizeAveragesBlocks()
    {
        Img img = new(2, 2, 1, [10, 20, 30, 40]);
        Assert.Equal(new byte[] { 25 }, Resize.Area(img, 1, 1).Data);
    }

    [Fact]
    public void IdentityWarpKeepsPixels()
    {
        byte[] data = [.. Enumerable.Range(0, 8 * 8).Select(i => (byte)(i * 3))];
        Img img = new(8, 8, 1, data);
        PointF[] quad = [new(0, 0), new(8, 0), new(8, 8), new(0, 8)];
        double[] m = Perspective.Transform(quad, quad);
        Assert.Equal(1.0, m[0], 1e-9);
        Assert.Equal(1.0, m[4], 1e-9);
        Img warped = Perspective.WarpCubic(img, m, 8, 8);
        Assert.Equal(data[(3 * 8) + 3], warped.Data[(3 * 8) + 3]);
    }

    [Fact]
    public void OtsuSeparatesTwoLevels()
    {
        byte t = Geometry.Otsu([.. Enumerable.Repeat((byte)20, 50), .. Enumerable.Repeat((byte)220, 50)]);
        Assert.InRange(t, 20, 220);
    }

    [Fact]
    public void ComponentsAreFourConnected()
    {
        bool[] mask = [true, false, false, false, true, false, false, false, true];
        Assert.Equal(3, Geometry.Components4(mask, 3, 3).Count);
    }

    [Fact]
    public void OrderQuadStartsTopLeft()
    {
        PointF[] q = Quads.Order([new(10, 0), new(10, 4), new(0, 4), new(0, 0)]);
        Assert.Equal(new PointF(0, 0), q[0]);
        Assert.Equal(new PointF(10, 0), q[1]);
        Assert.Equal(new PointF(10, 4), q[2]);
        Assert.Equal(new PointF(0, 4), q[3]);
    }

    [Fact]
    public void CollinearBoxesWithASmallGapMerge()
    {
        PointF[] a = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        PointF[] b = [new(12, 0), new(22, 0), new(22, 10), new(12, 10)];
        PointF[] far = [new(100, 0), new(110, 0), new(110, 10), new(100, 10)];
        Assert.Single(LineMerger.Merge([a, b], 0.6f));
        Assert.Equal(2, LineMerger.Merge([a, far], 0.6f).Count);
    }
}
