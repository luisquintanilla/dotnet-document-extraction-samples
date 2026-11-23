// Ported from LiteParse crates/liteparse/src/table_model/{features,units}.rs, Apache-2.0.
using OcriaNano.DocumentExtraction.Imaging;
using OcriaNano.DocumentExtraction.Layout;

namespace OcriaNano.DocumentExtraction.Tests.Layout;

public class FeatureTests
{
    [Fact]
    public void NumericText()
    {
        Assert.True(UnitFeatures.IsNumericText("1,234.5"));
        Assert.False(UnitFeatures.IsNumericText("abc"));
    }

    [Fact]
    public void GreyUsesBgrxWeightsOnBgraBytes()
    {
        // the first byte of a BGRA pixel is weighted as red (4899/16384), as the Rust code does for BGRx
        Bgra32Image img = new(1, 1, [255, 0, 0, 255]);
        GrayImage g = RuleDetector.Grey(img);
        Assert.Equal((byte)((255 * 4899) >> 14), g.Data[0]);
    }

    [Fact]
    public void DetectsDrawnRules()
    {
        const int w = 300, h = 300;
        Bgra32Image img = new(w, h);
        Array.Fill(img.Data, (byte)255);
        for (int x = 20; x < 280; x++)
        {
            for (int y = 100; y < 102; y++)
            {
                int o = ((y * w) + x) * 4;
                img.Data[o] = img.Data[o + 1] = img.Data[o + 2] = 0;
            }
        }

        Rules r = RuleDetector.Detect(img);
        Assert.Single(r.H);
        Assert.Empty(r.V);
    }

    [Fact]
    public void FeaturesHaveExpectedShapeAndNoRuleFlag()
    {
        List<Unit> units =
        [
            new Unit { Bx = new Box4(10, 10, 60, 20), PBox = new Box4(10, 10, 60, 20), Text = "Total", H = 10, Size = 10 },
            new Unit { Bx = new Box4(70, 10, 120, 20), PBox = new Box4(70, 10, 120, 20), Text = "12.50", H = 10, Size = 10 },
        ];
        float[] f = UnitFeatures.Compute(units, 200, 200, null);
        Assert.Equal(2 * UnitFeatures.Dim, f.Length);
        Assert.Equal(68, UnitFeatures.Dim);
        for (int i = 0; i < 2; i++)
        {
            int o = i * 68;
            Assert.All(f[(o + 53)..(o + 61)], v => Assert.Equal(0f, v));
            Assert.Equal(0f, f[o + 65]);
            Assert.Equal(-1f, f[o + 66]);
            Assert.Equal(-1f, f[o + 67]);
        }
    }
}
